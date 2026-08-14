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
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChatTwo;

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class Plugin : IDalamudPlugin
{
    public const string PluginName = "Chat 2";

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

    /// <summary>ChatTwo 触发的菜单会话是否进行中（2026-08-14 23:25 新增）。
    /// 区分"ChatTwo 触发的菜单"与"游戏原生/背包等触发的菜单"：
    /// OwnerAddon 恒 0 后两者无法区分（背包二级菜单的 OwnerAddon 也是 0），
    /// 而二级菜单（AddonContextSub）应只对 ChatTwo 会话移动位置。
    /// PayloadHandler 触发时置 true；一级菜单关闭且无二级菜单显示时置 false。</summary>
    public static bool ChatTwoMenuSession;

    public readonly WindowSystem WindowSystem = new(PluginName);
    public SettingsWindow SettingsWindow { get; }
    public ChatLog ChatLog { get; }
    public DbViewer DbViewer { get; }
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

            Config = Interface.GetPluginConfig() as Configuration ?? new Configuration();
            // 四透明度迁移：新字段（背景/标签页/输入框透明度）首次复制消息区透明度
            Config.EnsureAlphaMigration();

            // 以下选项已锁定，不再显示在设置界面中（用户要求）：
            // 强制开启：播放音效 / 显示新人频道加入按钮 / 显示隐藏按钮 / 显示原始道具帮助
            Config.PlaySounds = true;
            Config.ShowNoviceNetwork = true;
            Config.ShowHideButton = true;
            Config.NativeItemTooltips = true;
            // 强制关闭：显示聊天窗口标题栏 / 显示弹出标签页标题栏
            Config.ShowTitleBar = false;
            Config.ShowPopOutTitleBar = false;
            // 频道切换策略：改回"灵活"（此前用户要求固定严格，现恢复）
            Config.KeybindMode = KeybindMode.Flexible;
            // 中文适配：界面语言固定简体中文。
            // ⚠️ 不能设为 None：None 会跟随 Interface.UiLanguage（国服卫月返回 "en"），界面会变英文
            Config.LanguageOverride = LanguageOverride.ChineseSimplified;
            // 命令帮助方向功能已从设置移除，保持关闭
            Config.CommandHelpSide = CommandHelpSide.None;
            // 偏好页已删除：热键固定严格模式（仅无其他按键按下时触发，防打字误触）
            Config.KeybindMode = KeybindMode.Strict;
            // 已删除的设置项锁定默认值（等效于功能关闭）
            Config.PrettierTimestamps = false;      // 现代化布局
            Config.MoreCompactPretty = false;       // 更紧凑的现代布局
            Config.HideSameTimestamps = false;      // 隐藏重复的时间戳
            Config.CollapseDuplicateMessages = false; // 折叠重复消息
            Config.CollapseKeepUniqueLinks = false;   // 折叠时保留唯一链接
            Config.HideInBattle = false;            // 在战斗中隐藏聊天窗口
            Config.HideWhenInactive = false;        // 非活动时隐藏（已从设置移除）
            Config.InactivityHideActiveDuringBattle = false;
            // 采集/制作消息不记录（用户要求，设置项已从历史记录页移除）
            Config.DatabaseGatherCraftMessages = false;
            // 未读模式固定为"未看过的"（设置项已从标签页页删除，用户要求）
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
            CommandHelpWindow = new CommandHelpWindow(ChatLog);

            WindowSystem.AddWindow(ChatLog);
            WindowSystem.AddWindow(SettingsWindow);
            WindowSystem.AddWindow(DbViewer);
            WindowSystem.AddWindow(CommandHelpWindow);

            FontManager.BuildFonts();

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
        // 否则卫月标题栏等窗口 chrome 会随主字体缩放（用户实测，还会挡设置页保存按钮）。
        // 聊天消息/输入框等需要主字体的地方在各窗口内容里自行 Push（见 ChatLog.Draw / Popout.Draw）
        using (FontManager.SettingsFont.Push())
            WindowSystem.Draw();

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

        // ⚠️ 2026-08-14 07:57 实验：注释"屏幕外可见"hack，干净验证 OwnerAddon=0（bindToOwner=false 等效）是否单独有效。
        // if (GameFunctions.GameFunctions.IsNativeSubContextMenuVisible())
        // {
        //     GameFunctions.GameFunctions.KeepChatVisibleOffscreen();
        //     return;
        // }
        GameFunctions.GameFunctions.RestoreChatPosition(); // 保留：恢复位置兜底（防 ChatLog 卡屏幕外）

        if (!Config.HideChat)
            return;

        // 菜单激活时不隐藏聊天框面板（bindToOwner=true 的子菜单展开需要访问聊天框）。
        // ⚠️ 必须同时检查 ContextMenu addon 是否真的可见：
        // 游戏复用 ContextMenu addon（关闭后只是隐藏不销毁，PreFinalize 事件不触发），
        // ContextMenuActive 会残留 true；且打开失败（目标无效，如无 ContentId 的玩家）时
        // addon 根本不会显示。两种情况都会导致原生聊天框永不隐藏（用户实测）。
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

    public static bool InBattle => Condition[ConditionFlag.InCombat];
    public static bool GposeActive => Condition[ConditionFlag.WatchingCutscene];
    public static bool CutsceneActive => Condition[ConditionFlag.OccupiedInCutSceneEvent] || Condition[ConditionFlag.WatchingCutscene78];
}
