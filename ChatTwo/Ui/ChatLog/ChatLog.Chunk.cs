namespace ChatTwo.Ui.ChatLog;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

public partial class ChatLog
{
    public void DrawChannelName(Tab activeTab, bool sendChannelSwitch = false)
    {
        var currentChannel = ReadChannelName(activeTab);
        if (sendChannelSwitch && !currentChannel.SequenceEqual(PreviousChannel))
        {
            PreviousChannel = currentChannel;
        }

        // 输入框左侧的当前频道名用小号字体（要求）
        using var smallFont = Plugin.FontManager.SmallFont.Push();
        // 仿原生 FFXIV：手动渲染实现"裹一层"光晕——荧光白偏黄粗描边 + 亮白主文字
        var text = string.Concat(currentChannel.Select(c => c is TextChunk t ? t.Content : string.Empty));
        if (text.Length == 0)
            return;
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var pos = ImGui.GetCursorScreenPos();
        var glowCol = ImGui.GetColorU32(new Vector4(1.00f, 0.88f, 0.55f, 0.55f)); // 光晕：荧光黄（更黄更明显）
        var textCol = ImGui.GetColorU32(new Vector4(1.00f, 1.00f, 0.95f, 1f));     // 主文字：亮白
        // 4 方向（十字）0.5px 描边——更细，避免糊
        var glowOffsets = new Vector2[] { new(0f, -0.5f), new(0f, 0.5f), new(-0.5f, 0f), new(0.5f, 0f) };
        foreach (var off in glowOffsets)
            drawList.AddText(font, fontSize, pos + off, glowCol, text);
        drawList.AddText(font, fontSize, pos, textCol, text);
        // 推进光标（模拟 ImGui.Text 行为：X 到文本宽，Y 到下一行）
        var textSize = ImGui.CalcTextSize(text);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + textSize.X);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textSize.Y);
    }
}