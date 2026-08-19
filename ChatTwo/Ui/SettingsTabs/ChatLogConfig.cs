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
            // 已挪到"基础设置"页（Display.cs）

            // 播放音效 / 显示新人频道加入按钮 / 显示隐藏按钮 / 显示原始道具帮助：
            // 锁定开启且不再显示在设置中（默认值已在 Configuration 中固定）
            // 提示窗口偏移设置项已删除（TooltipOffset 对当前实现无效）

            // 仿原生窗口（v1.40.17+ 从"基础设置"移入，改名"仿原生窗口"）：
            // 使用 FFXIV 原生 UI 贴图（工具栏图标/底部标签页）
            ImGuiUtil.OptionCheckbox(ref Mutable.NativeBackground, Language.Options_NativeBackground_Name, Language.Options_NativeBackground_Description);
            ImGui.Spacing();

            // 隐藏标签页栏末尾的"+"按钮（快捷键/右键菜单仍可新建）——标签页相关，跟随仿原生窗口
            ImGuiUtil.OptionCheckbox(ref Mutable.HideNewTabButton, Language.Options_HideNewTabButton_Name, Language.Options_HideNewTabButton_Description);
            ImGui.Spacing();

            // 输入区缩放：影响输入框、左右图标、频道名行（v1.40.17+ 不再影响 tab 区）
            ImGuiUtil.DragFloatVertical(Language.Options_InputAreaScale_Name, Language.Options_InputAreaScale_Description, ref Mutable.InputAreaScale, 0.05f, 0.5f, 2.0f, $"{Mutable.InputAreaScale * 100f:N0}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();

            // 标签页缩放（v1.40.17+ 新增）：标签页文字/标签栏/末尾 + 按钮，与输入区缩放独立
            ImGuiUtil.DragFloatVertical(Language.Options_TabScale_Name, Language.Options_TabScale_Description, ref Mutable.TabScale, 0.05f, 0.5f, 2.0f, $"{Mutable.TabScale * 100f:N0}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();

            // 四透明度分离：消息区 / 标签页 / 输入框。
            // !!! 背景透明度移除：窗口背景永远透明 0（NativeBackground 改为素材开关）。
            // PopOut 统一跟随（独立透明度已移除）
            ImGuiUtil.DragFloatVertical(Language.Options_MessageAlpha_Name, Language.Options_MessageAlpha_Description, ref Mutable.WindowAlpha, .25f, 0f, 100f, $"{Mutable.WindowAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();

            // 自定义消息区背景颜色（调色盘）：默认关闭 = 跟随主题（自动略变淡）；开启后手动调 RGB。
            // 透明度统一由上面的"消息区透明度"控制（调色盘只调颜色，不调透明度）
            ImGuiUtil.OptionCheckbox(ref Mutable.CustomMessageLogBg, Language.Options_CustomMessageLogBg_Name, Language.Options_CustomMessageLogBg_Description);
            if (Mutable.CustomMessageLogBg)
            {
                var col = ColourUtil.RgbaToVector3(Mutable.MessageLogBgColor);
                if (ImGui.ColorEdit3("##custom-msg-bg-color", ref col, ImGuiColorEditFlags.NoInputs))
                    Mutable.MessageLogBgColor = ColourUtil.Vector3ToRgba(col);
            }
            ImGui.Spacing();

            // 自定义输入框背景颜色（调色盘）：默认关闭 = 跟随主题 FrameBg；开启后手动调 RGB。
            // 透明度统一由"输入框透明度"控制
            ImGuiUtil.OptionCheckbox(ref Mutable.CustomInputBg, Language.Options_CustomInputBg_Name, Language.Options_CustomInputBg_Description);
            if (Mutable.CustomInputBg)
            {
                var inputCol = ColourUtil.RgbaToVector3(Mutable.InputBgColor);
                if (ImGui.ColorEdit3("##custom-input-bg-color", ref inputCol, ImGuiColorEditFlags.NoInputs))
                    Mutable.InputBgColor = ColourUtil.Vector3ToRgba(inputCol);
            }
            ImGui.Spacing();

            // 自定义标签页栏背景颜色（仅非仿原生）：仿原生用三段式贴图素材，颜色由素材自带 → 隐藏
            if (!Mutable.NativeBackground)
            {
                ImGuiUtil.OptionCheckbox(ref Mutable.CustomTabBg, Language.Options_CustomTabBg_Name, Language.Options_CustomTabBg_Description);
                if (Mutable.CustomTabBg)
                {
                    var tabCol = ColourUtil.RgbaToVector3(Mutable.TabBgColor);
                    if (ImGui.ColorEdit3("##custom-tab-bg-color", ref tabCol, ImGuiColorEditFlags.NoInputs))
                        Mutable.TabBgColor = ColourUtil.Vector3ToRgba(tabCol);
                }
                ImGui.Spacing();
            }

            // !!! v1.40.17+ 仿原生界面（NativeBackground）下标签页用三段式贴图，透明度由素材自带，
            // TabAlpha 无效 → 隐藏设置项
            if (!Mutable.NativeBackground)
            {
                ImGuiUtil.DragFloatVertical(Language.Options_TabAlpha_Name, Language.Options_TabAlpha_Description, ref Mutable.TabAlpha, .25f, 0f, 100f, $"{Mutable.TabAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
                ImGui.Spacing();
            }

            ImGuiUtil.DragFloatVertical(Language.Options_InputAlpha_Name, Language.Options_InputAlpha_Description, ref Mutable.InputAlpha, .25f, 0f, 100f, $"{Mutable.InputAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();

            // 输入法候选框透明度（卫月接管候选渲染，ChatTwo 绘制层 hook 时应用）
            ImGuiUtil.DragFloatVertical(Language.Options_ImeCandidateAlpha_Name, Language.Options_ImeCandidateAlpha_Description, ref Mutable.ImeCandidateAlpha, .25f, 0f, 100f, $"{Mutable.ImeCandidateAlpha:N2}%%", ImGuiSliderFlags.AlwaysClamp);
            ImGui.Spacing();

            // 未读消息提示方式已搬至"基础设置"页（v1.40.11+）
            // 日志行数上限已搬至"基础设置"页

            // 显示聊天窗口标题栏 / 显示弹出标签页标题栏：锁定关闭且不再显示在设置中

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
