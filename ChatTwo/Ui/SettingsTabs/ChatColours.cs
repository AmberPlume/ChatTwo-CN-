using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

public sealed class ChatColours : ISettingsTab
{
    private Plugin Plugin { get; }
    private Configuration Mutable { get; }

    public string Name => Language.Options_ChatColours_Tab + "###tabs-chat-colours";

    public ChatColours(Plugin plugin, Configuration mutable)
    {
        Plugin = plugin;
        Mutable = mutable;

        #if DEBUG
        // Users can set colours for ExtraChat linkshells in the ExtraChat plugin directly.
        var sortable = ChatTypeExt.SortOrder
            .SelectMany(entry => entry.Item2)
            .Where(type => !type.IsGm() && !type.IsExtraChatLinkshell())
            .ToHashSet();
        var total = Enum.GetValues<ChatType>()
            .Where(type => !type.IsGm() && !type.IsExtraChatLinkshell())
            .ToHashSet();
        if (sortable.Count != total.Count)
        {
            Plugin.Log.Warning($"There are {sortable.Count} sortable channels, but there are {total.Count} total channels.");
            total.ExceptWith(sortable);
        }
        #endif
    }

    public void Draw(bool changed)
    {
        // 一键从游戏导入所有频道颜色
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Download, "import-all-colours", tooltip: Language.Options_ChatColours_ImportAll))
        {
            var imported = 0;
            foreach (var (_, types) in ChatTypeExt.SortOrder)
            {
                foreach (var type in types)
                {
                    var gameColour = Plugin.Functions.Chat.GetChannelColor(type);
                    if (gameColour != null)
                    {
                        Mutable.ChatColours[type] = gameColour.Value;
                        imported++;
                    }
                }
            }
            WrapperUtil.AddNotification(string.Format(Language.Options_ChatColours_ImportAll_Success, imported), NotificationType.Success);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(Language.Options_ChatColours_ImportAll_Label);
        ImGui.Spacing();

        foreach (var (_, types) in ChatTypeExt.SortOrder)
        {
            foreach (var type in types)
            {
                if (ImGuiUtil.IconButton(FontAwesomeIcon.UndoAlt, $"{type}", Language.Options_ChatColours_Reset))
                    Mutable.ChatColours.Remove(type);

                ImGui.SameLine();

                if (ImGuiUtil.IconButton(FontAwesomeIcon.LongArrowAltDown, $"{type}", Language.Options_ChatColours_Import))
                {
                    var gameColour = Plugin.Functions.Chat.GetChannelColor(type);
                    Mutable.ChatColours[type] = gameColour ?? type.DefaultColor() ?? 0;
                }

                ImGui.SameLine();

                var vec = Mutable.ChatColours.TryGetValue(type, out var colour)
                    ? ColourUtil.RgbaToVector3(colour)
                    : ColourUtil.RgbaToVector3(type.DefaultColor() ?? 0);
                if (ImGui.ColorEdit3(type.Name(), ref vec, ImGuiColorEditFlags.NoInputs))
                    Mutable.ChatColours[type] = ColourUtil.Vector3ToRgba(vec);
            }
        }

        ImGui.Spacing();
    }
}
