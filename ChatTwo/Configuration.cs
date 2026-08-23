using System.Collections;
using System.Text.Json.Serialization;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Bindings.ImGui;
using Lumina.Text.ReadOnly;

namespace ChatTwo;

[Serializable]
public enum MigrationStatus
{
    NotStarted,
    Started,
    Copied,
    Failed,
    Finished,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    private const int LatestVersion = 6;

    public int Version { get; set; } = LatestVersion;

    public bool HideChat = true;
    public bool HideDuringCutscenes = true;
    public bool HideWhenNotLoggedIn = true;
    public bool HideWhenUiHidden = true;
    public bool HideInLoadingScreens;
    public bool HideInBattle;
    public bool HideWhenInactive;
    public int InactivityHideTimeout = 10;
    public bool InactivityHideActiveDuringBattle = true;

    [Obsolete("Use InactivityHideChannelsV2 instead")]
    public Dictionary<ChatType, ChatSource> InactivityHideChannels = [];

    public Dictionary<ChatType, (ChatSource, ChatSource)> InactivityHideChannelsV2 = [];
    public bool InactivityHideExtraChatAll = true;
    public HashSet<Guid> InactivityHideExtraChatChannels = [];
    public bool ShowHideButton = true;
    public bool NativeItemTooltips = true;
    // !!! 清理：原作者"现代化布局"（PrettierTimestamps 表格渲染 / MoreCompactPretty）
    // 已移除，时间戳统一走 DrawTimestampInline 行内渲染；HideSameTimestamps 已回归为
    // MergeSameTimestamps（合并相同时间，见 DrawMessages）
    public bool ShowNoviceNetwork = true; // ：锁定开启（不再显示在设置中）
    public TabPosition TabPosition = TabPosition.Bottom;
    public bool PrintChangelog = true;
    public CommandHelpSide CommandHelpSide = CommandHelpSide.None;
    public KeybindMode KeybindMode = KeybindMode.Flexible;
    public LanguageOverride LanguageOverride = LanguageOverride.None;
    public bool CanResize = true;
    public bool ShowTitleBar;
    public bool ShowPopOutTitleBar; // ：关闭（不再显示在设置中）
    /// <summary>
    /// 对话历史数据库存放的文件夹路径。空 = 默认（ConfigDirectory）。
    /// </summary>
    public string DatabasePath = string.Empty;

    /// <summary>
    /// 历史记录导出文件夹路径。空 = 默认（空，首次导出时选择）。
    /// </summary>
    public string ExportDirectory = string.Empty;

    public bool DatabaseBattleMessages;
    public bool DatabaseGatherCraftMessages;
    public bool LoadPreviousSession;
    public bool FilterIncludePreviousSessions;
    public bool SortAutoTranslate;
    public bool PlaySounds = true;
    public bool KeepInputFocus = true;
    public int MaxLinesToRender = 10_000; // 1-10000
    public bool Use24HourClock;
    // 全局"显示时间戳"开关（与各 tab 的 DisplayTimestamp 叠加）
    public bool ShowTimestamp = true;
    // 时间戳子选项（替代旧"现代化布局"表格逻辑，见 DrawTimestampInline）
    /// <summary>去除时间戳方括号：[12:34] → 12:34（更短）。</summary>
    public bool RemoveTimestampBrackets;
    /// <summary>紧凑排布：压缩时间戳字符间距，让时间戳最短。</summary>
    public bool CompactTimestampSpacing;
    /// <summary>
    /// 时间戳字间距自由调整（px，负值更紧凑，正值更疏松）。只作用于时间戳，
    /// 与正文字间距（MessageLetterSpacing）相互独立。
    /// </summary>
    public float TimestampLetterSpacing = 0f;
    /// <summary>
    /// 合并相同时间（原版 HideSameTimestamps 回归）：同一分钟内连续的消息只显示
    /// 第一个的时间戳，后续相同时间戳不再重复（行内模式=正文顶格；独立列模式=时间戳留白占位）。
    /// </summary>
    public bool MergeSameTimestamps;
    /// <summary>时间戳单独成列：时间戳占左侧固定列，正文整体缩进（换行不回到时间戳下方）。</summary>
    public bool TimestampOwnColumn;
    /// <summary>时间戳列与正文之间的间隔（px，单独成列时生效，默认 8）。</summary>
    public float TimestampColumnGap = 8f;
    /// <summary>隐藏标签页栏末尾的"添加标签页"（+）按钮。</summary>
    public bool HideNewTabButton;
    /// <summary>自定义消息区背景颜色（关闭 = 跟随主题 WindowBg 混合 25% 白；透明度仍由 WindowAlpha 控制）。</summary>
    public bool CustomMessageLogBg;
    /// <summary>自定义消息区背景 RGB（RGBA 格式，alpha 忽略——统一走 WindowAlpha）。</summary>
    public uint MessageLogBgColor = ColourUtil.ComponentsToRgba(96, 96, 96);
    /// <summary>自定义输入框背景颜色（关闭 = 跟随主题 FrameBg；透明度仍由 InputAlpha 控制）。</summary>
    public bool CustomInputBg;
    /// <summary>自定义输入框背景 RGB（RGBA 格式，alpha 忽略——统一走 InputAlpha）。</summary>
    public uint InputBgColor = ColourUtil.ComponentsToRgba(96, 96, 96);
    /// <summary>自定义标签页栏背景颜色（仅非仿原生窗口；关闭 = 跟随默认深色条/顶部模式消息区同色）。</summary>
    public bool CustomTabBg;
    /// <summary>自定义标签页栏背景 RGB（RGBA 格式，alpha 忽略——透明度由 TabAlpha 或消息区同色规则决定）。</summary>
    public uint TabBgColor = ColourUtil.ComponentsToRgba(96, 96, 96);
    /// <summary>
    /// 正文（消息内容）字间距自由调整（px，负值更紧凑，正值更疏松）。
    /// 只作用于消息正文，不影响时间戳/发送者名。
    /// </summary>
    public float MessageLetterSpacing = 0f;
    /// <summary>
    /// 段落间距（px）：消息行之间的额外垂直间距。0 = 不额外留白；负值 = 在字体行高余量内
    /// 收紧行距（CJK 字体行高≈1.4×字号，负值可压缩到更贴；过负会文字重叠）。
    /// </summary>
    public float MessageLineSpacing = 0f;

    // 自定义字体开关（内部标志，无 UI）：false=用 Axis 游戏字体（默认原生观感）；true=用 GlobalFontV2 选的字体
    public bool FontsEnabled = false;

    /// <summary>
    /// 输入框字体大小（pt）。输入框高度跟随此字体自适应（原生逻辑）。
    /// </summary>
    public float InputFontSize = 12f;

    /// <summary>
    /// 设置界面字体大小（pt），独立于聊天主字体。
    /// </summary>
    public float SettingsFontSize = 14f;
    public ExtraGlyphRanges ExtraGlyphRanges = 0;
    public float FontSizeV2 = 14f;

    /// <summary>
    /// 输入区缩放（1.0 = 100%）。只影响输入区：输入框、输入框左右图标按钮、频道名行。
    /// tab 区由 TabScale 独立控制（拆分）。
    /// </summary>
    public float InputAreaScale = 1.0f;
    /// <summary>
    /// 标签页缩放（1.0 = 100%）。影响标签页文字、标签栏高度、末尾 + 按钮。
    /// 与输入区缩放（InputAreaScale）相互独立。
    /// </summary>
    public float TabScale = 1.0f;
    /// <summary>
    /// 标签页名称基准字号（pt，默认 12）。与 TabScale 相乘 = 标签页最终大小：
    /// 字号设基准，TabScale 设整体倍率（默认/仿原生 tab 均生效）。
    /// </summary>
    public float TabFontSizePt = 12f;
    /// <summary>
    /// 当前输入频道名称字号（pt，默认 12）。输入框上方频道名行，随输入区缩放联动。
    /// </summary>
    public float ChannelNameFontSizePt = 12f;
    /// <summary>
    /// 输入法候选词字号（pt，默认 16）。卫月接管候选渲染，ChatTwo 放大候选文字用（独立于输入框字号）。
    /// </summary>
    public float ImeCandidateFontSizePt = 16f;
    /// <summary>输入法候选框透明度（0-100，默认 100 不透明；0 = 完全透明）。</summary>
    public float ImeCandidateAlpha = 100f;
    /// <summary>
    /// 大开关：是否替换输入法候选框（放大候选词/页码、移除拼音/分隔线、调整布局）。
    /// 关闭时全部 IME detour 走透传（使用卫月原始 IME 渲染），默认关闭。
    /// </summary>
    public bool ModifyImeCandidate = false;
    /// <summary>
    /// 未读消息的频道过滤：为空 = 全部频道的新消息都计入未读；
    /// 非空 = 仅选中频道（含来源/目标细分）计入未读。
    /// </summary>
    public Dictionary<ChatType, (ChatSource Source, ChatSource Target)> UnreadChannels = [];
    // 是否已从原版 ChatTwo 迁移过配置（迁移按钮防重复用）
    public bool MigratedFromChatTwo;

    /// <summary>
    /// 待从原版 ChatTwo 导入的聊天历史数据库路径（迁移标记）。
    /// 设置页"迁移设置"勾选聊天历史时写入；下次启动时由 MessageManager
    /// 在创建 MessageStore 之前用 SQLite Online Backup 导入，成功后清空。
    /// 运行时不能直接复制该库文件：源库/目标库均可能被各自的插件连接占用。
    /// </summary>
    public string? PendingDbImportSource;
    public float SymbolsFontSizeV2 = 12.75f;
    public SingleFontSpec GlobalFontV2 = new()
    {
        // dalamud only ships KR as regular, which chat2 used previously for global fonts
        FontId = new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansCjkRegular),
        SizePt = 12.75f,
    };
    public SingleFontSpec JapaneseFontV2 = new()
    {
        FontId = new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansCjkMedium),
        SizePt = 12.75f,
    };
    public SingleFontSpec ItalicFontV2 = new()
    {
        FontId = new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansCjkRegular),
        SizePt = 12.75f,
    };

    public float WindowAlpha = 100f;
    // 四透明度分离：WindowAlpha=消息区透明度；新增背景/标签页/输入框透明度。
    // 哨兵 -1：首次加载时复制 WindowAlpha（旧配置无缝迁移，见 EnsureAlphaMigration）
    public float BackgroundAlpha = -1f;
    public float TabAlpha = -1f;
    public float InputAlpha = -1f;
    // 仿原生界面背景：只有消息区/输入框/标签页有背景，窗口其余区域完全透明
    public bool NativeBackground;
    // 未读消息提示方式（全局）：Highlight=高亮 / Breath=呼吸 / None=无（默认高亮）
    public UnreadNotifyMode UnreadNotifyMode = UnreadNotifyMode.Highlight;
    // 快捷锁定：聊天框锁定按钮状态（记忆上次锁定/解锁，重启不丢）
    public bool MoveLocked;
    public Dictionary<ChatType, uint> ChatColours = new();
    public List<Tab> Tabs = [];

    public bool OverrideStyle;
    public string? ChosenStyle;


    // Migration safety
    public MigrationStatus MigrationStatus = MigrationStatus.NotStarted;

    // !!! 实验功能设置页：菜单位置模式开关。
    // true（默认）= 菜单跟随鼠标（游戏原生跟手，当前方案）；false = 聊天框右侧固定（旧逻辑备份）。
    // 实验性功能：跟随鼠标时菜单可能压在聊天框内，挖洞预算不足时可能出现文字进入菜单/圆角降级/边缘字符消失。
    public bool ExperimentalMenuFollowMouse = true;

    public void UpdateFrom(Configuration other, bool backToOriginal)
    {
        if (backToOriginal)
            foreach (var tab in Tabs.Where(t => t.PopOut))
                tab.PopOut = false;

        HideChat = other.HideChat;
        HideDuringCutscenes = other.HideDuringCutscenes;
        HideWhenNotLoggedIn = other.HideWhenNotLoggedIn;
        HideWhenUiHidden = other.HideWhenUiHidden;
        HideInLoadingScreens = other.HideInLoadingScreens;
        HideInBattle = other.HideInBattle;
        HideWhenInactive = other.HideWhenInactive;
        InactivityHideTimeout = other.InactivityHideTimeout;
        InactivityHideActiveDuringBattle = other.InactivityHideActiveDuringBattle;
        InactivityHideChannelsV2 = other.InactivityHideChannelsV2.ToDictionary(pair => pair.Key, pair => pair.Value);
        InactivityHideExtraChatAll = other.InactivityHideExtraChatAll;
        InactivityHideExtraChatChannels = other.InactivityHideExtraChatChannels.ToHashSet();
        ShowHideButton = other.ShowHideButton;
        NativeItemTooltips = other.NativeItemTooltips;
        ShowNoviceNetwork = other.ShowNoviceNetwork;
        TabPosition = other.TabPosition;
        PrintChangelog = other.PrintChangelog;
        CommandHelpSide = other.CommandHelpSide;
        KeybindMode = other.KeybindMode;
        LanguageOverride = other.LanguageOverride;
        CanResize = other.CanResize;
        ShowTitleBar = other.ShowTitleBar;
        ShowPopOutTitleBar = other.ShowPopOutTitleBar;
        DatabasePath = other.DatabasePath;
        ExportDirectory = other.ExportDirectory;
        DatabaseBattleMessages = other.DatabaseBattleMessages;
        DatabaseGatherCraftMessages = other.DatabaseGatherCraftMessages;
        LoadPreviousSession = other.LoadPreviousSession;
        FilterIncludePreviousSessions = other.FilterIncludePreviousSessions;
        SortAutoTranslate = other.SortAutoTranslate;
        PlaySounds = other.PlaySounds;
        KeepInputFocus = other.KeepInputFocus;
        MaxLinesToRender = other.MaxLinesToRender;
        Use24HourClock = other.Use24HourClock;
        ShowTimestamp = other.ShowTimestamp;
        RemoveTimestampBrackets = other.RemoveTimestampBrackets;
        CompactTimestampSpacing = other.CompactTimestampSpacing;
        TimestampLetterSpacing = other.TimestampLetterSpacing;
        MergeSameTimestamps = other.MergeSameTimestamps;
        TimestampOwnColumn = other.TimestampOwnColumn;
        TimestampColumnGap = other.TimestampColumnGap;
        HideNewTabButton = other.HideNewTabButton;
        CustomMessageLogBg = other.CustomMessageLogBg;
        MessageLogBgColor = other.MessageLogBgColor;
        CustomInputBg = other.CustomInputBg;
        InputBgColor = other.InputBgColor;
        CustomTabBg = other.CustomTabBg;
        TabBgColor = other.TabBgColor;
        MessageLetterSpacing = other.MessageLetterSpacing;
        MessageLineSpacing = other.MessageLineSpacing;
        FontsEnabled = other.FontsEnabled;
        InputFontSize = other.InputFontSize;
        SettingsFontSize = other.SettingsFontSize;
        ExtraGlyphRanges = other.ExtraGlyphRanges;
        FontSizeV2 = other.FontSizeV2;
        InputAreaScale = other.InputAreaScale;
        TabScale = other.TabScale;
        TabFontSizePt = other.TabFontSizePt;
        ChannelNameFontSizePt = other.ChannelNameFontSizePt;
        ImeCandidateFontSizePt = other.ImeCandidateFontSizePt;
        ModifyImeCandidate = other.ModifyImeCandidate;
        ImeCandidateAlpha = other.ImeCandidateAlpha;
        UnreadChannels = other.UnreadChannels.ToDictionary(pair => pair.Key, pair => pair.Value);
        MigratedFromChatTwo = other.MigratedFromChatTwo;
        PendingDbImportSource = other.PendingDbImportSource;
        GlobalFontV2 = other.GlobalFontV2;
        JapaneseFontV2 = other.JapaneseFontV2;
        ItalicFontV2 = other.ItalicFontV2;
        SymbolsFontSizeV2 = other.SymbolsFontSizeV2;
        WindowAlpha = other.WindowAlpha;
        BackgroundAlpha = other.BackgroundAlpha;
        TabAlpha = other.TabAlpha;
        InputAlpha = other.InputAlpha;
        NativeBackground = other.NativeBackground;
        UnreadNotifyMode = other.UnreadNotifyMode;
        MoveLocked = other.MoveLocked;
        ChatColours = other.ChatColours.ToDictionary(entry => entry.Key, entry => entry.Value);
        // !!! Clone 不拷贝 Messages（内存消息列表，非配置字段）——若直接替换列表，
        // 每次保存（UpdateFrom）都会把消息区清空（旧 Tab 的 Messages 全丢，只有
        // 重载插件/新消息才能恢复）。按 Identifier 从旧列表转移 Messages 引用。
        var oldTabs = Tabs;
        Tabs = other.Tabs.Select(t => t.Clone()).ToList();
        foreach (var newTab in Tabs)
        {
            var old = oldTabs.FirstOrDefault(o => o.Identifier == newTab.Identifier);
            if (old != null)
                newTab.Messages = old.Messages;
        }
        OverrideStyle = other.OverrideStyle;
        ChosenStyle = other.ChosenStyle;
        MigrationStatus = other.MigrationStatus;
        ExperimentalMenuFollowMouse = other.ExperimentalMenuFollowMouse;
    }

    /// <summary>
    /// 四透明度迁移：旧配置没有 BackgroundAlpha/TabAlpha/InputAlpha（哨兵 -1），
    /// 首次调用时复制 WindowAlpha（消息区透明度），保持既有视觉不变。
    /// </summary>
    public void EnsureAlphaMigration()
    {
        if (BackgroundAlpha < 0f) BackgroundAlpha = WindowAlpha;
        if (TabAlpha < 0f) TabAlpha = WindowAlpha;
        if (InputAlpha < 0f) InputAlpha = WindowAlpha;
    }
}

[Serializable]
public enum UnreadMode
{
    All,
    Unseen,
    None,
}

public static class UnreadModeExt
{
    public static string Name(this UnreadMode mode) => mode switch
    {
        UnreadMode.All => Language.UnreadMode_All,
        UnreadMode.Unseen => Language.UnreadMode_Unseen,
        UnreadMode.None => Language.UnreadMode_None,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static string? Tooltip(this UnreadMode mode) => mode switch
    {
        UnreadMode.All => Language.UnreadMode_All_Tooltip,
        UnreadMode.Unseen => Language.UnreadMode_Unseen_Tooltip,
        UnreadMode.None => Language.UnreadMode_None_Tooltip,
        _ => null,
    };
}

/// <summary>未读消息的显示提示方式（全局设置）。默认高亮（在设置中）。</summary>
[Serializable]
public enum UnreadNotifyMode
{
    /// <summary>荧光绿常亮（默认）。</summary>
    Highlight,
    /// <summary>荧光绿 + 呼吸灯闪烁。</summary>
    Breath,
    /// <summary>不提示（与无未读时相同）。</summary>
    None,
}

public static class UnreadNotifyModeExt
{
    public static string Name(this UnreadNotifyMode mode) => mode switch
    {
        UnreadNotifyMode.Breath => Language.UnreadNotifyMode_Breath,
        UnreadNotifyMode.Highlight => Language.UnreadNotifyMode_Highlight,
        UnreadNotifyMode.None => Language.UnreadNotifyMode_None,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

[Serializable]
public class Tab
{
    public string Name = Language.Tab_DefaultName;

    [Obsolete("Removed in favor of SelectedChannels")]
    public Dictionary<ChatType, ChatSource> ChatCodes = new();

    public Dictionary<ChatType, (ChatSource, ChatSource)> SelectedChannels = new();
    public bool ExtraChatAll;
    public HashSet<Guid> ExtraChatChannels = [];

    public UnreadMode UnreadMode = UnreadMode.Unseen;
    /// <summary>
    /// 该标签页的新消息是否计入未读（全局"未读消息设置"控制，默认 true）。
    /// false = 本标签页收到匹配消息也不累积未读数（也不计入跨 tab 未读同步）。
    /// </summary>
    public bool UnreadEnabled = true;
    public bool UnhideOnActivity;
    public bool DisplayTimestamp = true;
    public InputChannel? Channel;
    /// <summary>输入频道始终锁定（需求）：勾选后每帧强制频道（原默认行为）；
    /// 不勾选则只在切换到本标签页时自动设置一次频道，之后可自由切换。</summary>
    public bool InputChannelLocked;
    public bool PopOut;
    public bool IndependentOpacity;
    public float Opacity = 100f;
    public bool InputDisabled;
    public bool SupportsInput;

    public bool CanResize = true;

    public bool IndependentHide;
    public bool HideDuringCutscenes = true;
    public bool HideWhenNotLoggedIn = true;
    public bool HideWhenUiHidden = true;
    public bool HideInLoadingScreens;
    public bool HideInBattle;
    public bool HideWhenInactive;

    public bool IsTempTab;
    public bool AllSenderMessages;
    public TellTarget TellTarget = TellTarget.Empty();

    [NonSerialized]
    [JsonIgnore] public uint Unread;
    [NonSerialized]
    [JsonIgnore] public uint LastSendUnread;
    [NonSerialized]
    [JsonIgnore] public long LastActivity;
    [NonSerialized]
    [JsonIgnore] public MessageList Messages = new();

    [NonSerialized]
    [JsonIgnore] public UsedChannel CurrentChannel = new();

    [NonSerialized]
    [JsonIgnore] public Guid Identifier = Guid.NewGuid();

    public bool Matches(Message message)
    {
        if (Channel == InputChannel.Tell && TellTarget.IsSet())
        {
            if (!message.Code.IsPlayerMessage())
                return false;

            if (TellTarget.ContentId == 0)
            {
                var target = TellTarget.Empty();
                foreach (var payload in new ReadOnlySeString(message.SenderSource.Encode()))
                {
                    if (target.FromCharacterLink(payload))
                        break; // Character link found
                }

                if (target.CompareNames(TellTarget))
                    TellTarget.ContentId = message.ContentId;
            }

            return message.MatchTellTarget(TellTarget, AllSenderMessages);
        }

        return message.Matches(SelectedChannels, ExtraChatAll, ExtraChatChannels);
    }

    public void AddMessage(Message message, bool unread = true)
    {
        Messages.AddPrune(message, MessageManager.MessageDisplayLimit);
        // 同一消息已在其他 tab 显示过 → 不计未读（跨 tab 未读共享）
        if (message.Seen)
            return;
        if (!unread)
            return;

        // !!! 未读消息设置（全局过滤，见"消息设置"页顶部"未读消息设置"区）：
        // ① 标签页维度：本标签页被取消勾选 → 不计未读
        // ② 频道维度：UnreadChannels 为空 = 全部频道；非空 = 仅选中频道（含来源/目标细分）
        if (!UnreadEnabled)
            return;
        var cfg = Plugin.Config;
        if (cfg.UnreadChannels.Count > 0 && !message.Matches(cfg.UnreadChannels, false, []))
            return;

        Unread += 1;
        if (message.Matches(Plugin.Config.InactivityHideChannelsV2, Plugin.Config.InactivityHideExtraChatAll, Plugin.Config.InactivityHideExtraChatChannels))
            LastActivity = Environment.TickCount64;
    }

    public void Clear()
        => Messages.Clear();

    public Tab Clone()
    {
        return new Tab
        {
            Name = Name,
            SelectedChannels = SelectedChannels.ToDictionary(pair => pair.Key, pair => pair.Value),
            ExtraChatAll = ExtraChatAll,
            ExtraChatChannels = ExtraChatChannels.ToHashSet(),
            UnreadMode = UnreadMode,
            UnreadEnabled = UnreadEnabled,
            UnhideOnActivity = UnhideOnActivity,
            Unread = Unread,
            LastActivity = LastActivity,
            DisplayTimestamp = DisplayTimestamp,
            Channel = Channel,
            InputChannelLocked = InputChannelLocked,
            PopOut = PopOut,
            IndependentOpacity = IndependentOpacity,
            Opacity = Opacity,
            Identifier = Identifier,
            InputDisabled = InputDisabled,
            SupportsInput = SupportsInput,
            CurrentChannel = CurrentChannel.Clone(),
            CanResize = CanResize,
            IndependentHide = IndependentHide,
            HideDuringCutscenes = HideDuringCutscenes,
            HideWhenNotLoggedIn = HideWhenNotLoggedIn,
            HideWhenUiHidden = HideWhenUiHidden,
            HideInLoadingScreens = HideInLoadingScreens,
            HideInBattle = HideInBattle,
            HideWhenInactive = HideWhenInactive,
            IsTempTab = IsTempTab,
            AllSenderMessages = AllSenderMessages,
            TellTarget = TellTarget.Clone(),
        };
    }

    /// <summary>
    /// MessageList provides an ordered list of messages with duplicate ID
    /// tracking, sorting and mutex protection.
    /// </summary>
    public class MessageList
    {
        private readonly SemaphoreSlim LockSlim = new(1, 1);

        private readonly List<Message> Messages;
        private readonly HashSet<Guid> TrackedMessageIds;

        public MessageList()
        {
            Messages = [];
            TrackedMessageIds = [];
        }

        public MessageList(int initialCapacity)
        {
            Messages = new List<Message>(initialCapacity);
            TrackedMessageIds = new HashSet<Guid>(initialCapacity);
        }

        public void AddPrune(Message message, int max)
        {
            LockSlim.Wait(-1);
            try
            {
                AddLocked(message);
                PruneMaxLocked(max);
            }
            finally
            {
                LockSlim.Release();
            }
        }

        public void AddSortPrune(IEnumerable<Message> messages, int max)
        {
            LockSlim.Wait(-1);
            try
            {
                foreach (var message in messages)
                    AddLocked(message);

                SortLocked();
                PruneMaxLocked(max);
            }
            finally
            {
                LockSlim.Release();
            }
        }

        private void AddLocked(Message message)
        {
            if (TrackedMessageIds.Contains(message.Id))
                return;

            Messages.Add(message);
            TrackedMessageIds.Add(message.Id);
        }

        public void Clear()
        {
            LockSlim.Wait(-1);
            try
            {
                Messages.Clear();
                TrackedMessageIds.Clear();
            }
            finally
            {
                LockSlim.Release();
            }
        }

        private void SortLocked()
        {
            Messages.Sort((a, b) => a.Date.CompareTo(b.Date));
        }

        private void PruneMaxLocked(int max)
        {
            while (Messages.Count > max)
            {
                TrackedMessageIds.Remove(Messages[0].Id);
                Messages.RemoveAt(0);
            }
        }

        /// <summary>
        /// Returns an array copy of the message list for usage outside of main thread
        /// </summary>
        public async Task<Message[]> GetCopy(int millisecondsTimeout = -1)
        {
            await LockSlim.WaitAsync(millisecondsTimeout);
            try
            {
                return Messages.ToArray();
            }
            finally
            {
                LockSlim.Release();
            }
        }

        /// <summary>
        /// GetReadOnly returns a read-only list of messages while holding a
        /// reader lock. The list should be used with a using statement.
        /// </summary>
        public RLockedMessageList GetReadOnly(int millisecondsTimeout = -1)
        {
            LockSlim.Wait(millisecondsTimeout);
            return new RLockedMessageList(LockSlim, Messages);
        }

        public class RLockedMessageList(SemaphoreSlim lockSlim, List<Message> messages) : IReadOnlyList<Message>, IDisposable
        {
            public IEnumerator<Message> GetEnumerator()
            {
                return messages.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public int Count => messages.Count;

            public Message this[int index] => messages[index];

            public void Dispose()
            {
                lockSlim.Release();
            }
        }
    }
}

public class UsedChannel
{
    public InputChannel Channel = InputChannel.Invalid;
    public List<Chunk> Name = [];
    public TellTarget? TellTarget;

    public bool UseTempChannel;
    public InputChannel TempChannel = InputChannel.Invalid;
    public TellTarget? TempTellTarget;

    public void ResetTempChannel()
    {
        UseTempChannel = false;
        TempTellTarget = null;
        TempChannel = InputChannel.Invalid;
    }

    public void SetChannel(InputChannel channel)
    {
        Channel = channel;
        Name = [];
    }

    public UsedChannel Clone()
    {
        return new UsedChannel
        {
            Channel = Channel,
            Name = Name,
            TellTarget = TellTarget?.Clone(),

            UseTempChannel = UseTempChannel,
            TempChannel = TempChannel,
            TempTellTarget = TempTellTarget?.Clone(),
        };
    }
}

[Serializable]
public enum TabPosition
{
    Top,
    Bottom,
    Side,
}

public static class TabPositionExt
{
    public static string Name(this TabPosition position) => position switch
    {
        TabPosition.Top => "顶部",
        TabPosition.Bottom => "底部",
        TabPosition.Side => "侧边",
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
    };
}

[Serializable]
public enum CommandHelpSide
{
    None,
    Left,
    Right,
}

public static class CommandHelpSideExt
{
    public static string Name(this CommandHelpSide side) => side switch
    {
        CommandHelpSide.None => Language.CommandHelpSide_None,
        CommandHelpSide.Left => Language.CommandHelpSide_Left,
        CommandHelpSide.Right => Language.CommandHelpSide_Right,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, null),
    };
}

[Serializable]
public enum KeybindMode
{
    Flexible,
    Strict,
}

public static class KeybindModeExt
{
    public static string Name(this KeybindMode mode) => mode switch
    {
        KeybindMode.Flexible => Language.KeybindMode_Flexible_Name,
        KeybindMode.Strict => Language.KeybindMode_Strict_Name,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static string? Tooltip(this KeybindMode mode) => mode switch
    {
        KeybindMode.Flexible => Language.KeybindMode_Flexible_Tooltip,
        KeybindMode.Strict => Language.KeybindMode_Strict_Tooltip,
        _ => null,
    };
}

[Serializable]
public enum LanguageOverride
{
    None,
    ChineseSimplified,
    ChineseTraditional,
    Dutch,
    English,
    French,
    German,
    Greek,

    // Italian,
    Japanese,

    // Korean,
    // Norwegian,
    PortugueseBrazil,
    Romanian,
    Russian,
    Spanish,
    Swedish,
}

public static class LanguageOverrideExt
{
    public static string Name(this LanguageOverride mode) => mode switch
    {
        LanguageOverride.None => Language.LanguageOverride_None,
        LanguageOverride.ChineseSimplified => "简体中文",
        LanguageOverride.ChineseTraditional => "繁體中文",
        LanguageOverride.Dutch => "Nederlands",
        LanguageOverride.English => "English",
        LanguageOverride.French => "Français",
        LanguageOverride.German => "Deutsch",
        LanguageOverride.Greek => "Ελληνικά",
        // LanguageOverride.Italian => "Italiano",
        LanguageOverride.Japanese => "日本語",
        // LanguageOverride.Korean => "한국어 (Korean)",
        // LanguageOverride.Norwegian => "Norsk",
        LanguageOverride.PortugueseBrazil => "Português do Brasil",
        LanguageOverride.Romanian => "Română",
        LanguageOverride.Russian => "Русский",
        LanguageOverride.Spanish => "Español",
        LanguageOverride.Swedish => "Svenska",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static string Code(this LanguageOverride mode) => mode switch
    {
        LanguageOverride.None => "",
        LanguageOverride.ChineseSimplified => "zh-hans",
        LanguageOverride.ChineseTraditional => "zh-hant",
        LanguageOverride.Dutch => "nl",
        LanguageOverride.English => "en",
        LanguageOverride.French => "fr",
        LanguageOverride.German => "de",
        LanguageOverride.Greek => "el",
        // LanguageOverride.Italian => "it",
        LanguageOverride.Japanese => "ja",
        // LanguageOverride.Korean => "ko",
        // LanguageOverride.Norwegian => "no",
        LanguageOverride.PortugueseBrazil => "pt-br",
        LanguageOverride.Romanian => "ro",
        LanguageOverride.Russian => "ru",
        LanguageOverride.Spanish => "es",
        LanguageOverride.Swedish => "sv",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

[Serializable]
[Flags]
public enum ExtraGlyphRanges
{
    ChineseFull = 1 << 0,
    ChineseSimplifiedCommon = 1 << 1,
    Cyrillic = 1 << 2,
    Japanese = 1 << 3,
    Korean = 1 << 4,
    Thai = 1 << 5,
    Vietnamese = 1 << 6,
}

public static class ExtraGlyphRangesExt
{
    public static string Name(this ExtraGlyphRanges ranges) => ranges switch
    {
        ExtraGlyphRanges.ChineseFull => Language.ExtraGlyphRanges_ChineseFull_Name,
        ExtraGlyphRanges.ChineseSimplifiedCommon => Language.ExtraGlyphRanges_ChineseSimplifiedCommon_Name,
        ExtraGlyphRanges.Cyrillic => Language.ExtraGlyphRanges_Cyrillic_Name,
        ExtraGlyphRanges.Japanese => Language.ExtraGlyphRanges_Japanese_Name,
        ExtraGlyphRanges.Korean => Language.ExtraGlyphRanges_Korean_Name,
        ExtraGlyphRanges.Thai => Language.ExtraGlyphRanges_Thai_Name,
        ExtraGlyphRanges.Vietnamese => Language.ExtraGlyphRanges_Vietnamese_Name,
        _ => throw new ArgumentOutOfRangeException(nameof(ranges), ranges, null),
    };

    public static unsafe nint Range(this ExtraGlyphRanges ranges) => ranges switch
    {
        ExtraGlyphRanges.ChineseFull => (nint)ImGui.GetIO().Fonts.GetGlyphRangesChineseFull(),
        ExtraGlyphRanges.ChineseSimplifiedCommon => (nint)ImGui.GetIO().Fonts.GetGlyphRangesChineseSimplifiedCommon(),
        ExtraGlyphRanges.Cyrillic => (nint)ImGui.GetIO().Fonts.GetGlyphRangesCyrillic(),
        ExtraGlyphRanges.Japanese => (nint)ImGui.GetIO().Fonts.GetGlyphRangesJapanese(),
        ExtraGlyphRanges.Korean => (nint)ImGui.GetIO().Fonts.GetGlyphRangesKorean(),
        ExtraGlyphRanges.Thai => (nint)ImGui.GetIO().Fonts.GetGlyphRangesThai(),
        ExtraGlyphRanges.Vietnamese => (nint)ImGui.GetIO().Fonts.GetGlyphRangesVietnamese(),
        _ => throw new ArgumentOutOfRangeException(nameof(ranges), ranges, null),
    };
}
