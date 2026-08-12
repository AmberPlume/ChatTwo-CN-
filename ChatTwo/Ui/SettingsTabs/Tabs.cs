using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Colors;
using System.IO;
using System.Text.Json;

namespace ChatTwo.Ui.SettingsTabs;

public sealed class Tabs : ISettingsTab
{
    private readonly Plugin Plugin;
    private Configuration Mutable { get; }

    public string Name => Language.Options_Tabs_Tab + "###tabs-tabs";

    /// <summary>
    /// 标签页配置导入/导出的 JSON 序列化选项。
    /// Tab 的字段大多是 public 字段（非属性），必须开启 IncludeFields；
    /// 运行时字段（Messages/CurrentChannel/Identifier 等）已标 [JsonIgnore]，不会进入文件。
    /// </summary>
    private static readonly JsonSerializerOptions TabJsonOptions = new()
    {
        IncludeFields = true,
        WriteIndented = true,
    };

    private int ToOpen = -2;

    public Tabs(Plugin plugin, Configuration mutable)
    {
        Plugin = plugin;
        Mutable = mutable;
    }

    /// <summary>导出标签页配置到用户选择的 JSON 文件。</summary>
    private void ExportTabs()
    {
        try
        {
            // 临时标签页（如正在进行的 tell 标签页）不导出
            var tabs = Mutable.Tabs.Where(t => !t.IsTempTab).ToList();
            var json = JsonSerializer.Serialize(tabs, TabJsonOptions);

            Plugin.FileDialogManager.SaveFileDialog(
                Language.Options_Tabs_Export_Title, ".json", "ChatTwo-tabs", "json",
                (ok, path) =>
                {
                    if (!ok || string.IsNullOrEmpty(path)) return;
                    try { File.WriteAllText(path, json); }
                    catch (Exception ex) { Plugin.Log.Error(ex, "[Tabs] 导出标签页配置失败"); }
                },
                // 起始路径用配置的标签页导出位置（空则对话框用默认位置）
                string.IsNullOrEmpty(Mutable.ExportDirectory) ? null : Mutable.ExportDirectory);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Tabs] 导出标签页配置失败");
        }
    }

    /// <summary>从用户浏览选择的 JSON 文件导入标签页配置（追加到现有标签页）。</summary>
    private void ImportTabs()
    {
        Plugin.FileDialogManager.OpenFileDialog(
            Language.Options_Tabs_Import_Title, ".json",
            (ok, paths) =>
            {
                if (!ok || paths is not { Count: > 0 }) return;
                var path = paths[0];
                try
                {
                    var text = File.ReadAllText(path);
                    var tabs = JsonSerializer.Deserialize<List<Tab>>(text, TabJsonOptions);
                    if (tabs == null || tabs.Count == 0)
                        return;

                    foreach (var tab in tabs)
                    {
                        // 运行时字段（Messages/CurrentChannel/Identifier 等）带 [JsonIgnore]，
                        // 反序列化时由字段初始化器自动生成默认值，无需手动重置
                        tab.IsTempTab = false;
                    }

                    Mutable.Tabs.AddRange(tabs);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "[Tabs] 导入标签页配置失败");
                }
            },
            1,
            // 起始路径用配置的标签页导出位置（空则对话框用默认位置）
            string.IsNullOrEmpty(Mutable.ExportDirectory) ? null : Mutable.ExportDirectory);
    }

    public void Draw(bool changed)
    {
        const string addTabPopup = "add-tab-popup";

        // 导入/导出按钮（一行）
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Upload, "import-tabs", tooltip: Language.Options_Tabs_Import_Name))
            ImportTabs();

        ImGui.SameLine();

        if (ImGuiUtil.IconButton(FontAwesomeIcon.Download, "export-tabs", tooltip: Language.Options_Tabs_Export_Name))
            ExportTabs();

        ImGui.NewLine();

        // 标签页导入/导出默认文件夹（放在导入导出与添加按钮之间，避免与历史记录混淆）
        var exportDir = Mutable.ExportDirectory;
        ImGui.SetNextItemWidth(-60);
        ImGui.InputText("##exportpath", ref exportDir, 512, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiUtil.IconButton(FontAwesomeIcon.FolderOpen, "exportpath", tooltip: Language.Options_Database_ExportDir_Browse))
            Plugin.FileDialogManager.OpenFolderDialog(Language.Options_Database_ExportDir_Browse_Title, (b, s) =>
            {
                if (b && !string.IsNullOrEmpty(s))
                    Mutable.ExportDirectory = s;
            }, string.IsNullOrEmpty(Mutable.ExportDirectory) ? null : Mutable.ExportDirectory, true);
        ImGuiUtil.HelpText(Language.Options_Database_ExportDir_Description);

        ImGui.NewLine();

        // 添加标签页按钮（独立一行）
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Plus, "add-tab", tooltip: Language.Options_Tabs_Add))
            ImGui.OpenPopup(addTabPopup);

        using (var popup = ImRaii.Popup(addTabPopup))
        {
            if (popup)
            {
                if (ImGui.Selectable(Language.Options_Tabs_NewTab))
                    Mutable.Tabs.Add(new Tab());

                ImGui.Separator();

                if (ImGui.Selectable(string.Format(Language.Options_Tabs_Preset, Language.Tabs_Presets_General)))
                    Mutable.Tabs.Add(TabsUtil.VanillaGeneral);

                if (ImGui.Selectable(string.Format(Language.Options_Tabs_Preset, Language.Tabs_Presets_Event)))
                    Mutable.Tabs.Add(TabsUtil.VanillaEvent);

                if (ImGui.Selectable(string.Format(Language.Options_Tabs_Preset, Language.Tabs_Presets_Tell)))
                    Mutable.Tabs.Add(TabsUtil.VanillaTellExclusive);
            }
        }

        var toRemove = -1;
        var doOpens = ToOpen > -2;
        for (var i = 0; i < Mutable.Tabs.Count; i++)
        {
            var tab = Mutable.Tabs[i];

            if (doOpens)
                ImGui.SetNextItemOpen(i == ToOpen);

            using var treeNode = ImRaii.TreeNode($"{tab.Name}###tab-{i}");
            if (!treeNode.Success)
                continue;

            using var pushedId = ImRaii.PushId($"tab-{i}");

            // 第一行：操作按钮 + 标签页名称输入框（用户要求同行）
            if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, tooltip: Language.Options_Tabs_Delete))
            {
                toRemove = i;
                ToOpen = -1;
            }

            ImGui.SameLine();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.ArrowUp, tooltip: Language.Options_Tabs_MoveUp) && i > 0)
            {
                (Mutable.Tabs[i - 1], Mutable.Tabs[i]) = (Mutable.Tabs[i], Mutable.Tabs[i - 1]);
                ToOpen = i - 1;
            }

            ImGui.SameLine();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.ArrowDown, tooltip: Language.Options_Tabs_MoveDown) && i < Mutable.Tabs.Count - 1)
            {
                (Mutable.Tabs[i + 1], Mutable.Tabs[i]) = (Mutable.Tabs[i], Mutable.Tabs[i + 1]);
                ToOpen = i + 1;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText(Language.Options_Tabs_Name, ref tab.Name, 512, ImGuiInputTextFlags.EnterReturnsTrue);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // ═══════════════ 基本 ═══════════════
            ImGui.Checkbox(Language.Options_Tabs_ShowTimestamps, ref tab.DisplayTimestamp);
            ImGui.Checkbox(Language.Options_Tabs_PopOut, ref tab.PopOut);
            if (tab.PopOut)
            {
                using var _ = ImRaii.PushIndent(10.0f);
                // 显示输入区域设置已删除：游戏原生弹出的消息窗口不能输入，PopOut 固定无输入区

                // 独立透明度已移除：PopOut 统一跟随主窗口四项透明度设置

                ImGui.Checkbox(Language.Options_Tabs_IndependentHide, ref tab.IndependentHide);
                if (tab.IndependentHide)
                {
                    using var __ = ImRaii.PushIndent(10.0f);
                    ImGuiUtil.OptionCheckbox(ref tab.HideDuringCutscenes, Language.Options_HideDuringCutscenes_Name);
                    ImGui.Spacing();

                    ImGuiUtil.OptionCheckbox(ref tab.HideWhenNotLoggedIn, Language.Options_HideWhenNotLoggedIn_Name);
                    ImGui.Spacing();

                    ImGuiUtil.OptionCheckbox(ref tab.HideWhenUiHidden, Language.Options_HideWhenUiHidden_Name);
                    ImGui.Spacing();

                    ImGuiUtil.OptionCheckbox(ref tab.HideInLoadingScreens, Language.Options_HideInLoadingScreens_Name);
                    ImGui.Spacing();

                    // "在战斗中隐藏"已删除：保持关闭（用户要求）
                }

                ImGuiUtil.OptionCheckbox(ref tab.CanResize, Language.Popout_CanResize_Name);
                ImGui.Spacing();
            }

            // 未读模式设置已删除：固定为"未看过的"（UnreadMode.Unseen，默认值，用户要求）

            if (Mutable.HideWhenInactive)
                ImGui.Checkbox(Language.Options_Tabs_InactivityBehaviour, ref tab.UnhideOnActivity);

            // 接收频道（本标签页显示哪些频道的消息）——放在"输入频道"上方
            using (var disabled = ImRaii.Disabled(tab.Channel == InputChannel.Tell))
            {
                ImGuiUtil.ChannelSelector(Language.Options_Tabs_Channels, tab.SelectedChannels);
                if (tab.Channel == InputChannel.Tell && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Language.Options_Tabs_TellTabChannelSelection);
            }

            // 输入频道：用与上方"接收频道"一致的折叠样式（标题即折叠行），避免
            // "标题+下拉框"和"标题在折叠上"两种形态混排（用户反馈视觉不统一）
            if (!tab.InputDisabled)
            {
                using (var node = ImRaii.TreeNode(Language.Options_Tabs_InputChannel))
                {
                    if (node.Success)
                    {
                        if (ImGui.Selectable(Language.Options_Tabs_NoInputChannel, tab.Channel == null))
                            tab.Channel = null;

                        foreach (var channel in Enum.GetValues<InputChannel>())
                            if (ImGui.Selectable(channel.ToChatType().Name(), tab.Channel == channel))
                                tab.Channel = channel;
                    }
                }

                var player = Plugin.ObjectTable.LocalPlayer;
                if (tab.Channel == InputChannel.Tell && player != null)
                {
                    ImGui.Checkbox(Language.Options_Tabs_SenderMessages, ref tab.AllSenderMessages);
                    ImGuiUtil.HelpText(Language.Options_Help_SenderMessagesV2);

                    var worlds = Sheets.WorldsOnDatacenter(player).OrderByDescending(world => world.DataCenter.RowId).ThenBy(world => world.Name.ToString()).ToList();

                    using (ImRaii.ItemWidth(ImGui.GetWindowWidth() / 3f))
                    {
                        ImGui.Text(Language.Options_Header_Target);
                        ImGui.SameLine();

                        var name = tab.TellTarget.Name;
                        if (ImGui.InputText("##targetInput", ref name, 21))
                            tab.TellTarget.Name = name;

                        ImGui.SameLine();

                        var selectedWorld = worlds.FindIndex(world => world.RowId == tab.TellTarget.World);
                        if (selectedWorld == -1)
                            selectedWorld = 0;

                        using (var combo = ImRaii.Combo("###player-world", worlds[selectedWorld].Name.ToString()))
                        {
                            if (combo.Success)
                            {
                                var lastDc = worlds.First().DataCenter.RowId;
                                foreach (var (idx, world) in worlds.Index())
                                {
                                    if (lastDc != world.DataCenter.RowId)
                                    {
                                        lastDc = world.DataCenter.RowId;
                                        ImGui.Separator();
                                    }

                                    if (ImGui.Selectable(world.Name.ToString(), selectedWorld == idx))
                                    {
                                        selectedWorld = idx;
                                        tab.TellTarget.World = worlds[selectedWorld].RowId;
                                    }
                                }
                            }
                        }
                    }

                    if (tab.TellTarget.ContentId == 0)
                        ImGuiUtil.WrappedTextWithColor(ImGuiColors.DalamudOrange, Language.Options_Tabs_ContentIdWarning);

                    var target = (Plugin.TargetManager.SoftTarget ?? Plugin.TargetManager.Target) as IPlayerCharacter;
                    using (ImRaii.Disabled(target == null))
                    {
                        if (ImGui.Button(Language.Options_Tab_SetTarget) && target != null)
                            tab.TellTarget.FromTarget(target);
                    }
                }
            }

            // 禁用此频道的输入（放在"输入频道"折叠菜单下方）
            ImGui.Checkbox(Language.Options_Tabs_NoInput, ref tab.InputDisabled);

            // ExtraChat 频道
            using (var disabled = ImRaii.Disabled(tab.Channel == InputChannel.Tell))
            {
                ImGuiUtil.ExtraChatSelector(Language.Options_Tabs_ExtraChatChannels, ref tab.ExtraChatAll, tab.ExtraChatChannels);
                if (tab.Channel == InputChannel.Tell && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Language.Options_Tabs_TellTabChannelSelection);
            }
        }

        if (toRemove > -1)
        {
            Mutable.Tabs.RemoveAt(toRemove);
            Plugin.WantedTab = 0;
        }

        if (doOpens)
            ToOpen = -2;
    }
}
