using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Ipc;

namespace ChatTwo;

public sealed class IpcManager : IDisposable
{
    private ICallGateProvider<string> RegisterGate { get; }
    private ICallGateProvider<string, object?> UnregisterGate { get; }
    private ICallGateProvider<object?> AvailableGate { get; }
    private ICallGateProvider<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?> InvokeGate { get; }
    private ICallGateProvider<object?> QuickChatPanelToggleGate { get; }

    public List<string> Registered { get; } = [];

    public IpcManager()
    {
        RegisterGate = Plugin.Interface.GetIpcProvider<string>("ChatTwo.Register");
        RegisterGate.RegisterFunc(Register);

        AvailableGate = Plugin.Interface.GetIpcProvider<object?>("ChatTwo.Available");

        UnregisterGate = Plugin.Interface.GetIpcProvider<string, object?>("ChatTwo.Unregister");
        UnregisterGate.RegisterAction(Unregister);

        InvokeGate = Plugin.Interface.GetIpcProvider<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?>("ChatTwo.Invoke");

        // 快捷聊天面板开关（DR QuickChatPanel 模块订阅）：只有 DR 订阅时才显示输入区按钮
        QuickChatPanelToggleGate = Plugin.Interface.GetIpcProvider<object?>("ChatTwo.QuickChatPanel.Toggle");

        AvailableGate.SendMessage();
    }

    /// <summary>快捷聊天面板按钮是否显示（DR 模块已订阅时 true）。</summary>
    public bool QuickChatPanelAvailable => QuickChatPanelToggleGate.SubscriptionCount > 0;

    /// <summary>通知订阅方切换快捷聊天面板开关。</summary>
    public void ToggleQuickChatPanel()
        => QuickChatPanelToggleGate.SendMessage();

    public void Invoke(string id, PlayerPayload? sender, ulong contentId, Payload? payload, SeString? senderString, SeString? content)
    {
        InvokeGate.SendMessage(id, sender, contentId, payload, senderString, content);
    }

    private string Register()
    {
        var id = Guid.NewGuid().ToString();
        Registered.Add(id);
        return id;
    }

    private void Unregister(string id)
    {
        Registered.Remove(id);
    }

    public void Dispose()
    {
        UnregisterGate.UnregisterFunc();
        RegisterGate.UnregisterFunc();
        Registered.Clear();
    }
}
