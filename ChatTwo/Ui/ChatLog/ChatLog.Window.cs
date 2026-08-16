using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ChatTwo.Code;
using ChatTwo.GameFunctions;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Ui.Handler;
using ChatTwo.Util;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Bindings.ImGui;
using Lumina.Extensions;

namespace ChatTwo.Ui.ChatLog;

/// <summary>
/// 消息区滚轮/滚动条交互状态。主窗口与 PopOut 各自持有一份，互不干扰——
/// 之前共用 ChatLog 字段导致 PopOut 拖动滚动条时带动主窗口、滚轮互抢。
/// </summary>
public sealed class MessageLogState
{
    public float PendingWheel;
    public bool UserScrolled;
    /// <summary>消息区是否已滚动到顶部（滚轮消费处更新）。聊天记录窗口的无限滚动依赖它：</summary>
    public bool AtTop;
    public bool DraggingScrollbar;
    public float ScrollbarDragStartY;
    public float ScrollbarDragStartScroll;

    // ⚠️ 选字状态必须按窗口隔离（2026-08-15 22:58 用户实测修复）：
    // 主窗口与 PopOut 都调用 ChatLog.DrawMessageLog（Popout.cs L175），若 Selection 挂在
    // ChatLog 实例上则所有窗口共享同一个 → 选几个字变一大片/无法取消/多 PopOut 只有一个能选。
    // MessageLogState 每个窗口独立持有（主窗口 MsgState / PopOut 各自 new），
    // 选字互不干扰。
    public TextSelectionState Selection = new();
}

public partial class ChatLog : Window, IChatWindow
{
    // cimgui 的 igSetNextWindowScroll（Begin 前锁定滚动位置，唤出帧当帧即生效）。
    // 防御式加载：导出不存在时返回 null，降级用 Begin 后的 SetScrollY(0)（下帧生效）。
    private static readonly unsafe delegate* unmanaged<Vector2, void> ResetScrollFn = LoadResetScrollFn();

    private static unsafe delegate* unmanaged<Vector2, void> LoadResetScrollFn()
    {
        try
        {
            var handle = NativeLibrary.Load("cimgui");
            var ptr = NativeLibrary.GetExport(handle, "igSetNextWindowScroll");
            return (delegate* unmanaged<Vector2, void>)ptr;
        }
        catch
        {
            return null;
        }
    }

    private const string ChatChannelPicker = "chat-channel-picker";

    private readonly Plugin Plugin;
    public readonly InputHandler InputHandler;

    public bool TellSpecial;
    private readonly Stopwatch LastResize = new();

    // Used to detect channel changes for the webinterface
    public Chunk[] PreviousChannel = [];

    private unsafe ImGuiViewport* LastViewport;
    private bool WasDocked;

    private bool DrewThisFrame;

    public bool IsHidden;
    public HideState CurrentHideState { get; set; } = HideState.None;

    // 快捷锁定：选字时锁定窗口移动（防止拖拽选字误拖动窗口）
    public bool MoveLocked;

    public Vector2 LastWindowPos { get; set; } = Vector2.Zero;
    public Vector2 LastWindowSize { get; set; } = Vector2.Zero;

    public readonly List<bool> PopOutDocked = [];
    public readonly HashSet<Guid> PopOutWindows = [];

    private bool IsResizingTopRight;
    private Vector2 ResizeStartMousePos;
    private Vector2 ResizeStartWindowPos;
    private Vector2 ResizeStartWindowSize;
    private bool MouseOverResizeHandle;

    private bool SuppressNextActivate;

    // 消息区交互状态（主窗口实例；PopOut 各自持有独立实例）
    public readonly MessageLogState MsgState = new();

    // 底部标签页栏末尾"+"新建标签页的命名输入（用户要求：点 + 弹窗命名后创建）
    private string NewTabName = string.Empty;

    // Tracks the tab index rendered in the previous frame, used to detect
    // tab switches in the bottom layout where DrawBottomTabBar runs after
    // DrawMessageLog.
    private int _renderedTabIndex = -1;

    public ChatLog(Plugin plugin) : base($"{Plugin.PluginName}###chat2")
    {
        Plugin = plugin;

        // 锁定状态持久化：从配置恢复上次的锁定/解锁（2026-08-15 新增）
        MoveLocked = Plugin.Config.MoveLocked;

        Size = new Vector2(500, 250);
        SizeCondition = ImGuiCond.FirstUseEver;

        PositionCondition = ImGuiCond.Always;

        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        InputHandler = new InputHandler(this, plugin, "MainChatLog");

        Plugin.Commands.Register("/clearlog2", "Clear the Chat 2 chat log").Execute += ClearLog;
        Plugin.Commands.Register("/chat2").Execute += ToggleChat;

        Plugin.ClientState.Login += Login;
        Plugin.ClientState.Logout += Logout;

        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "ItemDetail", MoveTooltip);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "ActionDetail", MoveTooltip);
        // PostShow：addon 显示完成的当帧立即 SetPosition，消除"首帧在原生位置、下一帧才移动"的闪烁
        // （与菜单打开后立即 SetPosition 同思路；提示框由游戏触发打开，用 PostShow 捕获时机）
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostShow, "ItemDetail", MoveTooltip);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostShow, "ActionDetail", MoveTooltip);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "ContextMenu", MoveContextMenu);
        // 二级菜单是独立的 AddonContextSub addon（2026-08-14 确认，非 ContextMenu 子节点）。
        // PreDraw 持续跟随聊天框移动；PostShow 在显示完成当帧 SetPosition 防"闪一下消失"
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "AddonContextSub", MoveContextSubMenu);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostShow, "AddonContextSub", MoveContextSubMenu);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContextMenu", OnContextMenuClosed);

        // 提示框零闪帧：hook ItemDetail/ActionDetail 的 SetPosition，detour 替换坐标（正式功能）
        InitSetPosHook();
        // OpenAddonByAgent vtable 22 hook：OpenContextMenu 前设 BlockedParentId=ChatLog（DR 注入识别用）
        InitOpenAddonByAgentHook();
        // 顶点挖洞（正式功能，2026-08-15 20:11 出宏）：hook igRender 剔除聊天框内菜单区域三角形
        RenderHole.Init(Plugin);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, "ItemDetail", MoveTooltip);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, "ActionDetail", MoveTooltip);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostShow, "ItemDetail", MoveTooltip);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostShow, "ActionDetail", MoveTooltip);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, "ContextMenu", MoveContextMenu);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, "AddonContextSub", MoveContextSubMenu);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostShow, "AddonContextSub", MoveContextSubMenu);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ContextMenu", OnContextMenuClosed);

        _setPosHook?.Dispose();
        _setPosHook = null;
        _openAddonByAgentHook?.Dispose();
        _openAddonByAgentHook = null;
        RenderHole.Dispose();

        Plugin.ClientState.Logout -= Logout;
        Plugin.ClientState.Login -= Login;
        Plugin.Commands.Register("/chat2").Execute -= ToggleChat;
        Plugin.Commands.Register("/clearlog2").Execute -= ClearLog;
    }

    private void Logout(int _, int __)
    {
        Plugin.MessageManager.ClearAllTabs();
    }

    private void Login()
    {
        Plugin.MessageManager.FilterAllTabsAsync();
    }

    public unsafe void Activated(ChatActivatedArgs args)
    {
        TellSpecial = args.TellSpecial;

        // Only suppress input focus when ChatTwo itself switches channels via a
        // Tab click. Hotkey channel switching (CMD_SAY, CMD_PARTY, ...) should
        // behave like the vanilla game — it opens/activates the input field.
        // SuppressNextActivate is set in TabSwitched() right before calling
        // SetChannelWithExtraChat().
        var suppressed = SuppressNextActivate;
        SuppressNextActivate = false;

        InputHandler.Activate = !suppressed || args.Input != null || args.AddIfNotPresent != null;
        InputHandler.PlayedClosingSound = false;
        if (Plugin.Config.PlaySounds)
            UIGlobals.PlaySoundEffect(InputHandler.ChatOpenSfx);

        // Don't set the channel or text content when activating a disabled tab.
        if (Plugin.CurrentTab.InputDisabled)
        {
            // The closing sound would've been immediately played in this case.
            InputHandler.PlayedClosingSound = true;
            return;
        }

        if (args.AddIfNotPresent != null && !InputHandler.ChatInput.Contains(args.AddIfNotPresent))
        {
            // Replace the full chat input if it's a command
            if (args.AddIfNotPresent.StartsWith('/'))
                InputHandler.ChatInput = args.AddIfNotPresent;
            else
                InputHandler.ChatInput += args.AddIfNotPresent;
        }

        if (args.Input != null)
        {
            // Replace the full chat input if it's a command
            if (args.Input.StartsWith('/'))
                InputHandler.ChatInput = args.Input;
            else
                InputHandler.ChatInput += args.Input;
        }

        var (info, reason, target) = (args.ChannelSwitchInfo, args.TellReason, args.TellTarget);

        if (info.Channel != null)
        {
            var targetChannel = info.Channel;
            if (info.Channel is InputChannel.Tell)
            {
                if (info.Rotate != RotateMode.None)
                {
                    var idx = Plugin.CurrentTab.CurrentChannel.TempChannel != InputChannel.Tell
                        ? 0 : info.Rotate == RotateMode.Reverse
                            ? -1 : 1;

                    var tellInfo = Plugin.Functions.Chat.GetTellHistoryInfo(idx);
                    if (tellInfo != null && reason != null)
                        Plugin.CurrentTab.CurrentChannel.TempTellTarget = new TellTarget(tellInfo.Name, (ushort) tellInfo.World, tellInfo.ContentId, reason.Value);
                }
                else
                {
                    Plugin.CurrentTab.CurrentChannel.TellTarget = null;
                    if (target != null)
                    {
                        if (info.Permanent)
                        {
                            Plugin.CurrentTab.CurrentChannel.TellTarget = target;
                        }
                        else
                        {
                            Plugin.CurrentTab.CurrentChannel.UseTempChannel = true;
                            Plugin.CurrentTab.CurrentChannel.TempTellTarget = target;
                        }
                    }
                }
            }
            else
            {
                Plugin.CurrentTab.CurrentChannel.TellTarget = null;
            }

            if (info.Channel is InputChannel.Linkshell1 or InputChannel.CrossLinkshell1 && info.Rotate != RotateMode.None)
            {
                var module = UIModule.Instance();

                // If any of these operations fail, do nothing.
                if (info.Permanent)
                {
                    // Rotate using the game's code.
                    if (info.Channel == InputChannel.Linkshell1)
                    {
                        Chat.RotateLinkshellHistory(info.Rotate);
                        targetChannel = info.Channel + (uint)module->LinkshellCycle;
                    }
                    else
                    {
                        Chat.RotateCrossLinkshellHistory(info.Rotate);
                        targetChannel = info.Channel + (uint)module->CrossWorldLinkshellCycle;
                    }
                }
                else
                {
                    targetChannel = Chat.ResolveTempInputChannel(Plugin.CurrentTab.CurrentChannel.TempChannel, info.Channel.Value, info.Rotate);
                }
            }

            if (targetChannel == null || !Chat.IsChannelOrExistingLinkshell(targetChannel.Value))
            {
                Plugin.Log.Warning($"Channel was set to an invalid value '{targetChannel}', ignoring");
                return;
            }

            if (info.Permanent)
            {
                Plugin.Functions.Chat.SetChannelWithExtraChat(targetChannel);
            }
            else
            {
                Plugin.CurrentTab.CurrentChannel.UseTempChannel = true;
                Plugin.CurrentTab.CurrentChannel.TempChannel = targetChannel.Value;
            }
        }

        if (info.Text != null && InputHandler.ChatInput.Length == 0)
            InputHandler.ChatInput = info.Text;
    }

    public float GetRemainingHeightForMessageLog(float extraBottomPadding = 0f)
    {
        // 输入区实际高度 = 频道名行（SmallFont，随输入区缩放）+ 输入框行（InputFont，不随缩放）：
        // 之前用 InputFont 的 FontSize×2 估算——100% 时高估（消息区多压→底部空白）、
        // 200% 时低估（频道名行×2 但估算没跟上→tab 被底部截断）
        float inputAreaHeight = 0f;
        using (Plugin.FontManager.SmallFont.Push())
            inputAreaHeight += ImGui.GetTextLineHeight();   // 频道名行
        using (Plugin.FontManager.InputFont.Push())
            inputAreaHeight += ImGui.GetFontSize();         // 输入框行（padding Y=0 → 高=FontSize）

        // 频道名行与输入行 ItemSpacing=0；tab 在输入行底部上移 2px（DrawChatLog 里
        // SetCursorPosY(tabCursor.Y-2)）→ 消息区多占 2px（用户实测：+10 会让滚动区留白变大，
        // 消息文本离输入框更远——方向相反，保持 +2f）
        var height = ImGui.GetContentRegionAvail().Y - inputAreaHeight + 2f - extraBottomPadding;
        // ⚠️ 已移除 title bar 空间补偿（cminY<0 时 height -= contentMinY）：
        // 它随 cminY 负值每帧放大消息区（+31px/次）→ ContentSize 涨 → ScrollMax 涨，
        // 与唤出帧滚动恢复叠加形成逐次累积（实验 2：scrollMaxY 39→70→101→132）。
        // 滚动恢复已由 PreOpenCheck 的 SetNextWindowScroll(0) 根治，此补偿不再需要。
        return height;
    }

    public void ChangeTab(int index) {
        Plugin.WantedTab = index;
        InputHandler.LastActivityTime = InputHandler.FrameTime;
    }

    public void ChangeTabDelta(int offset)
    {
        var newIndex = (Plugin.LastTab + offset) % Plugin.Config.Tabs.Count;
        while (newIndex < 0)
            newIndex += Plugin.Config.Tabs.Count;
        ChangeTab(newIndex);
    }

    private void TabSwitched(Tab newTab, Tab previousTab)
    {
        // 跨 tab 未读同步：新 tab 显示的消息 = 已读。其他 tab 有"同实例且尚未 Seen"的消息
        // （即到达时该 tab 计过未读的）→ 同步减计数。场景：用户在 C 时 A/B 都收到同频道
        // 消息都闪，切到 A 看后 B 的未读也一并清除
        SyncSeenAcrossTabs(newTab);

        // Use the fixed channel if set by the user, or set it to the current tabs channel if this tab wasn't accessed before
        if (newTab.Channel is not null)
            newTab.CurrentChannel.Channel = newTab.Channel.Value;
        else if (newTab.CurrentChannel.Channel is InputChannel.Invalid)
            newTab.CurrentChannel = previousTab.CurrentChannel;

        // Save cursor position before switching so we can restore it after the
        // channel change. If the input was not focused, suppress auto-activation
        // to avoid unexpectedly opening the input field.
        // Use UTF-8 byte count to get the correct cursor position for multi-byte
        // characters (e.g. CJK). C# string.Length is in characters, but ImGui's
        // CursorPos is in UTF-8 bytes — they differ for non-ASCII text.
        InputHandler.ActivatePos = InputHandler.InputFocused ? Encoding.UTF8.GetByteCount(InputHandler.ChatInput) : -1;
        SuppressNextActivate = !InputHandler.InputFocused;
        Plugin.Functions.Chat.SetChannelWithExtraChat(newTab.CurrentChannel.Channel);
    }

    // 切换 tab 时同步未读：新 tab 的可见消息 → 其他 tab 同实例的未读一并清除。
    // 只处理 message.Seen=false 的（到达时被计数过的）；Seen=true 的本来就没计数，不减
    private void SyncSeenAcrossTabs(Tab activeTab)
    {
        List<Message> msgs;
        using (var locked = activeTab.Messages.GetReadOnly(3))
            msgs = locked.ToList();
        if (msgs.Count == 0)
            return;

        foreach (var message in msgs)
        {
            if (message.Seen)
                continue;
            foreach (var tab in Plugin.Config.Tabs)
            {
                if (tab == activeTab || tab.Unread <= 0)
                    continue;
                using var locked = tab.Messages.GetReadOnly(3);
                if (locked.Contains(message))
                    tab.Unread--;
            }
        }

        foreach (var message in msgs)
            message.Seen = true;
    }

    public void BeginFrame()
    {
        DrewThisFrame = false;
    }

    public void FinalizeFrame()
    {
        if (!DrewThisFrame)
            InputHandler.InputFocused = false;
    }

    public override unsafe void PreOpenCheck()
    {
        // ⚠️【根因修复】主窗口从不需要滚动（布局自适应填满，滚动全在消息区 child 内）：
        // 隐藏聊天框→重新显示时 ImGui 会恢复窗口滚动位置（隐藏前 scroll 被推至 ScrollMax=39，
        // 内容区整体上移 → 仿原生透明下露出"顶部空白"）。SetNextWindowScroll(0) 在 Begin 前
        // 每帧锁定 scroll=0，唤出帧当帧即恢复正常（实验 2 日志证实 scrollY=39→70→101 逐次累积）。
        unsafe
        {
            if (ResetScrollFn != null)
                ResetScrollFn(Vector2.Zero);
            else
                ImGui.SetScrollY(0f); // 降级：仅在下帧生效（唤出帧当帧可能闪一行）
        }

        if (_wasHidden)
            _wasHidden = false;

        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing;

        Flags |= ImGuiWindowFlags.NoResize;

        if (!Plugin.Config.ShowTitleBar)
            Flags |= ImGuiWindowFlags.NoTitleBar;

        // ⚠️ [CtxClickPass] 菜单打开期间：聊天框窗口不捕获鼠标（NoMouseInputs）→
        // io.WantCaptureMouse 不因聊天框为 true → 游戏原生 UI 正常收到鼠标 →
        // 原生菜单在聊天框内也能点击（2026-08-15 点击穿透方案，点击无解则挖洞无意义）。
        // 代价：菜单打开期间聊天框不可滚动/选字/拖拽（模态菜单，可接受）。
        if (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession)
            Flags |= ImGuiWindowFlags.NoMouseInputs;

        // Hit-test using LastWindowPos/Size (set from inside Begin/End in a
        // previous frame — the only reliable coords we have before Begin runs).
        // 偏移量必须与 DrawTopRightResizeHandle 的绘制位置一致（默认 3px / 仿原生 X8 Y-2）
        var st = ImGui.GetStyle();
        const float hSize = 16f;
        var insetX = Plugin.Config.NativeBackground ? 8f : 3f;
        var insetY = Plugin.Config.NativeBackground ? 4f : 3f;
        var handleMin = new Vector2(
            LastWindowPos.X + LastWindowSize.X - hSize - st.WindowPadding.X - insetX,
            LastWindowPos.Y + st.WindowPadding.Y + insetY);
        var handleMax = handleMin + new Vector2(hSize, hSize);
        var mp = ImGui.GetIO().MousePos;
        MouseOverResizeHandle = mp.X >= handleMin.X && mp.X <= handleMax.X
                              && mp.Y >= handleMin.Y && mp.Y <= handleMax.Y;

        // Block native window drag when: locked (选字防误拖), actively resizing,
        // or cursor is over the handle (so clicking handle never starts a drag).
        if (MoveLocked || IsResizingTopRight || MouseOverResizeHandle)
            Flags |= ImGuiWindowFlags.NoMove;

        if (LastViewport == ImGuiHelpers.MainViewport.Handle && !WasDocked)
            // 仿原生着色：窗口整体透明，背景只保留在消息区/输入框/标签页
            BgAlpha = Plugin.Config.NativeBackground ? 0f : Plugin.Config.BackgroundAlpha / 100f;

        LastViewport = ImGui.GetWindowViewport().Handle;
        WasDocked = ImGui.IsWindowDocked();
    }

    public override bool DrawConditions()
    {
        InputHandler.FrameTime = Environment.TickCount64;
        if (IsHidden)
        {
            // 记录隐藏状态，供恢复显示时重置 ImGui 窗口数据（见 PreOpenCheck）
            _wasHidden = true;
            return false;
        }

        if (!Plugin.Config.HideWhenInactive || (!Plugin.Config.InactivityHideActiveDuringBattle && Plugin.InBattle) ||  InputHandler.Activate)
        {
            InputHandler.LastActivityTime =  InputHandler.FrameTime;
            return true;
        }

        var currentTab = Plugin.CurrentTab; // local to avoid calling the getter repeatedly
        var lastActivityTime = Plugin.Config.Tabs
            .Where(tab => !tab.PopOut && (tab.UnhideOnActivity || tab == currentTab))
            .Select(tab => tab.LastActivity)
            .Append( InputHandler.LastActivityTime)
            .Max();
        return  InputHandler.FrameTime - lastActivityTime <= 1000 * Plugin.Config.InactivityHideTimeout;
    }

    public override void PreDraw()
    {
        if (Plugin.Config.KeepInputFocus &&  InputHandler.Activate)
            ImGui.SetWindowFocus(WindowName);

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Push();
    }

    public override void PostDraw()
    {
        // Set Activate to false after draw to avoid repeatedly trying to focus
        // the text input in a tab with input disabled. The usual way that
        // Activate gets disabled is via the text input callback, but that
        // doesn't get called if the input is disabled.
        if (Plugin.CurrentTab.InputDisabled)
            InputHandler.Activate = false;

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Pop();
    }

    public override void OnClose()
    {
        // We force the main log to be always open
        IsOpen = true;
    }

    public override void Draw()
    {
        DrewThisFrame = true;
        // 聊天内容：默认 Axis 游戏字体（原生观感）；用户选了自定义字体后改用 RegularFont
        using var mainFont = (Plugin.Config.FontsEnabled ? Plugin.FontManager.RegularFont : Plugin.FontManager.Axis).Push();
        try
        {
            DrawChatLog();

            if (Plugin.Config.CanResize)
            {
                DrawTopRightResizeHandle();

                // Handle resize interaction INSIDE Begin/End — SetWindowPos/Size
                // on the current window CANNOT be overridden by Dalamud's PreOpenCheck.
                var mp = ImGui.GetIO().MousePos;
                var leftDown = ImGui.GetIO().MouseDown[(int) ImGuiMouseButton.Left];

                if (!IsResizingTopRight && MouseOverResizeHandle && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    IsResizingTopRight = true;
                    ResizeStartMousePos = mp;
                    ResizeStartWindowPos = ImGui.GetWindowPos();
                    ResizeStartWindowSize = ImGui.GetWindowSize();
                }

                if (IsResizingTopRight)
                {
                    var delta = mp - ResizeStartMousePos;

                    // Top-right handle: keep BOTTOM-LEFT corner fixed.
                    // Drag up-left → grow. Drag down-right → shrink.
                    var newPos = new Vector2(
                        ResizeStartWindowPos.X,
                        ResizeStartWindowPos.Y + delta.Y);
                    var newSize = new Vector2(
                        Math.Max(80f, ResizeStartWindowSize.X + delta.X),
                        Math.Max(80f, ResizeStartWindowSize.Y - delta.Y));

                    ImGui.SetWindowPos(newPos);
                    ImGui.SetWindowSize(newSize);

                    if (!leftDown)
                        IsResizingTopRight = false;
                }
            }

            AddPopOutsToDraw();
            InputHandler.AutoCompleteHandler.DrawAutoComplete();

            LastWindowPos = ImGui.GetWindowPos();
            LastWindowSize = ImGui.GetWindowSize();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing Chat Log window");
            // Prevent recurring draw failures from constantly trying to grab
            // input focus, which breaks every other ImGui window.
            InputHandler.Activate = false;
        }
    }

    private void DrawTopRightResizeHandle()
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var style = ImGui.GetStyle();
        const float hSize = 16f;

        // 缩放手柄位置：默认界面贴窗口背景右上角内侧 3px（恰到好处）；
        // 仿原生：X 内缩 8px 落在消息区背景内；Y 用户实测"再往上 2px 就对了"
        // （Y inset=-2 → 手柄顶 = WindowPadding.Y - 2）
        var insetX = Plugin.Config.NativeBackground ? 8f : 3f;
        var insetY = Plugin.Config.NativeBackground ? 4f : 3f;
        var localPos = new Vector2(
            windowSize.X - hSize - style.WindowPadding.X - insetX,
            style.WindowPadding.Y + insetY);

        var mousePos = ImGui.GetIO().MousePos;
        var handleRectMin = windowPos + localPos;
        var handleRectMax = handleRectMin + new Vector2(hSize, hSize);
        var hovered = mousePos.X >= handleRectMin.X && mousePos.X <= handleRectMax.X
                      && mousePos.Y >= handleRectMin.Y && mousePos.Y <= handleRectMax.Y;

        if (hovered || IsResizingTopRight)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var drawList = ImGui.GetWindowDrawList();
        // 仿原生 FFXIV 缩放手柄：金字塔形——三条平行的 NW-SE 斜线，长度递增
        // （最上面短、中间稍长、底部最长），短线一端对准右上角，淡白色
        var lineColor = hovered || IsResizingTopRight
            ? ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.8f))
            : ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.4f));

        const float thickness = 2f;
        // 金字塔形缩放手柄：三条平行斜线，方向"从左上到右下"（反斜杠 \，斜率 +1），长度递增，
        // 短线右端贴右上角 apex，中/长线沿 (1,-1) 方向向左下排列
        var p = windowPos + localPos; // handle 左上角（屏幕坐标）
        // 几何约束：左端 A/B/C 的 Y 相同（y=2）、右端 1/2/3 的 X 相同（x=15），
        // 三线斜率恒为 1（45°，y右 = 2 + 15 - x左），间距 6，长度递增 7.1 → 15.6 → 24
        drawList.AddLine(p + new Vector2(10f, 2f), p + new Vector2(15f, 7f), lineColor, thickness);   // 短 A→1
        drawList.AddLine(p + new Vector2(4f, 2f), p + new Vector2(15f, 13f), lineColor, thickness);   // 中 B→2
        drawList.AddLine(p + new Vector2(-2f, 2f), p + new Vector2(15f, 19f), lineColor, thickness);  // 长 C→3
    }

    // 滚轮一次只滚动一行（原版行为）。_pendingWheel 由 DrawChatLog/Draw 开头记录（已清零 IO，
    // ImGui 不自动滚），这里手动滚 1 行；边界由 ImGui clamp 自然处理
    private void HandleWheelScrollLineByLine(MessageLogState state)
    {
        // 不检查 IsWindowHovered：鼠标在滚动条/输入区上（child 外）也要能滚消息区；
        // DrawChatLog 开头已确认鼠标在本窗口内（RootAndChildWindows）才记录 PendingWheel
        // ⚠️ 滚到顶标志：内容不满一屏（无滚动）或当前已在顶部 → AtTop=true。
        // 聊天记录窗口的"滚动到顶自动加载上一天"依赖它（外层 child 的 GetScrollY 恒 0 不可靠）。
        state.AtTop = ImGui.GetScrollMaxY() <= 0f || ImGui.GetScrollY() <= 0f;
        if (Math.Abs(state.PendingWheel) < 0.001f) return;
        // ⚠️ 外层容器 child（##chat2-bottom-log）刚 Begin 时内容未画、maxY=0——此时消费
        // SetScrollY 无效且会吞掉滚动，导致内层真实滚动区（##chat2-messages）拿不到。
        // maxY<=0 时不消费，留给内层。
        if (ImGui.GetScrollMaxY() <= 0f)
            return;
        ImGui.SetScrollY(ImGui.GetScrollY() - state.PendingWheel * ImGui.GetTextLineHeight());
        state.PendingWheel = 0f;
        state.AtTop = ImGui.GetScrollY() <= 0f;
    }

    private void DrawCustomLeftScrollbar(MessageLogState state)
    {
        var scrollMax = ImGui.GetScrollMaxY();
        if (scrollMax <= 0f)
            return;

        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var scrollBarWidth = 4f;
        var padding = 0f;

        var barMin = new Vector2(windowPos.X + padding, windowPos.Y + padding);
        var barMax = new Vector2(windowPos.X + padding + scrollBarWidth, windowPos.Y + windowSize.Y - padding);

        var totalHeight = barMax.Y - barMin.Y;
        var scrollY = ImGui.GetScrollY();
        var visibleRatio = totalHeight / (totalHeight + scrollMax);
        var thumbHeight = Math.Max(12f, totalHeight * visibleRatio);
        var thumbY = barMin.Y + (scrollY / scrollMax) * (totalHeight - thumbHeight);

        var mousePos = ImGui.GetIO().MousePos;
        var thumbMin = new Vector2(barMin.X, thumbY);
        var thumbMax = new Vector2(barMax.X, thumbY + thumbHeight);
        var thumbHovered = mousePos.X >= thumbMin.X && mousePos.X <= thumbMax.X
                        && mousePos.Y >= thumbMin.Y && mousePos.Y <= thumbMax.Y;
        var barHovered = mousePos.X >= barMin.X && mousePos.X <= barMax.X
                      && mousePos.Y >= barMin.Y && mousePos.Y <= barMax.Y;

        var drawList = ImGui.GetWindowDrawList();
        // 仿原生 FFXIV 滚动条：轨道是一条很细的淡线 + thumb 荧光白稍带黄
        var trackX = barMin.X + scrollBarWidth / 2f;
        drawList.AddLine(new Vector2(trackX, barMin.Y), new Vector2(trackX, barMax.Y),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.22f)), 1f);
        var thumbColor = (thumbHovered || ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ? ImGui.GetColorU32(new Vector4(1.00f, 1.00f, 0.96f, 1.00f))
            : ImGui.GetColorU32(new Vector4(1.00f, 0.99f, 0.90f, 0.85f));

        drawList.AddRectFilled(thumbMin, thumbMax, thumbColor, scrollBarWidth / 2f);

        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var dragging = ImGui.IsMouseDragging(ImGuiMouseButton.Left);

        if (thumbHovered && clicked)
        {
            state.DraggingScrollbar = true;
            state.ScrollbarDragStartY = mousePos.Y;
            state.ScrollbarDragStartScroll = scrollY;
        }

        if (state.DraggingScrollbar && dragging)
        {
            var deltaY = mousePos.Y - state.ScrollbarDragStartY;
            var scrollDelta = (deltaY / (totalHeight - thumbHeight)) * scrollMax;
            ImGui.SetScrollY(state.ScrollbarDragStartScroll + scrollDelta);
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            state.DraggingScrollbar = false;
    }

    private unsafe void DrawChatLog()
    {
        // 滚动条颜色由 DrawCustomLeftScrollbar 自定义绘制（不走 ImGui 滚动条颜色）

        // 滚轮接管：鼠标在本窗口区域时，记录滚轮值并清零 IO——ImGui 不会自动滚 3 行，
        // 由消息区 child 手动按 1 行滚（边界由 ImGui clamp 自然处理，不会"弹回"）
        MsgState.UserScrolled = false; // 每帧重置；用户滚轮滚动时置 true → 本帧禁止自动贴底
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                ImGui.GetIO().MouseWheel = 0f;
                MsgState.PendingWheel = wheel;
                MsgState.UserScrolled = true;
            }
        }

        // 滚动恢复诊断（v1.40.9 已撤）：
        // cminY/cmaxY 是 Scroll 的投影，必须配合 GetScrollY/GetScrollMaxY 一起看；
        // 隐藏唤出后 scrollY = 唤出前 scrollMaxY（ImGui 固有恢复行为，issue #5993）。
        // 根因修复见 PreOpenCheck 的 SetNextWindowScroll(0)。

        // Position change has applied, so we set it to null again
        Position = null;

        var currentSize = ImGui.GetWindowSize();
        var resized = LastWindowSize != currentSize;
        LastWindowSize = currentSize;
        LastWindowPos = ImGui.GetWindowPos();

        if (resized)
            LastResize.Restart();

        LastViewport = ImGui.GetWindowViewport().Handle;
        WasDocked = ImGui.IsWindowDocked();

        var bottomTabs = Plugin.Config.TabPosition is TabPosition.Bottom;
        var sideTabs = Plugin.Config.TabPosition is TabPosition.Side;

        if (sideTabs)
        {
            DrawTabSidebar();
        }
        else if (bottomTabs)
        {
            DrawBottomTabLog();
        }
        else
        {
            DrawTabBar();
        }

        if (bottomTabs)
        {
            // 输入区缩放通过字体重建实现（FontManager 字号 × InputAreaScale），
            // 这里无需 SetWindowFontScale —— drawList 渲染的文字（tab 文字）随字体 atlas 自动缩放
            DrawChannelInputRow();
            // 输入行下移后，把 tab 拉回原位，保持贴窗口底部不被切
            var tabCursor = ImGui.GetCursorPos();
            ImGui.SetCursorPosY(tabCursor.Y - 2f);
            DrawBottomTabBar();
        }
        else
        {
            var activeTab = Plugin.CurrentTab;

            // This tab has a fixed channel, so we force this channel to be always set as current
            if (activeTab.Channel is not null)
                activeTab.CurrentChannel.SetChannel(activeTab.Channel.Value);

            DrawChannelInputRow();
        }
    }

    private void DrawChannelInputRow()
    {
        var activeTab = Plugin.CurrentTab;

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
            DrawChannelName(activeTab);

        // 气泡/输入框/右侧图标 这一行的中心 = 输入框行中心。图标上下居中于输入框行
        //（此前底部对齐，用户实测偏上，改为居中）
        var rowTop = ImGui.GetCursorPosY();
        // 输入框高度：单行 InputText 实际渲染高度 = FontSize + FramePadding.Y×2（GetFrameHeight），
        // 但渲染处已把 FramePadding.Y Push 成 0 → rect = FontSize。这里用 FontSize 与之一致
        float inputBoxHeight;
        using (Plugin.FontManager.InputFont.Push())
        {
            inputBoxHeight = ImGui.GetFontSize();
        }
        float iconButtonHeight;
        using (Plugin.FontManager.FontAwesomeSmall.Push())
            iconButtonHeight = ImGui.GetFrameHeight();
        var iconTop = rowTop + (inputBoxHeight - iconButtonHeight) / 2f;

        // 频道切换"气泡"按钮：上下居中于输入框行；右移半个字宽（一个字母≈输入字号一半，
        // 首行缩进感，输入框相应缩水，整体长度不变）。⚠️ 必须先回到行首 X——
        // 否则会沿用频道名行末的 X，气泡和输入框都被频道名宽度挤到右边
        ImGui.SetCursorPosX(inputBoxHeight * 0.5f); // 一个"字母"≈半字
        using (Plugin.FontManager.FontAwesomeSmall.Push())
        {
            ImGui.SetCursorPosY(iconTop);
            if (ImGui.Button(FontAwesomeIcon.Comment.ToIconString() + "##channel-switcher") && activeTab.Channel is null)
                ImGui.OpenPopup(ChatChannelPicker);
        }
        if (activeTab.Channel is not null && ImGui.IsItemHovered())
            ImGuiUtil.Tooltip(Language.ChatLog_SwitcherDisabled);

        using (var popup = ImRaii.Popup(ChatChannelPicker))
        {
            if (popup)
            {
                foreach (var (name, channel) in GetValidChannels())
                    if (ImGui.Selectable(name))
                        Plugin.Functions.Chat.SetChannelWithExtraChat(channel);
            }
        }

        ImGui.SameLine();
        ImGui.SetCursorPosY(rowTop);

        var buttonWidth = ImGuiUtil.CalcIconButtonSize().X;
        var showNovice = Plugin.Config.ShowNoviceNetwork && GameFunctions.GameFunctions.IsMentor();
        // Cog + 锁定 + 搜索恒显示；隐藏/新人按钮按配置
        var buttonsRight = 2 + 1 + (showNovice ? 1 : 0) + (Plugin.Config.ShowHideButton ? 1 : 0);
        var inputWidth = ImGui.GetContentRegionAvail().X - buttonWidth * buttonsRight - ImGui.GetStyle().ItemSpacing.X * buttonsRight;
        InputHandler.DrawInputArea(activeTab, inputWidth, ref TellSpecial);

        ImGui.SameLine();
        ImGui.SetCursorPosY(iconTop);

        // 右侧图标与左侧气泡同尺寸，底部对齐输入框底边（不随输入字体变化）
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Cog, font: Plugin.FontManager.FontAwesomeSmall))
            Plugin.SettingsWindow.Toggle();
        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip("设置");

        if (Plugin.Config.ShowHideButton)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(iconTop);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.EyeSlash, font: Plugin.FontManager.FontAwesomeSmall))
                UserHide();
            if (ImGui.IsItemHovered())
                ImGuiUtil.Tooltip("隐藏消息栏");
        }

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            InputHandler.LastActivityTime = InputHandler.FrameTime;

        if (showNovice)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(iconTop);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Leaf, font: Plugin.FontManager.FontAwesomeSmall))
                GameFunctions.GameFunctions.ClickNoviceNetworkButton();
            if (ImGui.IsItemHovered())
                ImGuiUtil.Tooltip("加入新人频道");
        }

        // 快捷锁定（放新人频道右侧）：选字时锁定窗口移动
        ImGui.SameLine();
        ImGui.SetCursorPosY(iconTop);
        if (ImGuiUtil.IconButton(MoveLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock, font: Plugin.FontManager.FontAwesomeSmall))
        {
            MoveLocked = !MoveLocked;
            // 持久化锁定状态（记忆上次状态，重启不丢）
            Plugin.Config.MoveLocked = MoveLocked;
            Plugin.SaveConfig();
        }
        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip(MoveLocked ? "解锁窗口移动" : "锁定窗口移动");

        // 聊天记录搜索（工具栏放大镜，Ctrl+F 也可打开）
        ImGui.SameLine();
        ImGui.SetCursorPosY(iconTop);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Search, font: Plugin.FontManager.FontAwesomeSmall))
            Plugin.SearchWindow.Toggle();
        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip(Language.Search_Title);
    }

    public Dictionary<string, InputChannel> GetValidChannels()
    {
        var channels = new Dictionary<string, InputChannel>();
        foreach (var channel in Enum.GetValues<InputChannel>())
        {
            if (!channel.IsValid())
                continue;

            var name = Sheets.LogFilterSheet.FirstOrNull(row => row.LogKind == (byte) channel.ToChatType())?.Name.ToString() ?? channel.ToChatType().Name();
            if (channel.IsLinkshell())
            {
                var lsName = Chat.GetLinkshellName(channel.LinkshellIndex());
                if (string.IsNullOrWhiteSpace(lsName))
                    continue;

                name += $": {lsName}";
            }

            if (channel.IsCrossLinkshell())
            {
                var lsName = Chat.GetCrossLinkshellName(channel.LinkshellIndex());
                if (string.IsNullOrWhiteSpace(lsName))
                    continue;

                name += $": {lsName}";
            }

            // Check if the linkshell with this index is registered in
            // the ExtraChat plugin by seeing if the command is
            // registered. The command gets registered only if a
            // linkshell is assigned (and even gets unassigned if the
            // index changes!).
            if (channel.IsExtraChatLinkshell() && !Plugin.CommandManager.Commands.ContainsKey(channel.Prefix()))
                continue;

            channels.Add(name, channel);
        }

        return channels;
    }

    private Chunk[] ReadChannelName(Tab activeTab)
    {
        Chunk[] channelNameChunks;
        // Check the temp channel before others
        if (activeTab.CurrentChannel.UseTempChannel)
        {
            if (activeTab.CurrentChannel.TempTellTarget != null && activeTab.CurrentChannel.TempTellTarget.IsSet())
            {
                channelNameChunks = GenerateTellTargetName(activeTab.CurrentChannel.TempTellTarget);
            }
            else
            {
                string name;
                if (activeTab.CurrentChannel.TempChannel.IsLinkshell())
                {
                    var idx = (uint) activeTab.CurrentChannel.TempChannel - (uint) InputChannel.Linkshell1;
                    var lsName = Chat.GetLinkshellName(idx);
                    name = $"LS #{idx + 1}: {lsName}";
                }
                else if (activeTab.CurrentChannel.TempChannel.IsCrossLinkshell())
                {
                    var idx = (uint) activeTab.CurrentChannel.TempChannel - (uint) InputChannel.CrossLinkshell1;
                    var cwlsName = Chat.GetCrossLinkshellName(idx);
                    name = $"CWLS [{idx + 1}]: {cwlsName}";
                }
                else
                {
                    name = activeTab.CurrentChannel.TempChannel.ToChatType().Name();
                }

                channelNameChunks = [new TextChunk(ChunkSource.None, null, name)];
            }
        }
        else if (activeTab.CurrentChannel.TellTarget?.IsSet() == true)
        {
            channelNameChunks = GenerateTellTargetName(activeTab.CurrentChannel.TellTarget);
        }
        else if (activeTab is { Channel: { } channel })
        {
            if (channel == InputChannel.Tell && activeTab.TellTarget.IsSet())
            {
                channelNameChunks = GenerateTellTargetName(activeTab.TellTarget);
            }
            else
            {
                // We cannot lookup ExtraChat channel names from index over
                // IPC so we just don't show the name if it's the tabs channel.
                //
                // We don't call channel.ToChatType().Name() as it has the
                // long name as used in the settings window.
                channelNameChunks = [new TextChunk(ChunkSource.None, null, channel.IsExtraChatLinkshell() ? $"ECLS [{channel.LinkshellIndex() + 1}]" : channel.ToChatType().Name())];
            }
        }
        else if (Plugin.ExtraChat.ChannelOverride is var (overrideName, _))
        {
            // If the current channel is not an ExtraChat Linkshell add a warning for the user
            var warning = activeTab.CurrentChannel.Channel.IsExtraChatLinkshell()
                ? ""
                : $" (Warning: {activeTab.CurrentChannel.Channel.ToChatType().Name()})";

            channelNameChunks = [new TextChunk(ChunkSource.None, null, $"{overrideName}{warning}")];
        }
        else if (PlayerUtil.ScreenshotMode && activeTab.CurrentChannel.Channel is InputChannel.Tell && activeTab.CurrentChannel.TellTarget != null)
        {
            if (!string.IsNullOrWhiteSpace(activeTab.CurrentChannel.TellTarget.Name) && activeTab.CurrentChannel.TellTarget.World != 0)
            {
                // Note: don't use HidePlayerInString here because abbreviation settings do not affect this.
                var playerName = PlayerUtil.HashPlayer(activeTab.CurrentChannel.TellTarget.Name, activeTab.CurrentChannel.TellTarget.World);
                var world = Sheets.WorldSheet.TryGetRow(activeTab.CurrentChannel.TellTarget.World, out var worldRow)
                    ? worldRow.Name.ToString()
                    : "???";

                channelNameChunks =
                [
                    new TextChunk(ChunkSource.None, null, "Tell "),
                    new TextChunk(ChunkSource.None, null, playerName),
                    new IconChunk(ChunkSource.None, null, BitmapFontIcon.CrossWorld),
                    new TextChunk(ChunkSource.None, null, world),
                ];
            }
            else
            {
                // We still need to censor the name if we couldn't read valid data.
                channelNameChunks = [new TextChunk(ChunkSource.None, null, "Tell")];
            }
        }
        else
        {
            channelNameChunks = activeTab.CurrentChannel.Name.Count > 0
                ? activeTab.CurrentChannel.Name.ToArray()
                : [new TextChunk(ChunkSource.None, null, activeTab.CurrentChannel.Channel.ToChatType().Name())];
        }

        return channelNameChunks;
    }

    private Chunk[] GenerateTellTargetName(TellTarget tellTarget)
    {
        var playerName = tellTarget.Name;
        if (PlayerUtil.ScreenshotMode)
            // Note: don't use HidePlayerInString here because
            // abbreviation settings do not affect this.
            playerName = PlayerUtil.HashPlayer(tellTarget.Name, tellTarget.World);

        var world = Sheets.WorldSheet.TryGetRow(tellTarget.World, out var worldRow)
            ? worldRow.Name.ToString()
            : "???";

        return
        [
            new TextChunk(ChunkSource.None, null, "Tell "),
            new TextChunk(ChunkSource.None, null, playerName),
            new IconChunk(ChunkSource.None, null, BitmapFontIcon.CrossWorld),
            new TextChunk(ChunkSource.None, null, world)
        ];
    }

    public void UserHide()
    {
        CurrentHideState = HideState.User;
    }

    private bool _wasHidden;

    // 消息区背景色：RGB 取当前 style 的 WindowBg（与默认界面窗口背景同源，保持
    // 相同透明度下观感一致；用户导入的自定义样式也自动跟随）。
    // ⚠️ alpha 必须乘 WindowBg 的 alpha 分量：ImGui 渲染窗口背景时最终 alpha =
    // BgAlpha × WindowBg.alpha（相乘）——只取 WindowAlpha/100 会丢掉样式 alpha，
    // 在 WindowBg.alpha<1 时消息区比默认界面更不透明（更深），用户实测过
    private Vector4 MessageLogBgColor()
    {
        var winBg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        return new Vector4(winBg.X, winBg.Y, winBg.Z, winBg.W * (Plugin.Config.WindowAlpha / 100f));
    }

    public void DrawMessageLog(Tab tab, PayloadHandler handler, float childHeight, bool switchedTab, MessageLogState state, Guid? scrollToMessageId = null, Action<Message>? onMessageClick = null)
    {
        // 字体 atlas 异步构建（插件加载后首个 Draw 帧可能尚未就绪）：主字体未就绪时
        // IFontHandle.Push() 是 no-op，消息会用默认字体渲染并写入错误的高度缓存
        // （message.Height），导致之后布局错乱/首行截断。等字体就绪再渲染。
        var mainFontHandle = Plugin.Config.FontsEnabled ? Plugin.FontManager.RegularFont : Plugin.FontManager.Axis;
        if (!mainFontHandle.Available && mainFontHandle.LoadException == null)
            return;

        // 仿原生着色：消息区背景颜色取 WindowBg 的 RGB（与默认界面窗口背景一致，
        // 纯黑 (0,0,0) 在相同 alpha 下会明显更深——用户实测），alpha 跟随窗口透明度设置。
        // ⚠️ 嵌套模式（childHeight<=0，bottom tab 布局外层已显式画圆角背景）不画内层背景——
        // 否则内层矩形背景会盖住外层圆角弧线（用户实测：只有底边两角圆、顶边两角直角）
        using var msgBg = ImRaii.PushColor(ImGuiCol.ChildBg, MessageLogBgColor(), Plugin.Config.NativeBackground && childHeight > 0f);
        // 消息框圆角（放消息的区域，不是输入框；4px 太小用户看不到，加大 8px）
        using var msgRound = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);

        // ⚠️ 不用 NoScrollbar：ImGui 对 NoScrollbar 窗口的 SetScrollY 是 no-op（手动滚动失效）。
        // 改用 NoScrollWithMouse（阻止自动滚）+ 隐藏 ImGui 滚动条（透明）——滚动完全由我们控制
        using var sbGrab = ImRaii.PushColor(ImGuiCol.ScrollbarGrab, 0u);
        using var sbGrabHovered = ImRaii.PushColor(ImGuiCol.ScrollbarGrabHovered, 0u);
        using var sbGrabActive = ImRaii.PushColor(ImGuiCol.ScrollbarGrabActive, 0u);
        using var sbBg = ImRaii.PushColor(ImGuiCol.ScrollbarBg, 0u);
        using var sbSize = ImRaii.PushStyle(ImGuiStyleVar.ScrollbarSize, 0f);
        using var child = ImRaii.Child("##chat2-messages", new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoScrollWithMouse | (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession ? ImGuiWindowFlags.NoMouseInputs : ImGuiWindowFlags.None));
        if (!child.Success)
            return;

        // 仿原生消息区背景（非嵌套模式：PopOut 独立窗口 / 顶部 tab 布局）：
        // 窗口透明后 child 背景在此显式绘制圆角——ChildRounding 在本版本可能不读，
        // 与主窗口 bottomTabs 布局（外层 child 显式画）同源方案；嵌套模式 childHeight<=0
        // 由外层 child 画，这里跳过避免矩形盖掉外层圆角
        if (Plugin.Config.NativeBackground && childHeight > 0f)
        {
            var dl = ImGui.GetWindowDrawList();
            var cMin = ImGui.GetWindowPos();
            var cMax = cMin + ImGui.GetWindowSize();
            dl.PushClipRect(cMin, cMax, false);
            dl.AddRectFilled(cMin, cMax, ImGui.GetColorU32(MessageLogBgColor()), 8f);
            dl.PopClipRect();
        }

        HandleWheelScrollLineByLine(state);

        // ⚠️ 选字状态按窗口隔离（state.Selection，见 MessageLogState 注释）：
        // 主窗口与各 PopOut 的 DrawMessageLog 各自持有独立 Selection，互不干扰。
        var selection = state.Selection;
        selection.Chunks.Clear(); // rebuild every frame (scroll changes positions)
        ImGuiUtil.CurrentSelection = selection;
        var scrollY = ImGui.GetScrollY();
        selection.CurrentScrollY = scrollY;

        // 左缩进留出左侧滚动条空间（用户要求贴近滚动条，从 14 减到 10）
        ImGui.Indent(10f);

        if (tab.DisplayTimestamp && Plugin.Config.PrettierTimestamps)
            DrawLogTableStyle(tab, handler, switchedTab, state, scrollToMessageId, onMessageClick);
        else
            DrawLogNormalStyle(tab, handler, switchedTab, state, scrollToMessageId, onMessageClick);

        ImGuiUtil.CurrentSelection = null;

        if (selection.IsDragging)
        {
            var sDoc = new Vector2(selection.DragStart.X, selection.DragStart.Y - scrollY);
            var eDoc = new Vector2(selection.DragEnd.X, selection.DragEnd.Y - scrollY);
            var (s, e) = (selection.PointToChar(sDoc), selection.PointToChar(eDoc));
        }

        // --- Text selection interaction ---
        var mp = ImGui.GetIO().MousePos;
        var leftDown = ImGui.GetIO().MouseDown[(int)ImGuiMouseButton.Left];
        var leftClicked = ImGui.GetIO().MouseClicked[(int)ImGuiMouseButton.Left];
        var leftReleased = ImGui.GetIO().MouseReleased[(int)ImGuiMouseButton.Left];

        var childPos = ImGui.GetWindowPos();
        var childSize = ImGui.GetWindowSize();
        var inChild = ImGui.IsMouseHoveringRect(childPos, childPos + childSize);

        // Left custom scrollbar occupies X = childPos.X + 0 .. + 4 (宽度 4，贴左边缘，从右侧变细)
        var onLeftScrollbar = mp.X >= childPos.X - 2f && mp.X <= childPos.X + 8f;

        // Ctrl+C copy - check early so it works even during drag
        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C))
        {
            var text = selection.GetSelectedText();
            if (!string.IsNullOrEmpty(text))
                ImGui.SetClipboardText(text);
        }

        // 左键点空白（HoveredPayload==null）→ 关闭原生菜单 + 开始文本选择（刻意保留，模拟原生）；
        // 左键点 payload（玩家/道具链接）→ 不关菜单、不选字，交给 LeftClickPayload 弹菜单（仿原生
        // 左键点玩家名同样弹菜单）。原版条件即 HoveredPayload == null，曾 TEMPORARILY 移除以便
        // payload 上选字，现恢复以支持左键弹菜单（v1.40.9+）。
        if (leftClicked && inChild && !onLeftScrollbar && !state.DraggingScrollbar && ImGuiUtil.HoveredPayload == null)
        {
            // 左键点击聊天框空白区域时关闭原生上下文菜单（模拟游戏原生聊天框行为）
            if (Plugin.ContextMenuActive)
                CloseNativeContextMenu();

            selection.IsDragging = true;
            selection.HasSelection = false;
            selection.DragStart = new Vector2(mp.X, mp.Y + scrollY);
            selection.DragEnd = new Vector2(mp.X, mp.Y + scrollY);
        }
        // Left click outside child AND outside scrollbar → clear selection (like Word)
        else if (leftClicked && !inChild && !onLeftScrollbar)
        {
            selection.DropSelection();
        }

        if (selection.IsDragging)
        {
            selection.DragEnd = new Vector2(mp.X, mp.Y + scrollY);
        }

        if (leftReleased && selection.IsDragging)
        {
            selection.IsDragging = false;
            // If start and end are at same point → treat as click, clear selection
            if (Vector2.DistanceSquared(selection.DragStart, selection.DragEnd) < 2f)
                selection.DropSelection();
            else
                selection.HasSelection = true;
        }

        // Draw highlight (whether dragging or persistent)
        selection.DrawHighlight();

        DrawCustomLeftScrollbar(state);
    }

    private void DrawLogNormalStyle(Tab tab, PayloadHandler handler, bool switchedTab, MessageLogState state, Guid? scrollToMessageId = null, Action<Message>? onMessageClick = null)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
            DrawMessages(tab, handler, false, scrollToMessageId: scrollToMessageId, onMessageClick: onMessageClick);

        // ⚠️ 用户手动滚轮时禁止自动贴底：SetScrollY 设的是 ScrollTarget，当帧 GetScrollY()
        // 还是旧值（底部）→ 贴底判断仍成立 → SetScrollHereY(1f) 把滚动拉回底部 → 向上滚失效
        if (switchedTab || (!state.UserScrolled && ImGui.GetScrollY() >= ImGui.GetScrollMaxY()))
            ImGui.SetScrollHereY(1f);

        handler.Draw();
    }

    private void DrawLogTableStyle(Tab tab, PayloadHandler handler, bool switchedTab, MessageLogState state, Guid? scrollToMessageId = null, Action<Message>? onMessageClick = null)
    {
        var compact = Plugin.Config.MoreCompactPretty;
        var oldItemSpacing = ImGui.GetStyle().ItemSpacing;
        var oldCellPadding = ImGui.GetStyle().CellPadding;

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, oldCellPadding with { Y = 0 }, compact))
        {
            using var table = ImRaii.Table("timestamp-table", 2, ImGuiTableFlags.PreciseWidths);
            if (!table.Success)
                return;

            ImGui.TableSetupColumn("timestamps", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("messages", ImGuiTableColumnFlags.WidthStretch);

            DrawMessages(tab, handler, true, compact, oldCellPadding.Y, scrollToMessageId, onMessageClick);

            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, oldItemSpacing))
            using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, oldCellPadding))
            {
                // Custom styles can have cellPadding that go above 4, which GetScrollY isn't respecting
                var cellPaddingOffset = !compact && oldCellPadding.Y > 4f ? oldCellPadding.Y - 4f : 0f;
                // ⚠️ 用户手动滚轮时禁止自动贴底（同上，否则向上滚动被拉回底部）
                if (switchedTab || (!state.UserScrolled && ImGui.GetScrollY() + cellPaddingOffset >= ImGui.GetScrollMaxY()))
                    ImGui.SetScrollHereY(1f);

                handler.Draw();
            }
        }
    }

    private void DrawMessages(Tab tab, PayloadHandler handler, bool isTable, bool moreCompact = false, float oldCellPaddingY = 0, Guid? scrollToMessageId = null, Action<Message>? onMessageClick = null)
    {
        try
        {
            // This may produce ApplicationException which is catched below.
            using var messages = tab.Messages.GetReadOnly(3);

            var reset = false;
            if (LastResize is { IsRunning: true, Elapsed.TotalSeconds: > 0.25 })
            {
                LastResize.Stop();
                LastResize.Reset();
                reset = true;
            }

            var lastPosY = ImGui.GetCursorPosY();
            var lastTimestamp = string.Empty;

            var maxLines = Plugin.Config.MaxLinesToRender;
            var startLine = messages.Count > maxLines ? messages.Count - maxLines : 0;
            for (var i = startLine; i < messages.Count; i++)
            {
                var message = messages[i];

                // 滚动定位到指定消息（聊天记录窗口用）：目标消息滚动到可视区中央，仅定位一次
                if (scrollToMessageId is { } targetId && message.Id == targetId)
                {
                    ImGui.SetScrollHereY(0.5f);
                    scrollToMessageId = null;
                }

                if (reset)
                {
                    message.Height[tab.Identifier] = null;
                    message.IsVisible[tab.Identifier] = false;
                }

                // go to next row
                if (isTable)
                    ImGui.TableNextColumn();

                // Set the height of the previous message. `lastPosY` is set to
                // the top of the previous message, and the current cursor is at
                // the top of the current message.
                if (i > 0)
                {
                    var prevMessage = messages[i - 1];
                    prevMessage.Height.TryGetValue(tab.Identifier, out var prevHeight);
                    if (prevHeight == null || (prevMessage.IsVisible.TryGetValue(tab.Identifier, out var prevVisible) && prevVisible))
                    {
                        var newHeight = ImGui.GetCursorPosY() - lastPosY;

                        // Remove the padding from the bottom of the previous row and the top of the current row.
                        if (isTable && !moreCompact)
                            newHeight -= oldCellPaddingY * 2;

                        if (newHeight != 0)
                            prevMessage.Height[tab.Identifier] = newHeight;
                    }
                }
                lastPosY = ImGui.GetCursorPosY();

                // message has rendered once
                // message isn't visible, so render dummy
                message.Height.TryGetValue(tab.Identifier, out var height);
                message.IsVisible.TryGetValue(tab.Identifier, out var visible);
                if (height != null && !visible)
                {
                    var beforeDummy = ImGui.GetCursorPos();

                    // skip to the message column for vis test
                    if (isTable)
                        ImGui.TableNextColumn();

                    ImGui.Dummy(new Vector2(10f, height.Value));

                    var nowVisible = ImGui.IsItemVisible();
                    if (!nowVisible)
                        continue;

                    if (isTable)
                        ImGui.TableSetColumnIndex(0);

                    ImGui.SetCursorPos(beforeDummy);
                    message.IsVisible[tab.Identifier] = nowVisible;
                }

                if (tab.DisplayTimestamp && Plugin.Config.ShowTimestamp)
                {
                    var localTime = message.Date.ToLocalTime();
                    // 24 小时制去掉小时前导零（原生样式 [2:30] 而非 [02:30]）
                    var timestamp = Plugin.Config.Use24HourClock
                        ? $"{localTime.Hour}:{localTime.Minute:00}"
                        : localTime.ToString("t", null);
                    if (isTable)
                    {
                        if (!Plugin.Config.HideSameTimestamps || timestamp != lastTimestamp)
                        {
                            lastTimestamp = timestamp;
                            // 时间戳用主字体渲染（与聊天框文字大小一致，用户要求）
                            ImGui.TextUnformatted(timestamp);

                            // We use an IsItemHovered() check here instead of
                            // just calling Tooltip() to avoid computing the
                            // tooltip string for all visible items on every
                            // frame.
                            if (ImGui.IsItemHovered())
                                ImGuiUtil.Tooltip(localTime.ToString("F"));
                        }
                        else
                        {
                            // Avoids rendering issues caused by emojis in
                            // message content.
                            ImGui.TextUnformatted("");
                        }
                    }
                    else
                    {
                        // 时间戳用主字体渲染（与聊天框文字大小一致，用户要求）
                        InputHandler.ChunkHandler.DrawChunk(new TextChunk(ChunkSource.None, null, $"[{timestamp}] ") { Foreground = 0xFFFFFFFF, Color = ColourUtil.RgbaToVector4(0xFFFFFFFF)});
                        ImGui.SameLine();
                    }
                }

                if (isTable)
                    ImGui.TableNextColumn();

                var lineWidth = ImGui.GetContentRegionAvail().X;
                if (message.Sender.Count > 0)
                {
                    InputHandler.ChunkHandler.DrawChunks(message.Sender, true, handler, lineWidth);
                    ImGui.SameLine();
                }

                // We need to draw something otherwise the item visibility check below won't work.
                if (message.Content.Count == 0)
                    InputHandler.ChunkHandler.DrawChunks([new TextChunk(ChunkSource.Content, null, " ")], true, handler, lineWidth);
                else
                    InputHandler.ChunkHandler.DrawChunks(message.Content, true, handler, lineWidth);

                message.IsVisible[tab.Identifier] = ImGui.IsItemVisible();

                // 消息点击回调（聊天记录窗口用：点击消息定位上下文）
                if (onMessageClick != null && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    onMessageClick(message);
            }
        }
        catch (ApplicationException)
        {
            // We couldn't get a reader lock on messages within 3ms, so
            // don't draw anything (and don't log a warning either).
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Error drawing chat log");
        }
    }

    private void DrawBottomTabLog()
    {
        // Handle WantedTab before drawing messages so switchedTab is correct
        var activeTab = Plugin.CurrentTab;
        var switchedTab = _renderedTabIndex >= 0 && _renderedTabIndex != Plugin.LastTab;

        if (Plugin.WantedTab.HasValue)
        {
            var wanted = Plugin.WantedTab.Value;
            if (wanted < Plugin.Config.Tabs.Count)
            {
                switchedTab = Plugin.LastTab != wanted;
                var previousTab = Plugin.CurrentTab;
                Plugin.LastTab = wanted;
                var newTab = Plugin.Config.Tabs[wanted];
                newTab.Unread = 0;
                if (switchedTab)
                    TabSwitched(newTab, previousTab);
                Plugin.WantedTab = null;
                activeTab = Plugin.CurrentTab;
            }
        }

        if (activeTab.Channel is not null)
            activeTab.CurrentChannel.SetChannel(activeTab.Channel.Value);

        var style = ImGui.GetStyle();
        // 与 DrawBottomTabBar 的标签页高度保持一致（固定 TabFont + 0.3 系数），
        // 并压缩分隔线余量，避免消息栏底部与标签页之间出现黑边
        float tabBarHeight;
        {
            // ⚠️ 必须把 TabFont 限制在此小作用域：using var 的作用域是整个方法，
            // 会覆盖下面的 DrawMessageLog，导致消息文字被 12pt 字体渲染
            // （实测 msgFontSize=24px=12pt×4/3×1.5，改主字体消息完全不变）
            using var tabFont = Plugin.FontManager.TabFont.Push();
            tabBarHeight = (ImGui.GetTextLineHeight() + style.FramePadding.Y * 2) * 0.9f;
        }
        // separatorHeight（1+ItemSpacing*0.3）是历史"分隔线余量"——实际 DrawBottomTabBar 不画 separator
        // （tab 上移 2px 重叠已经是 separator），曾让 childHeight 偏大约 2px → 底部空白
        var extraBottomPadding = tabBarHeight;
        var childHeight = GetRemainingHeightForMessageLog(extraBottomPadding);

        // 消息框圆角（放消息的区域，不是输入框；4px 太小用户看不到，加大 8px）
        using var msgRound = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);

        // ⚠️ 不用 NoScrollbar：ImGui 对 NoScrollbar 窗口的 SetScrollY 是 no-op（手动滚动失效）。
        // 改用 NoScrollWithMouse（阻止自动滚）+ 隐藏 ImGui 滚动条（透明）——滚动完全由我们控制
        using var sbGrab = ImRaii.PushColor(ImGuiCol.ScrollbarGrab, 0u);
        using var sbGrabHovered = ImRaii.PushColor(ImGuiCol.ScrollbarGrabHovered, 0u);
        using var sbGrabActive = ImRaii.PushColor(ImGuiCol.ScrollbarGrabActive, 0u);
        using var sbBg = ImRaii.PushColor(ImGuiCol.ScrollbarBg, 0u);
        using var sbSize = ImRaii.PushStyle(ImGuiStyleVar.ScrollbarSize, 0f);
        using var child = ImRaii.Child("##chat2-bottom-log", new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoScrollWithMouse | (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession ? ImGuiWindowFlags.NoMouseInputs : ImGuiWindowFlags.None));
        if (!child.Success)
            return;

        // 仿原生着色：窗口透明后消息区背景在此显式绘制（不走 ImGui child 背景）——
        // 1) 颜色取 WindowBg 的 RGB + WindowAlpha（与默认界面窗口背景一致，纯黑会更深）；
        // 2) ImGui child 背景的 ChildRounding 圆角会被内层 child（##chat2-messages，
        //    8px padding 内）的矩形背景覆盖而不可见，显式 drawList 圆角矩形兜底
        if (Plugin.Config.NativeBackground)
        {
            var dl = ImGui.GetWindowDrawList();
            var cMin = ImGui.GetWindowPos();
            var cMax = cMin + ImGui.GetWindowSize();

            // ⚠️ child 默认 clip 会把顶部圆角弧线裁掉（用户实测：顶部直角、底部圆角）——
            // PushClipRect 完全替换 clip（第三个参数必须 false！true=取交集，等于没扩）到
            // 整个 child 矩形，四角圆角完整渲染（rounding 8 合适，20 过头）
            dl.PushClipRect(cMin, cMax, false);
            dl.AddRectFilled(cMin, cMax, ImGui.GetColorU32(MessageLogBgColor()), 8f);
            dl.PopClipRect();
        }

        HandleWheelScrollLineByLine(MsgState);

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            InputHandler.LastActivityTime = InputHandler.FrameTime;

        DrawMessageLog(activeTab, InputHandler.PayloadHandler, -1, switchedTab, MsgState);
        _renderedTabIndex = Plugin.LastTab;
    }

    private void DrawBottomTabBar()
    {
        var tabs = Plugin.Config.Tabs;
        var anyClicked = false;

        var style = ImGui.GetStyle();
        // 底部标签页文字用固定大小字体（TabFont，12pt），压缩短边高度
        using var tabFont = Plugin.FontManager.TabFont.Push();
        var tabHeight = (ImGui.GetTextLineHeight() + style.FramePadding.Y * 2) * 0.9f;
        var drawList = ImGui.GetWindowDrawList();
        var dividerColor = ImGui.GetColorU32(new Vector4(0.25f, 0.25f, 0.25f, 0.55f));
        // 标签页背景透明度独立（TabAlpha，四透明度之一）
        var tabAlpha = Plugin.Config.TabAlpha / 100f;
        var barBgColor = ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 0.5f * tabAlpha));
        var activeColor = ImGui.GetColorU32(new Vector4(0.28f, 0.28f, 0.28f, 0.6f * tabAlpha));

        // 仿原生着色时只给 tab 本身颜色（不画整条背景）；普通模式保持整条背景条
        var barStart = ImGui.GetCursorScreenPos();
        if (!Plugin.Config.NativeBackground)
        {
            var barWidth = ImGui.GetContentRegionAvail().X;
            drawList.AddRectFilled(barStart, new Vector2(barStart.X + barWidth, barStart.Y + tabHeight), barBgColor);
        }

        var unreadGreen = UnreadColor();

        var transparent = new Vector4(0, 0, 0, 0);
        using var btnBg = ImRaii.PushColor(ImGuiCol.Button, transparent);
        using var btnHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.35f, 0.35f, 0.35f, 0.3f));
        using var btnActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.25f, 0.4f));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);

        var first = true;
        for (var tabI = 0; tabI < tabs.Count; tabI++)
        {
            var tab = tabs[tabI];
            if (tab.PopOut)
                continue;

            var active = Plugin.LastTab == tabI;
            var hasUnread = !active && tab.UnreadMode != UnreadMode.None && tab.Unread > 0
                && Plugin.Config.UnreadNotifyMode != UnreadNotifyMode.None;

            if (!first)
            {
                // Thick vertical divider between tabs
                var divX = ImGui.GetCursorScreenPos().X;
                const float divWidth = 2f;
                drawList.AddRectFilled(
                    new Vector2(divX, barStart.Y + 2),
                    new Vector2(divX + divWidth, barStart.Y + tabHeight - 2),
                    dividerColor);
                ImGui.SameLine(0, 0);
            }
            first = false;

            var textWidth = ImGui.CalcTextSize(tab.Name).X;
            var size = new Vector2(textWidth + style.FramePadding.X * 2 + 20f, tabHeight);
            using var unreadCol = ImRaii.PushColor(ImGuiCol.Text, unreadGreen, hasUnread);

            // 按钮只负责命中检测（隐藏文字），文字手动绘制并垂直居中（同顶部标签页）
            var clicked = ImGui.Button($"##bottom-tab-{tabI}", size);

            var btnMin = ImGui.GetItemRectMin();
            var btnMax = ImGui.GetItemRectMax();
            // 仿原生着色：每个 tab 独立背景块（active 高亮 / 非 active 深色）
            // 普通模式：整条背景条已提供底色，只需 active 高亮
            if (active)
                drawList.AddRectFilled(btnMin, btnMax, activeColor);
            else if (Plugin.Config.NativeBackground)
                drawList.AddRectFilled(btnMin, btnMax, barBgColor);
            var activeFont = ImGui.GetFont();
            var tabTextSize = ImGui.CalcTextSize(tab.Name);
            // 垂直居中：CJK 字形视觉中心 ≈ baseline − FontSize × 0.38，再上提 5px（用户实测校准）
            // 用生效尺寸（随 UI 缩放）渲染 tab 文字：与 tab 框（GetTextLineHeight 随 UI 缩放）保持一致
            var effectiveFontSize = ImGui.GetFontSize();
            var fontScale = effectiveFontSize / activeFont.FontSize;
            var textPos = new Vector2(
                btnMin.X + (btnMax.X - btnMin.X - tabTextSize.X) / 2f,
                btnMin.Y + (btnMax.Y - btnMin.Y) / 2f - activeFont.Ascent * fontScale + effectiveFontSize * 0.38f - 2f * fontScale);
            // ⚠️ 必须显式指定字体：AddText(pos,col,text) 重载会用窗口开始时的字体；传 FontSize 不随 UI 缩放
            drawList.AddText(activeFont, effectiveFontSize, textPos, ImGui.GetColorU32(ImGuiCol.Text), tab.Name);

            DrawTabContextMenu(tab, tabI);

            ImGui.SameLine(0, 0);

            if (clicked || Plugin.WantedTab == tabI)
            {
                anyClicked = true;
                var previousTab = Plugin.CurrentTab;
                // ⚠️ hasTabSwitched 必须在本行前算：LastTab 已被赋值为 tabI 后再判断
                // `LastTab != tabI` 恒为 false → TabSwitched 永不执行 → 跨 tab 未读同步失效
                var hasTabSwitched = Plugin.WantedTab == tabI || Plugin.LastTab != tabI;
                Plugin.LastTab = tabI;
                tab.Unread = 0;
                if (hasTabSwitched)
                    TabSwitched(tab, previousTab);
            }
        }

        // 末尾"+"：用 IconButton（无边框图标按钮，与输入框右侧齿轮/新人频道一致），
        // 字号 FontAwesomeSmall（随输入区缩放字体重建）
        ImGui.SameLine(0, 0);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Plus, "new-tab-bottom", font: Plugin.FontManager.FontAwesomeSmall))
        {
            NewTabName = string.Empty;
            ImGui.OpenPopup("chat2-new-tab-name");
        }

        using (var namePopup = ImRaii.Popup("chat2-new-tab-name"))
        {
            if (namePopup)
            {
                ImGui.TextUnformatted("新标签页名称");
                ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
                ImGui.InputText("##new-tab-name-input", ref NewTabName, 64);
                if (ImGui.IsItemDeactivatedAfterEdit() && ImGui.IsKeyPressed(ImGuiKey.Enter))
                    ImGui.CloseCurrentPopup();

                ImGui.Spacing();

                var canCreate = !string.IsNullOrWhiteSpace(NewTabName);
                using var disabled = ImRaii.Disabled(!canCreate);
                if (ImGui.Button("创建") && canCreate)
                {
                    var newTab = TabsUtil.VanillaGeneral;
                    newTab.Name = NewTabName.Trim();
                    Plugin.Config.Tabs.Add(newTab);
                    Plugin.WantedTab = Plugin.Config.Tabs.Count - 1;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button("取消"))
                    ImGui.CloseCurrentPopup();
            }
        }

        ImGui.NewLine();

        if (anyClicked)
            Plugin.WantedTab = null;
    }

    private void DrawTabBar()
    {
        var style = ImGui.GetStyle();
        // 顶部标签页文字用固定大小字体（TabFont，12pt），压缩短边高度
        using var tabFont = Plugin.FontManager.TabFont.Push();
        var tabHeight = (ImGui.GetTextLineHeight() + style.FramePadding.Y * 2) * 0.9f;
        var drawList = ImGui.GetWindowDrawList();

        var unreadGreen = UnreadColor();
        var dividerColor = ImGui.GetColorU32(new Vector4(0.25f, 0.25f, 0.25f, 0.55f));
        var barBgColor = ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 0.5f));
        var activeColor = ImGui.GetColorU32(new Vector4(0.28f, 0.28f, 0.28f, 0.6f));

        // Draw background bar
        var barStart = ImGui.GetCursorScreenPos();
        var barWidth = ImGui.GetContentRegionAvail().X;
        drawList.AddRectFilled(barStart, new Vector2(barStart.X + barWidth, barStart.Y + tabHeight), barBgColor);

        var previousTab = Plugin.CurrentTab;
        var newTabIdx = -1;

        if (Plugin.WantedTab != null)
        {
            var w = Plugin.WantedTab.Value;
            if (w < Plugin.Config.Tabs.Count && !Plugin.Config.Tabs[w].PopOut)
                newTabIdx = w;
        }

        var reducedPadding = new Vector2(style.FramePadding.X + 4f, style.FramePadding.Y * 0.5f);
        using var framePad = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, reducedPadding);

        var transparent = new Vector4(0, 0, 0, 0);
        using var btnBg = ImRaii.PushColor(ImGuiCol.Button, transparent);
        using var btnHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.35f, 0.35f, 0.35f, 0.3f));
        using var btnActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.25f, 0.4f));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);

        var first = true;
        for (var tabI = 0; tabI < Plugin.Config.Tabs.Count; tabI++)
        {
            var tab = Plugin.Config.Tabs[tabI];
            if (tab.PopOut)
                continue;

            var hasUnread = tabI != Plugin.LastTab && tab.UnreadMode != UnreadMode.None && tab.Unread > 0
                && Plugin.Config.UnreadNotifyMode != UnreadNotifyMode.None;
            var isActive = Plugin.LastTab == tabI;

            if (!first)
            {
                // Thick vertical divider between tabs
                var divX = ImGui.GetCursorScreenPos().X;
                const float divWidth = 2f;
                drawList.AddRectFilled(
                    new Vector2(divX, barStart.Y + 3),
                    new Vector2(divX + divWidth, barStart.Y + tabHeight - 3),
                    dividerColor);
                ImGui.SameLine(0, 0);
            }
            first = false;

            var textWidth = ImGui.CalcTextSize(tab.Name).X;
            var buttonWidth = textWidth + style.FramePadding.X * 2 + 20f;

            using var unreadCol = ImRaii.PushColor(ImGuiCol.Text, unreadGreen, hasUnread);

            // Highlight active tab
            if (isActive)
            {
                var activePos = ImGui.GetCursorScreenPos();
                drawList.AddRectFilled(activePos, new Vector2(activePos.X + buttonWidth, activePos.Y + tabHeight), activeColor);
            }

            // 按钮只负责命中检测（隐藏文字），文字手动绘制并垂直居中：
            // 以字形行高（Ascent-Descent）为基准，避免 Noto CJK 行高不对称导致文字视觉偏下
            if (ImGui.Button($"##log-tab-{tabI}", new Vector2(buttonWidth, tabHeight)))
                newTabIdx = tabI;

            var btnMin = ImGui.GetItemRectMin();
            var btnMax = ImGui.GetItemRectMax();
            var activeFont = ImGui.GetFont();
            var tabTextSize = ImGui.CalcTextSize(tab.Name);
            // 垂直居中：CJK 字形视觉中心 ≈ baseline − FontSize × 0.38，再上提 5px（用户实测校准）
            // 用生效尺寸（随 UI 缩放）渲染 tab 文字：与 tab 框（GetTextLineHeight 随 UI 缩放）保持一致
            var effectiveFontSize = ImGui.GetFontSize();
            var fontScale = effectiveFontSize / activeFont.FontSize;
            var textPos = new Vector2(
                btnMin.X + (btnMax.X - btnMin.X - tabTextSize.X) / 2f,
                btnMin.Y + (btnMax.Y - btnMin.Y) / 2f - activeFont.Ascent * fontScale + effectiveFontSize * 0.38f - 2f * fontScale);
            // ⚠️ 必须显式指定字体：AddText(pos,col,text) 重载会用窗口开始时的字体；传 FontSize 不随 UI 缩放
            drawList.AddText(activeFont, effectiveFontSize, textPos, ImGui.GetColorU32(ImGuiCol.Text), tab.Name);

            DrawTabContextMenu(tab, tabI);

            ImGui.SameLine(0, 0);
        }

        ImGui.NewLine();

        // Bottom separator line
        var sepPos = ImGui.GetCursorScreenPos();
        drawList.AddLine(sepPos, new Vector2(sepPos.X + barWidth, sepPos.Y), dividerColor);

        // Handle tab switch
        var hasTabSwitched = false;
        if (newTabIdx >= 0)
        {
            hasTabSwitched = Plugin.LastTab != newTabIdx;
            Plugin.LastTab = newTabIdx;
            var newTab = Plugin.Config.Tabs[newTabIdx];
            newTab.Unread = 0;
            if (hasTabSwitched)
                TabSwitched(newTab, previousTab);
        }

        Plugin.WantedTab = null;

        // Draw active tab's message log
        if (Plugin.LastTab < Plugin.Config.Tabs.Count)
        {
            var activeTab = Plugin.Config.Tabs[Plugin.LastTab];
            activeTab.Unread = 0;
            DrawMessageLog(activeTab, InputHandler.PayloadHandler, GetRemainingHeightForMessageLog(), hasTabSwitched, MsgState);
        }
    }

    private void DrawTabSidebar()
    {
        var currentTab = -1;
        using var tabTable = ImRaii.Table("tabs-table", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable);
        if (!tabTable.Success)
            return;

        ImGui.TableSetupColumn("tabs", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("chat", ImGuiTableColumnFlags.WidthStretch, 4);

        ImGui.TableNextColumn();

        var hasTabSwitched = false;
        var childHeight = GetRemainingHeightForMessageLog();
        using (var child = ImRaii.Child("##chat2-tab-sidebar", new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoScrollbar | (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession ? ImGuiWindowFlags.NoMouseInputs : ImGuiWindowFlags.None)))
        {
            if (child)
            {
                var previousTab = Plugin.CurrentTab;
                var unreadGreen = UnreadColor();
                    for (var tabI = 0; tabI < Plugin.Config.Tabs.Count; tabI++)
                    {
                        var tab = Plugin.Config.Tabs[tabI];
                        if (tab.PopOut)
                            continue;

                        var hasUnread = tabI != Plugin.LastTab && tab.UnreadMode != UnreadMode.None && tab.Unread > 0
                && Plugin.Config.UnreadNotifyMode != UnreadNotifyMode.None;
                        using var unreadCol = ImRaii.PushColor(ImGuiCol.Text, unreadGreen, hasUnread);
                        var clicked = ImGui.Selectable($"{tab.Name}###log-tab-{tabI}", Plugin.LastTab == tabI || Plugin.WantedTab == tabI);
                    DrawTabContextMenu(tab, tabI);

                    if (!clicked && Plugin.WantedTab != tabI)
                        continue;

                    currentTab = tabI;
                    hasTabSwitched = Plugin.LastTab != tabI;
                    Plugin.LastTab = tabI;
                    if (hasTabSwitched)
                        TabSwitched(tab, previousTab);
                }
            }
        }

        ImGui.TableNextColumn();

        if (currentTab == -1 && Plugin.LastTab < Plugin.Config.Tabs.Count)
        {
            currentTab = Plugin.LastTab;
            Plugin.Config.Tabs[currentTab].Unread = 0;
        }

        if (currentTab > -1)
            DrawMessageLog(Plugin.Config.Tabs[currentTab], InputHandler.PayloadHandler, childHeight, hasTabSwitched, MsgState);

        Plugin.WantedTab = null;
    }

    /// <summary>
    /// 未读标签页文字颜色：按全局"未读消息提示方式"返回颜色。
    /// Breath=荧光绿呼吸灯；Highlight=荧光绿常亮；None=普通文字色（配合 hasUnread 条件不 push）。
    /// </summary>
    private static Vector4 UnreadColor()
    {
        switch (Plugin.Config.UnreadNotifyMode)
        {
            case UnreadNotifyMode.Highlight:
                return new Vector4(0.224f, 1f, 0.078f, 1f);
            case UnreadNotifyMode.None:
                return new Vector4(1f, 1f, 1f, 1f);
            default:
                return UnreadGreen();
        }
    }

    /// <summary>
    /// 未读标签页文字颜色：荧光绿 + 缓慢呼吸灯闪烁（亮度 0.5~1.0，周期约 1.3 秒）。
    /// 用户要求"荧光绿 + 呼吸灯"。
    /// </summary>
    private static Vector4 UnreadGreen()
    {
        // Environment.TickCount ms；2π / (TickCount 速率) ≈ 每 1.57s 一个周期 → 缓慢呼吸
        var pulse = 0.5f + 0.5f * MathF.Sin(Environment.TickCount * 0.004f);
        // 荧光绿 #39FF14，亮度随 pulse 呼吸
        return new Vector4(0.224f * pulse, 1.0f * pulse, 0.078f * pulse, 1.0f);
    }

    private void DrawTabContextMenu(Tab tab, int i)
    {
        using var contextMenu = ImRaii.ContextPopupItem($"tab-context-menu-{i}");
        if (!contextMenu.Success)
            return;

        var anyChanged = false;
        var tabs = Plugin.Config.Tabs;

        ImGui.SetNextItemWidth(300f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##tab-name", ref tab.Name, 128))
            anyChanged = true;

        // 图标用 FontAwesomeSmall（12px），与输入框文字大小协调（用户反馈原图标偏大）
        if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, font: Plugin.FontManager.FontAwesomeSmall, tooltip: Language.ChatLog_Tabs_Delete))
        {
            tabs.RemoveAt(i);
            Plugin.WantedTab = 0;

            anyChanged = true;
        }

        ImGui.SameLine();

        var (leftIcon, leftTooltip) = Plugin.Config.TabPosition is TabPosition.Side
            ? (FontAwesomeIcon.ArrowUp, Language.ChatLog_Tabs_MoveUp)
            : (FontAwesomeIcon.ArrowLeft, Language.ChatLog_Tabs_MoveLeft);
        if (ImGuiUtil.IconButton(leftIcon, font: Plugin.FontManager.FontAwesomeSmall, tooltip: leftTooltip) && i > 0)
        {
            (tabs[i - 1], tabs[i]) = (tabs[i], tabs[i - 1]);
            ImGui.CloseCurrentPopup();
            anyChanged = true;
        }

        ImGui.SameLine();

        var (rightIcon, rightTooltip) = Plugin.Config.TabPosition is TabPosition.Side
            ? (FontAwesomeIcon.ArrowDown, Language.ChatLog_Tabs_MoveDown)
            : (FontAwesomeIcon.ArrowRight, Language.ChatLog_Tabs_MoveRight);
        if (ImGuiUtil.IconButton(rightIcon, font: Plugin.FontManager.FontAwesomeSmall, tooltip: rightTooltip) && i < tabs.Count - 1)
        {
            (tabs[i + 1], tabs[i]) = (tabs[i], tabs[i + 1]);
            ImGui.CloseCurrentPopup();
            anyChanged = true;
        }

        ImGui.SameLine();
        // 弹出按钮补齐 FontAwesomeSmall（此前漏传 font 参数，默认字体偏大，与删除/左移/右移不一致）
        if (ImGuiUtil.IconButton(FontAwesomeIcon.WindowRestore, font: Plugin.FontManager.FontAwesomeSmall, tooltip: Language.ChatLog_Tabs_PopOut))
        {
            tab.PopOut = true;
            Plugin.SettingsWindow.SyncTabPopOut(tab.Identifier, true); // 与设置 Mutable 同步
            anyChanged = true;
        }

        if (anyChanged)
            Plugin.SaveConfig();
    }

    private void AddPopOutsToDraw()
    {
        if (PopOutDocked.Count != Plugin.Config.Tabs.Count)
        {
            PopOutDocked.Clear();
            PopOutDocked.AddRange(Enumerable.Repeat(false, Plugin.Config.Tabs.Count));
        }

        for (var i = 0; i < Plugin.Config.Tabs.Count; i++)
        {
            var tab = Plugin.Config.Tabs[i];
            if (!tab.PopOut)
                continue;

            if (PopOutWindows.Contains(tab.Identifier))
                continue;

            var window = new Popout(Plugin, tab, i);

            Plugin.WindowSystem.AddWindow(window);
            PopOutWindows.Add(tab.Identifier);
        }
    }
}

public class TextSelectionState
{
    public struct ChunkRect
    {
        public Vector2 Min;
        public Vector2 Max;
        public string Text;
        public float[] CharX; // length = Text.Length + 1, x boundary of each char (inclusive of trailing)
    }

    public List<ChunkRect> Chunks = [];
    public bool IsDragging;
    public bool HasSelection; // persists after mouse release
    public Vector2 DragStart;   // 文档相对坐标 (Y 已 + scrollY)
    public Vector2 DragEnd;     // 文档相对坐标
    public float CurrentScrollY; // 每帧由 DrawMessageLog 设置

    public void Clear()
    {
        Chunks.Clear();
        IsDragging = false;
        HasSelection = false;
    }

    public void DropSelection()
    {
        IsDragging = false;
        HasSelection = false;
    }

    public void AddChunk(Vector2 min, Vector2 max, string text, float[] charX)
    {
        if (string.IsNullOrEmpty(text) || charX == null)
            return;
        Chunks.Add(new ChunkRect { Min = min, Max = max, Text = text, CharX = charX });
    }

    /// <summary>
    /// Find which char index in which chunk line corresponds to a screen position.
    /// Returns (-1, -1) if Y is outside the visible chunk range (selection scrolled out of view).
    /// </summary>
    public (int chunkIdx, int charIdx) PointToChar(Vector2 p)
    {
        if (Chunks.Count == 0) return (-1, -1);

        // Visibility check
        float overallMinY = float.MaxValue, overallMaxY = float.MinValue;
        for (var i = 0; i < Chunks.Count; i++)
        {
            if (Chunks[i].Min.Y < overallMinY) overallMinY = Chunks[i].Min.Y;
            if (Chunks[i].Max.Y > overallMaxY) overallMaxY = Chunks[i].Max.Y;
        }
        const float margin = 16f;
        if (p.Y < overallMinY - margin || p.Y > overallMaxY + margin)
            return (-1, -1);

        // Strategy A: find chunks whose [Min.Y, Max.Y] strictly contains p.Y
        // (with 1px epsilon for glyph descent)
        // When multiple chunks on the same line match Y, pick the one whose
        // X range is closest to p.X (or contains it). This fixes the bug where
        // clicking on the right side of a line would incorrectly match the
        // leftmost chunk (e.g. timestamp) instead of the rightmost (content).
        const float yEpsilon = 1f;
        int strictMatch = -1;
        var boundaryCandidates = new List<int>(); // both neighbors of an exact boundary

        float bestXDist = float.MaxValue;
        for (var i = 0; i < Chunks.Count; i++)
        {
            var c = Chunks[i];
            if (p.Y >= c.Min.Y + yEpsilon && p.Y <= c.Max.Y - yEpsilon)
            {
                // Compute X distance: 0 if inside the chunk, otherwise distance to nearest edge
                float xDist;
                if (p.X < c.Min.X)
                    xDist = c.Min.X - p.X;
                else if (p.X > c.Max.X)
                    xDist = p.X - c.Max.X;
                else
                    xDist = 0f;

                if (xDist < bestXDist)
                {
                    bestXDist = xDist;
                    strictMatch = i;
                }
                else if (xDist == bestXDist && strictMatch >= 0 && c.Min.X > Chunks[strictMatch].Min.X)
                {
                    // Same distance: prefer the rightmost chunk (for right-side clicks)
                    strictMatch = i;
                }
            }
        }

        // Strategy B: p.Y is exactly on (or very near) a chunk boundary
        if (strictMatch < 0)
        {
            // Collect every chunk whose range overlaps [p.Y-1, p.Y+1]
            for (var i = 0; i < Chunks.Count; i++)
            {
                var c = Chunks[i];
                if (p.Y >= c.Min.Y - 1f && p.Y <= c.Max.Y + 1f)
                    boundaryCandidates.Add(i);
            }

            if (boundaryCandidates.Count > 0)
            {
                // Prefer the chunk whose X range also contains p.X
                // (same row, rightmost chunk wins if p.X is past all — which means
                // user dragged to the end of that line)
                int? xMatch = null;
                foreach (var idx in boundaryCandidates)
                {
                    var c = Chunks[idx];
                    if (p.X >= c.Min.X - 2f && p.X <= c.Max.X + 2f)
                    {
                        xMatch = idx;
                        break;
                    }
                }
                if (xMatch.HasValue)
                    strictMatch = xMatch.Value;
                else
                    strictMatch = boundaryCandidates[boundaryCandidates.Count - 1];
            }
        }

        // Strategy C: fallback — nearest centerY but only as last resort
        if (strictMatch < 0)
        {
            int bestI = -1;
            float bestDy = float.MaxValue;
            for (var i = 0; i < Chunks.Count; i++)
            {
                var c = Chunks[i];
                var centerY = (c.Min.Y + c.Max.Y) / 2f;
                var dy = Math.Abs(p.Y - centerY);
                if (dy < bestDy) { bestDy = dy; bestI = i; }
            }
            strictMatch = bestI;
        }

        if (strictMatch < 0) return (-1, -1);

        var chosen = Chunks[strictMatch];
        var localX = Math.Clamp(p.X - chosen.Min.X, 0f, Math.Max(0f, chosen.Max.X - chosen.Min.X));

        for (var k = 0; k < chosen.CharX.Length; k++)
        {
            if (chosen.CharX[k] >= localX)
                return (strictMatch, k);
        }

        return (strictMatch, chosen.Text.Length);
    }

    public string GetSelectedText()
    {
        if (Chunks.Count == 0) return string.Empty;
        if (!IsDragging && !HasSelection) return string.Empty;

        var s = new Vector2(DragStart.X, DragStart.Y - CurrentScrollY);
        var e = new Vector2(DragEnd.X, DragEnd.Y - CurrentScrollY);
        var start = PointToChar(s);
        var end = PointToChar(e);
        return ExtractSubstring(start, end);
    }

    private string ExtractSubstring((int chunkIdx, int charIdx) a, (int chunkIdx, int charIdx) b)
    {
        if (a.chunkIdx < 0 || b.chunkIdx < 0) return string.Empty;

        // Ensure a comes before b
        if (a.chunkIdx > b.chunkIdx || (a.chunkIdx == b.chunkIdx && a.charIdx > b.charIdx))
            (a, b) = (b, a);

        var sb = new System.Text.StringBuilder();

        // First (possibly partial) chunk
        var firstChunk = Chunks[a.chunkIdx];
        var startChar = Math.Clamp(a.charIdx, 0, firstChunk.Text.Length);

        if (a.chunkIdx == b.chunkIdx)
        {
            var singleEnd = Math.Clamp(b.charIdx, startChar, firstChunk.Text.Length);
            if (singleEnd > startChar)
                sb.Append(firstChunk.Text.AsSpan(startChar, singleEnd - startChar));
            return sb.ToString();
        }

        // Multi-chunk selection
        var lastCharOfFirst = Math.Clamp(firstChunk.Text.Length, startChar, firstChunk.Text.Length);
        if (lastCharOfFirst > startChar)
            sb.Append(firstChunk.Text.AsSpan(startChar, lastCharOfFirst - startChar));

        // Middle chunks: take entire text
        for (var i = a.chunkIdx + 1; i < b.chunkIdx; i++)
            sb.Append(Chunks[i].Text);

        // Last (possibly partial) chunk
        var lastChunk = Chunks[b.chunkIdx];
        var endChar = Math.Clamp(b.charIdx, 0, lastChunk.Text.Length);
        if (endChar > 0)
            sb.Append(lastChunk.Text.AsSpan(0, endChar));

        return sb.ToString();
    }

    public void DrawHighlight()
    {
        if (!IsDragging && !HasSelection) return;
        if (Chunks.Count == 0) return;

        var s = new Vector2(DragStart.X, DragStart.Y - CurrentScrollY);
        var e = new Vector2(DragEnd.X, DragEnd.Y - CurrentScrollY);
        var (start, end) = (PointToChar(s), PointToChar(e));
        if (start.chunkIdx < 0 || end.chunkIdx < 0) return;

        if (start.chunkIdx > end.chunkIdx || (start.chunkIdx == end.chunkIdx && start.charIdx > end.charIdx))
            (start, end) = (end, start);

        var drawList = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(new Vector4(0.25f, 0.5f, 1f, 0.35f));

        var yMin = Chunks[start.chunkIdx].Min.Y;
        var yMax = Chunks[start.chunkIdx].Max.Y;

        // First chunk - highlight from startChar to end of chunk
        var first = Chunks[start.chunkIdx];
        var sChar = Math.Clamp(start.charIdx, 0, first.Text.Length);
        var eChar = Math.Clamp(start.chunkIdx == end.chunkIdx ? end.charIdx : first.Text.Length, sChar, first.Text.Length);
        if (eChar > sChar)
        {
            drawList.AddRectFilled(
                new Vector2(first.Min.X + first.CharX[sChar], first.Min.Y),
                new Vector2(first.Min.X + first.CharX[eChar], first.Max.Y),
                color);
        }

        if (start.chunkIdx == end.chunkIdx) return;

        // Middle chunks - entire line
        for (var i = start.chunkIdx + 1; i < end.chunkIdx; i++)
        {
            var c = Chunks[i];
            drawList.AddRectFilled(c.Min, c.Max, color);
        }

        // Last chunk - highlight from start to endChar
        var last = Chunks[end.chunkIdx];
        var lastSChar = 0;
        var lastEChar = Math.Clamp(end.charIdx, 0, last.Text.Length);
        if (lastEChar > lastSChar)
        {
            drawList.AddRectFilled(
                new Vector2(last.Min.X + last.CharX[lastSChar], last.Min.Y),
                new Vector2(last.Min.X + last.CharX[lastEChar], last.Max.Y),
                color);
        }
    }
}
