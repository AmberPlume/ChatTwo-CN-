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
public sealed partial class Plugin : IDalamudPlugin
{
    public const string PluginName = "Chat 2";

    // 光标 hook / IME hook 共用
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint GetModuleHandle(string moduleName);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string procName);

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

    // PayloadHandler 置位，ChatLog.PreDraw 消费
    public static bool ContextMenuActive;

    // ChatTwo 会话内二级菜单位置才移动；区分原生/背包菜单
    public static bool ChatTwoMenuSession;

    // 菜单标志持续不可见超时 → 强制复位（避免 NoMouseInputs 残留）
    private const long MenuFallbackTimeoutMs = 1000;
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
    public DrQuickChatPanelIpc DrQuickChatPanel { get; }
    public ExtraChat ExtraChat { get; }
    public TypingIpc TypingIpc { get; }
    public ChatInputIpc ChatInputIpc { get; }
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

            // 禁用 ImGui 改 OS 光标 + 自绘（SetCursor hook → 原生手指；IME hook → 候选字放大）
            ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
            InitCursorHook();
            InitImeZoomHook();

            Config = Interface.GetPluginConfig() as Configuration ?? new Configuration();
            Config.EnsureAlphaMigration();   // 新四透明度字段旧配置迁移

            // 以下配置项在 CN 分支锁定默认值（不再暴露在设置界面）
            Config.PlaySounds = true;
            Config.ShowNoviceNetwork = true;
            Config.ShowHideButton = true;
            Config.NativeItemTooltips = true;
            Config.ShowTitleBar = false;
            Config.ShowPopOutTitleBar = false;
            Config.KeybindMode = KeybindMode.Flexible;         // 修饰键"包含"即触发
            Config.LanguageOverride = LanguageOverride.ChineseSimplified; // 强制简体，None 会回 "en"
            Config.CommandHelpSide = CommandHelpSide.None;
            // 已删除/关闭的设置项固定默认
            Config.HideInBattle = false;
            Config.HideWhenInactive = false;
            Config.InactivityHideActiveDuringBattle = false;
            Config.DatabaseGatherCraftMessages = false;
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
            DrQuickChatPanel = new DrQuickChatPanelIpc();

            TypingIpc = new TypingIpc(this);
            ChatInputIpc = new ChatInputIpc(this);
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

            // 立即加载原生素材，懒加载会在首次使用时回退 FontAwesome
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

    // Suppressing this warning because Dispose is called in Plugin if the
    // load fails, so some values may not be initialized.
    [SuppressMessage("ReSharper", "ConditionalAccessQualifierIsNonNullableAccordingToAPIContract")]
    public void Dispose()
    {
        Interface.LanguageChanged -= LanguageChanged;
        Interface.UiBuilder.Draw -= Draw;
        Framework.Update -= FrameworkUpdate;
        GameFunctions.GameFunctions.SetChatInteractable(true);

        _setCursorHook?.Dispose();
        _imeAddTextHook?.Dispose();
        _imeRectHook?.Dispose();
        _imeLineHook?.Dispose();
        _setCursorHook = null;

        WindowSystem?.RemoveAllWindows();
        ChatLog?.Dispose();
        DbViewer?.Dispose();
        SettingsWindow?.Dispose();

        TypingIpc?.Dispose();
        ChatInputIpc?.Dispose();
        ExtraChat?.Dispose();
        ContextMenuHandler?.Dispose();
        MessageManager?.DisposeAsync().AsTask().Wait();
        Functions?.Dispose();
        Commands?.Dispose();
        NativeIcons.DisposeAll();
    }

    private unsafe void Draw()
    {
        // 缓存前台 dl 供下一帧 IME detour
        ImeFrameState.ForegroundDl = (nint)ImGui.GetForegroundDrawList().Handle;
        ImeFrameState.BeginFrame();

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

        // SettingsFont 仅用于窗口级控件；聊天正文/输入框各自 Push 主字体
        using (FontManager.SettingsFont.Push())
            WindowSystem.Draw();

        // 消费窗口 Draw 置位的光标标志
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

        // ChatLog 卡屏幕外时复位
        GameFunctions.GameFunctions.RestoreChatPosition();

        // 菜单标志超 1s 持续不可见 → 强制复位（防止 NoMouseInputs 残留导致聊天框穿透），必须在 HideChat return 前
        try
        {
            var menuVisible = IsNativeContextMenuVisible() || IsNativeSubContextMenuVisible();
            if (ContextMenuActive && !menuVisible && Environment.TickCount64 - ContextMenuActivatedAt > MenuFallbackTimeoutMs)
                ContextMenuActive = false;
            if (ChatTwoMenuSession && !menuVisible && Environment.TickCount64 - ChatTwoMenuSessionAt > MenuFallbackTimeoutMs)
                ChatTwoMenuSession = false;
        }
        catch (Exception ex) { Plugin.Log.Debug($"[CtxFallback] error {ex.Message}"); }

        if (!Config.HideChat)
            return;

        // 菜单真可见时不隐藏（防 addon 复用残留 / 打开失败误判）
        if (ContextMenuActive && IsNativeContextMenuVisible())
            return;

        foreach (var name in ChatAddonNames)
            if (GameFunctions.GameFunctions.IsAddonInteractable(name))
                GameFunctions.GameFunctions.SetAddonInteractable(name, false);
    }

    public static bool InBattle => Condition[ConditionFlag.InCombat];
    public static bool GposeActive => Condition[ConditionFlag.WatchingCutscene];
    public static bool CutsceneActive => Condition[ConditionFlag.OccupiedInCutSceneEvent] || Condition[ConditionFlag.WatchingCutscene78];
}
