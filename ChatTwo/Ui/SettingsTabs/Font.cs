using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

public sealed class Font : ISettingsTab
{
    private Configuration Mutable { get; }

    public string Name => Language.Options_Font_Tab + "###tabs-font";

    public Font(Configuration mutable)
    {
        Mutable = mutable;
    }

    public void Draw(bool changed)
    {
        using var wrap = ImRaii.TextWrapPos(0.0f);

        // 字体设置说明：各设置项负责的文字位置一览
        ImGui.TextUnformatted(Language.Options_Font_PageHint);
        ImGui.Spacing();

        // 自定义字体：字体族下拉（无字号/样式列——字号统一由下面的"字体大小"控制）
        ImGui.TextUnformatted(Language.Options_Font_Name);
        ImGuiUtil.TooltipOnLastItem(Language.Options_Font_Description);   // 悬浮"自定义字体"标题即出说明
        FontFamilyChooser(Language.Options_Font_Name, Mutable.GlobalFontV2);
        ImGui.SameLine();
        if (ImGui.Button("Reset##global-font"))
        {
            Mutable.GlobalFontV2 = new SingleFontSpec { FontId = new DalamudAssetFontAndFamilyId(DalamudAsset.NotoSansCjkRegular), SizePt = Mutable.FontSizeV2 };
            Mutable.FontsEnabled = false;  // Reset → 回到 Axis 游戏字体
        }
        ImGui.Spacing();

        // 消息区文字大小
        ImGuiUtil.FontSizeCombo(Language.Options_FontSize_Name, ref Mutable.FontSizeV2);
        ImGuiUtil.TooltipOnLastItem(Language.Options_Font_Description);   // 悬浮下拉框即出说明
        ImGui.Spacing();

        // 输入框文字大小（输入框高度随之自适应）
        ImGuiUtil.FontSizeCombo(Language.Options_InputFontSize_Name, ref Mutable.InputFontSize);
        ImGuiUtil.TooltipOnLastItem(Language.Options_InputFontSize_Description);
        ImGui.Spacing();

        // 标签页名称文字大小（默认/仿原生 tab 均生效）
        ImGuiUtil.FontSizeCombo(Language.Options_TabFontSize_Name, ref Mutable.TabFontSizePt);
        ImGuiUtil.TooltipOnLastItem(Language.Options_TabFontSize_Description);
        ImGui.Spacing();

        // 当前输入频道文字大小
        ImGuiUtil.FontSizeCombo(Language.Options_ChannelFontSize_Name, ref Mutable.ChannelNameFontSizePt);
        ImGuiUtil.TooltipOnLastItem(Language.Options_ChannelFontSize_Description);
        ImGui.Spacing();

        // 改变输入法候选框（大开关）——默认关闭。关闭时全部 IME detour 走透传（卫月原始 IME）；
        // 开启后以下子选项生效。
        var modifyIme = Mutable.ModifyImeCandidate;
        if (ImGui.Checkbox(Language.Options_ModifyImeCandidate_Name, ref modifyIme))
            Mutable.ModifyImeCandidate = modifyIme;
        ImGuiUtil.TooltipOnLastItem(Language.Options_ModifyImeCandidate_Description);
        ImGui.Spacing();

        // 子选项：仅大开关开启时显示
        using (ImRaii.Disabled(!Mutable.ModifyImeCandidate))
        {
            // 输入法候选词文字大小
            ImGuiUtil.FontSizeCombo(Language.Options_ImeCandidateFontSize_Name, ref Mutable.ImeCandidateFontSizePt);
            ImGuiUtil.TooltipOnLastItem(Language.Options_ImeCandidateFontSize_Description);
            ImGui.Spacing();
        }

        // 输入法候选框透明度（独立于大开关——透明度是简单 col 调整，不影响渲染逻辑）
        ImGuiUtil.DragFloatVertical(Language.Options_ImeCandidateAlpha_Name, Language.Options_ImeCandidateAlpha_Description, ref Mutable.ImeCandidateAlpha, .25f, 0f, 100f, $"{Mutable.ImeCandidateAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
        ImGui.Spacing();

        // 设置界面文字大小（独立于聊天主字体）
        ImGuiUtil.FontSizeCombo(Language.Options_SettingsFontSize_Name, ref Mutable.SettingsFontSize);
        ImGuiUtil.TooltipOnLastItem(Language.Options_SettingsFontSize_Description);
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