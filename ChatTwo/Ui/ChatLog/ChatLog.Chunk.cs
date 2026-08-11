namespace ChatTwo.Ui.ChatLog;

public partial class ChatLog
{
    public void DrawChannelName(Tab activeTab, bool sendChannelSwitch = false)
    {
        var currentChannel = ReadChannelName(activeTab);
        if (sendChannelSwitch && !currentChannel.SequenceEqual(PreviousChannel))
        {
            PreviousChannel = currentChannel;
        }

        // 输入框左侧的当前频道名用小号字体（用户要求）
        using var smallFont = Plugin.FontManager.SmallFont.Push();
        InputHandler.ChunkHandler.DrawChunks(currentChannel);
    }
}