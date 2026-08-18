using System.Numerics;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Ui.ChatLog;
using ChatTwo.Ui.Handler;
using ChatTwo.Util;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Lumina.Extensions;

namespace ChatTwo.Ui;

public class Popout : Window, IChatWindow
{
    private readonly Plugin Plugin;
    private readonly Tab Tab;
    private readonly int Idx;

    private long FrameTime; // set every frame
    private long LastActivityTime = Environment.TickCount64;

    private readonly string ChatChannelPicker = "chat-popout-channel-picker";

    public readonly InputHandler InputHandler;

    public Vector2 LastWindowPos { get; set; } = Vector2.Zero;
    public Vector2 LastWindowSize { get; set; } = Vector2.Zero;
    public HideState CurrentHideState { get; set; } = HideState.None;

    // 仿原生金字塔缩放手柄（与主窗口一致）：拖拽右上角手柄缩放
    private bool IsResizingTopRight;
    private Vector2 ResizeStartMousePos;
    private Vector2 ResizeStartWindowPos;
    private Vector2 ResizeStartWindowSize;
    private bool MouseOverResizeHandle;

    // 快捷锁定：选字时锁定窗口移动（防止拖拽选字误拖动窗口）
    public bool MoveLocked;

    // 消息区屏幕矩形（上一帧 DrawMessageLog 记录，PopOut 自己的矩形——与主窗口隔离，
    // 共享 DrawMessageLog 时最后 Draw 的窗口会覆盖 ChatLog 上的字段，不能互读）
    public Vector2 LastMessageAreaMin = Vector2.Zero;
    public Vector2 LastMessageAreaMax = Vector2.Zero;

    // 消息区交互状态（独立于主窗口，互不干扰）
    public readonly MessageLogState MsgState = new();

    public Popout(Plugin plugin, Tab tab, int idx) : base($"{tab.Name}##popout")
    {
        Plugin = plugin;
        Tab = tab;
        Idx = idx;

        InputHandler = new InputHandler(this, plugin, $"ChatLog{idx}-{tab.Name}");

        Size = new Vector2(350, 350);
        SizeCondition = ImGuiCond.FirstUseEver;

        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        ChatChannelPicker += $"-{idx}-{tab.Name}";
    }

    public override void PreOpenCheck()
    {
        if (!Tab.PopOut)
            IsOpen = false;
    }

    public override bool DrawConditions()
    {        FrameTime = Environment.TickCount64;

        var isHidden = Tab.IndependentHide
            ? HideStateHelper.HideStateCheck(this, Tab.HideInBattle, Tab.HideDuringCutscenes, Tab.HideWhenNotLoggedIn, false)
            : Plugin.ChatLog.IsHidden;

        if (isHidden)
            return false;

        if (!Plugin.Config.HideWhenInactive || (!Plugin.Config.InactivityHideActiveDuringBattle && Plugin.InBattle) || !Tab.UnhideOnActivity)
        {
            LastActivityTime = FrameTime;
            return true;
        }

        // Activity in the tab, this popout window, or the main chat log window.
        var lastActivityTime = Math.Max(Tab.LastActivity, LastActivityTime);
        lastActivityTime = Math.Max(lastActivityTime, InputHandler.LastActivityTime);
        return FrameTime - lastActivityTime <= 1000 * Plugin.Config.InactivityHideTimeout;
    }

    /// <summary>鼠标是否在 PopOut 消息区矩形内（消息区永远不可拖）。矩形由 DrawMessageLog 每帧回调记录。</summary>
    public bool IsMouseOverMessageAreaPublic()
    {
        var mp = ImGui.GetIO().MousePos;
        return mp.X >= LastMessageAreaMin.X && mp.X <= LastMessageAreaMax.X
            && mp.Y >= LastMessageAreaMin.Y && mp.Y <= LastMessageAreaMax.Y;
    }

    public override void PreDraw()
    {
        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Push();

        Flags = ImGuiWindowFlags.None;
        // 与主窗口一致：窗口自身从不滚动（滚动全在消息区 child 内）——否则内容溢出会出现
        // ImGui 原生右滚动条（与我们的左滚动条并存）；NoResize 永禁原生缩放（右下角 grip），
        // 缩放走自定义金字塔手柄
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing;
        Flags |= ImGuiWindowFlags.NoResize;

        if (!Plugin.Config.ShowPopOutTitleBar)
            Flags |= ImGuiWindowFlags.NoTitleBar;

        // !!! 消息区任何情况下都不可拖（不依赖锁定开关），
        // NoMove 只禁窗口拖动、不影响文本选取；未锁定时其余区域可拖；
        // 打开"锁定窗口移动"后整个窗口锁死。
        // !!! 矩形用 PopOut 自己的（不能读 Plugin.ChatLog 的——共享 DrawMessageLog，
        // 主窗口最后画会覆盖 PopOut 的矩形）。
        // MoveLocked 从设置页读取（Config.MoveLocked，锁按钮移除后改设置项）。
        if (IsMouseOverMessageAreaPublic() || Plugin.Config.MoveLocked)
            Flags |= ImGuiWindowFlags.NoMove;

        if (!Tab.CanResize)
            Flags |= ImGuiWindowFlags.NoResize;

        if (Tab.CanResize)
        {
            // 原生缩放手柄 hit-test（偏移与 DrawTopRightResizeHandle 绘制一致）：
            // 手柄上时阻止窗口移动（拖手柄 = 缩放，不拖动）
            var st = ImGui.GetStyle();
            var hSize = NativeIcons.ResizeHandleSize();  // !!! 原生手柄素材尺寸
            var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
            var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
            var handleMin = new Vector2(
                LastWindowPos.X + LastWindowSize.X - hSize - st.WindowPadding.X - insetX,
                LastWindowPos.Y + st.WindowPadding.Y + insetY);
            var handleMax = handleMin + new Vector2(hSize, hSize);
            var mp = ImGui.GetIO().MousePos;
            MouseOverResizeHandle = mp.X >= handleMin.X && mp.X <= handleMax.X
                                  && mp.Y >= handleMin.Y && mp.Y <= handleMax.Y;
            if (IsResizingTopRight || MouseOverResizeHandle)
                Flags |= ImGuiWindowFlags.NoMove;
        }

        if (!Plugin.ChatLog.PopOutDocked[Idx])
        {
            // 背景透明度独立（BackgroundAlpha，四透明度之一）；PopOut 统一跟随主窗口。
            // !!! 修复：BgAlpha 是可空 float?，null=不透明背景——必须显式 0（停靠时保持不透明）
            BgAlpha = 0f;
            // 仿原生：窗口整体透明（背景只画在消息区，与主窗口一致）

        }

        // !!! [CtxClickPass] 菜单打开期间：PopOut 窗口也不捕获鼠标（NoMouseInputs）→
        // 与主窗口一致：菜单若在 PopOut 上打开，鼠标穿透到游戏原生菜单可点击。
        // （child 的 NoMouseInputs 由共享的 DrawMessageLog 内部逻辑覆盖，无需在此处理。）
        if (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession)
            Flags |= ImGuiWindowFlags.NoMouseInputs;
    }

    public override void Draw()
    {
        // !!! 鼠标在聊天窗口内 → 帧末光标决策（保持游戏指针；按钮/tab 上手指）
        Plugin.MarkCursorInChatWindow();

        // 弹出的聊天窗口内容与主窗口一致：默认 Axis，选了自定义字体后改 RegularFont
        using var mainFont = (Plugin.Config.FontsEnabled ? Plugin.FontManager.RegularFont : Plugin.FontManager.Axis).Push();
        using var id = ImRaii.PushId($"popout-{Tab.Identifier}");

        LastWindowSize = ImGui.GetWindowSize();
        LastWindowPos = ImGui.GetWindowPos();

        // 滚轮接管（与主窗口一致）：记录滚轮值并清零 IO，消息区 child 手动按 1 行滚
        MsgState.UserScrolled = false;
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

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            LastActivityTime = FrameTime;

        // PopOut 无输入区（游戏原生弹出的消息窗口本来就不能输入）：消息区 + 底部 tab 行
        var remainingHeight = ImGui.GetContentRegionAvail().Y - PopOutTabBarHeight();

        Plugin.ChatLog.DrawMessageLog(Tab, InputHandler.PayloadHandler, remainingHeight, false, MsgState,
            onMessageArea: (min, max) => { LastMessageAreaMin = min; LastMessageAreaMax = max; });

        // 底部行：左下角 tab 名 + 右侧 锁定/关闭
        DrawPopOutTabBar();

        // 仿原生金字塔缩放手柄（与主窗口一致）：右上角手柄拖拽缩放
        if (Tab.CanResize)
        {
            DrawTopRightResizeHandle();

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
                // Top-right handle: keep BOTTOM-LEFT corner fixed
                var newPos = new Vector2(ResizeStartWindowPos.X, ResizeStartWindowPos.Y + delta.Y);
                var newSize = new Vector2(
                    Math.Max(80f, ResizeStartWindowSize.X + delta.X),
                    Math.Max(80f, ResizeStartWindowSize.Y - delta.Y));
                ImGui.SetWindowPos(newPos);
                ImGui.SetWindowSize(newSize);

                if (!leftDown)
                    IsResizingTopRight = false;
            }
        }
    }

    // 底部 tab 栏高度（一行，与主窗口 tab 同尺寸基准）
    private float PopOutTabBarHeight()
    {
        // !!! 原生模式：与主窗口 tab 高度一致（17px×scale×TabScale，v1.40.17+ 拆分）
        if (Plugin.Config.NativeBackground)
            return 17f * ImGuiHelpers.GlobalScale * Plugin.Config.TabScale;

        using var tabFont = Plugin.FontManager.TabFont.Push();
        var style = ImGui.GetStyle();
        return (ImGui.GetTextLineHeight() + style.FramePadding.Y * 2) * 0.9f;
    }

        // 底部行：左下角 tab 名（像 tab 标签）+ 右侧 关闭 按钮。
        // 锁定按钮已移除（锁定改到设置页，且只锁消息区）。
        // 关闭是窗口级按钮，放这里两种模式（有无输入区）都可见。
        // !!! 无 Separator：实测分割线会让 tab 行下移被底部截断
        private void DrawPopOutTabBar()
        {
            // !!! 原生模式：tab 也用左帽+中段+右帽（与主窗口一致），关闭按钮换原生图片
            if (Plugin.Config.NativeBackground)
            {
                DrawPopOutTabBarNative();
                return;
            }

            using var tabFont = Plugin.FontManager.TabFont.Push();

            var lineHeight = ImGui.GetTextLineHeight();
            var availWidth = ImGui.GetContentRegionAvail().X;
            var iconSize = ImGui.GetFrameHeight();
            var nameWidth = ImGui.CalcTextSize(Tab.Name).X;
            var maxNameWidth = Math.Max(0f, availWidth - iconSize * 2 - ImGui.GetStyle().ItemSpacing.X * 2 - 8f);

            ImGui.TextUnformatted(nameWidth > maxNameWidth && maxNameWidth > 8f ? Tab.Name[..Math.Max(1, Tab.Name.Length * (int)(maxNameWidth / nameWidth))] : Tab.Name);

            // 右侧：关闭（收回主窗口）
            ImGui.SameLine();
            ImGui.SetCursorPosX(availWidth - iconSize - ImGui.GetStyle().ItemSpacing.X);
            var iconTop = ImGui.GetCursorPosY() + (lineHeight - iconSize) / 2f;
            ImGui.SetCursorPosY(iconTop);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, font: Plugin.FontManager.FontAwesomeSmall))
                IsOpen = false; // 触发 OnClose：Tab.PopOut=false，tab 收回主窗口
            if (ImGui.IsItemHovered())
                ImGuiUtil.Tooltip("关闭");
        }

        /// <summary>原生模式 tab 行：左帽 + 中段(名称) + 右帽 + 原生关闭按钮
        /// （尺寸/文字公式/hover 亮起与主窗口 DrawBottomTabBar 完全一致）。</summary>
        private void DrawPopOutTabBarNative()
        {
            using var tabFont = Plugin.FontManager.TabFont.Push();

            var scale = ImGuiHelpers.GlobalScale;
            var cfgTabScale = Plugin.Config.TabScale;  // !!! v1.40.17+ 标签页缩放与输入区缩放拆分
            var tabHeight = 17f * scale * cfgTabScale;
            var tabScale = tabHeight / 48f;  // 素材原始高 48px → 目标高度
            var capLeftSize = new Vector2(39f, 48f) * tabScale;
            var capRightSize = new Vector2(40f, 48f) * tabScale;
            var middleBaseSize = new Vector2(50f, 48f) * tabScale;
            var effectiveFontSize = tabHeight * 0.6f;  // 字号 = tab 高 3/5（与主窗口一致）
            var oneCharW = effectiveFontSize * 0.5f;   // 左右留白 = 半字（一个字母）
            var dl = ImGui.GetWindowDrawList();
            var tabTextColor = ImGui.GetColorU32(new Vector4(238f / 255f, 236f / 255f, 215f / 255f, 1f));
            // !!! 完整行宽必须在画 tab 组装前取（GetContentRegionAvail 是"光标到右缘"的剩余量）
            var availWidth = ImGui.GetContentRegionAvail().X;

            // 左帽：装饰
            var capLeft = NativeIcons.TabCapLeft;
            if (capLeft != null)
            {
                var start = ImGui.GetCursorScreenPos();
                dl.AddImage(capLeft.Handle, start, start + capLeftSize);
                ImGui.Dummy(capLeftSize);
                ImGui.SameLine(0, 0);
            }

            // 中段（真正的 tab：名称 + 左右各半字，下限素材基础宽）
            var middle = NativeIcons.TabMiddle;
            var nameWidth = ImGui.CalcTextSize(Tab.Name).X;
            var mSize = new Vector2(Math.Max(middleBaseSize.X, nameWidth + oneCharW * 2), tabHeight);
            var mMin = ImGui.GetCursorScreenPos();
            if (middle != null)
            {
                dl.AddImage(middle.Handle, mMin, mMin + mSize);
                ImGui.Dummy(mSize);
                if (ImGui.IsItemHovered())
                {
                    Plugin.AnyInteractiveHovered = true;  // 可点击元素 → 游戏手指光标
                    var glowMin = mMin + new Vector2(0f, 1.5f) * scale;
                    var glowMax = mMin + mSize - new Vector2(0f, 1.5f) * scale;
                    dl.AddRectFilled(glowMin, glowMax, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f)), 3f);
                }
                // 文字（与主窗口同公式：AddText pos=顶，几何居中 + 右移 5px×scale×cfgTabScale）
                // !!! v2：去掉 baseline 的 Ascent 项（版文字被压到 tab 下方）
                var activeFont = ImGui.GetFont();
                var textSize = ImGui.CalcTextSize(Tab.Name);
                var fontScale = effectiveFontSize / activeFont.FontSize;
                var textPos = new Vector2(
                    mMin.X + (mSize.X - textSize.X) / 2f + 5f * scale * cfgTabScale,
                    mMin.Y + (mSize.Y - effectiveFontSize) / 2f - 2f * fontScale);
                dl.AddText(activeFont, effectiveFontSize, textPos, tabTextColor, Tab.Name);
            }
            else
            {
                // 素材缺失回退：纯文本
                ImGui.TextUnformatted(Tab.Name);
            }
            ImGui.SameLine(0, 0);

            // 右帽：装饰
            var capRight = NativeIcons.TabCapRight;
            if (capRight != null)
            {
                var end = ImGui.GetCursorScreenPos();
                dl.AddImage(capRight.Handle, end, end + capRightSize);
                ImGui.Dummy(capRightSize);
                ImGui.SameLine(0, 0);
            }

            // 右侧：原生关闭按钮（收回主窗口；尺寸与主窗口图标按钮一致）
            var btnSize = new Vector2(ImGuiUtil.CalcIconButtonSize().X, tabHeight);
            ImGui.SetCursorPosX(availWidth - btnSize.X - ImGui.GetStyle().ItemSpacing.X);
            if (ImGuiUtil.NativeIconButton(NativeIcons.Close, "popout-close", "关闭", FontAwesomeIcon.Times,
                    size: btnSize, sfx: ImGuiUtil.BtnSfx.Dismiss))
                IsOpen = false; // 触发 OnClose：Tab.PopOut=false，tab 收回主窗口
        }

    // 原生缩放手柄素材（常态/高亮态，与主窗口同款；wrap 缺失回退金字塔）
    private void DrawTopRightResizeHandle()
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var style = ImGui.GetStyle();
        var hSize = NativeIcons.ResizeHandleSize();  // !!! 原生手柄素材尺寸
        var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
        var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
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

        // !!! 绘制已移至 PostDraw（前台 dl 置顶）；这里只保留 hit-test（否则出现双手柄）
    }

    public override void PostDraw()
    {
        Plugin.ChatLog.PopOutDocked[Idx] = ImGui.IsWindowDocked();

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Pop();

        // !!! 缩放手柄置顶（前台 dl；!!! End 后 GetWindowPos 不可靠 → 用 LastWindowPos）
        if (Tab.CanResize)
        {
            var style = ImGui.GetStyle();
            var hSize = NativeIcons.ResizeHandleSize();
            var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
            var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
            var localPos = new Vector2(
                LastWindowSize.X - hSize - style.WindowPadding.X - insetX,
                style.WindowPadding.Y + insetY);
            NativeIcons.DrawResizeHandle(ImGui.GetForegroundDrawList(), LastWindowPos + localPos,
                new Vector2(hSize, hSize), MouseOverResizeHandle || IsResizingTopRight);
        }
    }

    public override void OnClose()
    {
        // !!! 长按拖出 v2：HashSet → Dictionary（PopOutInstances）
        Plugin.ChatLog.PopOutInstances.Remove(Tab.Identifier);
        Plugin.WindowSystem.RemoveWindow(this);

        Tab.PopOut = false;
        Plugin.SettingsWindow.SyncTabPopOut(Tab.Identifier, false); // 与设置 Mutable 同步，防保存覆盖
        Plugin.SaveConfig();
    }

    private Dictionary<string, InputChannel> GetValidPopupChannels()
    {
        var channels = new Dictionary<string, InputChannel>();
        foreach (var channel in Enum.GetValues<InputChannel>())
        {
            if (channel is InputChannel.Invalid or InputChannel.Tell)
                continue;

            var name = Sheets.LogFilterSheet.FirstOrNull(row => row.LogKind == (byte) channel.ToChatType())?.Name.ToString() ?? channel.ToChatType().Name();
            if (channel.IsLinkshell())
            {
                var lsName = GameFunctions.Chat.GetLinkshellName(channel.LinkshellIndex());
                if (string.IsNullOrWhiteSpace(lsName))
                    continue;

                name += $": {lsName}";
            }

            if (channel.IsCrossLinkshell())
            {
                var lsName = GameFunctions.Chat.GetCrossLinkshellName(channel.LinkshellIndex());
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
}
