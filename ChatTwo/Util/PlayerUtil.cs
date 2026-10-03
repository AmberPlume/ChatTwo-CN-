using Dalamud.Game.Config;

namespace ChatTwo.Util;

public static class PlayerUtil
{
    public static bool ScreenshotMode;

    private static readonly string Salt = new Random().Next().ToString();

    private static long LastPlayerNameDisplayTypeRefresh;
    private static PlayerNameDisplayType CurrentPlayerNameDisplayType = PlayerNameDisplayType.FullName;

    private enum PlayerNameDisplayType : uint
    {
        FullName = 0,
        SurnameAbbreviated = 1,
        ForenameAbbreviated = 2,
        Initials = 3,
    }

    public static string HidePlayerInString(string str, string playerName, uint worldId)
    {
        var expected = AbbreviatePlayerName(playerName);
        var hash = HashPlayer(playerName, worldId);
        return str.Replace(playerName, expected).Replace(expected, hash);
    }

    public static string HashPlayer(string playerName, uint worldId)
    {
        var hashCode = $"{Salt}{playerName}{worldId}".GetHashCode();
        return $"Player {hashCode:X8}";
    }

    private static string AbbreviatePlayerName(string playerName)
    {
        if (LastPlayerNameDisplayTypeRefresh + 5_000 < Environment.TickCount64)
        {
            LastPlayerNameDisplayTypeRefresh = Environment.TickCount64;
            CurrentPlayerNameDisplayType = GetNameDisplayType();
        }

        if (CurrentPlayerNameDisplayType == PlayerNameDisplayType.FullName)
            return playerName;

        var split = playerName.Split(' ');
        if (split.Length != 2)
            return playerName;

        return CurrentPlayerNameDisplayType switch
        {
            PlayerNameDisplayType.SurnameAbbreviated => $"{split.First()} {split.Last().FirstOrDefault('A')}.",
            PlayerNameDisplayType.ForenameAbbreviated => $"{split.First().FirstOrDefault('A')}. {split.Last()}",
            PlayerNameDisplayType.Initials => $"{split.First().FirstOrDefault('A')}. {split.Last().FirstOrDefault('A')}.",
            _ => playerName,
        };
    }

    private static PlayerNameDisplayType GetNameDisplayType()
    {
        var ok = Plugin.GameConfig.TryGet(UiConfigOption.LogNameType, out uint type);
        if (!ok || !Enum.IsDefined((PlayerNameDisplayType)type))
            return PlayerNameDisplayType.FullName;

        return (PlayerNameDisplayType) type;
    }

    /// <summary>编号上限：6 位十进制（100000 之后用尽，降级为哈希编号）。</summary>
    private const int MaxAnonymousId = 100_000;

    /// <summary>编号位数，不足补前导零。</summary>
    private const int AnonymousDigitCount = 6;

    /// <summary>
    /// 取该玩家的匿名编号文本（如「玩家000001」）。首次遇到的玩家按当前最大编号 +1 分配并写入配置；
    /// 编号用尽（10 万）后降级为哈希编号，不再增长。
    /// </summary>
    public static string AnonymousName(string playerName, uint worldId)
    {
        if (string.IsNullOrEmpty(playerName))
            return playerName;

        var key = $"{playerName}@{worldId}";
        var table = Plugin.Config.AnonymousIds;
        if (!table.TryGetValue(key, out var id))
        {
            id = table.Count == 0 ? 1 : table.Values.Max() + 1;
            if (id > MaxAnonymousId)
                return $"玩家{HashSuffix(key)}";

            table[key] = id;
            Plugin.Interface.SavePluginConfig(Plugin.Config);
        }

        return $"玩家{id.ToString().PadLeft(AnonymousDigitCount, '0')}";
    }

    /// <summary>编号用尽后的降级方案：与玩家绑定的定长十进制摘要。</summary>
    private static string HashSuffix(string key)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(key))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return (hash % 1_000_000).ToString().PadLeft(AnonymousDigitCount, '0');
    }

    /// <summary>清空匿名编号表（设置页按钮）。</summary>
    public static void ResetAnonymousIds()
    {
        Plugin.Config.AnonymousIds.Clear();
        Plugin.Interface.SavePluginConfig(Plugin.Config);
    }

    // 匿名模式统一入口：开启时把文本里的玩家名换成编号，关闭时原样返回。
    // !!! 只替换绘制文本，不改 chunk.Link / 数据库 / 菜单定位
    public static string Anonymize(string text, string playerName, uint worldId)
    {
        if (!Plugin.Config.AnonymousMode || string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(text))
            return text;

        return text.Replace(playerName, AnonymousName(playerName, worldId));
    }
}