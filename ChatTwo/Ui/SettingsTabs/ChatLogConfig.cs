using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

public sealed class ChatLogConfig : ISettingsTab
{
    private readonly Plugin Plugin;
    private Configuration Mutable { get; }

    public string Name => Language.Options_ChatLog_Tab + "###tabs-chatlog";

    public ChatLogConfig(Plugin plugin, Configuration mutable)
    {
        Plugin = plugin;
        Mutable = mutable;
    }

    public void Draw(bool changed)
    {
        using (ImRaii.TextWrapPos(0.0f))
        {
            // 保持输入焦点 / 标签页位置 / 允许移动 / 允许调整大小：
            // 已按用户要求挪到"基础设置"页（Display.cs）

            // 播放音效 / 显示新人频道加入按钮 / 显示隐藏按钮 / 显示原始道具帮助：
            // 用户要求锁定开启且不再显示在设置中（默认值已在 Configuration 中固定）

            if (Mutable.NativeItemTooltips)
            {
                ImGuiUtil.DragFloatVertical(Language.Options_TooltipOffset_Name, Language.Options_TooltipOffset_Desc, ref Mutable.TooltipOffset, 1, 0f, 400f, $"{Mutable.TooltipOffset:N0}px", ImGuiSliderFlags.AlwaysClamp);
                ImGui.Spacing();
            }

            ImGuiUtil.DragFloatVertical(Language.Options_WindowOpacity_Name, ref Mutable.WindowAlpha, .25f, 0f, 100f, $"{Mutable.WindowAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();

            if (ImGuiUtil.InputIntVertical(Language.Options_MaxLinesToShow_Name, Language.Options_MaxLinesToShow_Description, ref Mutable.MaxLinesToRender))
                Mutable.MaxLinesToRender = Math.Clamp(Mutable.MaxLinesToRender, 1, 10_000);
            ImGui.Spacing();

            // 显示聊天窗口标题栏 / 显示弹出标签页标题栏：用户要求锁定关闭且不再显示在设置中

            ImGuiUtil.OptionCheckbox(ref Mutable.OverrideStyle, Language.Options_OverrideStyle_Name, Language.Options_OverrideStyle_Name_Desc);
            ImGui.Spacing();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextUnformatted(Language.Options_ChatTabForwardKeybind_Name);
            ImGui.SetNextItemWidth(-1);
            ImGuiUtil.KeybindInput("ChatTabForwardKeybind", ref Mutable.ChatTabForward);

            ImGui.TextUnformatted(Language.Options_ChatTabBackwardKeybind_Name);
            ImGui.SetNextItemWidth(-1);
            ImGuiUtil.KeybindInput("ChatTabBackwardKeybind", ref Mutable.ChatTabBackward);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextUnformatted(Language.Options_AdjustPosition_Name);
            ImGui.SetNextItemWidth(-1);
            var pos = Plugin.ChatLog.LastWindowPos;
            if (ImGui.DragFloat2($"##{Language.Options_AdjustPosition_Name}", ref pos, 1, 0, float.MaxValue, "%.0fpx"))
                Plugin.ChatLog.Position = pos;
            ImGuiUtil.WarningText(Language.Options_AdjustPosition_Warning);
            ImGui.Spacing();
        }

        if (!Mutable.OverrideStyle)
            return;

        var styles = StyleModel.GetConfiguredStyles();
        if (styles == null)
        {
            ImGui.TextUnformatted(Language.Options_OverrideStyle_NotAvailable);
            ImGui.Spacing();
            return;
        }

        var currentStyle = Mutable.ChosenStyle ?? Language.Options_OverrideStyle_NotSelected;
        using var combo = ImRaii.Combo(Language.Options_OverrideStyleDropdown_Name, currentStyle);
        if (combo)
        {
            foreach (var style in styles)
                if (ImGui.Selectable(style.Name, Mutable.ChosenStyle == style.Name))
                    Mutable.ChosenStyle = style.Name;
        }

        ImGui.Spacing();
    }
}
