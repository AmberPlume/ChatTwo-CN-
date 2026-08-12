using System.Numerics;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Ui.ChatLog;
using ChatTwo.Ui.Handler;
using ChatTwo.Util;
using Dalamud.Interface.Style;
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

        if (MoveLocked)
            Flags |= ImGuiWindowFlags.NoMove;

        if (!Tab.CanResize)
            Flags |= ImGuiWindowFlags.NoResize;

        if (Tab.CanResize)
        {
            // 仿原生缩放手柄 hit-test（偏移与 DrawTopRightResizeHandle 绘制一致）：
            // 手柄上时阻止窗口移动（拖手柄 = 缩放，不拖动）
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
            if (IsResizingTopRight || MouseOverResizeHandle)
                Flags |= ImGuiWindowFlags.NoMove;
        }

        if (!Plugin.ChatLog.PopOutDocked[Idx])
        {
            // 背景透明度独立（BackgroundAlpha，四透明度之一）；PopOut 统一跟随主窗口。
            // 仿原生：窗口整体透明（背景只画在消息区，与主窗口一致）
            BgAlpha = Plugin.Config.NativeBackground ? 0f : Plugin.Config.BackgroundAlpha / 100f;
        }
    }

    public override void Draw()
    {
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

        Plugin.ChatLog.DrawMessageLog(Tab, InputHandler.PayloadHandler, remainingHeight, false, MsgState);

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
        using var tabFont = Plugin.FontManager.TabFont.Push();
        var style = ImGui.GetStyle();
        return (ImGui.GetTextLineHeight() + style.FramePadding.Y * 2) * 0.9f;
    }

    // 底部行：左下角 tab 名（像 tab 标签）+ 右侧 锁定/关闭 按钮。
    // 锁定与关闭是窗口级按钮，放这里两种模式（有无输入区）都可见。
    // ⚠️ 无 Separator：用户实测分割线会让 tab 行下移被底部截断
    private void DrawPopOutTabBar()
    {
        using var tabFont = Plugin.FontManager.TabFont.Push();

        var lineHeight = ImGui.GetTextLineHeight();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var iconSize = ImGui.GetFrameHeight();
        var nameWidth = ImGui.CalcTextSize(Tab.Name).X;
        var maxNameWidth = Math.Max(0f, availWidth - iconSize * 2 - ImGui.GetStyle().ItemSpacing.X * 2 - 8f);

        ImGui.TextUnformatted(nameWidth > maxNameWidth && maxNameWidth > 8f ? Tab.Name[..Math.Max(1, Tab.Name.Length * (int)(maxNameWidth / nameWidth))] : Tab.Name);

        // 右侧：锁定（选字时锁定窗口移动）+ 关闭（收回主窗口）
        ImGui.SameLine();
        ImGui.SetCursorPosX(availWidth - iconSize * 2 - ImGui.GetStyle().ItemSpacing.X);
        var iconTop = ImGui.GetCursorPosY() + (lineHeight - iconSize) / 2f;
        ImGui.SetCursorPosY(iconTop);
        if (ImGuiUtil.IconButton(MoveLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock, font: Plugin.FontManager.FontAwesomeSmall))
            MoveLocked = !MoveLocked;
        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip(MoveLocked ? "解锁窗口移动" : "锁定窗口移动");

        ImGui.SameLine();
        ImGui.SetCursorPosY(iconTop);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, font: Plugin.FontManager.FontAwesomeSmall))
            IsOpen = false; // 触发 OnClose：Tab.PopOut=false，tab 收回主窗口
        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip("关闭");
    }

    // 仿原生 FFXIV 缩放手柄：金字塔形三条 NW-SE 斜线（与主窗口同款）
    private void DrawTopRightResizeHandle()
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var style = ImGui.GetStyle();
        const float hSize = 16f;
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
        var lineColor = hovered || IsResizingTopRight
            ? ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.8f))
            : ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.4f));
        const float thickness = 2f;
        var p = windowPos + localPos;
        drawList.AddLine(p + new Vector2(10f, 2f), p + new Vector2(15f, 7f), lineColor, thickness);
        drawList.AddLine(p + new Vector2(4f, 2f), p + new Vector2(15f, 13f), lineColor, thickness);
        drawList.AddLine(p + new Vector2(-2f, 2f), p + new Vector2(15f, 19f), lineColor, thickness);
    }

    public override void PostDraw()
    {
        Plugin.ChatLog.PopOutDocked[Idx] = ImGui.IsWindowDocked();

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Pop();
    }

    public override void OnClose()
    {
        Plugin.ChatLog.PopOutWindows.Remove(Tab.Identifier);
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
