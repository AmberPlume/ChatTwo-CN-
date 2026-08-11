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
        // 固定使用内置字体（Noto Sans CJK），符号字体已并入主字体；
        // 聊天主字体 / 输入框 / 设置界面各有独立的大小设置
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
}
