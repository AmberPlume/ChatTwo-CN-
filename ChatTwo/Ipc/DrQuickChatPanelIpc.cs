using Dalamud.Plugin.Ipc;

namespace ChatTwo.Ipc;

/// <summary>
/// DailyRoutines QuickChatPanel 模块对接：按钮显示 = DR 模块已启用（provider 存在），
/// 点击 = 调用 DR 的 DailyRoutines.Modules.QuickChatPanel.Toggle 切换面板开关。
/// </summary>
public sealed class DrQuickChatPanelIpc
{
    private ICallGateSubscriber<bool> ToggleGate { get; }

    public DrQuickChatPanelIpc()
    {
        ToggleGate = Plugin.Interface.GetIpcSubscriber<bool>("DailyRoutines.Modules.QuickChatPanel.Toggle");
    }

    /// <summary>DR QuickChatPanel 模块是否已启用（provider 存在才显示输入区按钮）。</summary>
    public bool Available => ToggleGate.HasFunction;

    /// <summary>请求 DR 切换快捷聊天面板开关。</summary>
    public void Toggle()
        => ToggleGate.InvokeFunc();
}
