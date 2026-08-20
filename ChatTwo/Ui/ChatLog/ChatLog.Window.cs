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

    // !!! 选字状态必须按窗口隔离（实测修复）：
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

    // 消息区屏幕矩形（上一帧 DrawMessageLog 记录，供"消息区永远不可拖" hit-test）
    // !!! 共享 DrawMessageLog 的窗口用 onMessageArea 回调写自己的矩形，勿改此字段
    public Vector2 LastMessageAreaMin = Vector2.Zero;
    public Vector2 LastMessageAreaMax = Vector2.Zero;

    public Vector2 LastWindowPos { get; set; } = Vector2.Zero;
    public Vector2 LastWindowSize { get; set; } = Vector2.Zero;

    // —— 分辨率变化窗口重定位（方案 A v3，按原生 HUD 逻辑） ——
    // 聊天框位置是绝对客户区坐标，游戏 HUD 按分辨率重排——全屏↔窗口切换（客户区大小
    // 变化）时聊天框不跟随 → 相对 HUD 位移（反馈）。
    // 原生 HUD 逻辑：元素带锚点，元素到锚点的边距随分辨率**等比缩放**。所以：
    // ①稳定帧记录窗口就近的边（左/右、上/下，锚点）+ 绝对像素边距
    // ②变化帧只标记；稳定后延迟一帧按"边距 × 缩放系数"重建位置（与原生 HUD 行为一致）
    // ③变化帧不刷新锚定（避免切换瞬间中间态/自动 clamp 污染 → 多次切换漂移超屏，v1 教训）
    // ④目标 clamp 到可视区兜底防超屏
    // 开销：稳定帧一次四则运算，可忽略。
    private Vector2 _lastDisplaySize = Vector2.Zero;
    private Vector2 _resizeFromSize = Vector2.Zero;
    private bool _resizePending;
    private bool _anchorLeft; // true=锚左（记录左边距），false=锚右（记录右边距）
    private float _marginX;
    private bool _anchorTop;  // true=锚上，false=锚下
    private float _marginY;

    public readonly List<bool> PopOutDocked = [];
    // !!! 长按拖出 v2：HashSet → Dictionary（需持有 Popout 实例用于拖拽时跟随指针）
    public readonly Dictionary<Guid, Popout> PopOutInstances = [];

    // !!! 长按拖出 v2：tab 按下时间/位置（长按 ≥600ms 后**移动**才拖出）；
    // _draggingTabOut = 拖出中的 tab 索引（拖拽期间画幽灵跟随指针，松手才建窗）；
    // _popOutPlaceId/_popOutPlacePos = 释放点（AddPopOutsToDraw 建窗时定位）
    private readonly Dictionary<int, long> _tabPressStart = [];
    private readonly Dictionary<int, Vector2> _tabPressPos = [];
    private int? _draggingTabOut;
    private Guid? _popOutPlaceId;
    private Vector2 _popOutPlacePos;

    private bool IsResizingTopRight;
    private Vector2 ResizeStartMousePos;
    private Vector2 ResizeStartWindowPos;
    private Vector2 ResizeStartWindowSize;
    private bool MouseOverResizeHandle;

    private bool SuppressNextActivate;

    // 消息区交互状态（主窗口实例；PopOut 各自持有独立实例）
    public readonly MessageLogState MsgState = new();

    // 底部标签页栏末尾"+"新建标签页的命名输入（点 + 弹窗命名后创建）
    private string NewTabName = string.Empty;

    // Tracks the tab index rendered in the previous frame, used to detect
    // tab switches in the bottom layout where DrawBottomTabBar runs after
    // DrawMessageLog.
    private int _renderedTabIndex = -1;

    public ChatLog(Plugin plugin) : base($"{Plugin.PluginName}###chat2")
    {
        Plugin = plugin;

        // 锁定状态持久化：从配置恢复上次的锁定/解锁（新增）
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
        // 二级菜单是独立的 AddonContextSub addon（确认，非 ContextMenu 子节点）。
        // PreDraw 持续跟随聊天框移动；PostShow 在显示完成当帧 SetPosition 防"闪一下消失"
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "AddonContextSub", MoveContextSubMenu);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostShow, "AddonContextSub", MoveContextSubMenu);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ContextMenu", OnContextMenuClosed);

        // 提示框零闪帧：hook ItemDetail/ActionDetail 的 SetPosition，detour 替换坐标（正式功能）
        InitSetPosHook();
        // OpenAddonByAgent vtable 22 hook：OpenContextMenu 前设 BlockedParentId=ChatLog（DR 注入识别用）
        InitOpenAddonByAgentHook();
        // 顶点挖洞（正式功能，出宏）：hook igRender 剔除聊天框内菜单区域三角形
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
        // SetCursorPosY(tabCursor.Y-2)）→ 消息区多占 2px（实测：+10 会让滚动区留白变大，
        // 消息文本离输入框更远——方向相反，保持 +2f）
        var height = ImGui.GetContentRegionAvail().Y - inputAreaHeight + 2f - extraBottomPadding;
        // !!! 已移除 title bar 空间补偿（cminY<0 时 height -= contentMinY）：
        // 它随 cminY 负值每帧放大消息区（+31px/次）→ ContentSize 涨 → ScrollMax 涨，
        // 与唤出帧滚动恢复叠加形成逐次累积（实验 2：scrollMaxY 39→70→101→132）。
        // 滚动恢复已由 PreOpenCheck 的 SetNextWindowScroll(0) 根治，此补偿不再需要。
        return height;
    }

    /// <summary>顶部标签模式下标签条的垂直高度（用于把缩放手柄/命中区下移到消息区顶部）。
    /// 仅默认窗口 + 顶部位置时非 0；反除字号比保持高度只随 TabScale，
    /// 与消息区顶部偏移一致（手柄恰好落在消息区右上角）。</summary>
    private float GetTopTabBarHeight()
    {
        if (Plugin.Config.NativeBackground || Plugin.Config.TabPosition is not TabPosition.Top)
            return 0f;
        using var tabFont = Plugin.FontManager.TabFont.Push();
        var style = ImGui.GetStyle();
        return (ImGui.GetTextLineHeight() / (Plugin.Config.TabFontSizePt / 12f) + style.FramePadding.Y * 2) * 0.9f;
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
        // （即到达时该 tab 计过未读的）→ 同步减计数。场景：在 C 时 A/B 都收到同频道
        // 消息都闪，切到 A 看后 B 的未读也一并清除
        SyncSeenAcrossTabs(newTab);

        // Use the fixed channel if set by the user, or set it to the current tabs channel if this tab wasn't accessed before
        // !!! 用 SetChannel（清空 Name）而非直接赋值 Channel：原直接赋值残留旧 Name，
        // 移除每帧锁定后 ReadChannelName 会读到旧名称（实测"频道名不变化"）
        if (newTab.Channel is not null)
            newTab.CurrentChannel.SetChannel(newTab.Channel.Value);
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
        // !!!【根因修复】主窗口从不需要滚动（布局自适应填满，滚动全在消息区 child 内）：
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

        // !!! [CtxClickPass] 菜单打开期间：聊天框窗口不捕获鼠标（NoMouseInputs）→
        // io.WantCaptureMouse 不因聊天框为 true → 游戏原生 UI 正常收到鼠标 →
        // 原生菜单在聊天框内也能点击（点击穿透方案，点击无解则挖洞无意义）。
        // 代价：菜单打开期间聊天框不可滚动/选字/拖拽（模态菜单，可接受）。
        if (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession)
            Flags |= ImGuiWindowFlags.NoMouseInputs;

        // Hit-test using LastWindowPos/Size (set from inside Begin/End in a
        // previous frame — the only reliable coords we have before Begin runs).
        // 偏移量必须与 DrawTopRightResizeHandle 的绘制位置一致（默认 3px / 仿原生 X8 Y-2）
        var st = ImGui.GetStyle();
        var hSize = NativeIcons.ResizeHandleSize();  // !!! 原生手柄素材尺寸
        var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
        var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
        var handleMin = new Vector2(
            LastWindowPos.X + LastWindowSize.X - hSize - st.WindowPadding.X - insetX,
            LastWindowPos.Y + st.WindowPadding.Y + insetY + GetTopTabBarHeight());
        var handleMax = handleMin + new Vector2(hSize, hSize);
        var mp = ImGui.GetIO().MousePos;
        MouseOverResizeHandle = mp.X >= handleMin.X && mp.X <= handleMax.X
                              && mp.Y >= handleMin.Y && mp.Y <= handleMax.Y;

        // !!! 消息区任何情况下都不可拖（不依赖锁定开关），
        // NoMove 只禁窗口拖动、不影响插件自身文本选取；未锁定时其余区域（tab/输入框/空白）
        // 仍可拖动窗口；打开"锁定窗口移动"后剩余区域也不可拖（整个窗口锁死）。
        // 缩放中/手柄上照旧禁止拖动。
        // !!! MoveLocked 实时读 Config（锁按钮移除后设置页是唯一入口，字段会过期）。
        if (IsMouseOverMessageAreaPublic() || Plugin.Config.MoveLocked || IsResizingTopRight || MouseOverResizeHandle)
            Flags |= ImGuiWindowFlags.NoMove;

        if (LastViewport == ImGuiHelpers.MainViewport.Handle && !WasDocked)
            // !!! 修复：BgAlpha 是可空 float?，null=不透明背景！必须显式 0（基类默认不透明，删了背景就回来）
            BgAlpha = 0f;


        LastViewport = ImGui.GetWindowViewport().Handle;
        WasDocked = ImGui.IsWindowDocked();
    }

    /// <summary>鼠标是否在消息区矩形内（消息区永远不可拖，hit-test 用）。矩形由 DrawMessageLog 每帧记录。</summary>
    public bool IsMouseOverMessageAreaPublic()
    {
        var mp = ImGui.GetIO().MousePos;
        return mp.X >= LastMessageAreaMin.X && mp.X <= LastMessageAreaMax.X
            && mp.Y >= LastMessageAreaMin.Y && mp.Y <= LastMessageAreaMax.Y;
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

        // !!! 分辨率重定位逻辑在 Draw() 开头（Begin 之后）执行：PreDraw 在窗口 Begin 前，
        // GetWindowPos/GetWindowSize 读到的是错误/过期值，SetWindowPos 也不可靠（v3 实测
        // 每次切换向同一方向漂移超屏的根因）。Draw 内位置读取与立即定位才正确。
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

        // !!! 缩放手柄置顶：child 有独立 dl 且后渲染会盖住 Draw 里的手柄 →
        // 用前台 dl 在 PostDraw 重画（绝对最上层）；!!! End 后 GetWindowPos 不可靠 → 用 LastWindowPos
        if (Plugin.Config.CanResize)
            DrawResizeHandleOverlay();
    }

    /// <summary>缩放手柄最上层绘制（PostDraw 前台 dl；位置与 DrawTopRightResizeHandle 一致）</summary>
    private void DrawResizeHandleOverlay()
    {
        var style = ImGui.GetStyle();
        var hSize = NativeIcons.ResizeHandleSize();
        var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
        var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
        var localPos = new Vector2(
            LastWindowSize.X - hSize - style.WindowPadding.X - insetX,
            style.WindowPadding.Y + insetY + GetTopTabBarHeight());
        var p = LastWindowPos + localPos;
        NativeIcons.DrawResizeHandle(ImGui.GetForegroundDrawList(), p, new Vector2(hSize, hSize),
            MouseOverResizeHandle || IsResizingTopRight);
    }

    public override void OnClose()
    {
        // We force the main log to be always open
        IsOpen = true;
    }

    public override void Draw()
    {
        DrewThisFrame = true;

        // !!! 鼠标在聊天窗口内 → 帧末光标决策（保持游戏指针；按钮/tab 上手指）
        Plugin.MarkCursorInChatWindow();

        // 分辨率变化窗口重定位（方案 A v4，原生 HUD 逻辑）：此处已处于窗口 Begin 之后，
        // GetWindowPos/GetWindowSize 可靠、SetWindowPos 立即生效（v3 在 PreDraw 读位置导致
        // 锚定污染、每次切换同向漂移超屏——已修正）。
        // 原生 HUD = 锚点 + 边距随分辨率等比缩放：稳定帧记录就近锚点与边距，
        // 切换帧延迟一拍后按"边距 × 缩放系数"重建位置，clamp 防超屏。
        var display = ImGui.GetIO().DisplaySize;
        if (display.X > 1f && display.Y > 1f)
        {
            if (_lastDisplaySize.X <= 0f)
            {
                _lastDisplaySize = display;
            }
            else if (display != _lastDisplaySize)
            {
                _resizeFromSize = _lastDisplaySize;
                _lastDisplaySize = display;
                _resizePending = true;
            }
            else if (_resizePending)
            {
                // 尺寸已稳定的第一帧：用记录的锚定边距 × 缩放系数重建位置
                // （此刻 pos 还是旧位置，不刷新锚定——避免污染导致累积漂移）
                _resizePending = false;
                var size = ImGui.GetWindowSize();
                var sx = display.X / _resizeFromSize.X;
                var sy = display.Y / _resizeFromSize.Y;
                var x = _anchorLeft ? _marginX * sx : display.X - _marginX * sx - size.X;
                var y = _anchorTop ? _marginY * sy : display.Y - _marginY * sy - size.Y;
                x = Math.Clamp(x, 0f, Math.Max(0f, display.X - size.X));
                y = Math.Clamp(y, 0f, Math.Max(0f, display.Y - size.Y));
                ImGui.SetWindowPos(new Vector2(x, y));
            }
            else
            {
                // 稳定帧：记录就近边锚定 + 绝对像素边距（窗口在左半→锚左，上半→锚上…）
                var pos = ImGui.GetWindowPos();
                var size = ImGui.GetWindowSize();
                var left = pos.X;
                var right = display.X - pos.X - size.X;
                _anchorLeft = left <= right;
                _marginX = _anchorLeft ? left : right;
                var top = pos.Y;
                var bottom = display.Y - pos.Y - size.Y;
                _anchorTop = top <= bottom;
                _marginY = _anchorTop ? top : bottom;
            }
        }

        // 聊天内容：默认 Axis 游戏字体（原生观感）；选了自定义字体后改用 RegularFont
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
        // !!! 原生手柄素材替换金字塔：尺寸基准 31x31（高亮 42x42 缩到同区域绘制）
        var hSize = NativeIcons.ResizeHandleSize();

        // 缩放手柄位置：默认界面贴窗口背景右上角内侧 3px（恰到好处）；
        // 仿原生：X 内缩 8px 落在消息区背景内；Y 内缩统一走 NativeIcons.ResizeHandleInsetY
        //（默认 3px / 仿原生 4px + 下移 5px，实测校准）。
        // !!! 顶部标签模式：手柄必须下移标签条高度（GetTopTabBarHeight）才落在消息区右上角
        var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
        var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
        var localPos = new Vector2(
            windowSize.X - hSize - style.WindowPadding.X - insetX,
            style.WindowPadding.Y + insetY + GetTopTabBarHeight());

        var mousePos = ImGui.GetIO().MousePos;
        var handleRectMin = windowPos + localPos;
        var handleRectMax = handleRectMin + new Vector2(hSize, hSize);
        var hovered = mousePos.X >= handleRectMin.X && mousePos.X <= handleRectMax.X
                      && mousePos.Y >= handleRectMin.Y && mousePos.Y <= handleRectMax.Y;

        if (hovered || IsResizingTopRight)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        // !!! 绘制已移至 PostDraw（前台 dl 置顶）；这里只保留 hit-test（否则出现双手柄）
    }

    // 滚轮一次只滚动一行（原版行为）。_pendingWheel 由 DrawChatLog/Draw 开头记录（已清零 IO，
    // ImGui 不自动滚），这里手动滚 1 行；边界由 ImGui clamp 自然处理
    private void HandleWheelScrollLineByLine(MessageLogState state)
    {
        // 不检查 IsWindowHovered：鼠标在滚动条/输入区上（child 外）也要能滚消息区；
        // DrawChatLog 开头已确认鼠标在本窗口内（RootAndChildWindows）才记录 PendingWheel
        // !!! 滚到顶标志：内容不满一屏（无滚动）或当前已在顶部 → AtTop=true。
        // 聊天记录窗口的"滚动到顶自动加载上一天"依赖它（外层 child 的 GetScrollY 恒 0 不可靠）。
        state.AtTop = ImGui.GetScrollMaxY() <= 0f || ImGui.GetScrollY() <= 0f;
        if (Math.Abs(state.PendingWheel) < 0.001f) return;
        // !!! 外层容器 child（##chat2-bottom-log）刚 Begin 时内容未画、maxY=0——此时消费
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
        MsgState.UserScrolled = false; // 每帧重置；滚轮滚动时置 true → 本帧禁止自动贴底
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

        // 仿原生界面（NativeBackground）下标签页固定底部（三段式贴图只支持底部），位置选项失效。
        // 默认窗口分类下：Bottom / Side / Top（Top 重新支持，标签条移到消息区上方）
        var topTabs = !Plugin.Config.NativeBackground && Plugin.Config.TabPosition is TabPosition.Top;
        var bottomTabs = Plugin.Config.NativeBackground || Plugin.Config.TabPosition is not (TabPosition.Side or TabPosition.Top);
        var sideTabs = !Plugin.Config.NativeBackground && Plugin.Config.TabPosition is TabPosition.Side;

        if (sideTabs)
        {
            DrawTabSidebar();
        }
        else if (topTabs)
        {
            DrawTopTabLog();
        }
        else if (bottomTabs)
        {
            DrawBottomTabLog();
        }
        else
        {
            DrawTabBar();
        }

        if (topTabs)
        {
            // 顶部模式：标签条在消息区上方（已在 DrawTopTabLog 内绘制），底部只剩输入行
            DrawChannelInputRow();
        }
        else if (bottomTabs)
        {
            // 缩放通过字体重建实现（输入区 = FontManager 字号 × InputAreaScale；标签页 × TabScale，
            // v1.40.17+ 拆分），这里无需 SetWindowFontScale —— drawList 渲染的文字随字体 atlas 自动缩放
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
        //（此前底部对齐，实测偏上，改为居中）
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
        // 首行缩进感，输入框相应缩水，整体长度不变）。!!! 必须先回到行首 X——
        // 否则会沿用频道名行末的 X，气泡和输入框都被频道名宽度挤到右边
        ImGui.SetCursorPosX(inputBoxHeight * 0.5f); // 一个"字母"≈半字
        ImGui.SetCursorPosY(iconTop); // !!! 修复：此前只设 X 没设 Y → 气泡贴顶不居中
        // 原生图标：新素材 icon_05 聊天气泡；wrap 未加载时回退 Comment FontAwesome。
        // !!! 音效改 UiSwitch(1)：游戏原生频道切换音效（原排除"选择频道"——确认要加）
        // !!! 修复：气泡可点条件按"输入频道锁定"（InputChannelLocked）而非"Channel 是否非 null"——
        // 设置了固定频道但未锁定的标签页也应能自由切换频道（快捷键可、气泡之前被误禁）
        if (ImGuiUtil.NativeIconButton(NativeIcons.Bubble, "channel-switcher-bubble", null, FontAwesomeIcon.Comment, sfx: ImGuiUtil.BtnSfx.UiSwitch)
            && !activeTab.InputChannelLocked)
            ImGui.OpenPopup(ChatChannelPicker);
        if (activeTab.InputChannelLocked && ImGui.IsItemHovered())
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
        // !!! v1.40.17+ 仿原生界面：输入框右侧按钮间距更紧凑（2px），普通模式维持默认 ItemSpacing
        var btnSpacing = Plugin.Config.NativeBackground
            ? 2f * ImGuiHelpers.GlobalScale
            : ImGui.GetStyle().ItemSpacing.X;
        // Cog + 搜索恒显示；隐藏/新人按钮按配置（锁按钮已移除）
        var buttonsRight = 1 + 1 + (showNovice ? 1 : 0) + (Plugin.Config.ShowHideButton ? 1 : 0);
        var inputWidth = ImGui.GetContentRegionAvail().X - buttonWidth * buttonsRight - btnSpacing * buttonsRight;
        InputHandler.DrawInputArea(activeTab, inputWidth, ref TellSpecial);

        ImGui.SameLine(0, btnSpacing);
        ImGui.SetCursorPosY(iconTop);

        // 右侧图标与左侧气泡同尺寸，底部对齐输入框底边（不随输入字体变化）
        // 原生图标：新素材 icon_00 齿轮
        ImGui.SetCursorPosY(iconTop);
        if (ImGuiUtil.NativeIconButton(NativeIcons.Gear, "chat-settings", "设置", FontAwesomeIcon.Cog))
            Plugin.SettingsWindow.Toggle();

        if (Plugin.Config.ShowHideButton)
        {
            ImGui.SameLine(0, btnSpacing);
            // 原生图标：新素材 icon_24 粗X（与聊天记录窗口关闭/重置同源）；SFX 25 关闭音
            ImGui.SetCursorPosY(iconTop);
            if (ImGuiUtil.NativeIconButton(NativeIcons.Close, "chat-hide", "隐藏消息栏", FontAwesomeIcon.EyeSlash, sfx: ImGuiUtil.BtnSfx.Dismiss))
                UserHide();
        }

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            InputHandler.LastActivityTime = InputHandler.FrameTime;

        if (showNovice)
        {
            ImGui.SameLine(0, btnSpacing);
            // 原生图标：新素材 icon_14 双叶嫩芽
            // !!! 无按钮音（点击触发游戏原生新人频道按钮，原生自带开关声音——再加会双响）
            ImGui.SetCursorPosY(iconTop);
            if (ImGuiUtil.NativeIconButton(NativeIcons.Leaf, "chat-novice", "加入新人频道", FontAwesomeIcon.Leaf, sfx: ImGuiUtil.BtnSfx.None))
                GameFunctions.GameFunctions.ClickNoviceNetworkButton();
        }

        // 聊天记录搜索（工具栏放大镜，Ctrl+F 也可打开）
        ImGui.SameLine(0, btnSpacing);
        ImGui.SetCursorPosY(iconTop);
        // 原生图标：新素材 icon_09 放大镜（与聊天记录窗口内"搜索"按钮 icon_34 不同图）
        if (ImGuiUtil.NativeIconButton(NativeIcons.ChatSearch, "chat-search", Language.Search_Title, FontAwesomeIcon.Search))
            Plugin.SearchWindow.Toggle();
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
        else if (activeTab is { Channel: { } channel } && activeTab.InputChannelLocked)
        {
            // 仅"始终锁定"的标签页固定显示 tab 配置的频道名（每帧强制，显示=实际一致）；
            // 未锁定的标签页走下方 else 分支显示 CurrentChannel 的实际频道
            // （修复：原条件不看锁定，手动切频道后名称不更新）
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
    // 相同透明度下观感一致；导入的自定义样式也自动跟随）。
    // !!! alpha 必须乘 WindowBg 的 alpha 分量：ImGui 渲染窗口背景时最终 alpha =
    // BgAlpha × WindowBg.alpha（相乘）——只取 WindowAlpha/100 会丢掉样式 alpha，
    // 在 WindowBg.alpha<1 时消息区比默认界面更不透明（更深），实测过
    /// <summary>消息区/侧面板背景色：默认 = WindowBg 的 RGB 向白色混合 25%（明显变淡，纯黑不再死黑）；
    /// 玩家可自定义 RGB（CustomMessageLogBg）。透明度统一由 WindowAlpha（消息区透明度）控制。
    /// SearchWindow 侧面板共用此方法（勿重复实现）。</summary>
    internal static Vector4 ChatBackgroundColor()
    {
        Vector3 rgb;
        if (Plugin.Config.CustomMessageLogBg)
        {
            rgb = ColourUtil.RgbaToVector3(Plugin.Config.MessageLogBgColor);
        }
        else
        {
            var winBg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
            var fade = 0.25f;
            rgb = new Vector3(
                winBg.X + (1f - winBg.X) * fade,
                winBg.Y + (1f - winBg.Y) * fade,
                winBg.Z + (1f - winBg.Z) * fade);
        }
        var alpha = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg].W * (Plugin.Config.WindowAlpha / 100f);
        return new Vector4(rgb.X, rgb.Y, rgb.Z, alpha);
    }

    private Vector4 MessageLogBgColor() => ChatBackgroundColor();

    public void DrawMessageLog(Tab tab, PayloadHandler handler, float childHeight, bool switchedTab, MessageLogState state, Guid? scrollToMessageId = null, Action<Message>? onMessageClick = null, Action<Vector2, Vector2>? onMessageArea = null)
    {
        // 字体 atlas 异步构建（插件加载后首个 Draw 帧可能尚未就绪）：主字体未就绪时
        // IFontHandle.Push() 是 no-op，消息会用默认字体渲染并写入错误的高度缓存
        // （message.Height），导致之后布局错乱/首行截断。等字体就绪再渲染。
        var mainFontHandle = Plugin.Config.FontsEnabled ? Plugin.FontManager.RegularFont : Plugin.FontManager.Axis;
        if (!mainFontHandle.Available && mainFontHandle.LoadException == null)
            return;

        // 仿原生着色：消息区背景颜色取 WindowBg 的 RGB（与默认界面窗口背景一致，
        // 纯黑 (0,0,0) 在相同 alpha 下会明显更深——实测），alpha 跟随窗口透明度设置。
        // !!! 嵌套模式（childHeight<=0，bottom tab 布局外层已显式画圆角背景）不画内层背景——
        // 否则内层矩形背景会盖住外层圆角弧线（实测：只有底边两角圆、顶边两角直角）
        using var msgBg = ImRaii.PushColor(ImGuiCol.ChildBg, MessageLogBgColor(), childHeight > 0f);
        // 消息框圆角（放消息的区域，不是输入框；4px 太小看不到，加大 8px）
        using var msgRound = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);

        // !!! 不用 NoScrollbar：ImGui 对 NoScrollbar 窗口的 SetScrollY 是 no-op（手动滚动失效）。
        // 改用 NoScrollWithMouse（阻止自动滚）+ 隐藏 ImGui 滚动条（透明）——滚动完全由我们控制
        using var sbGrab = ImRaii.PushColor(ImGuiCol.ScrollbarGrab, 0u);
        using var sbGrabHovered = ImRaii.PushColor(ImGuiCol.ScrollbarGrabHovered, 0u);
        using var sbGrabActive = ImRaii.PushColor(ImGuiCol.ScrollbarGrabActive, 0u);
        using var sbBg = ImRaii.PushColor(ImGuiCol.ScrollbarBg, 0u);
        using var sbSize = ImRaii.PushStyle(ImGuiStyleVar.ScrollbarSize, 0f);
        using var child = ImRaii.Child("##chat2-messages", new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoScrollWithMouse | (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession ? ImGuiWindowFlags.NoMouseInputs : ImGuiWindowFlags.None));
        if (!child.Success)
            return;

        // 记录消息区屏幕矩形（供各窗口"消息区永远不可拖"hit-test）。
        // !!! 共享 DrawMessageLog 的窗口（PopOut/SearchWindow）必须传 onMessageArea 写各自的
        // 矩形——否则共用 ChatLog 字段会被最后 Draw 的窗口覆盖（主窗口/PopOut 互相污染）。
        var areaMin = ImGui.GetWindowPos();
        var areaMax = areaMin + ImGui.GetWindowSize();
        if (onMessageArea != null)
            onMessageArea(areaMin, areaMax);
        else
        {
            LastMessageAreaMin = areaMin;
            LastMessageAreaMax = areaMax;
        }

        // 仿原生消息区背景（非嵌套模式：PopOut 独立窗口 / 顶部 tab 布局）：
        // 窗口透明后 child 背景在此显式绘制圆角——ChildRounding 在本版本可能不读，
        // 与主窗口 bottomTabs 布局（外层 child 显式画）同源方案；嵌套模式 childHeight<=0
        // 由外层 child 画，这里跳过避免矩形盖掉外层圆角
        if (childHeight > 0f)
        {
            var dl = ImGui.GetWindowDrawList();
            var cMin = ImGui.GetWindowPos();
            var cMax = cMin + ImGui.GetWindowSize();
            // !!! 顶部标签模式：消息区顶边两角直角（紧贴同色 tab 条，左右边缘平齐），
            // 只圆底边两角；其余场景四角圆（默认）
            var topTabs = !Plugin.Config.NativeBackground && Plugin.Config.TabPosition is TabPosition.Top;
            dl.PushClipRect(cMin, cMax, false);
            dl.AddRectFilled(cMin, cMax, ImGui.GetColorU32(MessageLogBgColor()), 8f,
                topTabs ? ImDrawFlags.RoundCornersBottom : ImDrawFlags.None);
            dl.PopClipRect();
        }

        HandleWheelScrollLineByLine(state);

        // !!! 选字状态按窗口隔离（state.Selection，见 MessageLogState 注释）：
        // 主窗口与各 PopOut 的 DrawMessageLog 各自持有独立 Selection，互不干扰。
        var selection = state.Selection;
        selection.Chunks.Clear(); // rebuild every frame (scroll changes positions)
        ImGuiUtil.CurrentSelection = selection;
        var scrollY = ImGui.GetScrollY();
        selection.CurrentScrollY = scrollY;

        // 左缩进留出左侧滚动条空间（贴近滚动条，从 14 减到 10）
        ImGui.Indent(10f);

        // !!! v1.40.17 清理：原作者"现代化布局"（DrawLogTableStyle 2 列表格渲染）已移除，
        // 时间戳统一走 DrawTimestampInline 行内渲染（见 DrawLogNormalStyle → DrawMessages）
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
            DrawMessages(tab, handler, scrollToMessageId: scrollToMessageId, onMessageClick: onMessageClick);

        // !!! 手动滚轮时禁止自动贴底：SetScrollY 设的是 ScrollTarget，当帧 GetScrollY()
        // 还是旧值（底部）→ 贴底判断仍成立 → SetScrollHereY(1f) 把滚动拉回底部 → 向上滚失效
        if (switchedTab || (!state.UserScrolled && ImGui.GetScrollY() >= ImGui.GetScrollMaxY()))
            ImGui.SetScrollHereY(1f);

        handler.Draw();
    }

    private void DrawMessages(Tab tab, PayloadHandler handler, Guid? scrollToMessageId = null, Action<Message>? onMessageClick = null)
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
            // !!! 合并相同时间：记录上一条已显示的时间戳（每帧重置 → 每条日志首条总是显示）
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
                        // !!! 只缓存正值高度：负值段落间距收紧行距时 newHeight 可能 ≤0，
                        // 负高度会污染可见性占位（Dummy 负高度不受支持）
                        if (newHeight > 0)
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

                    ImGui.Dummy(new Vector2(10f, height.Value));

                    var nowVisible = ImGui.IsItemVisible();
                    if (!nowVisible)
                        continue;

                    ImGui.SetCursorPos(beforeDummy);
                    message.IsVisible[tab.Identifier] = nowVisible;
                }

                // 时间戳单独成列：时间戳占左侧固定列，正文整体缩进——ImGui.Indent 修改
                // DC.Indent，ItemSize 结尾光标 X 重置到 Indent.x（TextEx/正文换行自动保持缩进，
                // 不会绕回时间戳下方）。PushIndent 自动恢复；时间戳本身仍注册选字
                IDisposable? tsIndent = null;
                if (tab.DisplayTimestamp && Plugin.Config.ShowTimestamp)
                {
                    var localTime = message.Date.ToLocalTime();
                    // 24 小时制去掉小时前导零（原生样式 [2:30] 而非 [02:30]）
                    var timestamp = Plugin.Config.Use24HourClock
                        ? $"{localTime.Hour}:{localTime.Minute:00}"
                        : localTime.ToString("t", null);
                    // 时间戳用主字体渲染（与聊天框文字大小一致）。
                    // !!! v1.40.17 清理：旧"现代化布局"表格分支已移除，统一 DrawTimestampInline 行内渲染
                    //（支持 去括号/紧凑排布/时间戳字间距）
                    // !!! 合并相同时间（原版 HideSameTimestamps 回归）：同一分钟内连续消息只显示
                    // 第一个时间戳。行内模式=跳过绘制+SameLine（正文顶格）；独立列模式=不绘制但
                    // 保留缩进宽度（列对齐不被破坏，时间戳位置留白）
                    var sameAsLast = Plugin.Config.MergeSameTimestamps && timestamp == lastTimestamp;
                    if (Plugin.Config.TimestampOwnColumn)
                    {
                        if (!sameAsLast)
                            lastTimestamp = timestamp;
                        var tsW = DrawTimestampInline(timestamp, draw: !sameAsLast);
                        // 列宽 = 时间戳文本宽 + 时间戳与正文间距（可调，默认 8px×scale）。
                        // !!! scaled:false 必须——tsW 已是像素宽，PushIndent 默认 scaled=true 会再乘
                        // GlobalScale（1.5）→ 缩进 1.5 倍 → 时间戳与正文巨大空白（实测）
                        var colGap = Plugin.Config.TimestampColumnGap * ImGuiHelpers.GlobalScale;
                        tsIndent = ImRaii.PushIndent(tsW + colGap, scaled: false);
                    }
                    else
                    {
                        if (!sameAsLast)
                        {
                            lastTimestamp = timestamp;
                            DrawTimestampInline(timestamp);
                            ImGui.SameLine();
                        }
                    }
                }

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

                tsIndent?.Dispose();

                message.IsVisible[tab.Identifier] = ImGui.IsItemVisible();

                // 消息点击回调（聊天记录窗口用：点击消息定位上下文）
                if (onMessageClick != null && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    onMessageClick(message);

                // 段落间距（v1.40.17+）：消息行之间的额外垂直间距。
                // 放在消息内容之后 → 下一行的高度测量（GetCursorPosY - lastPosY）自然包含该间距，
                // 隐藏消息的 Dummy 占位（height 缓存）也一致。
                // 负值 = 收紧行距（利用字体行高余量，如 CJK 行高≈1.4×字号；过负会文字重叠）：
                // 用光标上移实现而非负高度 Dummy（ImGui Dummy 负高度不受支持）
                var lineSpacing = Plugin.Config.MessageLineSpacing;
                if (lineSpacing != 0f && i < messages.Count - 1)
                {
                    var spacingPx = lineSpacing * ImGuiHelpers.GlobalScale;
                    if (spacingPx > 0f)
                        ImGui.Dummy(new Vector2(0f, spacingPx));
                    else
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + spacingPx);
                }
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

    /// <summary>
    /// 绘制行内时间戳（v1.40.17+，替代旧"现代化布局"表格逻辑的 DrawChunk 方式）：
    /// 逐字符 AddText 实现 字间距自由调整；配合 去括号 / 紧凑排布 两个独立子选项。
    /// 用 Dummy 锚定光标（SameLine / GetContentRegionAvail 依赖上一个 item 的矩形）。
    /// 返回时间戳文本渲染宽度（不含 tailGap；单独成列模式用其做正文缩进量）。
    /// draw=false = 仅计算宽度不绘制（合并相同时间时独立列模式的留白占位，保持列对齐）。
    /// </summary>
    private static float DrawTimestampInline(string timestamp, bool draw = true)
    {
        var cfg = Plugin.Config;
        var text = cfg.RemoveTimestampBrackets ? timestamp : $"[{timestamp}]";
        var compact = cfg.CompactTimestampSpacing;
        // !!! v1.40.17+ 时间戳字间距（自由调整）×scale + 紧凑排布预设 -1px（两子选项独立，可叠加）。
        // 正文另有独立字间距（MessageLetterSpacing，WrapText 路径），两者互不串位
        var spacing = cfg.TimestampLetterSpacing * ImGuiHelpers.GlobalScale + (compact ? -1f * ImGuiHelpers.GlobalScale : 0f);
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize();
        var col = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        var dl = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorScreenPos();
        var pos = startPos;
        var totalW = 0f;
        foreach (var ch in text)
        {
            var chStr = ch.ToString();
            var cw = ImGui.CalcTextSize(chStr).X;
            if (draw)
                dl.AddText(font, size, pos, col, chStr);
            pos.X += cw + spacing;
            totalW += cw + spacing;
        }

        // 与发送者名之间的间隔（原实现是 chunk 尾随空格；紧凑排布用小间隔）
        var tailGap = compact ? 2f * ImGuiHelpers.GlobalScale : ImGui.CalcTextSize(" ").X;
        ImGui.Dummy(new Vector2(totalW + tailGap, 0f));

        // !!! 必须手动注册进选字系统：旧实现经 DrawChunk→WrapText 会自动 AddChunk，
        // 自绘后若不注册，PointToChar 在时间戳区域映射不到字符 → 自由选取选不到时间戳
        //（v1.40.17 回归修复）。矩形用行高（非 0 高 Dummy），charX 含间距。
        // draw=false 时没有可见文字，不注册（避免选中"看不见的字"）
        if (draw && ImGuiUtil.CurrentSelection != null && text.Length > 0)
        {
            var lineHeight = ImGui.GetTextLineHeight();
            var charX = new float[text.Length + 1];
            charX[0] = 0f;
            for (var ci = 0; ci < text.Length; ci++)
            {
                var ch = text[ci];
                if (char.IsHighSurrogate(ch) && ci + 1 < text.Length && char.IsLowSurrogate(text[ci + 1]))
                {
                    var pair = ch.ToString() + text[ci + 1];
                    var sz = ImGui.CalcTextSize(pair).X;
                    charX[ci + 1] = charX[ci] + sz + spacing;
                    charX[ci + 2] = charX[ci + 1]; // same boundary for low surrogate
                    ci++; // skip the low surrogate
                }
                else
                {
                    var sz = ImGui.CalcTextSize(char.ToString(ch)).X;
                    charX[ci + 1] = charX[ci] + sz + spacing;
                }
            }
            charX[text.Length] = totalW;
            ImGuiUtil.CurrentSelection.AddChunk(startPos, startPos + new Vector2(totalW + tailGap, lineHeight), text, charX);
        }

        return totalW;
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

        // 输入频道：勾选"输入频道始终锁定"的标签页每帧强制频道（手动切换会被拉回）；
        // 未勾选的只依赖 TabSwitched（切换 tab 时设置一次），之后可自由切换频道
        // （需求：默认不要每帧锁定）
        if (activeTab.Channel is not null && activeTab.InputChannelLocked)
            activeTab.CurrentChannel.SetChannel(activeTab.Channel.Value);

        var style = ImGui.GetStyle();
        // 与 DrawBottomTabBar 的标签页高度保持一致（固定 TabFont + 0.3 系数），
        // 并压缩分隔线余量，避免消息栏底部与标签页之间出现黑边
        float tabBarHeight;
        {
            // !!! 必须把 TabFont 限制在此小作用域：using var 的作用域是整个方法，
            // 会覆盖下面的 DrawMessageLog，导致消息文字被 12pt 字体渲染
            // （实测 msgFontSize=24px=12pt×4/3×1.5，改主字体消息完全不变）
            using var tabFont = Plugin.FontManager.TabFont.Push();
            // !!! Bug 修复：按素材模式计算预留高度——
            // 原生：17px×scale×TabScale + 下移 4px（tab 高度随"标签页缩放"等比例变；
            // v1.40.17+ 与输入区缩放拆分）；
            // Legacy：旧公式（TabFont 12pt×tabScale 的 TextLineHeight）——之前固定 17px
            // 导致缩放后 Legacy tab 变高被底边切
            tabBarHeight = Plugin.Config.NativeBackground
                ? 17f * ImGuiHelpers.GlobalScale * Plugin.Config.TabScale + 4f
                : (ImGui.GetTextLineHeight() / (Plugin.Config.TabFontSizePt / 12f) + style.FramePadding.Y * 2) * 0.9f + 2f;
        }
        // separatorHeight（1+ItemSpacing*0.3）是历史"分隔线余量"——实际 DrawBottomTabBar 不画 separator
        // （tab 上移 2px 重叠已经是 separator），曾让 childHeight 偏大约 2px → 底部空白
        var extraBottomPadding = tabBarHeight;
        var childHeight = GetRemainingHeightForMessageLog(extraBottomPadding);

        // 消息框圆角（放消息的区域，不是输入框；4px 太小看不到，加大 8px）
        using var msgRound = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);

        // !!! 不用 NoScrollbar：ImGui 对 NoScrollbar 窗口的 SetScrollY 是 no-op（手动滚动失效）。
        // 改用 NoScrollWithMouse（阻止自动滚）+ 隐藏 ImGui 滚动条（透明）——滚动完全由我们控制
        using var sbGrab = ImRaii.PushColor(ImGuiCol.ScrollbarGrab, 0u);
        using var sbGrabHovered = ImRaii.PushColor(ImGuiCol.ScrollbarGrabHovered, 0u);
        using var sbGrabActive = ImRaii.PushColor(ImGuiCol.ScrollbarGrabActive, 0u);
        using var sbBg = ImRaii.PushColor(ImGuiCol.ScrollbarBg, 0u);
        using var sbSize = ImRaii.PushStyle(ImGuiStyleVar.ScrollbarSize, 0f);
        // !!! 消息区顶部下移 2px（childHeight 基于剩余空间自动适配）
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2f);
        using var child = ImRaii.Child("##chat2-bottom-log", new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoScrollWithMouse | (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession ? ImGuiWindowFlags.NoMouseInputs : ImGuiWindowFlags.None));
        if (!child.Success)
            return;

        // !!! 大工程（NativeBackground 改为素材开关）：窗口背景永远透明 0，
        // 消息区背景**无条件**绘制（非原生模式窗口不再提供底色）——
        // 颜色取 WindowBg 的 RGB + WindowAlpha；圆角矩形（rounding 8）
        {
            var dl = ImGui.GetWindowDrawList();
            var cMin = ImGui.GetWindowPos();
            var cMax = cMin + ImGui.GetWindowSize();

            // !!! child 默认 clip 会把顶部圆角弧线裁掉（实测：顶部直角、底部圆角）——
            // PushClipRect 完全替换 clip（第三个参数必须 false！true=取交集，等于没扩）到
            // 整个 child 矩形，四角圆角完整渲染
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

    private void DrawTopTabLog()
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

        // 输入频道锁定（同底部模式）
        if (activeTab.Channel is not null && activeTab.InputChannelLocked)
            activeTab.CurrentChannel.SetChannel(activeTab.Channel.Value);

        // 顶部标签页：先画标签条（样式与底部完全一致；topTabs 必非原生 → Legacy 纯色 tab），
        // 再画消息区填满剩余空间。非嵌套结构（匹配 Side 模式）：消息 childHeight>0，
        // 圆角背景由 DrawMessageLog 内部绘制（##chat2-messages）——避免额外外层 child 的上下文问题。
        var barTopY = ImGui.GetCursorPosY();
        DrawBottomTabBar();
        // !!! 消息区紧贴标签条底部：Legacy 末尾 NewLine 行高 > tab 高会产生空隙，
        // 光标拉回 tab 条底（GetTopTabBarHeight 与背景条高度一致）→ 同色区域连成一体
        ImGui.SetCursorPosY(barTopY + GetTopTabBarHeight());

        DrawMessageLog(activeTab, InputHandler.PayloadHandler, GetRemainingHeightForMessageLog(), switchedTab, MsgState);
        _renderedTabIndex = Plugin.LastTab;
    }

    private void DrawBottomTabBar()
    {
        // !!! 大工程：NativeBackground = 素材开关。
        // false（非原生）→ 旧版纯色 tab（300d94d 恢复）；true → 原生三段式贴图
        if (!Plugin.Config.NativeBackground)
        {
            DrawBottomTabBarLegacy();
            return;
        }

        var tabs = Plugin.Config.Tabs;
        var anyClicked = false;

        // 底部标签页文字用固定大小字体（TabFont，12pt）
        using var tabFont = Plugin.FontManager.TabFont.Push();
        var drawList = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var style = ImGui.GetStyle();

        // !!! 原生 tab 三段式 v2（重制素材，高统一 48px）：
        // 拼装顺序 = 左帽 → 分割线 → (中段 + 分割线)×N → 右帽 → "+"（+ 前无分割线）。
        // !!! Bug 修复：tab 高度必须 × TabScale——tab 文字字号 = 12pt×tabScale（v1.40.17+ 拆分），
        // 之前只变长不变高 → 比例破坏；左/下固定、缩放向右上。
        // !!! tab 高度只由"标签页缩放"(TabScale) 控制，"标签页名称文字大小"(TabFontSizePt)
        // 只改文字字号不改高度（高度已有 TabScale 管，见 v1.40.17 拆分）
        var tabHeight = 17f * scale * Plugin.Config.TabScale;
        var tabScale = tabHeight / 48f;  // 素材原始高 48px → 目标高度
        var capLeftSize = new Vector2(39f, 48f) * tabScale;
        var capRightSize = new Vector2(40f, 48f) * tabScale;
        var middleBaseSize = new Vector2(50f, 48f) * tabScale;  // 中段基础宽（短名下限）
        var dividerSize = new Vector2(6f, 48f) * tabScale;      // 分割线
        var indicatorSize = new Vector2(21f, 21f) * tabScale;   // 指示点随 tab 缩小
        var indicatorOffset = new Vector2(1.0f, 1.5f) * scale;  // 金点固定边距（x=1.0 往左微调，不随 tab 延伸）
        var capLeft = NativeIcons.TabCapLeft;
        var capRight = NativeIcons.TabCapRight;
        var middle = NativeIcons.TabMiddle;
        var divider = NativeIcons.TabDivider;
        var indicator = NativeIcons.TabIndicator;

        // tab 区整体下移 4px（实测 tab 离输入框太近， 再 +1；tabBarHeight 已同步）
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f * scale);

        // 左帽：装饰 + 可拖动聊天框
        if (capLeft != null)
        {
            var start = ImGui.GetCursorScreenPos();
            drawList.AddImage(capLeft.Handle, start, start + capLeftSize);
            ImGui.Dummy(capLeftSize);
            ImGui.SameLine(0, 0);
        }

        // 局部函数：画一根分割线并推进光标
        void DrawDivider()
        {
            if (divider == null)
                return;
            var pos = ImGui.GetCursorScreenPos();
            drawList.AddImage(divider.Handle, pos, pos + dividerSize);
            ImGui.Dummy(dividerSize);
            ImGui.SameLine(0, 0);
        }

        // 左帽后第一根分割线（与 tab 之间同款）
        DrawDivider();

        // !!! tab 自适应长度：窗口面积不足（tab 条放不下全部完全展开的 tab）时按比例
        // 缩短中段宽度（左右帽/分隔线不动），文字随之缩减（私聊→私）；面积充足时 factor=1 原逻辑。
        // 字号 = tab 高 3/5 基准 × 字号比（文字大小独立设置；高度不随字号变）
        var effectiveFontSize = tabHeight * 0.6f * (Plugin.Config.TabFontSizePt / 12f);
        var oneCharW = effectiveFontSize * 0.5f;  // !!! 左右留白 = 半个字（一个字母），实测整字太大
        var availTabW = ImGui.GetContentRegionAvail().X;
        var plusTabW = Plugin.Config.HideNewTabButton ? 0f : ImGuiUtil.CalcIconButtonSize().X;
        var fixedTabW = capLeftSize.X + dividerSize.X + capRightSize.X + plusTabW;
        var fullTabWidths = new List<float>(tabs.Count);
        var sumFullTabW = 0f;
        foreach (var t in tabs)
        {
            if (t.PopOut)
                continue;
            var tw = Math.Max(middleBaseSize.X, ImGui.CalcTextSize(t.Name).X + oneCharW * 2);
            fullTabWidths.Add(tw);
            sumFullTabW += tw;
        }
        // 每中段后一根分隔线；预算 = 可用宽 - 固定开销，factor 下限 0.15（极端窄窗仍可点）
        var tabBudget = availTabW - fixedTabW - dividerSize.X * fullTabWidths.Count;
        var tabWidthFactor = tabBudget <= 0f ? 0.15f : Math.Min(1f, tabBudget / Math.Max(sumFullTabW, 1f));
        // 中段下限 = 单字 + 左右留白（文字至少 1 字）
        var minMidW = effectiveFontSize + oneCharW * 2;
        var tabWIdx = 0;

        var unreadGreen = UnreadColor();
        // Button 背景全透明（贴图自绘；hover 用 tint 提亮，不靠 Button 自带背景）
        var transparent = new Vector4(0, 0, 0, 0);
        using var btnBg = ImRaii.PushColor(ImGuiCol.Button, transparent);
        using var btnHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, transparent);
        using var btnActive = ImRaii.PushColor(ImGuiCol.ButtonActive, transparent);
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);

        for (var tabI = 0; tabI < tabs.Count; tabI++)
        {
            var tab = tabs[tabI];
            if (tab.PopOut)
                continue;

            var active = Plugin.LastTab == tabI;
            var hasUnread = !active && tab.UnreadMode != UnreadMode.None && tab.Unread > 0
                && Plugin.Config.UnreadNotifyMode != UnreadNotifyMode.None;

            // 中段 = 真正的 tab：宽度 = 文字 + 左右各留一个字空间（缩短中段）；
            // Button 只负责命中
            // !!! 面积不足时按 tabWidthFactor 压缩（下限 minMidW 至少 1 字 + 留白）
            var size = new Vector2(
                Math.Max(minMidW, fullTabWidths[tabWIdx] * tabWidthFactor),
                tabHeight);
            tabWIdx++;
            var clicked = ImGui.Button($"##bottom-tab-{tabI}", size);
            var btnMin = ImGui.GetItemRectMin();
            var btnMax = ImGui.GetItemRectMax();

            // !!! 长按拖出 v2：按住 ≥600ms 后**移动鼠标**才开始拖出
            //（原 v1 松手即弹突兀）。拖拽期间画 tab 幽灵跟随指针（见循环后），
            // 松手才创建 PopOut（定位在释放点）——期间不建窗，避免指针落在新窗口
            // 消息区上触发文本选中等干扰。
            var now = Environment.TickCount64;
            var leftDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            var mousePos = ImGui.GetIO().MousePos;
            var tabDown = ImGui.IsItemActive();
            var longPressed = false;

            // 按下（按住 tab 的第一帧）→ 记录时间/位置
            if (tabDown && !_tabPressStart.ContainsKey(tabI) && _draggingTabOut == null)
            {
                _tabPressStart[tabI] = now;
                _tabPressPos[tabI] = mousePos;
            }

            // 长按达标 + 鼠标移动（拖拽手势）→ 开始拖出（幽灵跟随；松手才建窗）
            if (_draggingTabOut == null
                && _tabPressStart.TryGetValue(tabI, out var downAt)
                && leftDown
                && now - downAt >= 600
                && (mousePos - _tabPressPos[tabI]).Length() > 10f * scale)
            {
                _draggingTabOut = tabI;
                longPressed = true;
            }

            // 松开 → 清理按下记录；拖出中的 tab 松手 = 拖出完成（记录释放点，建窗定位用）
            if (!leftDown && _tabPressStart.Remove(tabI))
            {
                _tabPressPos.Remove(tabI);
                if (_draggingTabOut == tabI)
                {
                    _draggingTabOut = null;
                    _popOutPlaceId = tab.Identifier;
                    _popOutPlacePos = ImGui.GetIO().MousePos;
                    tab.PopOut = true;  // AddPopOutsToDraw 下一帧创建 PopOut
                    longPressed = true; // 拖出非点击，跳过本帧切换
                }
            }

            if (middle != null)
            {
                drawList.AddImage(middle.Handle, btnMin, btnMax);

                // !!! hover 亮起：tint>1 被 ImU32 钳制（原 1.22 从未生效），改用
                // 半透明白雾；上下各缩 1.5px 贴合中段贴图视觉边界（左右不缩——指定）
                if (ImGui.IsItemHovered())
                {
                    Plugin.AnyInteractiveHovered = true;  // tab 也是可点击元素 → 手指光标
                    var glowMin = btnMin + new Vector2(0f, 1.5f) * scale;
                    var glowMax = btnMax - new Vector2(0f, 1.5f) * scale;
                    drawList.AddRectFilled(glowMin, glowMax, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f)), 3f);
                }

                // 选中态：左上角金色指示点（固定边距，不随 tab 宽度变化）
                if (active && indicator != null)
                {
                    var indMin = btnMin + indicatorOffset;
                    drawList.AddImage(indicator.Handle, indMin, indMin + indicatorSize);
                }
            }

            // !!! tab 字颜色：f5f3df → 略微加深 (238,236,215)（反馈"略深一点点"）；
            // 未读 tab 保持绿色
            var tabTextColor = hasUnread
                ? ImGui.GetColorU32(unreadGreen)
                : ImGui.GetColorU32(new Vector4(238f / 255f, 236f / 255f, 215f / 255f, 1f));
            var activeFont = ImGui.GetFont();
            // 压缩后宽度不足时文字渐进缩减（私聊→私）；宽度充足时原样
            var displayText = FitTabText(tab.Name, size.X - oneCharW * 2);
            var tabTextSize = ImGui.CalcTextSize(displayText);
            var fontScale = effectiveFontSize / activeFont.FontSize;
            // !!! 修复 v2：AddText pos = 文字顶（频道名 DrawChannelName 用 GetCursorScreenPos
            // 直接当顶，看正常——铁证）； 的 baseline 语义 + Ascent 项反而把文字压到
            // tab 下方（实测"超出下边 3/5"）。直接顶语义几何居中：
            // 文字顶 = tab 顶 + (tab高 - 字高)/2 - 2×fs（保留历史"上提 2px"视觉微调）
            var textPos = new Vector2(
                // 水平居中后往右 5px（+3 基础上再右移 2px）。
                // !!! Bug 修复：修正量必须 × TabScale——tab 随标签页缩放等比例变大后，
                // 绝对 5px 修正占比被淡化（文字又不居中），乘缩放保持相对比例
                btnMin.X + (btnMax.X - btnMin.X - tabTextSize.X) / 2f + 5f * scale * Plugin.Config.TabScale,
                btnMin.Y + (btnMax.Y - btnMin.Y - effectiveFontSize) / 2f - 2f * fontScale);
            // !!! 必须显式指定字体：AddText(pos,col,text) 重载会用窗口开始时的字体
            drawList.AddText(activeFont, effectiveFontSize, textPos, tabTextColor, displayText);

            DrawTabContextMenu(tab, tabI);

            ImGui.SameLine(0, 0);

            // 分割线在每个中段后（含最后一个——中段N与右帽之间也要；+ 前（右帽后）无）
            DrawDivider();

            // !!! 重构：点击切换逻辑抽公共 HandleTabClick（原生/非原生共用）；
            // !!! 长按拖出时（longPressed）跳过切换（只拖出不切 tab）
            if (!longPressed && HandleTabClick(tabI, clicked, tab))
                anyClicked = true;
        }

        // 安全清理：tab 被删除/索引变化或鼠标已松开时清掉拖出状态（防残留卡住）
        if (_draggingTabOut is { } staleIdx && (staleIdx >= tabs.Count || !ImGui.IsMouseDown(ImGuiMouseButton.Left)))
            _draggingTabOut = null;

        // !!! 拖出幽灵：拖拽期间在指针处画 tab 三段式跟随（半透明；松手才建窗，见 AddPopOutsToDraw）
        if (_draggingTabOut is { } ghostIdx && ghostIdx < tabs.Count && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var ghostTab = tabs[ghostIdx];
            var ghostPos = ImGui.GetIO().MousePos + new Vector2(8f * scale, 8f * scale);
            var ghostTint = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.75f));
            var fg = ImGui.GetForegroundDrawList();
            if (capLeft != null)
            {
                fg.AddImage(capLeft.Handle, ghostPos, ghostPos + capLeftSize, Vector2.Zero, Vector2.One, ghostTint);
                ghostPos.X += capLeftSize.X;
            }
            if (middle != null)
            {
                var ghostNameW = ImGui.CalcTextSize(ghostTab.Name).X;
                var ghostMidSize = new Vector2(Math.Max(middleBaseSize.X, ghostNameW + tabHeight * 0.6f * 0.5f * 2), tabHeight);
                fg.AddImage(middle.Handle, ghostPos, ghostPos + ghostMidSize, Vector2.Zero, Vector2.One, ghostTint);
                ghostPos.X += ghostMidSize.X;
            }
            if (capRight != null)
            {
                fg.AddImage(capRight.Handle, ghostPos, ghostPos + capRightSize, Vector2.Zero, Vector2.One, ghostTint);
                ghostPos.X += capRightSize.X;
            }
        }

        // 右帽：最右侧装饰（素材 47x51）
        if (capRight != null)
        {
            var end = ImGui.GetCursorScreenPos();
            drawList.AddImage(capRight.Handle, end, end + capRightSize);
            ImGui.Dummy(capRightSize);
            ImGui.SameLine(0, 0);
        }

        // 末尾"+"：原生加号图标（新素材 icon_11）；高度对齐原生 tab（51px×scale）。
        // 无按钮音（排除项"添加tab"）。可被"隐藏添加标签页按钮"选项隐藏
        if (!Plugin.Config.HideNewTabButton && ImGuiUtil.NativeIconButton(NativeIcons.Plus, "new-tab-bottom", null, FontAwesomeIcon.Plus,
                size: new Vector2(ImGuiUtil.CalcIconButtonSize().X, tabHeight), sfx: ImGuiUtil.BtnSfx.None))
        {
            NewTabName = string.Empty;
            ImGui.OpenPopup("chat2-new-tab-name");
        }

        DrawNewTabPopup();

        ImGui.NewLine();

        if (anyClicked)
            Plugin.WantedTab = null;
    }

        // !!! 重构：tab 双模式公共逻辑（原生/非原生共用，改一处即可）

    /// <summary>tab 文字按可用宽度渐进缩减（完整 → 逐字 → 单字）；宽度充足时返回原样。</summary>
    private static string FitTabText(string name, float availW)
    {
        if (availW <= 0f || string.IsNullOrEmpty(name))
            return name;
        if (ImGui.CalcTextSize(name).X <= availW)
            return name;
        for (var n = name.Length - 1; n >= 1; n--)
        {
            var sub = name[..n];
            if (ImGui.CalcTextSize(sub).X <= availW)
                return sub;
        }
        return name[..1];
    }

    /// <summary>tab 点击切换（音效 1 + 切换逻辑）；返回是否发生点击。</summary>
    private bool HandleTabClick(int tabI, bool clicked, Tab tab)
    {
        if (!clicked && Plugin.WantedTab != tabI)
            return false;
        // tab 切换音效 = 游戏原生频道切换 SFX 1（确认）
        if (Plugin.Config.PlaySounds)
            unsafe { UIGlobals.PlaySoundEffect(1); }
        var previousTab = Plugin.CurrentTab;
        // !!! hasTabSwitched 必须在本行前算：LastTab 已被赋值为 tabI 后再判断
        // `LastTab != tabI` 恒为 false → TabSwitched 永不执行 → 跨 tab 未读同步失效
        var hasTabSwitched = Plugin.WantedTab == tabI || Plugin.LastTab != tabI;
        Plugin.LastTab = tabI;
        tab.Unread = 0;
        if (hasTabSwitched)
            TabSwitched(tab, previousTab);
        return true;
    }

    /// <summary>新建 tab 弹窗（"+"/右键菜单新建共用）。</summary>
    private void DrawNewTabPopup()
    {
        using var namePopup = ImRaii.Popup("chat2-new-tab-name");
        if (!namePopup)
            return;
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

    private void DrawBottomTabBarLegacy()
        {
            var tabs = Plugin.Config.Tabs;
            var anyClicked = false;

            var style = ImGui.GetStyle();
            // 底部标签页文字用固定大小字体（TabFont，12pt），压缩短边高度
            // !!! tab 高度只随 TabScale：从含字号的行高反除字号比（TabFontSizePt/12），
            // "标签页名称文字大小"只改文字不改高度（高度已有 TabScale 管）
            using var tabFont = Plugin.FontManager.TabFont.Push();
            var tabHeight = (ImGui.GetTextLineHeight() / (Plugin.Config.TabFontSizePt / 12f) + style.FramePadding.Y * 2) * 0.9f;
            var drawList = ImGui.GetWindowDrawList();
            var dividerColor = ImGui.GetColorU32(new Vector4(0.25f, 0.25f, 0.25f, 0.55f));
            // 标签页背景透明度独立（TabAlpha，四透明度之一）
            var tabAlpha = Plugin.Config.TabAlpha / 100f;
            // 顶部模式：tab 栏与消息区同色（WindowAlpha 统一控制透明度），底部画分界线；
            // 底部模式：独立深色条（TabAlpha 控制）；CustomTabBg（仅非原生）优先用自定义 RGB
            var isTopTabs = !Plugin.Config.NativeBackground && Plugin.Config.TabPosition is TabPosition.Top;
            uint barBgColor;
            if (Plugin.Config.CustomTabBg && ColourUtil.RgbaToVector4(Plugin.Config.TabBgColor) is { } customTabBg)
            {
                // 透明度沿用当前模式规则：顶部=消息区同规则（WindowBg.alpha×WindowAlpha），
                // 底部=深色条的 TabAlpha 系数
                var barAlpha = isTopTabs
                    ? ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg].W * (Plugin.Config.WindowAlpha / 100f)
                    : 0.5f * tabAlpha;
                barBgColor = ImGui.GetColorU32(new Vector4(customTabBg.X, customTabBg.Y, customTabBg.Z, barAlpha));
            }
            else
            {
                barBgColor = isTopTabs
                    ? ImGui.GetColorU32(MessageLogBgColor())
                    : ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 0.5f * tabAlpha));
            }
            var activeColor = ImGui.GetColorU32(new Vector4(0.28f, 0.28f, 0.28f, 0.6f * tabAlpha));

            // 仿原生着色时只给 tab 本身颜色（不画整条背景）；普通模式保持整条背景条
            var barStart = ImGui.GetCursorScreenPos();
            if (!Plugin.Config.NativeBackground)
            {
                // !!! 顶部模式 tab 背景与消息区背景精确同宽：GetContentRegionAvail 比消息区
                // child 的 -1 宽多 1px（实测 749 vs 748）→ 用上一帧记录的消息区矩形宽保证平齐
                var msgW = LastMessageAreaMax.X - LastMessageAreaMin.X;
                var barWidth = isTopTabs && msgW > 0f ? msgW : ImGui.GetContentRegionAvail().X;
                // !!! 顶部模式：右上角磨圆上移（消息区右上角圆角让给 tab 条，8f 同款）；
                // 消息区顶边改直角后左右边缘与 tab 条平齐
                drawList.AddRectFilled(barStart, new Vector2(barStart.X + barWidth, barStart.Y + tabHeight), barBgColor, 8f,
                    isTopTabs ? ImDrawFlags.RoundCornersTopRight : ImDrawFlags.None);
                // 顶部模式：tab 栏底边画分界线（与消息区相接处 2px 亮线）
                if (isTopTabs)
                {
                    var sepColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.18f));
                    drawList.AddRectFilled(
                        new Vector2(barStart.X, barStart.Y + tabHeight - 2f),
                        new Vector2(barStart.X + barWidth, barStart.Y + tabHeight),
                        sepColor);
                }
            }

            var unreadGreen = UnreadColor();

            // !!! tab 自适应长度（同原生）：窗口面积不足时按比例缩短 tab 宽（仅长度），
            // 文字随之缩减（私聊→私）；面积充足时 factor=1 原逻辑
            var availTabW = ImGui.GetContentRegionAvail().X;
            var plusTabW = Plugin.Config.HideNewTabButton ? 0f : ImGuiUtil.CalcIconButtonSize().X;
            var fullTabWidths = new List<float>(tabs.Count);
            var sumFullTabW = 0f;
            foreach (var t in tabs)
            {
                if (t.PopOut)
                    continue;
                var tw = ImGui.CalcTextSize(t.Name).X + style.FramePadding.X * 2 + 20f;
                fullTabWidths.Add(tw);
                sumFullTabW += tw;
            }
            // 分隔线 2px 只画在 tab 之间（first 之后的 tab 前）；预算 = 可用宽 - "+" - 分隔线
            var legacyDividerTotal = Math.Max(0, fullTabWidths.Count - 1) * 2f;
            var legacyBudget = availTabW - plusTabW - legacyDividerTotal;
            var tabWidthFactor = legacyBudget <= 0f ? 0.15f : Math.Min(1f, legacyBudget / Math.Max(sumFullTabW, 1f));
            // 下限 = 单字 + padding + 微留白（文字至少 1 字）
            var minTabW = ImGui.CalcTextSize("字").X + style.FramePadding.X * 2 + 6f;
            var tabWIdx = 0;

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

                // 面积不足时按 tabWidthFactor 压缩（下限 minTabW 至少 1 字可点）
                var size = new Vector2(
                    Math.Max(minTabW, fullTabWidths[tabWIdx] * tabWidthFactor),
                    tabHeight);
                tabWIdx++;
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
                // 压缩后宽度不足时文字渐进缩减（私聊→私）；宽度充足时原样
                var displayText = FitTabText(tab.Name, size.X - style.FramePadding.X * 2);
                var tabTextSize = ImGui.CalcTextSize(displayText);
                // 垂直居中：CJK 字形视觉中心 ≈ baseline − FontSize × 0.38，再上提 5px（实测校准）
                // 用生效尺寸（随 UI 缩放）渲染 tab 文字：与 tab 框（GetTextLineHeight 随 UI 缩放）保持一致
                var effectiveFontSize = ImGui.GetFontSize();
                var fontScale = effectiveFontSize / activeFont.FontSize;
                var textPos = new Vector2(
                    btnMin.X + (btnMax.X - btnMin.X - tabTextSize.X) / 2f,
                    btnMin.Y + (btnMax.Y - btnMin.Y) / 2f - activeFont.Ascent * fontScale + effectiveFontSize * 0.38f - 2f * fontScale);
                // !!! 必须显式指定字体：AddText(pos,col,text) 重载会用窗口开始时的字体；传 FontSize 不随 UI 缩放
                drawList.AddText(activeFont, effectiveFontSize, textPos, ImGui.GetColorU32(ImGuiCol.Text), displayText);

                DrawTabContextMenu(tab, tabI);

                ImGui.SameLine(0, 0);

                // !!! 重构：点击切换逻辑抽公共 HandleTabClick（原生/非原生共用）
                if (HandleTabClick(tabI, clicked, tab))
                    anyClicked = true;
            }

            // 末尾"+"：用 IconButton（无边框图标按钮，与输入框右侧齿轮/新人频道一致），
            // 字号 FontAwesomeTab（随"标签页缩放"字体重建，v1.40.17+ 与输入区图标拆分）。
            // 可被"隐藏添加标签页按钮"选项隐藏
            ImGui.SameLine(0, 0);
            if (!Plugin.Config.HideNewTabButton && ImGuiUtil.IconButton(FontAwesomeIcon.Plus, "new-tab-bottom", font: Plugin.FontManager.FontAwesomeTab))
            {
                NewTabName = string.Empty;
                ImGui.OpenPopup("chat2-new-tab-name");
            }

            DrawNewTabPopup();

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
            // 垂直居中：CJK 字形视觉中心 ≈ baseline − FontSize × 0.38，再上提 5px（实测校准）
            // 用生效尺寸（随 UI 缩放）渲染 tab 文字：与 tab 框（GetTextLineHeight 随 UI 缩放）保持一致
            var effectiveFontSize = ImGui.GetFontSize();
            var fontScale = effectiveFontSize / activeFont.FontSize;
            var textPos = new Vector2(
                btnMin.X + (btnMax.X - btnMin.X - tabTextSize.X) / 2f,
                btnMin.Y + (btnMax.Y - btnMin.Y) / 2f - activeFont.Ascent * fontScale + effectiveFontSize * 0.38f - 2f * fontScale);
            // !!! 必须显式指定字体：AddText(pos,col,text) 重载会用窗口开始时的字体；传 FontSize 不随 UI 缩放
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
    /// 荧光绿 + 呼吸灯。
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

        // 图标用 FontAwesomeSmall（12px），与输入框文字大小协调（反馈原图标偏大）
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

            if (PopOutInstances.ContainsKey(tab.Identifier))
                continue;

            var window = new Popout(Plugin, tab, i);

            // !!! 长按拖出：窗口建在释放点（跟随指针拖出后松手定位，不突兀）。
            // !!! 修复①：PositionCondition 必须 Once——默认 0 会被 ImGui 当 Always，
            // 每帧 SetNextWindowPos 强制回释放点 → 窗口"钉死"不可拖动（实测根因）。
            // !!! 修复②：释放点做视口限制——靠近屏幕边缘时窗口被裁（底部 tab 行
            // 出屏 → tab 文字看起来"错位"），钳制到视口内保证整体可见
            if (_popOutPlaceId == tab.Identifier)
            {
                var vp = ImGuiHelpers.MainViewport;
                var winSize = new Vector2(350f, 350f) * ImGuiHelpers.GlobalScale;  // Popout 默认尺寸
                var pos = _popOutPlacePos;
                pos.X = Math.Clamp(pos.X, vp.Pos.X, vp.Pos.X + Math.Max(0f, vp.Size.X - winSize.X));
                pos.Y = Math.Clamp(pos.Y, vp.Pos.Y, vp.Pos.Y + Math.Max(0f, vp.Size.Y - winSize.Y));
                window.Position = pos;
                window.PositionCondition = ImGuiCond.Once;
                _popOutPlaceId = null;
            }

            Plugin.WindowSystem.AddWindow(window);
            PopOutInstances[tab.Identifier] = window;
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
