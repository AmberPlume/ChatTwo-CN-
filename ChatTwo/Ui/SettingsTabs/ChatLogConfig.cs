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
            // 提示窗口偏移设置项已删除（TooltipOffset 对当前实现无效）

            // 输入区缩放：影响输入框、左右图标、tab 文字、末尾 + 按钮
            ImGuiUtil.DragFloatVertical(Language.Options_InputAreaScale_Name, Language.Options_InputAreaScale_Description, ref Mutable.InputAreaScale, 0.05f, 0.5f, 2.0f, $"{Mutable.InputAreaScale * 100f:N0}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();

            // 四透明度分离：消息区（原窗口透明度，字段名 WindowAlpha 保留）/
            // 背景 / 标签页 / 输入框。PopOut 统一跟随四项（独立透明度已移除）
            ImGuiUtil.DragFloatVertical(Language.Options_MessageAlpha_Name, Language.Options_MessageAlpha_Description, ref Mutable.WindowAlpha, .25f, 0f, 100f, $"{Mutable.WindowAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();
            ImGuiUtil.DragFloatVertical(Language.Options_BackgroundAlpha_Name, Language.Options_BackgroundAlpha_Description, ref Mutable.BackgroundAlpha, .25f, 0f, 100f, $"{Mutable.BackgroundAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();
            ImGuiUtil.DragFloatVertical(Language.Options_TabAlpha_Name, Language.Options_TabAlpha_Description, ref Mutable.TabAlpha, .25f, 0f, 100f, $"{Mutable.TabAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();
            ImGuiUtil.DragFloatVertical(Language.Options_InputAlpha_Name, Language.Options_InputAlpha_Description, ref Mutable.InputAlpha, .25f, 0f, 100f, $"{Mutable.InputAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();

            // 未读消息提示方式已搬至"基础设置"页（v1.40.11+）

            if (ImGuiUtil.InputIntVertical(Language.Options_MaxLinesToShow_Name, Language.Options_MaxLinesToShow_Description, ref Mutable.MaxLinesToRender))
                Mutable.MaxLinesToRender = Math.Clamp(Mutable.MaxLinesToRender, 1, 10_000);
            ImGui.Spacing();

            // 显示聊天窗口标题栏 / 显示弹出标签页标题栏：用户要求锁定关闭且不再显示在设置中

            ImGuiUtil.OptionCheckbox(ref Mutable.OverrideStyle, Language.Options_OverrideStyle_Name, Language.Options_OverrideStyle_Name_Desc);
            ImGui.Spacing();

            // 覆盖样式下拉紧跟复选框（此前在方法末尾隔着快捷键区，展开位置不对）
            if (Mutable.OverrideStyle)
            {
                var styles = StyleModel.GetConfiguredStyles();
                if (styles == null)
                {
                    ImGui.TextUnformatted(Language.Options_OverrideStyle_NotAvailable);
                    ImGui.Spacing();
                }
                else
                {
                    var currentStyle = Mutable.ChosenStyle ?? Language.Options_OverrideStyle_NotSelected;
                    using var combo = ImRaii.Combo(Language.Options_OverrideStyleDropdown_Name, currentStyle);
                    if (combo)
                    {
                        foreach (var style in styles)
                            if (ImGui.Selectable(style.Name, Mutable.ChosenStyle == style.Name))
                                Mutable.ChosenStyle = style.Name;
                    }
                }
                ImGui.Spacing();
            }

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
    }
}
