using Dalamud.Plugin.Ipc;

namespace ChatTwo.Ipc;

/// <summary>
/// 输入框读写 IPC（供 DR QuickChatPanel 等第三方聊天面板适配）。
/// 所有回调都是同步执行的——调用方必须在游戏主线程（框架线程）调用，
/// 例如在 AddonLifecycle 回调（PostSetup/PostDraw）或框架更新内。
/// </summary>
public sealed class ChatInputIpc : IDisposable
{
    private Plugin Plugin { get; }

    private ICallGateProvider<string> GetGate { get; }
    private ICallGateProvider<bool> SendGate { get; }
    private ICallGateProvider<(float X, float Y, float W, float H)> WindowRectGate { get; }

    public ChatInputIpc(Plugin plugin)
    {
        Plugin = plugin;

        GetGate = Plugin.Interface.GetIpcProvider<string>("ChatTwo.Input.Get");
        GetGate.RegisterFunc(GetText);

        SendGate = Plugin.Interface.GetIpcProvider<bool>("ChatTwo.Input.Send");
        SendGate.RegisterFunc(Send);

        // 聊天主窗口屏幕矩形（ImGui 坐标），供第三方面板（如 DR QuickChatPanel）跟随定位
        WindowRectGate = Plugin.Interface.GetIpcProvider<(float, float, float, float)>("ChatTwo.GetChatWindowRect");
        WindowRectGate.RegisterFunc(GetWindowRect);
    }

    /// <summary>聊天主窗口当前屏幕矩形（ImGui 坐标，每帧更新）。</summary>
    private (float X, float Y, float W, float H) GetWindowRect()
    {
        var log = Plugin.ChatLog;
        if (log == null)
            return default;

        return (log.LastWindowPos.X, log.LastWindowPos.Y,
                log.LastWindowSize.X, log.LastWindowSize.Y);
    }

    /// <summary>
    /// 读当前输入框文本。空输入返回空串，与输入框是否聚焦无关（ChatTwo 的
    /// 输入框失焦后仍保留未发送文本）。
    /// </summary>
    private string GetText()
        => Plugin.ChatLog?.InputHandler?.ChatInput ?? string.Empty;

    /// <summary>
    /// 把当前输入框内容按"按回车"的完整语义发送（频道前缀/自动翻译/tell
    /// 特殊处理/输入历史），发送后清空输入框（SendChatBox 末尾把 ref 参数
    /// 置空）。返回是否有内容被发送：空白文本返回 false 且不做任何操作。
    /// </summary>
    private bool Send()
    {
        var chatLog = Plugin.ChatLog;
        var input = chatLog?.InputHandler;
        // 输入禁用 tab 下原生输入框无文本（原版语义），ChatTwo 的 ChatInput 字段可能
        // 残留旧文本——禁用时不发送不清空，防止误发
        if (chatLog == null || input == null || Plugin.CurrentTab.InputDisabled)
            return false;
        if (string.IsNullOrWhiteSpace(input.ChatInput))
            return false;

        input.SendHandler.SendChatBox(Plugin.CurrentTab, ref input.ChatInput, ref chatLog.TellSpecial);
        return true;
    }

    public void Dispose()
    {
        GetGate.UnregisterFunc();
        SendGate.UnregisterFunc();
        WindowRectGate.UnregisterFunc();
    }
}
