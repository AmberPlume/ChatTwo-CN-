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
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Extensions;

namespace ChatTwo.Ui;

/// <summary>
/// 弹出窗口（浏览器式 tab 分组）：一个窗口可承载 1..N 个弹出 tab。
/// 单 tab = 现状视觉（名字 + 关闭）；多 tab = 主窗口同款 tab 栏（legacy/三段式）。
/// 拖拽 tab 条（600ms 长按 + 移动 10px）→ 拖到另一窗口 tab 栏 = 合并；拖出空白 = 分离。
/// 窗口名 = "Chat 2 Popout##popout-{自增序号}"：含 "##popout"（RenderHole 挖洞识别），
/// 序号实例唯一（不可按 tab 命名，见构造——分离首 tab 会与源窗口撞名）。
/// 显示名 "Chat 2 Popout"。
/// </summary>
public class Popout : Window, IChatWindow
{
    private readonly Plugin Plugin;

    // tab 列表（≥1），顺序 = Config.Tabs 全局顺序（AddTab 按序插入，保证与主窗口一致）
    private readonly List<Tab> Tabs = [];
    private int CurrentTabIdx;
    private Tab CurrentTab => Tabs[CurrentTabIdx];

    // per-tab 独立状态：InputHandler（含 PayloadHandler）+ 消息区状态（滚动/选字隔离，与主窗口互不干扰）
    private readonly Dictionary<Guid, InputHandler> Handlers = [];
    private readonly Dictionary<Guid, MessageLogState> MsgStates = [];

    private long FrameTime; // set every frame
    private long LastActivityTime = Environment.TickCount64;

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

    // 消息区屏幕矩形（上一帧 DrawMessageLog 记录，本窗口自己的——共享 DrawMessageLog 时
    // 最后 Draw 的窗口会覆盖 ChatLog 上的字段，不能互读）
    public Vector2 LastMessageAreaMin = Vector2.Zero;
    public Vector2 LastMessageAreaMax = Vector2.Zero;

    // tab 栏屏幕矩形（供其他窗口拖拽命中判定；PostDraw 更新）
    public Vector2 TabBarMin;
    public Vector2 TabBarMax;

    // 停靠状态（BgAlpha 判定：停靠时不透明）
    private bool _docked;

    // ---- tab 拖拽（浏览器式合并/分离）状态 ----
    // 判定状态机与主窗口统一（TabDragTracker，key = tabId；长按 600ms + 移动 10px）
    private readonly TabDragTracker<Guid> _tabDrag = new();
    private Tab? _draggingTab;                                     // 拖拽中的 tab（幽灵/EndTabDrag 用）
    private bool DragHighlight;                                    // 本窗口是命中目标（高亮）

    // 窗口组 id（跨会话）：同组 tab 共享同值，重载后 AddPopOutsToDraw 按组分窗恢复合并。
    // 合并（AddTabFrom）把迁入 tab 置同值；分离建新窗分配新 Guid；收回（RecallTab）置 Empty
    public Guid GroupId { get; private set; }

    // 首帧定位/定尺寸（拖出跟手或恢复记忆）：PositionCondition/SizeCondition 只对
    // ImGui 从未用过的窗口名生效——窗口名静态自增且重启归零，ini 同名记录会让条件失效，
    // 回落 ini 旧值 → 必须首帧 SetWindowPos/SetWindowSize 无条件覆盖（per-tab 记忆不依赖 ini）
    private Vector2? _pendingInitialPos;
    private Vector2? _pendingInitialSize;
    private Vector2? _pendingReleasePos; // 首帧重算用：实际尺寸 ≠ 计划尺寸时按实际重算位置
    private bool _closed; // OnClose 幂等：DrawInternal 状态机 + CloseImmediate 双路径防重复清理

    // 几何记忆写回（跨会话）：窗口名每次启动归零 → ImGui ini 尺寸记忆不可靠，
    // 尺寸/位置必须自己持久化到 Tab；变化稳定后写全部 Tabs（合并窗口同几何）
    private (Vector2 size, Vector2 pos)? _persistedGeo; // 最近基线（首帧取实际值）
    private long _geoChangeAt;                          // 上次几何变化时刻（稳定超时才写回）

    // 窗口名实例唯一（自增序号）：不可用 tab.Identifier——多 tab 窗口名 = 第一个 tab 的 id，
    // 分离该 tab 时新窗口与源窗口同名 → AddWindow 撞名崩溃 + 防御检查误杀源窗（tab 被收回）
    private static int _windowSeq;
    private static string NextWindowName() => $"Chat 2 Popout##popout-{++_windowSeq}";

    public Popout(Plugin plugin, Tab tab, Vector2? releasePos = null) : base(NextWindowName())
    {
        Plugin = plugin;
        AddTabInternal(tab);

        // 窗口组：拖出/分离（有释放点）= 新窗口 → 新组；恢复（无释放点）沿用主 tab 持久组
        //（多 tab 合并窗重载后按组分窗恢复）；首次弹出（无组）→ 新组
        GroupId = releasePos.HasValue || tab.PopOutGroup == Guid.Empty
            ? Guid.NewGuid()
            : tab.PopOutGroup;
        tab.PopOutGroup = GroupId;

        // 尺寸单位换算：Tab.PopOutSize 存 ImGui 实际单位（= GetWindowSize 所见），
        // 但基类 Size 是逻辑单位——WindowHost 会 SetNextWindowSize(Size × GlobalScale) 再应用
        //（反编译 WindowHost.cs:615）→ 记忆值必须 ÷GlobalScale 还原逻辑，否则每轮 ×scale 膨胀。
        // 默认 350 = 逻辑单位（×scale 后 = v1.41.4.0 固定 350 的观感，兼容）。
        var planned = tab.PopOutSize ?? new Vector2(350, 350);
        Size = tab.PopOutSize.HasValue ? planned / ImGuiHelpers.GlobalScale : planned;
        SizeCondition = ImGuiCond.FirstUseEver;
        _pendingInitialSize = Size;

        // 位置：拖出/分离（有释放点）→ 按释放点跟手定位；
        // 自动恢复/按钮弹出（无释放点）→ 套记忆位置（钳制视口防屏幕外）
        // 注：内部几何（GetWindowSize/Pos/鼠标）均为实际单位 → 估算实际尺寸 = Size × scale
        var actualSize = Size!.Value * ImGuiHelpers.GlobalScale;
        _pendingReleasePos = releasePos;
        if (releasePos.HasValue)
            _pendingInitialPos = PosFromReleasePoint(releasePos.Value, actualSize);
        else if (tab.PopOutPos is { } savedPos)
            _pendingInitialPos = ClampToViewport(savedPos, actualSize);

        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    /// <summary>释放点 → 窗口左上角：鼠标按下的 tab 条在窗口里的实际位置对齐释放点。
    /// Bottom = 条中心在窗底；Top = 条中心在窗顶；Side = 列中心在窗左。
    /// （拖出后松手窗口落在指针处，不突兀；钳制视口内保证整体可见。）</summary>
    private Vector2 PosFromReleasePoint(Vector2 releasePos, Vector2 winSize)
    {
        Vector2 pos;
        switch (TabBarPlace())
        {
            case TabPosition.Top:
                pos = releasePos - new Vector2(0f, TabBarHeight(Plugin) / 2f);
                break;
            case TabPosition.Side:
                pos = releasePos - new Vector2(TabSidebarWidth() / 2f, winSize.Y / 2f);
                break;
            default: // Bottom
                pos = releasePos - new Vector2(0f, winSize.Y - TabBarHeight(Plugin) / 2f);
                break;
        }
        return ClampToViewport(pos, winSize);
    }

    // ---- tab 集合管理 ----

    private void AddTabInternal(Tab tab)
    {
        Tabs.Add(tab);
        Handlers[tab.Identifier] = new InputHandler(this, Plugin, $"popout-{tab.Identifier}");
        MsgStates[tab.Identifier] = new MessageLogState();
    }

    /// <summary>合并：从源窗口迁入 tab（handler/state 随行，状态连续）。按全局顺序插入。</summary>
    public void AddTabFrom(Tab tab, InputHandler handler, MessageLogState state)
    {
        if (Tabs.Any(t => t.Identifier == tab.Identifier))
            return; // 防重复（重复合并/残留映射）：tab 已在目标窗口时不重复插入
        tab.PopOutGroup = GroupId; // 迁入本窗口组（合并后同组，重载同窗恢复）
        handler.MainWindow = this;  // handler 归属窗口更新（原 handler.MainWindow 是源窗口）
        var tabGlobalIdx = Plugin.Config.Tabs.IndexOf(tab);
        var insertAt = Tabs.Count;
        for (var i = 0; i < Tabs.Count; i++)
        {
            if (Plugin.Config.Tabs.IndexOf(Tabs[i]) > tabGlobalIdx)
            {
                insertAt = i;
                break;
            }
        }
        Tabs.Insert(insertAt, tab);
        Handlers[tab.Identifier] = handler;
        MsgStates[tab.Identifier] = state;
        // 合并后同窗几何一致：迁入成员继承目标窗当前几何（否则重载恢复时成员
        // 仍带合并前自己的旧位置）。目标已 Draw → _persistedGeo 必有值。
        if (_persistedGeo is { } geo)
        {
            tab.PopOutSize = geo.size;
            tab.PopOutPos = geo.pos;
        }
        if (insertAt <= CurrentTabIdx)
            CurrentTabIdx++;
    }

    /// <summary>主窗口 tab 直拖合并（无现有 handler/state，新建）。</summary>
    public void AddTabFromMain(Tab tab)
    {
        if (Tabs.Any(t => t.Identifier == tab.Identifier))
            return;
        AddTabFrom(tab, new InputHandler(this, Plugin, $"popout-{tab.Identifier}"), new MessageLogState());
        Plugin.ChatLog.PopOutInstances[tab.Identifier] = this;
    }

    /// <summary>移除 tab；返回 true = 列表已空（调用方应关窗）。CurrentTabIdx 自动修复。</summary>
    private bool RemoveTabInternal(Guid id)
    {
        var idx = Tabs.FindIndex(t => t.Identifier == id);
        if (idx < 0)
            return Tabs.Count == 0;
        Tabs.RemoveAt(idx);
        Handlers.Remove(id);
        MsgStates.Remove(id);
        if (Tabs.Count == 0)
        {
            CurrentTabIdx = 0; // 清索引防 PostDraw 越界（Tabs[CurrentTabIdx] 空窗崩溃）
            return true;
        }
        if (CurrentTabIdx >= Tabs.Count)
            CurrentTabIdx = Tabs.Count - 1;
        else if (idx < CurrentTabIdx)
            CurrentTabIdx--;
        return false;
    }

    /// <summary>本窗口是否包含该 tab（主窗口 AddPopOutsToDraw 自愈用）。</summary>
    public bool ContainsTab(Guid id) => Tabs.Any(t => t.Identifier == id);

    /// <summary>收回 tab 到主窗口（PopOut=false + 清组 + 设置 Mutable 同步）。</summary>
    private void RecallTab(Tab tab)
    {
        if (!tab.PopOut)
            return;
        tab.PopOut = false;
        tab.PopOutGroup = Guid.Empty;
        Plugin.SettingsWindow.SyncTabPopOut(tab.Identifier, false);
    }

    /// <summary>每帧自愈：移除被收回/删除的 tab；空则请求关窗。主窗口 AddPopOutsToDraw 统一调度。</summary>
    public void SyncTabs()
    {
        for (var i = Tabs.Count - 1; i >= 0; i--)
        {
            var t = Tabs[i];
            var cfgTab = Plugin.Config.Tabs.FirstOrDefault(x => x.Identifier == t.Identifier);
            if (cfgTab == null || !cfgTab.PopOut)
            {
                Tabs.RemoveAt(i);
                Handlers.Remove(t.Identifier);
                MsgStates.Remove(t.Identifier);
                Plugin.ChatLog.PopOutInstances.Remove(t.Identifier);
                if (i < CurrentTabIdx)
                    CurrentTabIdx--;
            }
        }
        if (CurrentTabIdx >= Tabs.Count)
            CurrentTabIdx = Math.Max(0, Tabs.Count - 1);
        if (Tabs.Count == 0)
            CloseImmediate(); // 空窗立即关闭（不等下一帧 OnClose：状态机残留会漏关）
    }

    /// <summary>有效 tab 位置：仿原生固定底部（三段式贴图只支持底部，与主窗口同规则）。</summary>
    private TabPosition TabBarPlace() =>
        Plugin.Config.NativeBackground ? TabPosition.Bottom : Plugin.Config.TabPosition;

    // ---- 窗口生命周期 ----

    public override void PreOpenCheck()
    {
        if (Tabs.Count == 0)
            IsOpen = false;
    }

    public override bool DrawConditions()
    {
        SyncTabs();
        if (Tabs.Count == 0)
            return false;

        FrameTime = Environment.TickCount64;
        var current = CurrentTab;

        var isHidden = current.IndependentHide
            ? HideStateHelper.HideStateCheck(this, current.HideInBattle, current.HideDuringCutscenes, current.HideWhenNotLoggedIn, false)
            : Plugin.ChatLog.IsHidden;

        if (isHidden)
            return false;

        if (!Plugin.Config.HideWhenInactive || (!Plugin.Config.InactivityHideActiveDuringBattle && Plugin.InBattle) || !current.UnhideOnActivity)
        {
            LastActivityTime = FrameTime;
            return true;
        }

        // Activity in the tab, this popout window, or the main chat log window.
        var lastActivityTime = Math.Max(current.LastActivity, LastActivityTime);
        lastActivityTime = Math.Max(lastActivityTime, Handlers[current.Identifier].LastActivityTime);
        return FrameTime - lastActivityTime <= 1000 * Plugin.Config.InactivityHideTimeout;
    }

    /// <summary>鼠标是否在消息区矩形内（消息区永远不可拖）。矩形由 DrawMessageLog 每帧回调记录。</summary>
    public bool IsMouseOverMessageAreaPublic()
    {
        var mp = ImGui.GetIO().MousePos;
        return mp.X >= LastMessageAreaMin.X && mp.X <= LastMessageAreaMax.X
            && mp.Y >= LastMessageAreaMin.Y && mp.Y <= LastMessageAreaMax.Y;
    }

    public override void PreDraw()
    {
        SyncTabs();
        if (Tabs.Count == 0)
            return;

        // 首帧无条件应用构造 Size（覆盖 ini 同名记录）：基类 Begin 前按 SizeCondition
        // SetNextWindowSize——FirstUseEver 对 ini 有记录的窗口名失效 → 尺寸回落 ini 旧值
        //（Draw 内 SetWindowSize 无效）→ 首帧临时 Always，消费后回 FirstUseEver 可自由缩放。
        if (_pendingInitialSize is { })
        {
            SizeCondition = ImGuiCond.Always; // 本帧基类无条件应用构造 Size（覆盖 ini）
            _pendingInitialSize = null;
        }
        else
        {
            SizeCondition = ImGuiCond.FirstUseEver; // 之后用户可自由缩放（几何由 persist 记忆）
        }

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Push();

        Flags = ImGuiWindowFlags.None;
        // 与主窗口一致：窗口自身从不滚动（滚动全在消息区 child 内）；NoResize 永禁原生缩放
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing;
        Flags |= ImGuiWindowFlags.NoResize;

        if (!Plugin.Config.ShowPopOutTitleBar)
            Flags |= ImGuiWindowFlags.NoTitleBar;

        // 消息区任何情况下都不可拖；MoveLocked 锁死全窗；拖 tab 期间禁移窗口（否则拖拽变拖动）。
        // 矩形用本窗口自己的（不能读 Plugin.ChatLog 的——共享 DrawMessageLog，主窗口最后画会覆盖）。
        if (IsMouseOverMessageAreaPublic() || Plugin.Config.MoveLocked || _tabDrag.Dragging != null)
            Flags |= ImGuiWindowFlags.NoMove;

        var canResize = CurrentTab.CanResize;
        if (!canResize)
            Flags |= ImGuiWindowFlags.NoResize;

        if (canResize)
        {
            // 原生缩放手柄 hit-test（偏移与 DrawTopRightResizeHandle 绘制一致）：
            // 手柄上时阻止窗口移动（拖手柄 = 缩放，不拖动）
            var st = ImGui.GetStyle();
            var hSize = NativeIcons.ResizeHandleSize();
            var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
            var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
            // Top 布局手柄下移 tab 条高度（与主窗口 GetTopTabBarHeight 同规则）
            var topBarOffset = TabBarPlace() == TabPosition.Top ? PopOutTabBarHeight() : 0f;
            var handleMin = new Vector2(
                LastWindowPos.X + LastWindowSize.X - hSize - st.WindowPadding.X - insetX,
                LastWindowPos.Y + st.WindowPadding.Y + insetY + topBarOffset);
            var handleMax = handleMin + new Vector2(hSize, hSize);
            var mp = ImGui.GetIO().MousePos;
            MouseOverResizeHandle = mp.X >= handleMin.X && mp.X <= handleMax.X
                                  && mp.Y >= handleMin.Y && mp.Y <= handleMax.Y;
            if (IsResizingTopRight || MouseOverResizeHandle)
                Flags |= ImGuiWindowFlags.NoMove;
        }

        if (!_docked)
        {
            // 背景透明度独立（BackgroundAlpha，四透明度之一）；PopOut 统一跟随主窗口。
            // float?，null=不透明背景——必须显式 0（停靠时保持不透明）
            BgAlpha = 0f;
        }

        // [CtxClickPass] 菜单打开期间：PopOut 窗口也不捕获鼠标（NoMouseInputs）→
        // 与主窗口一致：菜单若在 PopOut 上打开，鼠标穿透到游戏原生菜单可点击。
        if (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession)
            Flags |= ImGuiWindowFlags.NoMouseInputs;
    }

    public override void Draw()
    {
        if (Tabs.Count == 0)
            return;

        // 首帧定位（拖出/分离后跟手）：SetWindowPos 在当前窗口上下文内立即生效，
        // 不依赖 PositionCondition——Once 只对 ImGui 从未用过的窗口名生效，
        // 同名窗口曾存在过（ImGui WindowSettings 记录）→ Once 失效回落旧位置
        if (_pendingInitialPos is { } initPos)
        {
            // 拖出/分离：位置按首帧实际尺寸重算（无条件——构造计划为估算值，
            // 首帧 Begin 后 GetWindowSize 才是真实渲染尺寸 → 计划可能偏离 → 指针不落 tab 条）
            if (_pendingReleasePos is { } rp)
            {
                var actualSize = ImGui.GetWindowSize();
                if (actualSize.X >= 100f && actualSize.Y >= 100f)
                    initPos = PosFromReleasePoint(rp, actualSize);
            }
            ImGui.SetWindowPos(initPos);
            _pendingInitialPos = null;
            _pendingReleasePos = null;
        }

        // 鼠标在聊天窗口内 → 帧末光标决策（保持游戏指针；按钮/tab 上手指）
        Plugin.MarkCursorInChatWindow();

        using var mainFont = (Plugin.Config.FontsEnabled ? Plugin.FontManager.RegularFont : Plugin.FontManager.Axis).Push();
        using var id = ImRaii.PushId("popout-group");

        LastWindowSize = ImGui.GetWindowSize();
        LastWindowPos = ImGui.GetWindowPos();

        var state = MsgStates[CurrentTab.Identifier];

        // （与主窗口一致）：记录滚轮值并清零 IO，消息区 child 手动按 1 行滚
        state.UserScrolled = false;
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (Math.Abs(wheel) > 0.001f)
            {
                ImGui.GetIO().MouseWheel = 0f;
                state.PendingWheel = wheel;
                state.UserScrolled = true;
            }
        }

        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            LastActivityTime = FrameTime;

        var handler = Handlers[CurrentTab.Identifier];
        var place = TabBarPlace();

        // 顶部模式：tab 栏先画，消息区占剩余全部
        if (place == TabPosition.Top)
        {
            var barTopY = ImGui.GetCursorPosY();
            DrawPopOutTabBar();
            // 消息区起点精确 = tab 栏顶 + TabBarHeight：流式 item 的 NewLine 行高
            //（按钮/图标高 > 预留行高）会推出空行 → 手柄（按 TabBarHeight 下移）与消息区错位；
            // 与主窗口"光标拉回 tab 条底（barTopY + GetTopTabBarHeight）"同规则
            ImGui.SetCursorPosY(barTopY + PopOutTabBarHeight());
        }

        // tab 栏交互（拖拽合并/收回关闭）可能在本帧清空 Tabs → 后续 CurrentTab 越界崩（防御）
        if (CurrentTabIdx >= Tabs.Count)
        {
            if (Tabs.Count == 0)
                CloseImmediate();
            return;
        }

        if (place == TabPosition.Side)
        {
            // 侧边模式：左 tab 列表 + 右消息区（Table 两列，与主窗口 DrawTabSidebar 同构）
            using var table = ImRaii.Table("popout-tabs-table", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable);
            if (table)
            {
                ImGui.TableSetupColumn("tabs", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn("chat", ImGuiTableColumnFlags.WidthStretch, 4);

                ImGui.TableNextColumn();
                DrawPopOutTabBarSide();
                if (CurrentTabIdx >= Tabs.Count)
                {
                    if (Tabs.Count == 0)
                        CloseImmediate();
                    return;
                }

                ImGui.TableNextColumn();
                var sideH = ImGui.GetContentRegionAvail().Y;
                Plugin.ChatLog.DrawMessageLog(CurrentTab, handler.PayloadHandler, sideH, false, state,
                    onMessageArea: (min, max) => { LastMessageAreaMin = min; LastMessageAreaMax = max; });
            }
        }
        else
        {
            // 底部模式：消息区先画，tab 栏占剩余底部
            var remainingHeight = ImGui.GetContentRegionAvail().Y - (place == TabPosition.Bottom ? PopOutTabBarHeight() : 0f);
            Plugin.ChatLog.DrawMessageLog(CurrentTab, handler.PayloadHandler, remainingHeight, false, state,
                onMessageArea: (min, max) => { LastMessageAreaMin = min; LastMessageAreaMax = max; });

            if (place == TabPosition.Bottom)
                DrawPopOutTabBar();
        }

        // tab 栏交互（拖拽合并/收回）可能已清空 Tabs → 手柄区 CurrentTab 越界崩（防御）
        if (CurrentTabIdx >= Tabs.Count)
        {
            if (Tabs.Count == 0)
                CloseImmediate();
            return;
        }

        // 仿原生金字塔缩放手柄（右上角，与主窗口一致）
        if (CurrentTab.CanResize)
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
                // 右上角手柄跟手（keep bottom-left：左/底边固定，右上角完全跟随鼠标）。
                // 手柄在哪个角就跟哪个角跑——所有布局统一，不随 tab 位置分叉
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

        // 拖 tab 中：更新命中目标（高亮）+ 画拖拽幽灵
        if (_tabDrag.Dragging != null)
        {
            UpdateDragTarget();
            DrawTabDragGhost();
        }

        PersistGeometry();
    }

    /// <summary>窗口几何记忆：变化时内存即时写回 tab 字段（退出/重载兜底落盘不丢最新值），
    /// 落盘只在稳定 ~800ms 后调度一次（缩放/移动连续变化期间不写盘）。
    /// 窗口名每次启动归零 → ini 尺寸记忆不可靠，必须自己持久化。</summary>
    private void PersistGeometry()
    {
        var size = ImGui.GetWindowSize();
        var pos = ImGui.GetWindowPos();
        if (_persistedGeo is null)
        {
            _persistedGeo = (size, pos); // 首帧基线（构造 Size 可能被 ini/首帧覆盖，取实际值）
            return;
        }

        var (lastSize, lastPos) = _persistedGeo.Value;
        var moved = Math.Abs(size.X - lastSize.X) > 0.5f || Math.Abs(size.Y - lastSize.Y) > 0.5f
                 || Math.Abs(pos.X - lastPos.X) > 0.5f || Math.Abs(pos.Y - lastPos.Y) > 0.5f;
        if (moved)
        {
            _geoChangeAt = Environment.TickCount64;
            _persistedGeo = (size, pos);
            WriteGeoToTabs(size, pos); // 内存即时（任何时刻退出，Dispose SaveConfig 落盘即最新）
            return;
        }

        if (_geoChangeAt != 0 && Environment.TickCount64 - _geoChangeAt > 800)
        {
            _geoChangeAt = 0;
            Plugin.DeferredSaveFrames = 60; // 稳定后延迟 ~1s 落盘一次
        }
    }

    private void WriteGeoToTabs(Vector2 size, Vector2 pos)
    {
        foreach (var t in Tabs)
        {
            t.PopOutSize = size;
            t.PopOutPos = pos;
        }
    }

    // ---- tab 栏（底部/顶部共用绘制；侧边单独） ----

    /// <summary>tab 栏高度（一行，与主窗口 tab 同尺寸基准）。静态：主窗口建窗定位复用。</summary>
    public static float TabBarHeight(Plugin plugin)
    {
        // 原生模式：与主窗口 tab 高度一致（17px×scale×TabScale，拆分）+ FramePadding.Y×2
        // （行高撑起量——中段/关闭钮 rect 高 = size+FramePadding.Y×2，缺它 tab 栏底部被切）
        if (Plugin.Config.NativeBackground)
            return 17f * ImGuiHelpers.GlobalScale * Plugin.Config.TabScale + ImGui.GetStyle().FramePadding.Y * 2;

        using var tabFont = plugin.FontManager.TabFont.Push();
        var style = ImGui.GetStyle();
        // 与 DrawPopOutTabBarLegacy 实际绘制高度同公式（/字号比：字号只改文字不改高）
        return (ImGui.GetTextLineHeight() / (Plugin.Config.TabFontSizePt / 12f) + style.FramePadding.Y * 2) * 0.9f;
    }

    private float PopOutTabBarHeight() => TabBarHeight(Plugin);

    /// <summary>侧边模式 tab 列宽：最宽 tab 文字 + 留白。</summary>
    private float TabSidebarWidth()
    {
        using var tabFont = Plugin.FontManager.TabFont.Push();
        var w = 0f;
        foreach (var t in Tabs)
            w = Math.Max(w, ImGui.CalcTextSize(t.Name).X);
        var style = ImGui.GetStyle();
        return w + style.FramePadding.X * 2 + 20f;
    }

    /// <summary>
    /// 底部/顶部 tab 栏：单 tab = 现状视觉（名字 + 关闭）；多 tab = 主窗口同款
    /// （仿原生三段式 / 非原生 legacy 背景条）。tab 按钮统一走 TabButton 交互（点击切换/长按拖拽）。
    /// </summary>
    private void DrawPopOutTabBar()
    {
        if (Plugin.Config.NativeBackground)
        {
            DrawPopOutTabBarNative();
            return;
        }

        // 单/多 tab 统一 legacy 视觉（背景条 + tab 按钮 + 行尾关闭）：
        // 单独弹出也是 tab 栏样式，与合并窗口一致（原单 tab 独立样式已删）
        DrawPopOutTabBarLegacy();
    }

    /// <summary>非原生多 tab 栏（legacy 背景条 + tab 按钮 + 分隔线，与主窗口 DrawBottomTabBarLegacy 同款公式）。</summary>
    private void DrawPopOutTabBarLegacy()
    {
        using var tabFont = Plugin.FontManager.TabFont.Push();
        var style = ImGui.GetStyle();
        var tabHeight = (ImGui.GetTextLineHeight() / (Plugin.Config.TabFontSizePt / 12f) + style.FramePadding.Y * 2) * 0.9f;
        var drawList = ImGui.GetWindowDrawList();
        var dividerColor = ImGui.GetColorU32(new Vector4(0.25f, 0.25f, 0.25f, 0.55f));
        var tabAlpha = Plugin.Config.TabAlpha / 100f;
        var isTopTabs = TabBarPlace() is TabPosition.Top;

        uint barBgColor;
        if (Plugin.Config.CustomTabBg && ColourUtil.RgbaToVector4(Plugin.Config.TabBgColor) is { } customTabBg)
        {
            var barAlpha = isTopTabs
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg].W * (Plugin.Config.WindowAlpha / 100f)
                : 0.5f * tabAlpha;
            barBgColor = ImGui.GetColorU32(new Vector4(customTabBg.X, customTabBg.Y, customTabBg.Z, barAlpha));
        }
        else
        {
            barBgColor = isTopTabs
                ? ImGui.GetColorU32(MessageLogBgColorForTab())
                : ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 0.5f * tabAlpha));
        }
        var activeColor = ImGui.GetColorU32(new Vector4(0.28f, 0.28f, 0.28f, 0.6f * tabAlpha));
        var barStart = ImGui.GetCursorScreenPos();
        drawList.AddRectFilled(barStart, new Vector2(barStart.X + ImGui.GetContentRegionAvail().X, barStart.Y + tabHeight), barBgColor, 8f,
            isTopTabs ? ImDrawFlags.RoundCornersTopRight : ImDrawFlags.None);

        var unreadGreen = UnreadColorForPopout();
        var transparent = new Vector4(0, 0, 0, 0);
        using var btnBg = ImRaii.PushColor(ImGuiCol.Button, transparent);
        using var btnHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.35f, 0.35f, 0.35f, 0.3f));
        using var btnActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.25f, 0.25f, 0.4f));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);

        // tab 自适应：窗口面积不足时按比例压缩 tab 宽（下限单字可点），文字随之缩减（与主窗口同策略）
        var closeW = ImGuiUtil.CalcIconButtonSize().X + ImGui.GetStyle().ItemSpacing.X;
        var sumFullW = 0f;
        var fullWidths = new List<float>(Tabs.Count);
        foreach (var t in Tabs)
        {
            var tw = ImGui.CalcTextSize(t.Name).X + style.FramePadding.X * 2 + 20f;
            fullWidths.Add(tw);
            sumFullW += tw;
        }
        var tabBudget = ImGui.GetContentRegionAvail().X - closeW;
        var widthFactor = tabBudget <= 0f ? 0.15f : Math.Min(1f, tabBudget / Math.Max(sumFullW, 1f));
        var minTabW = ImGui.CalcTextSize("字").X + style.FramePadding.X * 2 + 6f;
        // 硬约束：minTabW 下限使 Σ 可能超预算（窄窗多 tab → 右缘关闭按钮被挤出可视区）
        // → 实际宽度先按 factor 算再等比回缩，保底 4px 可点（文字 Fit 自动缩减）
        var actualWidths = new List<float>(Tabs.Count);
        var sumActual = 0f;
        for (var i = 0; i < Tabs.Count; i++)
        {
            var w = Math.Max(minTabW, fullWidths[i] * widthFactor);
            actualWidths.Add(w);
            sumActual += w;
        }
        if (sumActual > tabBudget && tabBudget > 0f)
        {
            var shrink = tabBudget / sumActual;
            for (var i = 0; i < actualWidths.Count; i++)
                actualWidths[i] = Math.Max(4f, actualWidths[i] * shrink);
        }

        var first = true;
        for (var i = 0; i < Tabs.Count; i++)
        {
            var tab = Tabs[i];
            var active = i == CurrentTabIdx;
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

            var size = new Vector2(actualWidths[i], tabHeight);
            using var unreadCol = ImRaii.PushColor(ImGuiCol.Text, unreadGreen, hasUnread);

            var clicked = ImGui.Button($"##popout-tab-{tab.Identifier}", size);
            var btnMin = ImGui.GetItemRectMin();
            var btnMax = ImGui.GetItemRectMax();
            if (active)
                drawList.AddRectFilled(btnMin, btnMax, activeColor);
            var activeFont = ImGui.GetFont();
            // 压缩后宽度不足时文字渐进缩减（与主窗口 FitTabText 同策略）
            var displayText = FitPopOutTabText(tab.Name, size.X - style.FramePadding.X * 2);
            var tabTextSize = ImGui.CalcTextSize(displayText);
            var effectiveFontSize = ImGui.GetFontSize();
            var fontScale = effectiveFontSize / activeFont.FontSize;
            var textPos = new Vector2(
                btnMin.X + (btnMax.X - btnMin.X - tabTextSize.X) / 2f,
                btnMin.Y + (btnMax.Y - btnMin.Y) / 2f - activeFont.Ascent * fontScale + effectiveFontSize * 0.38f - 2f * fontScale);
            drawList.AddText(activeFont, effectiveFontSize, textPos, ImGui.GetColorU32(ImGuiCol.Text), displayText);

            TabButtonInteraction(tab.Identifier, clicked, tab, () => SwitchTab(tab));

            ImGui.SameLine(0, 0);
        }

        // 右侧：关闭（收回当前 tab）——绝对定位行尾（X 用行首取的栏宽；
        // SameLine 后 GetContentRegionAvail().X 是剩余宽，会把按钮设到 tab 流中间）
        // 图标高钳到 tab 行高：iconSize > tabHeight 时按钮底超出 → NewLine 行高虚增（空行/手柄错位）
        var iconSize = Math.Min(ImGui.GetFrameHeight(), tabHeight);
        var iconTop = barStart.Y + (tabHeight - iconSize) / 2f;
        ImGui.SetCursorScreenPos(new Vector2(
            barStart.X + tabBudget + closeW - iconSize - ImGui.GetStyle().ItemSpacing.X, iconTop));
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, font: Plugin.FontManager.FontAwesomeSmall))
            CloseCurrentTab();
        if (ImGui.IsItemHovered())
            ImGuiUtil.Tooltip("关闭");

        ImGui.NewLine();
    }

    /// <summary>仿原生 tab 栏：单 tab = 左帽+中段+右帽+关闭（现状）；多 tab = 左帽+分割线+(中段+分割线)×N+右帽+关闭。</summary>
    private void DrawPopOutTabBarNative()
    {
        using var tabFont = Plugin.FontManager.TabFont.Push();

        var scale = ImGuiHelpers.GlobalScale;
        var cfgTabScale = Plugin.Config.TabScale;
        var tabHeight = 17f * scale * cfgTabScale;
        var tabScale = tabHeight / 48f;
        var capLeftSize = new Vector2(39f, 48f) * tabScale;
        var capRightSize = new Vector2(40f, 48f) * tabScale;
        var middleBaseSize = new Vector2(50f, 48f) * tabScale;
        var dividerSize = new Vector2(6f, 48f) * tabScale;
        var indicatorSize = new Vector2(21f, 21f) * tabScale;
        var indicatorOffset = new Vector2(1.0f, 1.5f) * scale;
        // 字号 = tab 高 3/5 基准 × 字号比（与主窗口同公式；高度不随字号变）
        var effectiveFontSize = tabHeight * 0.6f * (Plugin.Config.TabFontSizePt / 12f);
        var oneCharW = effectiveFontSize * 0.5f;
        var dl = ImGui.GetWindowDrawList();
        var tabTextColor = ImGui.GetColorU32(new Vector4(238f / 255f, 236f / 255f, 215f / 255f, 1f));
        var availWidth = ImGui.GetContentRegionAvail().X;
        var capLeft = NativeIcons.TabCapLeft;
        var capRight = NativeIcons.TabCapRight;
        var middle = NativeIcons.TabMiddle;
        var divider = NativeIcons.TabDivider;
        var indicator = NativeIcons.TabIndicator;

        // tab 自适应长度（与主窗口同策略）：面积不足按比例缩短中段，文字渐进缩减
        // （factor 上限 1：面积充足时保持自然宽度，不拉伸——拉伸会破坏 tab 宽度控制）。
        // 关闭按钮绝对定位在行尾，也要计入固定开销
        var closeBtnW = ImGuiUtil.CalcIconButtonSize().X + ImGui.GetStyle().ItemSpacing.X;
        var fixedTabW = capLeftSize.X + dividerSize.X + capRightSize.X + closeBtnW;
        var fullTabWidths = new List<float>(Tabs.Count);
        var sumFullTabW = 0f;
        foreach (var t in Tabs)
        {
            var tw = Math.Max(middleBaseSize.X, ImGui.CalcTextSize(t.Name).X + oneCharW * 2);
            fullTabWidths.Add(tw);
            sumFullTabW += tw;
        }
        var tabBudget = availWidth - fixedTabW - dividerSize.X * Tabs.Count;
        var tabWidthFactor = tabBudget <= 0f ? 0.15f : Math.Min(1f, tabBudget / Math.Max(sumFullTabW, 1f));
        var minMidW = effectiveFontSize + oneCharW * 2;
        // 硬约束：minMidW 下限使 Σ 可能超预算（窄窗多 tab → 右帽/关闭按钮被挤出可视区）
        // → 实际宽度先按 factor 算再等比回缩，保底 4px 可点（文字 Fit 自动缩减）
        var actualWidths = new List<float>(Tabs.Count);
        var sumActual = 0f;
        for (var i = 0; i < Tabs.Count; i++)
        {
            var w = Math.Max(minMidW, fullTabWidths[i] * tabWidthFactor);
            actualWidths.Add(w);
            sumActual += w;
        }
        if (sumActual > tabBudget && tabBudget > 0f)
        {
            var shrink = tabBudget / sumActual;
            for (var i = 0; i < actualWidths.Count; i++)
                actualWidths[i] = Math.Max(4f, actualWidths[i] * shrink);
        }

        // 左帽
        if (capLeft != null)
        {
            var start = ImGui.GetCursorScreenPos();
            dl.AddImage(capLeft.Handle, start, start + capLeftSize);
            ImGui.Dummy(capLeftSize);
            ImGui.SameLine(0, 0);
        }

        void DrawDivider()
        {
            if (divider == null)
                return;
            var pos = ImGui.GetCursorScreenPos();
            dl.AddImage(divider.Handle, pos, pos + dividerSize);
            ImGui.Dummy(dividerSize);
            ImGui.SameLine(0, 0);
        }

        // 左帽后固定一根分隔线（与主窗口同结构：左帽-分隔线-中段-分隔线-...-右帽）
        DrawDivider();

        var unreadGreen = UnreadColorForPopout();
        var transparent = new Vector4(0, 0, 0, 0);
        using var btnBg = ImRaii.PushColor(ImGuiCol.Button, transparent);
        using var btnHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, transparent);
        using var btnActive = ImRaii.PushColor(ImGuiCol.ButtonActive, transparent);
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f);

        for (var i = 0; i < Tabs.Count; i++)
        {
            var tab = Tabs[i];
            var active = i == CurrentTabIdx;
            var hasUnread = !active && tab.UnreadMode != UnreadMode.None && tab.Unread > 0
                && Plugin.Config.UnreadNotifyMode != UnreadNotifyMode.None;

            var mSize = new Vector2(actualWidths[i], tabHeight);
            var mMin = ImGui.GetCursorScreenPos();
            if (middle != null)
            {
                dl.AddImage(middle.Handle, mMin, mMin + mSize);
                ImGui.Dummy(mSize);
                var btnMin = ImGui.GetItemRectMin();
                var btnMax = ImGui.GetItemRectMax();
                if (ImGui.IsItemHovered())
                {
                    Plugin.AnyInteractiveHovered = true;
                    var glowMin = btnMin + new Vector2(0f, 1.5f) * scale;
                    var glowMax = btnMax - new Vector2(0f, 1.5f) * scale;
                    dl.AddRectFilled(glowMin, glowMax, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f)), 3f);
                }
                if (active && indicator != null)
                {
                    var indMin = btnMin + indicatorOffset;
                    dl.AddImage(indicator.Handle, indMin, indMin + indicatorSize);
                }
            }
            else
            {
                ImGui.TextUnformatted(tab.Name);
            }

            // 文字（与主窗口同公式：AddText pos=顶，几何居中 + 右移 5px×scale×cfgTabScale）；
            // 压缩后宽度不足时文字渐进缩减（与主窗口 FitTabText 同策略）
            var activeFont = ImGui.GetFont();
            var displayText = FitPopOutTabText(tab.Name, mSize.X - oneCharW * 2);
            var textSize = ImGui.CalcTextSize(displayText);
            var fontScale = effectiveFontSize / activeFont.FontSize;
            var textPos = new Vector2(
                mMin.X + (mSize.X - textSize.X) / 2f + 5f * scale * cfgTabScale,
                mMin.Y + (mSize.Y - effectiveFontSize) / 2f - 2f * fontScale);
            dl.AddText(activeFont, effectiveFontSize, textPos,
                hasUnread ? ImGui.GetColorU32(unreadGreen) : tabTextColor, displayText);

            // 中段按钮命中（InvisibleButton 覆盖中段矩形；点击切换 + 长按拖拽）
            var clicked = false;
            if (middle != null)
            {
                var prevPos = ImGui.GetCursorPos();
                ImGui.SetCursorScreenPos(mMin);
                clicked = ImGui.InvisibleButton($"##popout-tab-native-{tab.Identifier}", mSize);
                ImGui.SetCursorPos(prevPos);
            }
            TabButtonInteraction(tab.Identifier, clicked, tab, () => SwitchTab(tab));

            ImGui.SameLine(0, 0);
            // 每中段后一根分隔线（含最后一个——中段与右帽之间也要，与主窗口同结构）
            DrawDivider();
        }

        // 右帽
        if (capRight != null)
        {
            var end = ImGui.GetCursorScreenPos();
            dl.AddImage(capRight.Handle, end, end + capRightSize);
            ImGui.Dummy(capRightSize);
            ImGui.SameLine(0, 0);
        }

        // 右侧：原生关闭按钮（收回当前 tab）
        var btnSize = new Vector2(ImGuiUtil.CalcIconButtonSize().X, tabHeight);
        ImGui.SetCursorPosX(availWidth - btnSize.X - ImGui.GetStyle().ItemSpacing.X);
        if (ImGuiUtil.NativeIconButton(NativeIcons.Close, "popout-close", "关闭", FontAwesomeIcon.Times,
                size: btnSize, sfx: ImGuiUtil.BtnSfx.Dismiss))
            CloseCurrentTab();
    }

    /// <summary>侧边模式：垂直 tab 列表（Selectable，与主窗口 DrawTabSidebar 同构）。</summary>
    private void DrawPopOutTabBarSide()
    {
        using var tabFont = Plugin.FontManager.TabFont.Push();
        var childHeight = ImGui.GetContentRegionAvail().Y;
        using (var child = ImRaii.Child("##popout-tab-sidebar", new Vector2(-1, childHeight), false, ImGuiWindowFlags.NoScrollbar))
        {
            if (!child)
                return;
            var unreadGreen = UnreadColorForPopout();
            for (var i = 0; i < Tabs.Count; i++)
            {
                var tab = Tabs[i];
                var active = i == CurrentTabIdx;
                var hasUnread = !active && tab.UnreadMode != UnreadMode.None && tab.Unread > 0
                    && Plugin.Config.UnreadNotifyMode != UnreadNotifyMode.None;
                using var unreadCol = ImRaii.PushColor(ImGuiCol.Text, unreadGreen, hasUnread);
                var clicked = ImGui.Selectable($"{tab.Name}###popout-tab-side-{tab.Identifier}", active);
                TabButtonInteraction(tab.Identifier, clicked, tab, () => SwitchTab(tab));
            }

            // 底部：关闭（收回当前 tab）
            ImGui.Spacing();
            if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, font: Plugin.FontManager.FontAwesomeSmall))
                CloseCurrentTab();
            if (ImGui.IsItemHovered())
                ImGuiUtil.Tooltip("关闭");
        }
    }

    /// <summary>tab 文字按可用宽度渐进缩减（完整 → 逐字 → 单字）；宽度充足时返回原样。</summary>
    private static string FitPopOutTabText(string name, float availW)
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

    // ---- tab 按钮交互（点击切换 + 600ms 长按拖拽，与主窗口 tab 拖出同款阈值） ----

    /// <summary>统一 tab 按钮交互（与主窗口共用 TabDragTracker 判定：600ms 长按 + 10px 移动）。
    /// clicked = 本帧按钮点击；onClick = 切换回调（仅未长按时触发）。</summary>
    private void TabButtonInteraction(Guid tabId, bool clicked, Tab tab, Action onClick)
    {
        var now = Environment.TickCount64;
        var scale = ImGuiHelpers.GlobalScale;
        var leftDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var mousePos = ImGui.GetIO().MousePos;

        _tabDrag.TrackPress(ImGui.IsItemActive(), tabId, now, mousePos);
        var dragBegan = _tabDrag.TryBeginDrag(tabId, leftDown, now, mousePos, scale);
        if (dragBegan)
            _draggingTab = tab; // 拖拽目标 tab（幽灵/EndTabDrag 用）
        _tabDrag.UpdateActive(tabId, leftDown, now);
        var dragEnded = _tabDrag.TryEndDrag(tabId, leftDown, now);
        if (dragEnded == DragEndResult.Completed)
        {
            _draggingTab = null;
            EndTabDrag(tab, mousePos);
            return; // 拖拽松手非点击，跳过切换
        }
        if (dragEnded == DragEndResult.Stale)
        {
            // 拖拽中断过（隐藏恢复）→ 取消；清理命中高亮
            _draggingTab = null;
            if (Plugin.ChatLog.DragHighlightWindow != null)
            {
                Plugin.ChatLog.DragHighlightWindow.DragHighlight = false;
                Plugin.ChatLog.DragHighlightWindow = null;
            }
            return;
        }

        if (clicked && _tabDrag.Dragging == null)
            onClick();
    }

    /// <summary>拖拽中更新命中目标（高亮）；每帧由 Draw 调用。</summary>
    private void UpdateDragTarget()
    {
        var target = Plugin.ChatLog.FindPopOutTarget(ImGui.GetIO().MousePos, this);
        if (Plugin.ChatLog.DragHighlightWindow != null && Plugin.ChatLog.DragHighlightWindow != target)
            Plugin.ChatLog.DragHighlightWindow.DragHighlight = false;
        Plugin.ChatLog.DragHighlightWindow = target;
        if (target != null)
            target.DragHighlight = true;
    }

    /// <summary>拖拽幽灵：鼠标处画 tab 名小标签（半透明跟随）。</summary>
    private void DrawTabDragGhost()
    {
        if (_draggingTab == null)
            return;
        var fg = ImGui.GetForegroundDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var p = ImGui.GetIO().MousePos + new Vector2(8f, 8f) * scale;
        var tabFont = Plugin.FontManager.TabFont;
        var push = tabFont.Available ? tabFont.Push() : null;
        try
        {
            var textSize = ImGui.CalcTextSize(_draggingTab.Name);
            var pad = 6f * scale;
            fg.AddRectFilled(p, p + textSize + new Vector2(pad * 2, pad), ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 0.85f)), 4f);
            fg.AddText(p + new Vector2(pad, pad / 2f), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.9f)), _draggingTab.Name);
        }
        finally
        {
            push?.Dispose();
        }
    }

    /// <summary>拖拽结束：命中目标 → 合并；多 tab → 分离；单 tab → 移动窗口。</summary>
    private void EndTabDrag(Tab tab, Vector2 releasePos)
    {
        var target = Plugin.ChatLog.FindPopOutTarget(releasePos, this);
        if (Plugin.ChatLog.DragHighlightWindow != null)
        {
            Plugin.ChatLog.DragHighlightWindow.DragHighlight = false;
            Plugin.ChatLog.DragHighlightWindow = null;
        }

        // 合并优先：窗口重叠/邻近时释放点可能同时落在源、目标 tab 栏内，
        // 先判定命中（拖到别的窗口 tab 栏 = 合并）；未命中再查自己 tab 栏 = 取消
        if (target != null)
        {
            // 防重复：tab 已在目标窗口（重复合并/残留映射）→ 视为取消，不迁移不分离
            if (target.ContainsTab(tab.Identifier))
                return;
            // 合并：handler/state 随行迁移
            var handler = Handlers[tab.Identifier];
            var state = MsgStates[tab.Identifier];
            target.AddTabFrom(tab, handler, state);
            Plugin.ChatLog.PopOutInstances[tab.Identifier] = target;
            Plugin.SaveConfig(); // 组迁移 + 成员几何同步持久化
            if (RemoveTabInternal(tab.Identifier))
                CloseImmediate(); // 源窗口空了 → 立即关闭（不等下一帧 OnClose，防残留撞名）
        }
        else if (TabBarMin != Vector2.Zero
            && releasePos.X >= TabBarMin.X && releasePos.X <= TabBarMax.X
            && releasePos.Y >= TabBarMin.Y && releasePos.Y <= TabBarMax.Y)
        {
            // 拖回自己窗口 tab 栏 = 取消（不分离不移动）
        }
        else if (Tabs.Count > 1)
        {
            // 分离：状态随行迁移（滚动/选字/输入缓存保留），新窗口定位释放点。
            // 覆盖 AddTabInternal 新建的 handler/state（孤儿无事件订阅，可被 GC）。
            var handler = Handlers[tab.Identifier];
            var state = MsgStates[tab.Identifier];
            RemoveTabInternal(tab.Identifier);
            // 定位：释放点对齐新窗口 tab 条（Top/Side 非底部，换算见构造 PosFromReleasePoint）
            var w = new Popout(Plugin, tab, releasePos);
            w.Handlers[tab.Identifier] = handler;
            w.MsgStates[tab.Identifier] = state;
            handler.MainWindow = w;
            // 防御：窗口名实例唯一，同名残留仅剩"未成功移除的泄漏窗口"这一种 → 关闭后再添加。
            //（不能用 tab 命名：多 tab 窗口名=第一个 tab id，分离该 tab 时 existing 会命中源窗口
            //  自身 → 误关源窗 → 其余 tab 被收回主窗口）
            var existing = Plugin.WindowSystem.Windows.FirstOrDefault(x => x.WindowName == w.WindowName);
            if (existing is Popout stale)
                stale.CloseImmediate();
            Plugin.WindowSystem.AddWindow(w);
            Plugin.ChatLog.PopOutInstances[tab.Identifier] = w;
            if (Tabs.Count == 0)
                CloseImmediate();
        }
        else
        {
            // 单 tab 拖出空白 = 移动窗口（X 中心对齐既有行为；Y/X 按 tab 条实际位置：
            // Top 条在窗顶、Side 列在窗左，Bottom 条在窗底）。
            // 已显示窗口 Once 条件不生效 → 用 SetWindowPos 立即定位（不钉死，仅本帧生效）
            var size = ImGui.GetWindowSize();
            Vector2 newPos;
            switch (TabBarPlace())
            {
                case TabPosition.Top:
                    newPos = releasePos - new Vector2(size.X / 2f, TabBarHeight(Plugin) / 2f);
                    break;
                case TabPosition.Side:
                    newPos = releasePos - new Vector2(TabSidebarWidth() / 2f, size.Y / 2f);
                    break;
                default: // Bottom
                    newPos = releasePos - new Vector2(size.X / 2f, size.Y - TabBarHeight(Plugin) / 2f);
                    break;
            }
            ImGui.SetWindowPos(ClampToViewport(newPos, size));
        }
    }

    /// <summary>释放点钳制到视口内（窗口整体可见）。</summary>
    private static Vector2 ClampToViewport(Vector2 pos, Vector2 winSize)
    {
        var vp = ImGuiHelpers.MainViewport;
        return new Vector2(
            Math.Clamp(pos.X, vp.Pos.X, vp.Pos.X + Math.Max(0f, vp.Size.X - winSize.X)),
            Math.Clamp(pos.Y, vp.Pos.Y, vp.Pos.Y + Math.Max(0f, vp.Size.Y - winSize.Y)));
    }

    /// <summary>切换当前 tab：音效 1 + 未读清零 + 跨 tab 未读同步（不碰主窗口 LastTab）。</summary>
    private void SwitchTab(Tab tab)
    {
        if (CurrentTab == tab)
            return;
        CurrentTabIdx = Tabs.FindIndex(t => t.Identifier == tab.Identifier);
        if (CurrentTabIdx < 0)
            return;
        if (Plugin.Config.PlaySounds)
            unsafe { UIGlobals.PlaySoundEffect(1); }
        tab.Unread = 0;
        Plugin.ChatLog.SyncSeenAcrossTabs(tab);
    }

    /// <summary>关闭按钮：收回当前 tab（多 tab 自动切相邻；单 tab 关窗走 OnClose）。</summary>
    private void CloseCurrentTab()
    {
        var tab = CurrentTab;
        if (Tabs.Count > 1)
        {
            RecallTab(tab);
            Plugin.ChatLog.PopOutInstances.Remove(tab.Identifier);
            RemoveTabInternal(tab.Identifier);
            Plugin.SaveConfig();
        }
        else
        {
            // CloseImmediate 而非 IsOpen=false：后者依赖状态机（窗口从未绘制过时
            // internalLastIsOpen 停在 false → OnClose 永不调用 → 窗口残留 WindowSystem，
            // 同名重建 AddWindow 崩溃）；立即回收 + 移除
            CloseImmediate();
        }
    }

    // ---- 绘制辅助（与主窗口同款公式的简化本地副本） ----

    private uint MessageLogBgColorForTab()
    {
        if (Plugin.Config.CustomMessageLogBg && ColourUtil.RgbaToVector4(Plugin.Config.MessageLogBgColor) is { } c)
            return ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, Plugin.Config.WindowAlpha / 100f));
        var bg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        return ImGui.GetColorU32(new Vector4(bg.X, bg.Y, bg.Z, Plugin.Config.WindowAlpha / 100f));
    }

    /// <summary>未读 tab 文字颜色（与主窗口 UnreadColor 同逻辑）。</summary>
    private Vector4 UnreadColorForPopout()
    {
        switch (Plugin.Config.UnreadNotifyMode)
        {
            case UnreadNotifyMode.Highlight:
                return new Vector4(0.224f, 1f, 0.078f, 1f);
            case UnreadNotifyMode.None:
                return new Vector4(1f, 1f, 1f, 1f);
            default:
            {
                var pulse = 0.5f + 0.5f * MathF.Sin(Environment.TickCount * 0.004f);
                return new Vector4(0.224f * pulse, 1.0f * pulse, 0.078f * pulse, 1.0f);
            }
        }
    }

    // 原生缩放手柄素材（常态/高亮态，与主窗口同款）
    private void DrawTopRightResizeHandle()
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var style = ImGui.GetStyle();
        var hSize = NativeIcons.ResizeHandleSize();
        var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
        var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
        // Top 布局手柄下移 tab 条高度（与主窗口 GetTopTabBarHeight 同规则）
        var topBarOffset = TabBarPlace() == TabPosition.Top ? PopOutTabBarHeight() : 0f;
        var localPos = new Vector2(
            windowSize.X - hSize - style.WindowPadding.X - insetX,
            style.WindowPadding.Y + insetY + topBarOffset);

        var mousePos = ImGui.GetIO().MousePos;
        var handleRectMin = windowPos + localPos;
        var handleRectMax = handleRectMin + new Vector2(hSize, hSize);
        var hovered = mousePos.X >= handleRectMin.X && mousePos.X <= handleRectMax.X
                      && mousePos.Y >= handleRectMin.Y && mousePos.Y <= handleRectMax.Y;

        if (hovered || IsResizingTopRight)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
    }

    public override void PostDraw()
    {
        // 空窗防御：合并/分离当帧 Tabs 可能已清空（IsOpen=false 不影响本帧 PostDraw 执行），
        // CurrentTab 越界会中断整个 WindowSystem.Draw
        if (Tabs.Count == 0)
            return;
        _docked = ImGui.IsWindowDocked();
        UpdateTabBarRect();

        // 命中目标高亮（浏览器式：tab 栏外圈描边 + 半透明填充）
        if (DragHighlight && Plugin.ChatLog.DragHighlightWindow == this)
        {
            var dl = ImGui.GetForegroundDrawList();
            var min = TabBarMin - new Vector2(3f, 3f);
            var max = TabBarMax + new Vector2(3f, 3f);
            dl.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.4f, 0.22f)), 4f);
            dl.AddRect(min, max, ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.4f, 0.95f)), 4f, ImDrawFlags.None, 2f);
        }

        if (Plugin.Config is { OverrideStyle: true, ChosenStyle: not null })
            StyleModel.GetConfiguredStyles()?.FirstOrDefault(style => style.Name == Plugin.Config.ChosenStyle)?.Pop();

        // （前台 dl；End 后 GetWindowPos 不可靠 → 用 LastWindowPos）
        if (CurrentTab.CanResize)
        {
            var style = ImGui.GetStyle();
            var hSize = NativeIcons.ResizeHandleSize();
            var insetX = NativeIcons.ResizeHandleInsetX(Plugin.Config.NativeBackground);
            var insetY = NativeIcons.ResizeHandleInsetY(Plugin.Config.NativeBackground);
            // Top 布局手柄下移 tab 条高度（与主窗口 GetTopTabBarHeight 同规则）
            var topBarOffset = TabBarPlace() == TabPosition.Top ? PopOutTabBarHeight() : 0f;
            var localPos = new Vector2(
                LastWindowSize.X - hSize - style.WindowPadding.X - insetX,
                style.WindowPadding.Y + insetY + topBarOffset);
            NativeIcons.DrawResizeHandle(ImGui.GetForegroundDrawList(), LastWindowPos + localPos,
                new Vector2(hSize, hSize), MouseOverResizeHandle || IsResizingTopRight);
        }
    }

    /// <summary>tab 栏屏幕矩形（拖拽命中判定用；PostDraw 每帧更新）。</summary>
    private void UpdateTabBarRect()
    {
        var pos = LastWindowPos;
        var size = LastWindowSize;
        switch (TabBarPlace())
        {
            case TabPosition.Top:
                TabBarMin = pos;
                TabBarMax = new Vector2(pos.X + size.X, pos.Y + PopOutTabBarHeight());
                break;
            case TabPosition.Side:
                TabBarMin = pos;
                TabBarMax = new Vector2(pos.X + TabSidebarWidth(), pos.Y + size.Y);
                break;
            default:
                TabBarMin = new Vector2(pos.X, pos.Y + size.Y - PopOutTabBarHeight());
                TabBarMax = new Vector2(pos.X + size.X, pos.Y + size.Y);
                break;
        }
    }

    public override void OnClose()
    {
        if (_closed)
            return;
        _closed = true;
        // 几何兜底写回（节流未到期就关闭时也要记住；未 Draw 过则 _persistedGeo 为 null 不写）
        if (_persistedGeo is { } g)
            WriteGeoToTabs(g.size, g.pos);
        // 所有剩余 tab 收回主窗口（含拖拽合并后空窗关闭的情况——Tabs 为空则无操作）
        foreach (var t in Tabs)
        {
            Plugin.ChatLog.PopOutInstances.Remove(t.Identifier);
            RecallTab(t);
        }
        Tabs.Clear();
        Handlers.Clear();
        MsgStates.Clear();
        Plugin.WindowSystem.RemoveWindow(this);
        Plugin.SaveConfig();
    }

    /// <summary>立即关闭并从 WindowSystem 移除（不等下一帧 DrawInternal 状态机调 OnClose；
    /// 幂等——内部 OnClose 有 _closed 防重复）。合并/自愈空窗路径统一走这里。</summary>
    public void CloseImmediate()
    {
        IsOpen = false;
        OnClose();
    }
}
