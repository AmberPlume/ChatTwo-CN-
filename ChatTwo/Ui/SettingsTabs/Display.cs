using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

public sealed class Display : ISettingsTab
{
    private Configuration Mutable { get; }

    public string Name => Language.Options_Display_Tab + "###tabs-display";

    public Display(Configuration mutable)
    {
        Mutable = mutable;
    }

    public void Draw(bool changed)
    {
        using var wrap = ImRaii.TextWrapPos(0.0f);

        ImGuiUtil.OptionCheckbox(ref Mutable.HideChat, Language.Options_HideChat_Name, Language.Options_HideChat_Description);
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideDuringCutscenes, Language.Options_HideDuringCutscenes_Name, string.Format(Language.Options_HideDuringCutscenes_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideWhenNotLoggedIn, Language.Options_HideWhenNotLoggedIn_Name, string.Format(Language.Options_HideWhenNotLoggedIn_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideWhenUiHidden, Language.Options_HideWhenUiHidden_Name, string.Format(Language.Options_HideWhenUiHidden_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideInLoadingScreens, Language.Options_HideInLoadingScreens_Name, string.Format(Language.Options_HideInLoadingScreens_Description, Plugin.PluginName));
        ImGui.Spacing();

        // "在战斗中隐藏" / "非活动时隐藏"已删除：保持关闭（用户要求）

        ImGui.Separator();
        ImGui.Spacing();

        // ═══════════════ 窗口 ═══════════════
        // 以下项从"窗口调整"页挪入（用户要求：基础设置统一管窗口行为）
        ImGuiUtil.OptionCheckbox(ref Mutable.KeepInputFocus, Language.Options_KeepInputFocus_Name, Language.Options_KeepInputFocus_Description);
        ImGui.Spacing();

        using (var tabCombo = ImGuiUtil.BeginComboVertical(Language.Options_TabPosition_Name, Mutable.TabPosition.Name()))
        {
            if (tabCombo.Success)
            {
                foreach (var tabPos in Enum.GetValues<TabPosition>())
                    if (ImGui.Selectable(tabPos.Name(), Mutable.TabPosition == tabPos))
                        Mutable.TabPosition = tabPos;
            }
        }
        ImGuiUtil.HelpText(Language.Options_TabPosition_Description);
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.CanMove, Language.Options_CanMove_Name);
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.CanResize, Language.Options_CanResize_Name);
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.Use24HourClock, Language.Options_Use24HourClock_Name, Language.Options_Use24HourClock_Description);
        ImGui.Spacing();

        // 定型文列表排序（原在"偏好"页，偏好页已删除，移至此处）
        ImGuiUtil.OptionCheckbox(ref Mutable.SortAutoTranslate, Language.Options_SortAutoTranslate_Name, Language.Options_SortAutoTranslate_Description);
        ImGui.Spacing();

        // "现代化布局/更紧凑的现代布局/隐藏重复的时间戳"已删除：保持传统时间戳样式（用户要求）
        // "折叠重复消息"及其子选项已删除（用户要求）

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ═══════════════ 字体 ═══════════════
        // 自定义字体：字体族下拉（无字号/样式列——字号统一由下面的"字体大小"控制）
        ImGui.TextUnformatted(Language.Options_Font_Name);
        FontFamilyChooser(Language.Options_Font_Name, Mutable.GlobalFontV2);
        ImGui.SameLine();
        if (ImGui.Button("Reset##global-font"))
        {
            Mutable.GlobalFontV2 = new SingleFontSpec { FontId = new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansCjkRegular), SizePt = Mutable.FontSizeV2 };
            Mutable.FontsEnabled = false;  // Reset → 回到 Axis 游戏字体
        }
        ImGuiUtil.HelpText(Language.Options_Font_Description);
        ImGui.Spacing();

        // 主字体大小
        ImGuiUtil.FontSizeCombo(Language.Options_FontSize_Name, ref Mutable.FontSizeV2);
        ImGuiUtil.HelpText(string.Format(Language.Options_Font_Description, Plugin.PluginName));
        ImGui.Spacing();

        // 输入框字体大小（输入框高度随之自适应）
        ImGuiUtil.FontSizeCombo(Language.Options_InputFontSize_Name, ref Mutable.InputFontSize);
        ImGuiUtil.HelpText(Language.Options_InputFontSize_Description);
        ImGui.Spacing();

        // 设置界面字体大小（独立于聊天主字体）
        ImGuiUtil.FontSizeCombo(Language.Options_SettingsFontSize_Name, ref Mutable.SettingsFontSize);
        ImGuiUtil.HelpText(Language.Options_SettingsFontSize_Description);

        ImGui.Spacing();
    }

    // 字体族下拉（替代内置 SingleFontChooserDialog——它自带字号/样式列无法隐藏，字号由"字体大小"统一控制）
    private void FontFamilyChooser(string label, SingleFontSpec current)
    {
        var families = _fontFamilies.Value;
        // 当前字体可能带样式（如 Regular），匹配所属字体族（族下任一字体包含当前 FontId 即命中）
        var currentName = current.FontId.ToString();
        var selectedIdx = families.FindIndex(f => f.Fonts.Any(fid => fid.ToString() == currentName));
        if (selectedIdx == -1)
            selectedIdx = 0;

        if (ImGui.Combo($"##font-family-{label}", ref selectedIdx, families.Select(f => f.EnglishName).ToArray(), families.Count))
        {
            var family = families[selectedIdx];
            Mutable.GlobalFontV2 = new SingleFontSpec { FontId = family.Fonts[family.FindBestMatch(400, 100, 0)], SizePt = Mutable.FontSizeV2 };
            Mutable.FontsEnabled = true;  // 选了自定义字体 → 消息改用自定义字体
        }
    }

    private static readonly Lazy<List<IFontFamilyId>> _fontFamilies = new(() =>
    {
        var list = new List<IFontFamilyId> { DalamudDefaultFontAndFamilyId.Instance };
        list.AddRange(IFontFamilyId.ListDalamudFonts());
        list.AddRange(IFontFamilyId.ListGameFonts());
        var systemFonts = IFontFamilyId.ListSystemFonts(true);
        systemFonts.Sort((a, b) => string.Compare(a.EnglishName, b.EnglishName, StringComparison.CurrentCultureIgnoreCase));
        list.AddRange(systemFonts);
        return list;
    });
}
