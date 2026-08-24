using ChatTwo.Code;
using Dalamud.Interface.FontIdentifier;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatTwo.Util;

/// <summary>
/// 从原版 Chat Two（InternalName=ChatTwo）迁移设置与聊天历史。
/// 原版 ChatTwo.json 由 Dalamud 用 Newtonsoft + TypeNameHandling 保存：字体字段
/// FontId 是 IFontId 接口且带 $type，用 System.Text.Json 反序列化必抛
/// NotSupportedException（接口/抽象类型不支持）。因此统一走 Newtonsoft：
/// 整对象只做 JObject 白名单提取（顶层 $type 指向原版程序集，必须忽略），
/// 字体子对象单独带 TypeNameHandling.Objects 还原。
/// </summary>
public static class ChatTwoMigrator
{
    // 与 Dalamud PluginConfigurations.SerializeConfig 一致的设置（TypeNameHandling.Objects 写/读 $type）
    private static readonly JsonSerializerSettings FontSettings = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
    };

    private static readonly JsonSerializer FontSerializer = JsonSerializer.Create(FontSettings);

    /// <summary>
    /// 把原版 ChatTwo.json（pluginConfigs/ChatTwo.json）中两版共有的设置合并进 target。
    /// 白名单方式：只拷贝两版共有且语义一致的字段，CN 特有字段不受影响；
    /// 任一字段解析失败都只跳过该字段，绝不抛出。
    /// </summary>
    public static void MergeConfigFrom(Configuration target, string srcConfigPath)
    {
        JObject root;
        try
        {
            root = JObject.Parse(File.ReadAllText(srcConfigPath));
        }
        catch
        {
            return;
        }

        // 两版共有且语义一致的标量/枚举（白名单）
        CopyBool(root, "HideChat", v => target.HideChat = v);
        CopyBool(root, "HideDuringCutscenes", v => target.HideDuringCutscenes = v);
        CopyBool(root, "HideWhenNotLoggedIn", v => target.HideWhenNotLoggedIn = v);
        CopyBool(root, "HideWhenUiHidden", v => target.HideWhenUiHidden = v);
        CopyBool(root, "HideInLoadingScreens", v => target.HideInLoadingScreens = v);
        CopyBool(root, "HideInBattle", v => target.HideInBattle = v);
        CopyBool(root, "HideWhenInactive", v => target.HideWhenInactive = v);
        CopyBool(root, "InactivityHideActiveDuringBattle", v => target.InactivityHideActiveDuringBattle = v);
        CopyBool(root, "InactivityHideExtraChatAll", v => target.InactivityHideExtraChatAll = v);
        CopyBool(root, "ShowHideButton", v => target.ShowHideButton = v);
        CopyBool(root, "NativeItemTooltips", v => target.NativeItemTooltips = v);
        CopyBool(root, "ShowNoviceNetwork", v => target.ShowNoviceNetwork = v);
        CopyBool(root, "PrintChangelog", v => target.PrintChangelog = v);
        CopyBool(root, "PlaySounds", v => target.PlaySounds = v);
        CopyBool(root, "KeepInputFocus", v => target.KeepInputFocus = v);
        CopyBool(root, "Use24HourClock", v => target.Use24HourClock = v);
        CopyBool(root, "FontsEnabled", v => target.FontsEnabled = v);
        CopyBool(root, "OverrideStyle", v => target.OverrideStyle = v);
        CopyBool(root, "ShowTitleBar", v => target.ShowTitleBar = v);
        CopyBool(root, "ShowPopOutTitleBar", v => target.ShowPopOutTitleBar = v);
        // 原版 CanMove（允许移动）→ CN MoveLocked（锁定移动）反相映射
        if (root["CanMove"] is { Type: JTokenType.Boolean } canMove)
            target.MoveLocked = !canMove.Value<bool>();

        CopyInt(root, "InactivityHideTimeout", v => target.InactivityHideTimeout = v);
        CopyInt(root, "MaxLinesToRender", v => target.MaxLinesToRender = v);

        CopyFloat(root, "FontSizeV2", v => target.FontSizeV2 = v);
        CopyFloat(root, "SymbolsFontSizeV2", v => target.SymbolsFontSizeV2 = v);
        CopyFloat(root, "WindowAlpha", v => target.WindowAlpha = v);

        CopyEnum(root, "CommandHelpSide", (CommandHelpSide v) => target.CommandHelpSide = v);
        CopyEnum(root, "KeybindMode", (KeybindMode v) => target.KeybindMode = v);
        CopyEnum(root, "LanguageOverride", (LanguageOverride v) => target.LanguageOverride = v);

        CopyString(root, "ChosenStyle", v => target.ChosenStyle = v);

        // 字体（IFontId 接口字段，需 TypeNameHandling 还原 $type 具体实现类）
        CopyFont(root, "GlobalFontV2", v => target.GlobalFontV2 = v);
        CopyFont(root, "JapaneseFontV2", v => target.JapaneseFontV2 = v);
        CopyFont(root, "ItalicFontV2", v => target.ItalicFontV2 = v);

        // 集合
        if (root["ChatColours"] is { Type: JTokenType.Object } colours)
        {
            try { target.ChatColours = colours.ToObject<Dictionary<ChatType, uint>>() ?? []; }
            catch { /* 格式异常则保留当前 */ }
        }

        if (root["InactivityHideExtraChatChannels"] is { } channels)
        {
            try { target.InactivityHideExtraChatChannels = channels.ToObject<HashSet<Guid>>() ?? []; }
            catch { }
        }

        // ChatCodes（只看 Source）→ CN SelectedChannels（Source+Target）
        // CN 的匹配逻辑只判断 Source（HasFlag），因此 Target 置 None 即等价原版语义。
        if (root["Tabs"] is { Type: JTokenType.Array } tabs)
        {
            try
            {
                var migrated = tabs.ToObject<List<Tab>>() ?? [];
                foreach (var tab in migrated)
                {
#pragma warning disable CS0618 // 迁移必须读取原版遗留字段
                    if (tab.SelectedChannels.Count == 0 && tab.ChatCodes.Count > 0)
                    {
                        foreach (var (type, src) in tab.ChatCodes)
                            tab.SelectedChannels[type] = (src, ChatSource.None);
                    }
#pragma warning restore CS0618
                }

                target.Tabs = migrated;
            }
            catch { }
        }
    }

    /// <summary>
    /// 用 SQLite Online Backup 把原版库整体备份到 dstDbPath（覆盖）。
    /// 目标库必须未被打开（MessageManager 创建 MessageStore 之前调用）。
    /// 先写入临时文件再原子替换，失败不残留、不破坏现有目标库。
    /// 源库 WAL 数据自动并入备份（只读连接读的是含 WAL 的一致快照）。
    /// </summary>
    public static bool TryImportDatabase(string srcDbPath, string dstDbPath)
    {
        var tmpPath = dstDbPath + ".importing";
        try
        {
            var dstDir = Path.GetDirectoryName(dstDbPath);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);
            File.Delete(tmpPath);

            var srcCsb = new SqliteConnectionStringBuilder
            {
                DataSource = srcDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };

            using (var srcConn = new SqliteConnection(srcCsb.ToString()))
            {
                srcConn.Open();

                var dstCsb = new SqliteConnectionStringBuilder
                {
                    DataSource = tmpPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                };

                using var dstConn = new SqliteConnection(dstCsb.ToString());
                dstConn.Open();
                srcConn.BackupDatabase(dstConn);
            }

            File.Move(tmpPath, dstDbPath, true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to import database from original ChatTwo");
            try { File.Delete(tmpPath); }
            catch { }

            return false;
        }
    }

    private static void CopyBool(JObject root, string key, Action<bool> set)
    {
        if (root[key] is { Type: JTokenType.Boolean } t)
            set(t.Value<bool>());
    }

    private static void CopyInt(JObject root, string key, Action<int> set)
    {
        if (root[key] is { Type: JTokenType.Integer } t)
            set(t.Value<int>());
    }

    private static void CopyFloat(JObject root, string key, Action<float> set)
    {
        if (root[key] is { Type: JTokenType.Float or JTokenType.Integer } t)
            set(t.Value<float>());
    }

    private static void CopyString(JObject root, string key, Action<string?> set)
    {
        if (root[key] is { Type: JTokenType.String } t)
            set(t.Value<string>());
    }

    private static void CopyEnum<T>(JObject root, string key, Action<T> set) where T : struct, Enum
    {
        if (root[key] is { Type: JTokenType.Integer } t)
        {
            var value = t.Value<int>();
            if (Enum.IsDefined(typeof(T), value))
                set((T)(object)value);
        }
    }

    private static void CopyFont(JObject root, string key, Action<SingleFontSpec> set)
    {
        if (root[key] is not { Type: JTokenType.Object })
            return;

        try
        {
            var spec = root[key]!.ToObject<SingleFontSpec>(FontSerializer);
            if (spec?.FontId != null)
                set(spec);
        }
        catch
        {
            // 字体格式异常（旧版结构/版本不匹配）→ 保留目标当前字体
        }
    }
}
