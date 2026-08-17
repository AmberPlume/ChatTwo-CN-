using System.Numerics;
using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Ui.ChatLog;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

/// <summary>
/// 聊天记录窗口（QQ 历史记录风格，2026-08-17 v3 定稿）。
/// 形态：仿 PopOut 的弹出窗口（无标题栏、仿原生透明背景、右上角金字塔缩放手柄），
///       顶栏 = 搜索框 + 玩家/日期筛选按钮，右侧 = 筛选面板（玩家列表 / 日历）。
/// 交互：搜索玩家/关键词 → 结果列表 → 点击定位 → 用 DrawMessageLog 复用原生渲染
///       显示该消息前后的完整上下文（100% 仿原生：时间戳/发送者/颜色/换行）。
/// 频道筛选：套用当前 Tab 的 SelectedChannels。
/// 入口：聊天框工具栏放大镜按钮。
/// </summary>
public class SearchWindow : Window
{
    /// <summary>定位消息前后各加载 N 分钟（稀疏时自动扩大）。</summary>
    private const int ContextWindowMinutes = 20;
    private const int ContextMaxExpand = 4;
    /// <summary>上下文最多保留条数（前后各 200）。</summary>
    private const int ContextMaxMessages = 401;
    /// <summary>玩家列表上限（从当前结果集提取，避免性能问题）。</summary>
    private const int MaxPlayers = 200;

    private enum Mode
    {
        Browse,  // 按日期浏览（原生渲染）
        Results, // 搜索结果列表（可点击定位）
        Context, // 定位后的上下文流（原生渲染）
    }

    private enum SidePanel
    {
        None,
        Players,
        Calendar,
    }

    private static readonly DateTime MinimalDate = new(2021, 1, 1);

    private readonly Plugin Plugin;

    // —— 虚拟 tab + 渲染状态（复用 DrawMessageLog 原生渲染） ——
    private readonly Tab DisplayTab = new()
    {
        Name = "聊天记录",
        DisplayTimestamp = true,
    };
    private readonly MessageLogState State = new();

    // —— 模式 / 筛选 ——
    private Mode CurrentMode = Mode.Browse;
    private SidePanel Panel = SidePanel.None;
    private string SearchTerm = "";
    private string PlayerFilter = "";
    private string ChannelTabName = "";   // 频道来源 Tab 名（右键选择，空=当前 Tab）
    private DateTime BrowseDate = DateTime.Today;
    private DateTime CalendarMonth = DateTime.Today;
    // ⚠️ 2026-08-17 用户决策：锁按钮移除，MoveLocked 从设置页读取（Config.MoveLocked）
    private bool MoveLocked => Plugin.Config.MoveLocked;

    // 消息区屏幕矩形（上一帧 DrawMessageLog 回调记录，本窗口自己的——不污染 ChatLog 字段）
    private Vector2 LastMessageAreaMin = Vector2.Zero;
    private Vector2 LastMessageAreaMax = Vector2.Zero;

    // 消息区容器（##history-area child）矩形：缩放手柄锚点（2026-08-17 用户决策：手柄移到消息区右上角）
    private Vector2 MsgAreaMin = Vector2.Zero;
    private Vector2 MsgAreaMax = Vector2.Zero;

    // —— 无限滚动（默认模式）：滚动到顶自动加载上一天 ——
    private bool DateLocked;             // true=固定日期浏览；false=滚动模式（默认）
    private DateTime LoadStartDate;      // 滚动模式已加载的最早日期

    private bool FocusSearchBar = true;

    // —— 数据 ——
    private bool IsLoading;
    private Message[] Loaded = [];        // 异步加载结果（加载完成后注入 DisplayTab）
    private Guid? PendingScrollTo;
    private bool PendingApply;            // 加载完成，待注入渲染
    private Message[] ShownMessages = []; // 当前展示的消息集（供玩家列表提取）

    // —— 缩放手柄（右上角，仿 PopOut） ——
    private bool IsResizingTopRight;
    private bool MouseOverResizeHandle;
    private Vector2 ResizeStartMousePos;
    private Vector2 ResizeStartWindowPos;
    private Vector2 ResizeStartWindowSize;

    public SearchWindow(Plugin plugin) : base(Language.Search_Title + "###chat2-search")
    {
        Plugin = plugin;

        Size = new Vector2(460, 520);
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        IsOpen = false;
    }

    /// <summary>鼠标是否在消息区矩形内（消息区永远不可拖，不依赖锁定开关）。矩形由 DrawMessageLog 每帧回调记录。</summary>
    private bool IsMouseOverMessageArea()
    {
        var mp = ImGui.GetIO().MousePos;
        return mp.X >= LastMessageAreaMin.X && mp.X <= LastMessageAreaMax.X
            && mp.Y >= LastMessageAreaMin.Y && mp.Y <= LastMessageAreaMax.Y;
    }

    public override void PreDraw()
    {
        // 仿原生：无标题栏；窗口自身不滚动（滚动在消息区 child 内）
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar
              | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing
              | ImGuiWindowFlags.NoResize;
        BgAlpha = 0f; // ⚠️ 04:10 float? null=不透明，必须显式 0


        // ⚠️ 2026-08-17 用户决策（17:38 纠正）：消息区任何情况下都不可拖（不依赖锁定开关），
        // 未锁定时其余区域可拖；锁定时整个窗口锁死。缩放手柄的 NoMove 由下方 CanResize 分支处理。
        if (IsMouseOverMessageArea() || MoveLocked)
            Flags |= ImGuiWindowFlags.NoMove;

        // 缩放手柄 hit-test（与绘制位置一致——锚点=消息区容器右上角，用上一帧 MsgArea 记录；
        // PreDraw 在 Begin 前读 GetWindowPos 不可靠，故用 DrawMessageArea 里记录的矩形）
        if (Plugin.Config.CanResize)
        {
            var st = ImGui.GetStyle();
            var hSize = NativeIcons.ResizeHandleSize();  // ⚠️ 2026-08-18 原生手柄素材尺寸
            var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
            var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
            var areaMin = MsgAreaMax.X > 0f ? MsgAreaMin : ImGui.GetWindowPos();
            var areaSize = MsgAreaMax.X > 0f ? MsgAreaMax - MsgAreaMin : ImGui.GetWindowSize();
            var handleMin = new Vector2(
                areaMin.X + areaSize.X - hSize - st.WindowPadding.X - insetX,
                areaMin.Y + st.WindowPadding.Y + insetY);
            var handleMax = handleMin + new Vector2(hSize, hSize);
            var mp = ImGui.GetIO().MousePos;
            MouseOverResizeHandle = mp.X >= handleMin.X && mp.X <= handleMax.X
                                  && mp.Y >= handleMin.Y && mp.Y <= handleMax.Y;
            if (MouseOverResizeHandle)
                Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public new void Toggle()
    {
        IsOpen = !IsOpen;
        if (IsOpen)
        {
            FocusSearchBar = true;
            if (ChannelTabName.Length == 0)
            {
                ChannelTabName = Plugin.CurrentTab.Name;
                DisplayTab.SelectedChannels = Plugin.CurrentTab.SelectedChannels;
            }
            ShowBrowse(DateTime.Today);
        }
    }

    public override void Draw()
    {
        // ⚠️ 2026-08-18 鼠标在聊天窗口内 → 帧末光标决策（保持游戏指针；按钮/tab 上手指）
        Plugin.MarkCursorInChatWindow();

        // ⚠️ 2026-08-17 用户决策（布局重构）：顶栏完全移除（原 DrawCornerControls 状态行沉底
        // 到 DrawStatusRow）；缩放手柄从窗口右上角移到消息区右上角。顶部只留消息区。

        // 消息区高度 = 剩余高度 - 底部搜索栏 - 底部状态行
        var bottomH = ImGui.GetFrameHeight() + 6f * ImGuiHelpers.GlobalScale;
        var statusH = ImGui.GetFrameHeight() + 6f * ImGuiHelpers.GlobalScale;
        var msgH = ImGui.GetContentRegionAvail().Y - bottomH - statusH;

        // 异步加载完成 → 注入虚拟 tab 并渲染（原生，统一消息流显示）
        if (PendingApply && !IsLoading)
        {
            PendingApply = false;
            DisplayTab.Messages.Clear();
            DisplayTab.Messages.AddSortPrune(Loaded, int.MaxValue);
            ShownMessages = Loaded;
        }

        DrawMessageArea(msgH);

        if (Panel != SidePanel.None)
        {
            ImGui.SameLine();
            DrawSidePanel(msgH);
        }

        DrawBottomBar();
        DrawStatusRow();
        DrawTabPickerPopup();

        if (Plugin.Config.CanResize)
            DrawTopRightResizeHandle();
    }

    /// <summary>频道来源 Tab 选择弹窗（底部 Tab 按钮 / 消息区右键共用）。</summary>
    private void DrawTabPickerPopup()
    {
        if (ImGui.BeginPopup("history-tab-picker"))
        {
            ImGui.TextUnformatted(Language.Search_ChannelTab);
            ImGui.Separator();
            foreach (var tab in Plugin.Config.Tabs)
            {
                if (ImGui.Selectable(tab.Name, ChannelTabName == tab.Name))
                {
                    ChannelTabName = tab.Name;
                    DisplayTab.SelectedChannels = tab.SelectedChannels;
                    RequeryCurrent();
                }
            }
            ImGui.EndPopup();
        }
    }

    // ================= 右上角控制（锁 + 关闭 + 状态） =================

    /// <summary>底部状态行（2026-08-17 布局重构：原顶栏状态整体沉底，位于底部搜索栏之下）。</summary>
    private void DrawStatusRow()
    {
        var spacing = 3f * ImGuiHelpers.GlobalScale;

        // 左侧：状态标签（搜索词 / 锁定日期 / 最新）+ 玩家筛选 + 频道 + 加载
        ImGui.AlignTextToFramePadding();
        if (CurrentMode == Mode.Context)
        {
            if (ImGuiUtil.IconButton(FontAwesomeIcon.ArrowLeft, id: "ctx-back", tooltip: Language.Search_Back))
                ShowBrowse(BrowseDate, DateLocked);
            ImGui.SameLine(0, spacing);
        }

        // 主状态：搜索词 > 锁定日期 > 最新（滚动模式）
        if (SearchTerm.Length > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, string.Format(Language.Search_ResultsFor, SearchTerm));
        }
        else if (DateLocked)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, BrowseDate.ToString("yyyy-MM-dd"));
            // 重置：清除日期锁定 → 回滚动模式（原生粗X图标 icon_24；SFX 25 关闭/重置音）
            ImGui.SameLine(0, 2f * ImGuiHelpers.GlobalScale);
            if (ImGuiUtil.NativeIconButton(NativeIcons.Close, "date-clear", Language.Search_Clear, FontAwesomeIcon.Times, sfx: ImGuiUtil.BtnSfx.Dismiss))
            {
                DateLocked = false;
                ShowBrowse(DateTime.Today);
            }
        }
        else
        {
            // 滚动模式：从当前时间开始往回，不固定日期（用户 2026-08-17 要求）
            ImGui.TextColored(ImGuiColors.DalamudGrey, Language.Search_Latest);
        }

        // 玩家筛选（重置按钮在右侧；原生粗X图标 icon_24；SFX 25 关闭/重置音）
        if (PlayerFilter.Length > 0)
        {
            ImGui.SameLine(0, spacing);
            ImGui.TextColored(ImGuiColors.DalamudOrange, $"@{PlayerFilter}");
            ImGui.SameLine(0, 2f * ImGuiHelpers.GlobalScale);
            if (ImGuiUtil.NativeIconButton(NativeIcons.Close, "player-clear", Language.Search_Clear, FontAwesomeIcon.Times, sfx: ImGuiUtil.BtnSfx.Dismiss))
            {
                PlayerFilter = "";
                RequeryCurrent();
            }
        }

        if (ChannelTabName.Length > 0)
        {
            ImGui.SameLine(0, spacing);
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"({ChannelTabName})");
        }

        if (IsLoading)
        {
            ImGui.SameLine(0, spacing);
            ImGui.TextColored(ImGuiColors.DalamudGrey, Language.Search_Loading);
        }

        if (CurrentMode == Mode.Context)
        {
            ImGui.SameLine(0, spacing);
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Home, id: "ctx-home", tooltip: Language.Search_Today))
                ShowBrowse(DateTime.Today);
        }

        // 右侧留白（消息区右上角是缩放手柄，本行不再放按钮）
    }

    // ================= 底部搜索栏（频道Tab + 搜索框 + 按钮） =================

    private void DrawBottomBar()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var spacing = 3f * scale;
        var btnW = ImGuiUtil.CalcIconButtonSize().X;
        var avail = ImGui.GetContentRegionAvail().X;

        // ⚠️ 04:29 统一主窗口输入行逻辑（用户要求）：
        // 搜索框 = 输入框大小（InputFont + FramePadding.Y=0 → 高度 = FontSize）；
        // 频道按钮 + 🔍👤📅× = 输入框旁边按钮逻辑（FontAwesomeSmall 高度，垂直居中于搜索框行）
        float searchBoxHeight;
        using (Plugin.FontManager.InputFont.Push())
            searchBoxHeight = ImGui.GetFontSize();
        float iconButtonHeight;
        using (Plugin.FontManager.FontAwesomeSmall.Push())
            iconButtonHeight = ImGui.GetFrameHeight();
        var rowTop = ImGui.GetCursorPosY();
        var iconTop = rowTop + (searchBoxHeight - iconButtonHeight) / 2f;

        // ⚠️ 布局模仿主窗口输入行（纯流式，从不出错）：
        // 先精确计算搜索框宽度（给所有按钮预留空间），然后流式 SameLine 排布。
        // ⚠️ 2026-08-17 用户决策：删除 tab 旁的 ChevronDown 小箭头（无意义），tab 按钮自身即可弹选择。
        var tabText = ChannelTabName.Length > 0 ? ChannelTabName : "频道";
        var tabTextW = ImGui.CalcTextSize(tabText).X + ImGui.GetStyle().FramePadding.X * 2 + 14f;
        var tabTotalW = tabTextW;

        var leftBtns = 3;  // 🔍 👤 📅
        var rightBtns = 1; // ×（锁按钮已移除，2026-08-17 用户决策）
        // 所有按钮 + 所有间隔（tab后 1 + 搜索框后 4 + 🔒后 1 = 6 个 spacing）+ 充裕余量
        var searchW = Math.Max(40f * scale,
            avail - tabTotalW - btnW * (leftBtns + rightBtns) - spacing * (leftBtns + rightBtns + 1) - 20f * scale);

        // 频道 tab 按钮（高度与右侧图标按钮一致，垂直居中于搜索框行；点击弹频道选择）
        ImGui.SetCursorPosY(iconTop);
        if (ImGui.Button(tabText, new Vector2(tabTextW, iconButtonHeight)))
            ImGui.OpenPopup("history-tab-picker");
        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip(Language.Search_ChannelTab);

        // 搜索框（InputFont + FramePadding.Y=0 → 与主窗口输入框同高）
        ImGui.SameLine(0, spacing);
        ImGui.SetCursorPosY(rowTop);
        if (FocusSearchBar)
        {
            FocusSearchBar = false;
            ImGui.SetKeyboardFocusHere();
        }
        bool submit;
        using (var inputFont = Plugin.FontManager.InputFont.Push())
        using (var pad = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 0f)))
        {
            ImGui.SetNextItemWidth(searchW);
            submit = ImGui.InputTextWithHint("##history-search", Language.Search_Hint, ref SearchTerm, 100,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        // 🔍 搜索（不清 PlayerFilter：可与玩家筛选叠加）
        // 原生图标：用户新素材 icon_34；wrap 未加载时回退 Search FontAwesome。
        // ⚠️ 无按钮音（用户排除项"搜索按钮"——回车提交搜索时 InputText 也无声音，保持一致）
        ImGui.SameLine(0, spacing);
        ImGui.SetCursorPosY(iconTop);
        var searchClicked = ImGuiUtil.NativeIconButton(NativeIcons.SearchGo, "search-go", Language.Search_Go, FontAwesomeIcon.Search, sfx: ImGuiUtil.BtnSfx.None);
        if (searchClicked || submit)
        {
            if (SearchTerm.Length > 0)
                ShowResults(SearchTerm);
            else
                ShowBrowse(BrowseDate, DateLocked);
        }

        // 👤 筛选玩家（用户新素材 icon_01；wrap 未加载时回退 User/UserCircle）
        // 音效：展开面板=打开 23，再按收起=关闭 24（2026-08-17 用户方案）
        ImGui.SameLine(0, spacing);
        ImGui.SetCursorPosY(iconTop);
        var playersActive = Panel == SidePanel.Players;
        if (ImGuiUtil.NativeIconButton(NativeIcons.Players, "panel-players", Language.Search_Players,
                playersActive ? FontAwesomeIcon.UserCircle : FontAwesomeIcon.User,
                sfx: playersActive ? ImGuiUtil.BtnSfx.Close : ImGuiUtil.BtnSfx.Open))
            Panel = playersActive ? SidePanel.None : SidePanel.Players;

        // 📅 筛选日期（用户新素材 icon_01，与玩家同图——"选日期"也是一种"筛选"）
        // 音效：展开=打开 23，再按收起=关闭 24
        ImGui.SameLine(0, spacing);
        ImGui.SetCursorPosY(iconTop);
        var calActive = Panel == SidePanel.Calendar;
        if (ImGuiUtil.NativeIconButton(NativeIcons.Funnel, "panel-calendar", Language.Search_Calendar,
                calActive ? FontAwesomeIcon.CalendarCheck : FontAwesomeIcon.CalendarAlt,
                sfx: calActive ? ImGuiUtil.BtnSfx.Close : ImGuiUtil.BtnSfx.Open))
            Panel = calActive ? SidePanel.None : SidePanel.Calendar;

        // × 关闭（原生图标：用户新素材 icon_24 粗X；wrap 未加载时回退 Times）
        // ⚠️ SFX 25 关闭音（2026-08-17 用户方案：关闭/重置筛选按钮统一 25）
        ImGui.SameLine(0, spacing);
        ImGui.SetCursorPosY(iconTop);
        if (ImGuiUtil.NativeIconButton(NativeIcons.Close, "window-close", Language.Search_Close, FontAwesomeIcon.Times, sfx: ImGuiUtil.BtnSfx.Dismiss))
            IsOpen = false;
    }

    // ================= 消息区（原生渲染 + 右键 Tab 频道选择） =================

    private void DrawMessageArea(float height)
    {
        var rightW = Panel == SidePanel.None ? 0f : 220f * ImGuiHelpers.GlobalScale;
        using var area = ImRaii.Child("##history-area", new Vector2(-1 - rightW, height), false, ImGuiWindowFlags.NoScrollbar);
        if (!area.Success)
            return;

        // 记录消息区容器矩形（缩放手柄锚点：手柄画在消息区右上角，2026-08-17 用户决策）
        MsgAreaMin = ImGui.GetWindowPos();
        MsgAreaMax = MsgAreaMin + ImGui.GetWindowSize();

        // 右键：快捷选择使用哪个 Tab 的接收频道
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("history-tab-picker");

        if (!IsLoading)
        {
            using var locked = DisplayTab.Messages.GetReadOnly();
            if (locked.Count == 0)
            {
                ImGui.TextUnformatted(Language.Search_Empty);
                return;
            }
        }

        // ⚠️ 滚轮接管（与 DrawChatLog 一致）：SearchWindow 不经过 DrawChatLog，
        // 而 DrawMessageLog 的 HandleWheelScrollLineByLine 只读 PendingWheel——
        // 不在这里记录滚轮并清零 IO，滚轮会被窗口的 NoScrollWithMouse 吞掉 → 无法滚动
        State.UserScrolled = false;
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                ImGui.GetIO().MouseWheel = 0f;
                State.PendingWheel = wheel;
                State.UserScrolled = true;
            }
        }

        // ⚠️ 用户手动滚动后取消"定位到目标"（避免抢滚动）
        if (State.UserScrolled)
            PendingScrollTo = null;

        // 无限滚动（默认滚动模式）：滚轮向上 + 消息区已在顶部 → 加载上一天并合并。
        // ⚠️ 用 State.AtTop（滚轮消费处更新）而非 ImGui.GetScrollY()——后者读的是外层
        // child（恒 0，真实滚动在内层 ##chat2-messages），曾导致检测失效（用户实测）。
        // 搜索模式（有搜索词）为全量快照，不参与无限滚动。
        if (!DateLocked && !IsLoading && SearchTerm.Length == 0
            && State.PendingWheel > 0 && State.AtTop && LoadStartDate > MinimalDate.Date)
            LoadEarlier();

        Plugin.ChatLog.DrawMessageLog(DisplayTab, Plugin.ChatLog.InputHandler.PayloadHandler,
            ImGui.GetContentRegionAvail().Y, false, State, PendingScrollTo, onMessageClick: ShowContext,
            onMessageArea: (min, max) => { LastMessageAreaMin = min; LastMessageAreaMax = max; });
        // ⚠️ 不在此清空 PendingScrollTo：首帧消息刚注入、child 滚动范围未建立，
        // SetScrollHereY 无效——若清空则永远定位不到。保留到定位成功或用户滚动/切换。
    }

    // ================= 搜索结果列表 =================

    // ================= 右侧筛选面板 =================

    private void DrawSidePanel(float height)
    {
        var w = 220f * ImGuiHelpers.GlobalScale;
        // ⚠️ 背景与消息区统一（用户 2026-08-17）：默认 Child 背景透明 → 面板"完全透明"。
        // 与 ChatLog.DrawMessageLog 同款条件——NativeBackground（窗口透明）时 push WindowBg 色背景，
        // 非 Native 时透出窗口背景（行为完全一致）；透明度跟随 WindowAlpha 设置。
        var bgColor = SidePanelBgColor();
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, bgColor, true);
        using var panel = ImRaii.Child("##history-side", new Vector2(w, height), true);
        if (!panel.Success)
            return;

        if (Panel == SidePanel.Players)
            DrawPlayerPanel();
        else if (Panel == SidePanel.Calendar)
            DrawCalendar();
    }

    /// <summary>侧面板背景色：与消息区背景（ChatLog.MessageLogBgColor）一致——WindowBg 的 RGB + WindowAlpha 透明度。</summary>
    private Vector4 SidePanelBgColor()
    {
        var winBg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        return new Vector4(winBg.X, winBg.Y, winBg.Z, winBg.W * (Plugin.Config.WindowAlpha / 100f));
    }

    private void DrawPlayerPanel()
    {
        ImGui.TextUnformatted(Language.Search_Players);
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##player-filter", Language.Search_PlayerHint, ref PlayerFilter, 50);

        if (PlayerFilter.Length > 0)
        {
            ImGui.SameLine();
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, id: "player-input-clear", tooltip: Language.Search_Clear))
            {
                PlayerFilter = "";
                RequeryCurrent();
            }
        }

        ImGui.Separator();

        // 玩家列表：从当前展示集合提取发送者去重（上限 200，避免性能问题）
        var players = ShownMessages
            .Select(m => ChunkUtil.ToRawString(m.Sender).Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .Take(MaxPlayers)
            .OrderBy(s => s)
            .Where(s => PlayerFilter.Length == 0 || s.Contains(PlayerFilter, StringComparison.InvariantCultureIgnoreCase))
            .ToList();

        if (players.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, Language.Search_NoPlayers);
            return;
        }

        using var list = ImRaii.Child("##player-list");
        if (!list.Success)
            return;

        foreach (var player in players)
        {
            var selected = string.Equals(player, PlayerFilter, StringComparison.InvariantCultureIgnoreCase);
            if (ImGui.Selectable(player, selected))
            {
                PlayerFilter = player;
                RequeryCurrent();
            }
        }
    }

    /// <summary>按当前玩家筛选重新查询（保留搜索词或日期范围）。</summary>
    /// <summary>按当前筛选重新查询（玩家筛选变化 / 频道切换后）。</summary>
    private void RequeryCurrent()
    {
        if (SearchTerm.Length > 0)
            ShowResults(SearchTerm);
        else
            ShowBrowse(BrowseDate, DateLocked);
    }

    private void DrawCalendar()
    {
        ImGui.TextUnformatted(Language.Search_Calendar);
        ImGui.Spacing();

        // 月份头部：◀ 2026年8月 ▶
        if (ImGuiUtil.IconButton(FontAwesomeIcon.ChevronLeft, id: "cal-prev"))
            CalendarMonth = CalendarMonth.AddMonths(-1);
        ImGui.SameLine(0, 8f * ImGuiHelpers.GlobalScale);
        var title = CalendarMonth.ToString("yyyy年M月");
        ImGui.TextUnformatted(title);
        ImGui.SameLine(0, 8f * ImGuiHelpers.GlobalScale);
        if (ImGuiUtil.IconButton(FontAwesomeIcon.ChevronRight, id: "cal-next"))
            CalendarMonth = CalendarMonth.AddMonths(1);

        ImGui.Spacing();

        // 7 列等宽表格（Table 自动等分，两位数日期不会挤）
        var weekNames = new[] { "日", "一", "二", "三", "四", "五", "六" };
        var firstDay = new DateTime(CalendarMonth.Year, CalendarMonth.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(CalendarMonth.Year, CalendarMonth.Month);
        var offset = (int)firstDay.DayOfWeek; // Sunday = 0
        var today = DateTime.Today;

        using var table = ImRaii.Table("##cal-grid", 7, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (var i = 0; i < 7; i++)
        {
            ImGui.TableSetupColumn(weekNames[i], ImGuiTableColumnFlags.NoResize);
        }

        // 表头
        ImGui.TableNextRow();
        for (var i = 0; i < 7; i++)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(ImGuiColors.DalamudGrey, weekNames[i]);
        }

        // 日期网格
        var day = 1;
        var cellH = ImGui.GetFrameHeight();
        for (var row = 0; row < 6; row++) // 最多 6 行
        {
            ImGui.TableNextRow();
            for (var col = 0; col < 7; col++)
            {
                ImGui.TableNextColumn();
                var cellIndex = row * 7 + col;

                if (cellIndex < offset || day > daysInMonth)
                {
                    ImGui.Dummy(new Vector2(0, cellH));
                    continue;
                }

                var date = new DateTime(CalendarMonth.Year, CalendarMonth.Month, day);
                var isToday = date == today;
                var isSelected = date == BrowseDate;

                ImGui.PushID($"cal-{date:yyyyMMdd}");
                var styleColor = 1;
                ImGui.PushStyleColor(ImGuiCol.Button, isSelected
                    ? new Vector4(0.25f, 0.4f, 0.65f, 0.6f)
                    : Vector4.Zero);
                if (isToday)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    styleColor++;
                }
                // ⚠️ 去按钮内边距：两位数日期（10~31）在窄列里被 FramePadding 挤掉
                using var cellPad = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero);
                if (ImGui.Button(day.ToString(), new Vector2(-1, cellH)))
                {
                    ShowBrowse(date, lockDate: true); // 选日期 = 固定只看这天
                    Panel = SidePanel.None;
                }

                ImGui.PopStyleColor(styleColor);
                ImGui.PopID();
                day++;
            }
        }

        ImGui.Spacing();
        if (DateLocked)
        {
            // 清除日期固定 → 回滚动模式（用户 2026-08-17：日历里不要"今天"按钮，重置在标题栏）
            if (ImGui.Button(Language.Search_Clear + " (日期)", new Vector2(-1, 0)))
            {
                ShowBrowse(DateTime.Today);
                Panel = SidePanel.None;
            }
        }
    }

    // ================= 模式切换与查询 =================

    private void ShowBrowse(DateTime date, bool lockDate = false)
    {
        CurrentMode = Mode.Browse;
        if (lockDate)
        {
            // 固定日期：只看这一天
            DateLocked = true;
            BrowseDate = date.Date;
            StartLoad(() => LoadRange(BrowseDate, BrowseDate.AddDays(1).AddTicks(-1)));
        }
        else
        {
            // 滚动模式（默认）：从今天开始，滚到顶加载上一天
            DateLocked = false;
            BrowseDate = DateTime.Today;
            LoadStartDate = DateTime.Today;
            StartLoad(() => LoadRange(LoadStartDate, DateTime.Now));
        }
    }

    /// <summary>按日期范围 + 玩家筛选加载消息（时间正序，供消息流显示）。</summary>
    private void LoadRange(DateTime after, DateTime before)
    {
        var channels = GetChannels();
        using var e = Plugin.MessageManager.Store.GetDateRange(after, before, channels, Plugin.PlayerState.ContentId);
        var msgs = e.ToArray();
        if (PlayerFilter.Length > 0)
            msgs = msgs.Where(m => ChunkUtil.ToRawString(m.Sender).Trim()
                .Equals(PlayerFilter, StringComparison.InvariantCultureIgnoreCase)).ToArray();
        Loaded = msgs.OrderBy(m => m.Date).ToArray();
        PendingScrollTo = null;
    }

    /// <summary>滚动模式：加载上一天并合并（上方插入），定位回旧内容顶部。</summary>
    private void LoadEarlier()
    {
        if (DateLocked || IsLoading || LoadStartDate <= MinimalDate.Date)
            return;

        var prevDay = LoadStartDate.AddDays(-1);
        var oldFirst = ShownMessages.Length > 0 ? ShownMessages[0].Id : (Guid?)null;
        var channels = GetChannels();
        var player = PlayerFilter;

        StartLoad(() =>
        {
            using var e = Plugin.MessageManager.Store.GetDateRange(prevDay, prevDay.AddDays(1).AddTicks(-1), channels, Plugin.PlayerState.ContentId);
            var earlier = e.ToArray();
            if (player.Length > 0)
                earlier = earlier.Where(m => ChunkUtil.ToRawString(m.Sender).Trim()
                    .Equals(player, StringComparison.InvariantCultureIgnoreCase)).ToArray();

            LoadStartDate = prevDay; // 标记已加载（即使空，防死循环）
            Loaded = earlier.OrderBy(m => m.Date).Concat(ShownMessages).ToArray();
            PendingScrollTo = oldFirst; // 定位回旧内容顶部 → 新增内容在上方可见
        });
    }

    private void ShowResults(string term)
    {
        CurrentMode = Mode.Browse;
        DateLocked = false;
        var player = PlayerFilter; // 快照：异步任务里读字段有竞态
        StartLoad(() =>
        {
            var channels = GetChannels();
            using var e = Plugin.MessageManager.Store.GetDateRange(MinimalDate, DateTime.Now, channels, Plugin.PlayerState.ContentId);
            Loaded = e.ToArray()
                .Where(m =>
                    (ChunkUtil.ToRawString(m.Sender).Contains(term, StringComparison.InvariantCultureIgnoreCase) ||
                     ChunkUtil.ToRawString(m.Content).Contains(term, StringComparison.InvariantCultureIgnoreCase)) &&
                    (player.Length == 0 || ChunkUtil.ToRawString(m.Sender).Trim()
                        .Equals(player, StringComparison.InvariantCultureIgnoreCase)))
                .OrderBy(m => m.Date) // 消息流正序（与聊天框一致）
                .Take(2000)
                .ToArray();
            PendingScrollTo = null;
        });
    }

    private void ShowContext(Message target)
    {
        CurrentMode = Mode.Context;
        StartLoad(() =>
        {
            var channels = GetChannels();
            var targetTime = target.Date.LocalDateTime;

            var window = TimeSpan.FromMinutes(ContextWindowMinutes);
            Message[] loaded = [];
            var targetIdx = -1;
            for (var expand = 0; expand < ContextMaxExpand; expand++)
            {
                using var e = Plugin.MessageManager.Store.GetDateRange(targetTime - window, targetTime + window, channels, Plugin.PlayerState.ContentId);
                loaded = e.ToArray();
                targetIdx = Array.FindIndex(loaded, m => m.Id == target.Id);
                if (targetIdx >= 0)
                    break;
                window *= 2;
            }

            if (targetIdx < 0)
            {
                Loaded = [target]; // 找不到上下文也至少显示这条
                PendingScrollTo = target.Id;
                return;
            }

            var start = Math.Max(0, targetIdx - ContextMaxMessages / 2);
            var end = Math.Min(loaded.Length, targetIdx + ContextMaxMessages / 2 + 1);
            Loaded = loaded[start..end];
            PendingScrollTo = target.Id;
        });
    }

    // ================= 缩放手柄（消息区右上角，仿 PopOut） =================
    // ⚠️ 2026-08-17 用户决策：手柄从窗口右上角移到消息区右上角（锚点 = ##history-area 容器矩形）。

    private void DrawTopRightResizeHandle()
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var style = ImGui.GetStyle();
        var hSize = NativeIcons.ResizeHandleSize();  // ⚠️ 2026-08-18 原生手柄素材尺寸

        // 锚点 = 消息区容器右上角（上一帧 DrawMessageArea 记录；窗口刚打开第一帧为 Zero 则退化到窗口）
        var areaMin = MsgAreaMax.X > 0f ? MsgAreaMin : windowPos;
        var areaSize = MsgAreaMax.X > 0f ? MsgAreaMax - MsgAreaMin : windowSize;

        var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
        var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
        var localPos = new Vector2(
            areaSize.X - hSize - style.WindowPadding.X - insetX,
            style.WindowPadding.Y + insetY);

        var mousePos = ImGui.GetIO().MousePos;
        var handleRectMin = areaMin + localPos;
        var handleRectMax = handleRectMin + new Vector2(hSize, hSize);
        var hovered = mousePos.X >= handleRectMin.X && mousePos.X <= handleRectMax.X
                      && mousePos.Y >= handleRectMin.Y && mousePos.Y <= handleRectMax.Y;

        if (hovered || IsResizingTopRight)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        // 交互：按下开始缩放（保持左下角固定）
        if (!IsResizingTopRight && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            IsResizingTopRight = true;
            ResizeStartMousePos = mousePos;
            ResizeStartWindowPos = windowPos;
            ResizeStartWindowSize = windowSize;
        }

        if (IsResizingTopRight)
        {
            var delta = mousePos - ResizeStartMousePos;
            var newPos = new Vector2(ResizeStartWindowPos.X, ResizeStartWindowPos.Y + delta.Y);
            var newSize = new Vector2(
                Math.Max(360f, ResizeStartWindowSize.X + delta.X),
                Math.Max(320f, ResizeStartWindowSize.Y - delta.Y));
            ImGui.SetWindowPos(newPos);
            ImGui.SetWindowSize(newSize);

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                IsResizingTopRight = false;
        }

        // ⚠️ 2026-08-18 绘制已移至 PostDraw（前台 dl 置顶）；这里只保留 hit-test + resize 交互
    }

    // ================= 工具 =================

    /// <summary>套用当前 Tab 频道设定；空（=全部）时用全部频道。</summary>
    private byte[] GetChannels()
    {
        // 频道来源：右键选定的 Tab（ChannelTabName）；未选择时用当前 Tab
        var tab = ChannelTabName.Length == 0
            ? Plugin.CurrentTab
            : Plugin.Config.Tabs.FirstOrDefault(t => t.Name == ChannelTabName) ?? Plugin.CurrentTab;
        if (tab.SelectedChannels.Count > 0)
            return tab.SelectedChannels.Select(p => (byte)p.Key).ToArray();
        return Enum.GetValues<ChatType>().Select(c => (byte)c).ToArray();
    }

    private int LoadVersion;

    private void StartLoad(Action loadAction)
    {
        // ⚠️ 版本号：IsLoading 时不 return（快速连续点击会导致后一次操作被忽略 → 消息区不更新）。
        // 只有最新一次加载能结束 IsLoading，旧任务结果被覆盖。
        var ver = ++LoadVersion;
        IsLoading = true;
        PendingApply = true;
        Task.Run(() =>
        {
            try
            {
                loadAction();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "聊天记录加载失败");
            }
            finally
            {
                if (ver == LoadVersion)
                    IsLoading = false;
            }
        });
    }

    // ⚠️ 2026-08-18 缩放手柄置顶（前台 dl；锚点 = 消息区矩形 MsgAreaMin/Max，与 DrawTopRightResizeHandle 一致）
    public override void PostDraw()
    {
        if (!Plugin.Config.CanResize)
            return;
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var style = ImGui.GetStyle();
        var hSize = NativeIcons.ResizeHandleSize();
        var areaMin = MsgAreaMax.X > 0f ? MsgAreaMin : windowPos;
        var areaSize = MsgAreaMax.X > 0f ? MsgAreaMax - MsgAreaMin : windowSize;
        var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
        var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
        var localPos = new Vector2(
            areaSize.X - hSize - style.WindowPadding.X - insetX,
            style.WindowPadding.Y + insetY);
        NativeIcons.DrawResizeHandle(ImGui.GetForegroundDrawList(), areaMin + localPos,
            new Vector2(hSize, hSize), MouseOverResizeHandle || IsResizingTopRight);
    }
}
