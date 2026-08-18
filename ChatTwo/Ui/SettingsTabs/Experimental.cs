using ChatTwo.Util;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace ChatTwo.Ui.SettingsTabs;

/// <summary>
/// 实验功能设置页（新增）。
/// 存放处于实验阶段的开关；每个开关附"可能导致的问题"说明，方便自行权衡/回退。
/// </summary>
public sealed class Experimental : ISettingsTab
{
    private Configuration Mutable { get; }

    public string Name => "实验功能" + "###tabs-experimental";

    public Experimental(Configuration mutable)
    {
        Mutable = mutable;
    }

    public void Draw(bool changed)
    {
        using var wrap = ImRaii.TextWrapPos(0.0f);

        ImGui.TextColored(ImGuiColors.DalamudOrange, "实验性功能");
        ImGuiUtil.TooltipOnLastItem("以下功能处于实验阶段，可能影响视觉体验或稳定性。如遇问题可在此关闭。");

        ImGuiHelpers.ScaledDummy(10.0f);

        // ── 菜单位置模式 ──
        ImGui.Checkbox("菜单位置跟随鼠标", ref Mutable.ExperimentalMenuFollowMouse);
        ImGuiUtil.TooltipOnLastItem(
            "开启：右键菜单出现在鼠标位置（游戏原生）。\n" +
            "关闭：右键菜单固定出现在右侧（视觉稳定）。\n\n" +
            "开启时可能出现的问题：\n" +
            "· 边缘文字可能进入菜单区域（文本量大的标签页更明显）\n" +
            "· 圆角阶梯可能降级（圆角变粗，极端情况接近直角）\n" +
            "· 跨边界字符可能整字消失（仅菜单覆盖的文本量极大时的处理，菜单关闭后消失的字会正常出现）");

        ImGuiHelpers.ScaledDummy(10.0f);
    }
}
