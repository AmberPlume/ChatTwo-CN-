using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using ChatTwo.Ipc;
using ChatTwo.Resources;
using ChatTwo.Ui;
using ChatTwo.Ui.ChatLog;
using ChatTwo.Util;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChatTwo;

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class Plugin : IDalamudPlugin
{
    public const string PluginName = "Chat 2";

    // !!! 精细鼠标光标（游戏原生）：NoMouseCursorChange 下 ImGui 不碰光标；
    // 可点击元素（按钮/tab/链接）hover 时显示**游戏自己的光标句柄**（原生手指）。
    // 方案：hook user32 SetCursor（游戏设光标必经）——detour 里鼠标在聊天窗口可点击
    // 元素上时把句柄换成游戏手指（Cursor+0x70）再放行：完全同步、无闪烁、无音效。
    // 句柄偏移扫描确认：Arrow=+0x18 / Clickable=+0x70（结构固定，句柄值每进程变）。
    internal static bool CursorInChatWindow;
    internal static bool AnyInteractiveHovered;
    // !!! detour 专用缓存（主线程帧末更新；volatile 保证游戏线程可见）：
    // 工作 flag 帧末清空，游戏线程的 SetCursor 常发生在清空之后 → 直接读工作 flag 永远 false
    private static volatile bool _detourInChat;
    private static volatile bool _detourClickable;


    /// <summary>标记鼠标在本聊天窗口内（三窗口共用；帧末 UpdateCursorDecision 统一决策光标）。
    /// !!! 用鼠标位置 vs 窗口矩形判定（不用 IsWindowHovered——按下瞬间它返回 false，
    /// 会导致点击时手指消失）；与 hover/焦点/弹窗无关。</summary>
    internal static void MarkCursorInChatWindow()
    {
        var mp = ImGui.GetIO().MousePos;
        var wMin = ImGui.GetWindowPos();
        var wMax = wMin + ImGui.GetWindowSize();
        if (mp.X >= wMin.X && mp.X <= wMax.X && mp.Y >= wMin.Y && mp.Y <= wMax.Y)
            CursorInChatWindow = true;
    }

    /// <summary>帧末：把本帧 ImGui 计算的 hover 状态缓存给 detour（下一帧持续有效），并清工作 flag。</summary>
    internal static void UpdateCursorDecision()
    {
        var prevClickable = _detourInChat && _detourClickable;
        _detourInChat = CursorInChatWindow;
        _detourClickable = AnyInteractiveHovered;
        // 手指状态 false→true（进入可点击元素）时播一次游戏原生悬停音（SetCursorType 触发）；
        // 只在进入时调一次——hook 拦截显示层不受影响，也不会连续响
        if (!prevClickable && _detourInChat && _detourClickable)
            PlayHoverSfx();
        CursorInChatWindow = false;
        AnyInteractiveHovered = false;
    }

    /// <summary>游戏原生悬停音：调 SetCursorType 让游戏播"进入可点击区域"的音效（原生行为）。
    /// 仅显示层 hook 控制光标，此调用只播音效/同步内部状态，不影响手指显示。</summary>
    private static unsafe void PlayHoverSfx()
    {
        try
        {
            var stage = AtkStage.Instance();
            if (stage != null)
                stage->AtkCursor.SetCursorType(AtkCursor.CursorType.Clickable, true);
        }
        catch (Exception ex)
        {
            Log.Error($"[Cursor] hover sfx failed: {ex.Message}");
        }
    }

    // !!! hook user32 SetCursor：游戏设光标必经此处，detour 按缓存状态换句柄
    //（观察版 hook 曾验证此路径有效；用 GetProcAddress 拿导出地址，非游戏模块内偏移）
    private static Hook<SetCursorDelegate>? _setCursorHook;
    private delegate nint SetCursorDelegate(nint hCursor);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint GetModuleHandle(string moduleName);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string procName);

    private void InitCursorHook()
    {
        try
        {
            var user32 = GetModuleHandle("user32.dll");
            var addr = GetProcAddress(user32, "SetCursor");
            if (addr == 0)
                return;
            _setCursorHook = GameInteropProvider.HookFromAddress<SetCursorDelegate>(addr, SetCursorDetour);
            _setCursorHook.Enable();
        }
        catch (Exception ex)
        {
            Log.Error($"[CursorHook] init failed: {ex.Message}");
        }
    }

    /// <summary>鼠标在聊天窗口内且 hover 可点击元素 → 游戏手指句柄；否则原样透传。
    /// !!! detour 必须极简（任意线程可能调用）：无分配、只读 volatile bool + 一次内存读取。</summary>
    private static unsafe nint SetCursorDetour(nint hCursor)
    {
        if (_detourInChat && _detourClickable)
        {
            var cursor = Cursor.Instance();
            if (cursor != null)
            {
                var clickable = *(nint*)((byte*)cursor + 0x70);  // 扫描确认：Clickable=+0x70
                if (clickable != 0)
                    return _setCursorHook!.Original(clickable);
            }
        }
        return _setCursorHook!.Original(hCursor);
    }


    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IDalamudPluginInterface Interface { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IKeyState KeyState { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static IPartyList PartyList { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] public static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] public static INotificationManager Notification { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static ISeStringEvaluator Evaluator { get; private set; } = null!;
    [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] public static IPartyFinderGui PartyFinderGui { get; private set; } = null!;

    public static Configuration Config = null!;
    public static FileDialogManager FileDialogManager { get; private set; } = null!;

    /// <summary>原生右键菜单是否激活（非null表示激活），由 PayloadHandler 设置，ChatLog.PreDraw 使用</summary>
    public static bool ContextMenuActive;

    /// <summary>ChatTwo 触发的菜单会话是否进行中（新增）。
    /// 区分"ChatTwo 触发的菜单"与"游戏原生/背包等触发的菜单"：
    /// OwnerAddon 恒 0 后两者无法区分（背包二级菜单的 OwnerAddon 也是 0），
    /// 而二级菜单（AddonContextSub）应只对 ChatTwo 会话移动位置。
    /// PayloadHandler 触发时置 true；一级菜单关闭且无二级菜单显示时置 false。</summary>
    public static bool ChatTwoMenuSession;

    // !!! 兜底复位（实测：左键点击玩家后聊天框穿透保持，直到原生菜单开关才恢复）：
    // 菜单标志置位但菜单（或二级菜单）持续不可见超时 → FrameworkUpdate 强制复位，防 NoMouseInputs 残留。
    public static long ContextMenuActivatedAt;
    public static long ChatTwoMenuSessionAt;

    public readonly WindowSystem WindowSystem = new(PluginName);
    public SettingsWindow SettingsWindow { get; }
    public ChatLog ChatLog { get; }
    public DbViewer DbViewer { get; }
    public SearchWindow SearchWindow { get; }
    public CommandHelpWindow CommandHelpWindow { get; }

    public Commands Commands { get; }
    public GameFunctions.GameFunctions Functions { get; }
    public GameFunctions.ContextMenuHandler ContextMenuHandler { get; }
    public MessageManager MessageManager { get; }
    public IpcManager Ipc { get; }
    public ExtraChat ExtraChat { get; }
    public TypingIpc TypingIpc { get; }
    public FontManager FontManager { get; }

    public int DeferredSaveFrames = -1;

    public DateTime GameStarted { get; }

    public Vector4 DefaultText = Vector4.Zero;

    // Tab management needs to happen outside the chatlog window class for access reasons
    public int LastTab { get; set; }
    public int? WantedTab { get; set; }
    public Tab CurrentTab
    {
        get
        {
            var i = LastTab;
            return i > -1 && i < Config.Tabs.Count ? Config.Tabs[i] : new Tab();
        }
    }

    public Plugin()
    {
        try
        {
            GameStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime();

            // !!! 鼠标指针保持游戏原生：禁止 ImGui 修改 OS 光标。ImGui 默认在
            // hover 窗口时把光标设成 Windows 箭头（覆盖 FFXIV 游戏指针）；SetMouseCursor(None)
            // 方案实测指针直接消失（ImGui 的 None 会调 SetCursor(NULL) 隐藏）。NoMouseCursorChange
            // 让 ImGui 完全不碰光标 → 游戏原生指针全程保持。
            ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;

            // !!! hook user32 SetCursor：游戏设光标必经，detour 换手指句柄实现稳定原生手指
            InitCursorHook();

            // !!! 音效观察 hook（复测）

            // !!! 观察游戏 UI 音效（定位原生按钮点击音效，替换 BtnSfx 手动试听值）

            Config = Interface.GetPluginConfig() as Configuration ?? new Configuration();
            // 四透明度迁移：新字段（背景/标签页/输入框透明度）首次复制消息区透明度
            Config.EnsureAlphaMigration();

            // 以下选项已锁定，不再显示在设置界面中：
            // 强制开启：播放音效 / 显示新人频道加入按钮 / 显示隐藏按钮 / 显示原始道具帮助
            Config.PlaySounds = true;
            Config.ShowNoviceNetwork = true;
            Config.ShowHideButton = true;
            Config.NativeItemTooltips = true;
            // 强制关闭：显示聊天窗口标题栏 / 显示弹出标签页标题栏
            Config.ShowTitleBar = false;
            Config.ShowPopOutTitleBar = false;
            // 频道切换策略：灵活模式（体感灵活，保留）。
            // !!! 原版遗留：L133 曾有"偏好页已删除：热键固定严格模式"的覆盖行，与这里的 Flexible
            // 冲突（后写覆盖先写），且与体感（按住 W 移动时也能切频道）矛盾 → 已删除 Strict 覆盖行。
            // Flexible：修饰键"包含"即触发（Ctrl+R、Ctrl+Shift+R 都行）；Strict 需"完全相等"。
            Config.KeybindMode = KeybindMode.Flexible;
            // 中文适配：界面语言固定简体中文。
            // !!! 不能设为 None：None 会跟随 Interface.UiLanguage（国服卫月返回 "en"），界面会变英文
            Config.LanguageOverride = LanguageOverride.ChineseSimplified;
            // 命令帮助方向功能已从设置移除，保持关闭
            Config.CommandHelpSide = CommandHelpSide.None;
            // 已删除的设置项锁定默认值（等效于功能关闭）
            // !!! v1.40.17 清理：PrettierTimestamps/MoreCompactPretty/HideSameTimestamps（原作者
            // "现代化布局"表格渲染）字段已删除，时间戳统一走 DrawTimestampInline 行内渲染
            Config.HideInBattle = false;            // 在战斗中隐藏聊天窗口
            Config.HideWhenInactive = false;        // 非活动时隐藏（已从设置移除）
            Config.InactivityHideActiveDuringBattle = false;
            // 采集/制作消息不记录（设置项已从历史记录页移除）
            Config.DatabaseGatherCraftMessages = false;
            // 未读模式固定为"未看过的"（设置项已从标签页页删除）
            foreach (var tab in Config.Tabs)
            {
                tab.HideInBattle = false;
                tab.UnreadMode = UnreadMode.Unseen;
            }

            if (Config.Tabs.Count == 0)
                Config.Tabs.Add(TabsUtil.VanillaGeneral);

            LanguageChanged(Interface.UiLanguage);
            ImGuiUtil.Initialize(this);

            FileDialogManager = new FileDialogManager();

            // This is called by followup functions if the player is already logged in

            Commands = new Commands();
            Functions = new GameFunctions.GameFunctions(this);

            ContextMenuHandler = new GameFunctions.ContextMenuHandler(this);
            Ipc = new IpcManager();

            TypingIpc = new TypingIpc(this);
            ExtraChat = new ExtraChat();
            FontManager = new FontManager();

            CensorFilter.Initialize();

            MessageManager = new MessageManager(this); // Does it require UI?

            ChatLog = new ChatLog(this);
            SettingsWindow = new SettingsWindow(this);
            DbViewer = new DbViewer(this);
            SearchWindow = new SearchWindow(this);
            CommandHelpWindow = new CommandHelpWindow(ChatLog);

            WindowSystem.AddWindow(ChatLog);
            WindowSystem.AddWindow(SettingsWindow);
            WindowSystem.AddWindow(DbViewer);
            WindowSystem.AddWindow(SearchWindow);
            WindowSystem.AddWindow(CommandHelpWindow);

            FontManager.BuildFonts();

            // 原生 UI 图标（聊天记录窗口工具栏使用的 FFXIV 原生 PNG）。
            // !!! 修复：恢复构造函数直接 Load（首次成功验证过的逻辑， 日志
            // search=True）。之前改懒加载时把这里的调用删了、又没给 NativeIcons 设 _tp，
            // 导致 EnsureLoaded 永远不加载 → 全部回退 FontAwesome（实测图标消失）。
            // 失败时按钮回退到 FontAwesome，不会阻塞插件启动。
            NativeIcons.Load(TextureProvider);

            Interface.UiBuilder.DisableCutsceneUiHide = true;
            Interface.UiBuilder.DisableGposeUiHide = true;

            // let all the other components register, then initialize commands
            Commands.Initialise();

            if (Interface.Reason is not PluginLoadReason.Boot)
                MessageManager.FilterAllTabsAsync();

            Framework.Update += FrameworkUpdate;
            Interface.UiBuilder.Draw += Draw;
            Interface.LanguageChanged += LanguageChanged;

            #if !DEBUG
            // Avoid 300ms hitch when sending first message by preloading the
            // auto-translate cache. Don't do this in debug because it makes
            // profiling difficult.
            AutoTranslate.PreloadCache();
            #endif
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Plugin load threw an error, turning off plugin");
            Dispose();

            // Re-throw the exception to fail the plugin load.
            throw;
        }
    }

    // Suppressing this warning because Dispose() is called in Plugin() if the
    // load fails, so some values may not be initialized.
    [SuppressMessage("ReSharper", "ConditionalAccessQualifierIsNonNullableAccordingToAPIContract")]
    public void Dispose()
    {
        Interface.LanguageChanged -= LanguageChanged;
        Interface.UiBuilder.Draw -= Draw;
        Framework.Update -= FrameworkUpdate;
        GameFunctions.GameFunctions.SetChatInteractable(true);

        _setCursorHook?.Dispose();
        _setCursorHook = null;

        WindowSystem?.RemoveAllWindows();
        ChatLog?.Dispose();
        DbViewer?.Dispose();
        SettingsWindow?.Dispose();




        TypingIpc?.Dispose();
        ExtraChat?.Dispose();
        Ipc?.Dispose();
        ContextMenuHandler?.Dispose();
        MessageManager?.DisposeAsync().AsTask().Wait();
        Functions?.Dispose();
        Commands?.Dispose();
        NativeIcons.DisposeAll();
    }

    private void Draw()
    {
        ChatLog.BeginFrame();

        if (Config.HideInLoadingScreens && Condition[ConditionFlag.BetweenAreas])
        {
            ChatLog.FinalizeFrame();
            TypingIpc.Update();
            return;
        }

        ChatLog.IsHidden = HideStateHelper.HideStateCheck(ChatLog, Config.HideInBattle, Config.HideDuringCutscenes, Config.HideWhenNotLoggedIn, ChatLog.InputHandler.Activate);

        Interface.UiBuilder.DisableUserUiHide = !Config.HideWhenUiHidden;
        DefaultText = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

        // 窗口级字体用 SettingsFont（固定大小，设置里可调）：
        // 否则卫月标题栏等窗口 chrome 会随主字体缩放（实测，还会挡设置页保存按钮）。
        // 聊天消息/输入框等需要主字体的地方在各窗口内容里自行 Push（见 ChatLog.Draw / Popout.Draw）
        using (FontManager.SettingsFont.Push())
            WindowSystem.Draw();

        // !!! 光标决策（窗口 Draw 里置位的标志在此统一处理）
        UpdateCursorDecision();

        ChatLog.FinalizeFrame();
        TypingIpc.Update();

        FileDialogManager.Draw();
    }


    public void SaveConfig()
    {
        Interface.SavePluginConfig(Config);
    }

    public void LanguageChanged(string langCode)
    {
        var info = Config.LanguageOverride is LanguageOverride.None
            ? new CultureInfo(langCode)
            : new CultureInfo(Config.LanguageOverride.Code());

        Language.Culture = info;
    }

    private static readonly string[] ChatAddonNames =
    [
        "ChatLog",
        "ChatLogPanel_0",
        "ChatLogPanel_1",
        "ChatLogPanel_2",
        "ChatLogPanel_3",
    ];

    private void FrameworkUpdate(IFramework framework)
    {
        if (DeferredSaveFrames >= 0 && DeferredSaveFrames-- == 0)
            SaveConfig();

        // !!! 实验：注释"屏幕外可见"hack，干净验证 OwnerAddon=0（bindToOwner=false 等效）是否单独有效。
        // if (GameFunctions.GameFunctions.IsNativeSubContextMenuVisible())
        // {
        // GameFunctions.GameFunctions.KeepChatVisibleOffscreen();
        // return;
        // }
        GameFunctions.GameFunctions.RestoreChatPosition(); // 保留：恢复位置兜底（防 ChatLog 卡屏幕外）

        // !!! 兜底复位：菜单标志残留（打开失败/关闭路径异常）会让 NoMouseInputs
        // 持续生效 → 聊天框穿透（实测：左键点击玩家后穿透保持，直到原生菜单开关才恢复）。
        // 标志置位后菜单（或二级菜单）持续不可见超过 1s → 强制复位两个标志。
        // 必须放在 HideChat return 之前（可能未隐藏原生聊天框，此兜底需每帧执行）。
        try
        {
            var menuVisible = IsNativeContextMenuVisible() || IsNativeSubContextMenuVisible();
            if (ContextMenuActive && !menuVisible && Environment.TickCount64 - ContextMenuActivatedAt > 1000)
                ContextMenuActive = false;
            if (ChatTwoMenuSession && !menuVisible && Environment.TickCount64 - ChatTwoMenuSessionAt > 1000)
                ChatTwoMenuSession = false;
        }
        catch (Exception ex) { Plugin.Log.Debug($"[CtxFallback] error {ex.Message}"); }

        if (!Config.HideChat)
            return;

        // 菜单激活时不隐藏聊天框面板（bindToOwner=true 的子菜单展开需要访问聊天框）。
        // !!! 必须同时检查 ContextMenu addon 是否真的可见：
        // 游戏复用 ContextMenu addon（关闭后只是隐藏不销毁，PreFinalize 事件不触发），
        // ContextMenuActive 会残留 true；且打开失败（目标无效，如无 ContentId 的玩家）时
        // addon 根本不会显示。两种情况都会导致原生聊天框永不隐藏（实测）。
        // 菜单不可见（打开失败或已关闭）→ 恢复隐藏。
        if (ContextMenuActive && IsNativeContextMenuVisible())
            return;

        foreach (var name in ChatAddonNames)
            if (GameFunctions.GameFunctions.IsAddonInteractable(name))
                GameFunctions.GameFunctions.SetAddonInteractable(name, false);
    }

    public static bool IsNativeContextMenuVisible()
    {
        try
        {
            unsafe
            {
                var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
                return ctxAddon != null && ctxAddon->IsVisible;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>二级菜单（AddonContextSub）是否可见（兜底复位用）。</summary>
    public static unsafe bool IsNativeSubContextMenuVisible()
    {
        try
        {
            var addon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("AddonContextSub");
            return addon != null && addon->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    public static bool InBattle => Condition[ConditionFlag.InCombat];
    public static bool GposeActive => Condition[ConditionFlag.WatchingCutscene];
    public static bool CutsceneActive => Condition[ConditionFlag.OccupiedInCutSceneEvent] || Condition[ConditionFlag.WatchingCutscene78];
}
