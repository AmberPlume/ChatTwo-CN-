using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using System.IO;
using System.Numerics;
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

    // !!! 标签页列表只保留一行操作按钮 + 名称，
    // 点击名称弹出独立编辑窗口（PopupModal），设置页不再出现超长展开树。
    private int EditingTab = -1;   // 正在编辑的标签页索引（-1 = 无）
    private bool EditPopupOpen;    // 编辑弹窗打开状态（ref bool 用，点 X 关闭自动置 false）

    /// <summary>打开指定标签页的编辑弹窗（新建/移动后跟随调用）。</summary>
    private void OpenTabEditor(int index)
    {
        EditingTab = index;
        EditPopupOpen = true;
    }

    public Tabs(Plugin plugin, Configuration mutable)
    {
        Plugin = plugin;
        Mutable = mutable;
    }

    /// <summary>导出标签页配置到选择的 JSON 文件。</summary>
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

    /// <summary>从浏览选择的 JSON 文件导入标签页配置（追加到现有标签页）。</summary>
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
        ImGuiUtil.TooltipOnLastItem(Language.Options_Database_ExportDir_Description);   // 悬浮路径框即出说明
        ImGui.SameLine();
        if (ImGuiUtil.IconButton(FontAwesomeIcon.FolderOpen, "exportpath", tooltip: Language.Options_Database_ExportDir_Browse))
            Plugin.FileDialogManager.OpenFolderDialog(Language.Options_Database_ExportDir_Browse_Title, (b, s) =>
            {
                if (b && !string.IsNullOrEmpty(s))
                    Mutable.ExportDirectory = s;
            }, string.IsNullOrEmpty(Mutable.ExportDirectory) ? null : Mutable.ExportDirectory, true);

        ImGui.NewLine();

        // 添加标签页按钮（独立一行）
        if (ImGuiUtil.IconButton(FontAwesomeIcon.Plus, "add-tab", tooltip: Language.Options_Tabs_Add))
            ImGui.OpenPopup(addTabPopup);

        using (var popup = ImRaii.Popup(addTabPopup))
        {
            if (popup)
            {
                if (ImGui.Selectable(Language.Options_Tabs_NewTab))
                {
                    Mutable.Tabs.Add(new Tab());
                    OpenTabEditor(Mutable.Tabs.Count - 1);   // 新建后立即打开编辑器
                    ImGui.CloseCurrentPopup();
                }

                ImGui.Separator();

                if (ImGui.Selectable(string.Format(Language.Options_Tabs_Preset, Language.Tabs_Presets_General)))
                {
                    Mutable.Tabs.Add(TabsUtil.VanillaGeneral);
                    OpenTabEditor(Mutable.Tabs.Count - 1);
                    ImGui.CloseCurrentPopup();
                }

                if (ImGui.Selectable(string.Format(Language.Options_Tabs_Preset, Language.Tabs_Presets_Event)))
                {
                    Mutable.Tabs.Add(TabsUtil.VanillaEvent);
                    OpenTabEditor(Mutable.Tabs.Count - 1);
                    ImGui.CloseCurrentPopup();
                }

                if (ImGui.Selectable(string.Format(Language.Options_Tabs_Preset, Language.Tabs_Presets_Tell)))
                {
                    Mutable.Tabs.Add(TabsUtil.VanillaTellExclusive);
                    OpenTabEditor(Mutable.Tabs.Count - 1);
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        var toRemove = -1;

        // 未读消息设置（放在编辑标签页上方）
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted(Language.Options_UnreadSettings_Title);
        ImGuiUtil.TooltipOnLastItem(Language.Options_UnreadSettings_Description);   // 悬浮标题即出说明
        ImGui.Spacing();

        // 计入未读的频道：复用 ChannelSelector（空 = 全部频道）；说明悬浮在标题上
        ImGuiUtil.ChannelSelector(Language.Options_UnreadSettings_Channels, Mutable.UnreadChannels, Language.Options_UnreadSettings_Channels_Description);
        ImGui.Spacing();

        // 计入未读的标签页：可折叠节点，全选/清空 + 逐个勾选（对应 tab.UnreadEnabled）
        using (var tabsNode = ImRaii.TreeNode(Language.Options_UnreadSettings_Tabs))
        {
            if (tabsNode.Success)
            {
                ImGuiUtil.TooltipOnLastItem(Language.Options_UnreadSettings_Tabs_Description);   // 悬浮标题即出说明
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Check, "unread-tabs-all", tooltip: Language.Options_UnreadSettings_Tabs_SelectAll))
                {
                    foreach (var t in Mutable.Tabs)
                        t.UnreadEnabled = true;
                }
                ImGui.SameLine();
                if (ImGuiUtil.IconButton(FontAwesomeIcon.Times, "unread-tabs-none", tooltip: Language.Options_UnreadSettings_Tabs_SelectNone))
                {
                    foreach (var t in Mutable.Tabs)
                        t.UnreadEnabled = false;
                }
                for (var i = 0; i < Mutable.Tabs.Count; i++)
                    ImGui.Checkbox($"{Mutable.Tabs[i].Name}###unread-tab-{i}", ref Mutable.Tabs[i].UnreadEnabled);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 时间戳（显示时间戳从"基础设置"移入；不折叠，勾选后缩进出现子选项）
        ImGuiUtil.OptionCheckbox(ref Mutable.ShowTimestamp, Language.Options_ShowTimestamp_Name, Language.Options_ShowTimestamp_Description);
        // 未勾选开启时只看到"显示时间戳"；勾选后下方首行缩进出现子选项（相互独立互不干扰）
        if (Mutable.ShowTimestamp)
        {
            using (ImRaii.PushIndent(10.0f))
            {
                ImGui.Spacing();
                ImGuiUtil.OptionCheckbox(ref Mutable.RemoveTimestampBrackets, Language.Options_Timestamp_RemoveBrackets_Name, Language.Options_Timestamp_RemoveBrackets_Description);
                ImGui.Spacing();
                ImGuiUtil.OptionCheckbox(ref Mutable.CompactTimestampSpacing, Language.Options_Timestamp_Compact_Name, Language.Options_Timestamp_Compact_Description);
                ImGui.Spacing();
                // 合并相同时间（原版 HideSameTimestamps 回归）：同一分钟连续消息只显示第一个时间戳
                ImGuiUtil.OptionCheckbox(ref Mutable.MergeSameTimestamps, Language.Options_Timestamp_MergeSame_Name, Language.Options_Timestamp_MergeSame_Description);
                ImGui.Spacing();
                // 时间戳单独成列：正文整体缩进，换行不回到时间戳下方
                ImGuiUtil.OptionCheckbox(ref Mutable.TimestampOwnColumn, Language.Options_Timestamp_OwnColumn_Name, Language.Options_Timestamp_OwnColumn_Description);
                ImGui.Spacing();
                // 时间戳字间距（只作用时间戳；正文有独立的"字间距"）
                ImGuiUtil.DragFloatVertical(Language.Options_Timestamp_LetterSpacing_Name, Language.Options_Timestamp_LetterSpacing_Description, ref Mutable.TimestampLetterSpacing, 0.1f, -3f, 6f, $"{Mutable.TimestampLetterSpacing:0.0}px", ImGuiSliderFlags.AlwaysClamp);
                ImGui.Spacing();
                // 时间戳与正文间距（单独成列时生效；0 表示贴紧时间戳右缘）
                ImGuiUtil.DragFloatVertical(Language.Options_Timestamp_ColumnGap_Name, Language.Options_Timestamp_ColumnGap_Description, ref Mutable.TimestampColumnGap, 0.5f, 0f, 40f, $"{Mutable.TimestampColumnGap:0.0}px", ImGuiSliderFlags.AlwaysClamp);
                ImGui.Spacing();
            }
        }

        // 正文字间距（作用于消息正文，不影响时间戳/发送者名；与段落间距同级）
        ImGuiUtil.DragFloatVertical(Language.Options_LetterSpacing_Name, Language.Options_LetterSpacing_Description, ref Mutable.MessageLetterSpacing, 0.1f, -3f, 6f, $"{Mutable.MessageLetterSpacing:0.0}px", ImGuiSliderFlags.AlwaysClamp);
        ImGui.Spacing();

        // 段落间距独立于时间戳开关（消息行距；负值在字体行高余量内收紧，过负会文字重叠）
        ImGuiUtil.DragFloatVertical(Language.Options_MessageSpacing_Name, Language.Options_MessageSpacing_Description, ref Mutable.MessageLineSpacing, 0.5f, -8f, 20f, $"{Mutable.MessageLineSpacing:0.0}px", ImGuiSliderFlags.AlwaysClamp);
        ImGui.Spacing();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 标签页列表（一行 = 操作按钮 + 名称，点名称开独立编辑窗）
        for (var i = 0; i < Mutable.Tabs.Count; i++)
        {
            var tab = Mutable.Tabs[i];
            using var pushedId = ImRaii.PushId($"tab-row-{i}");

            if (ImGuiUtil.IconButton(FontAwesomeIcon.TrashAlt, tooltip: Language.Options_Tabs_Delete))
            {
                toRemove = i;
                if (EditingTab == i)
                    EditPopupOpen = false;
            }

            ImGui.SameLine();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.ArrowUp, tooltip: Language.Options_Tabs_MoveUp) && i > 0)
            {
                (Mutable.Tabs[i - 1], Mutable.Tabs[i]) = (Mutable.Tabs[i], Mutable.Tabs[i - 1]);
                if (EditingTab == i) EditingTab = i - 1;   // 编辑跟随移动
            }

            ImGui.SameLine();

            if (ImGuiUtil.IconButton(FontAwesomeIcon.ArrowDown, tooltip: Language.Options_Tabs_MoveDown) && i < Mutable.Tabs.Count - 1)
            {
                (Mutable.Tabs[i + 1], Mutable.Tabs[i]) = (Mutable.Tabs[i], Mutable.Tabs[i + 1]);
                if (EditingTab == i) EditingTab = i + 1;
            }

            ImGui.SameLine();

            // 名称按钮：点击打开独立编辑窗口
            if (ImGui.Button($"{tab.Name}###open-edit-{i}", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                OpenTabEditor(i);
            if (ImGui.IsItemHovered())
                ImGuiUtil.Tooltip(Language.Options_Tabs_EditTooltip);
        }

        if (toRemove > -1)
        {
            Mutable.Tabs.RemoveAt(toRemove);
            Plugin.WantedTab = 0;
            if (EditingTab == toRemove || EditingTab >= Mutable.Tabs.Count)
                EditPopupOpen = false;
        }

        // 编辑窗（不用模态窗口 → 普通浮动窗，设置页仍可操作）
        if (EditPopupOpen && EditingTab >= 0 && EditingTab < Mutable.Tabs.Count)
        {
            ImGui.SetNextWindowSize(new Vector2(540f * ImGuiHelpers.GlobalScale, 560f * ImGuiHelpers.GlobalScale), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            var tab = Mutable.Tabs[EditingTab];
            var isOpen = EditPopupOpen;
            if (ImGui.Begin($"{Language.Options_Tabs_EditTitle}###tab-edit-window", ref isOpen))
            {
                if (!isOpen)
                {
                    EditPopupOpen = false;   // 点右上角 X 关闭
                }
                else
                {
                    // 可滚动内容区（窗口不滚动，内容放 Child 里）
                    using (var content = ImRaii.Child("##tab-edit-scroll", new Vector2(520f * ImGuiHelpers.GlobalScale, 480f * ImGuiHelpers.GlobalScale)))
                    {
                        if (content.Success)
                            DrawTabEditContents(tab);
                    }

                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    if (ImGui.Button(Language.Options_Tabs_EditDone))
                        EditPopupOpen = false;
                }
            }
            ImGui.End();
        }
    }

    /// <summary>标签页编辑弹窗内容：名称 + 该标签页的全部设置。</summary>
    private void DrawTabEditContents(Tab tab)
    {
        // 名称（重命名移入编辑窗，列表行只做入口）
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText(Language.Options_Tabs_Name, ref tab.Name, 512, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 基本
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

                // "在战斗中隐藏"已删除：保持关闭
            }

            ImGuiUtil.OptionCheckbox(ref tab.CanResize, Language.Popout_CanResize_Name);
            ImGui.Spacing();
        }

        // 未读模式设置已删除：固定为"未看过的"（UnreadMode.Unseen，默认值）

        if (Mutable.HideWhenInactive)
            ImGui.Checkbox(Language.Options_Tabs_InactivityBehaviour, ref tab.UnhideOnActivity);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 频道
        // 接收频道（本标签页显示哪些频道的消息）——放在"输入频道"上方
        using (var disabled = ImRaii.Disabled(tab.Channel == InputChannel.Tell))
        {
            ImGuiUtil.ChannelSelector(Language.Options_Tabs_Channels, tab.SelectedChannels);
            if (tab.Channel == InputChannel.Tell && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Language.Options_Tabs_TellTabChannelSelection);
        }

        // ExtraChat 频道
        using (var disabled2 = ImRaii.Disabled(tab.Channel == InputChannel.Tell))
        {
            ImGuiUtil.ExtraChatSelector(Language.Options_Tabs_ExtraChatChannels, ref tab.ExtraChatAll, tab.ExtraChatChannels);
            if (tab.Channel == InputChannel.Tell && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Language.Options_Tabs_TellTabChannelSelection);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 输入
        // 输入频道：用与上方"接收频道"一致的折叠样式（标题即折叠行），避免
        // "标题+下拉框"和"标题在折叠上"两种形态混排（视觉不统一）
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

            // 输入频道始终锁定（需求）：仅当选择了具体频道（非"无"）时显示。
            // 勾选 = 每帧强制频道（手动切换被拉回）；不勾选 = 只在切换到此标签页时自动设置一次
            if (tab.Channel != null)
                ImGui.Checkbox(Language.Options_Tabs_InputChannelLocked, ref tab.InputChannelLocked);

            var player = Plugin.ObjectTable.LocalPlayer;
            if (tab.Channel == InputChannel.Tell && player != null)
            {
                ImGui.Checkbox(Language.Options_Tabs_SenderMessages, ref tab.AllSenderMessages);
                ImGuiUtil.TooltipOnLastItem(Language.Options_Help_SenderMessagesV2);   // 悬浮勾选框/文字即出说明

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
    }
}
