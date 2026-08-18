using Dalamud.Game.ClientState.Objects.SubKinds;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ChatTwo;

public static class Sheets
{
    public static readonly ExcelSheet<Item> ItemSheet;
    public static readonly ExcelSheet<World> WorldSheet;
    public static readonly ExcelSheet<Status> StatusSheet;
    public static readonly ExcelSheet<LogKind> LogKindSheet;
    public static readonly ExcelSheet<LogFilter> LogFilterSheet;
    public static readonly ExcelSheet<EventItem> EventItemSheet;
    public static readonly ExcelSheet<Completion> CompletionSheet;
    public static readonly ExcelSheet<TerritoryType> TerritorySheet;
    public static readonly ExcelSheet<TextCommand> TextCommandSheet;
    public static readonly ExcelSheet<EventItemHelp> EventItemHelpSheet;

    static Sheets()
    {
        ItemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        WorldSheet = Plugin.DataManager.GetExcelSheet<World>();
        StatusSheet = Plugin.DataManager.GetExcelSheet<Status>();
        LogKindSheet = Plugin.DataManager.GetExcelSheet<LogKind>();
        LogFilterSheet = Plugin.DataManager.GetExcelSheet<LogFilter>();
        EventItemSheet = Plugin.DataManager.GetExcelSheet<EventItem>();
        CompletionSheet = Plugin.DataManager.GetExcelSheet<Completion>();
        TerritorySheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        TextCommandSheet = Plugin.DataManager.GetExcelSheet<TextCommand>();
        EventItemHelpSheet = Plugin.DataManager.GetExcelSheet<EventItemHelp>();
    }

    public static bool IsInForay() =>
        TerritorySheet.TryGetRow(Plugin.ClientState.TerritoryType, out var row) &&
        row.TerritoryIntendedUse.RowId is 41 or 61;

    public static IEnumerable<World> WorldsOnDatacenter(IPlayerCharacter character)
    {
        var dcRow = character.HomeWorld.Value.DataCenter.Value.Region.RowId;
        // !!! 不要用 IsPublic 过滤：CN 世界行 IsPublic 全为 false（实测旅人栈桥=false），
        // 过滤后列表为空，调用处 worlds[0] 会越界崩溃。只按数据中心过滤。
        return WorldSheet.Where(world => world.DataCenter.Value.Region.RowId == dcRow);
    }
}