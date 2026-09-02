using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ChatTwo.Code;
using ChatTwo.GameFunctions;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ChatTwo.Ui.Handler;

public class InputHandler
{
    public const uint ChatOpenSfx = 35u;
    public const uint ChatCloseSfx = 3u;

    private const ImGuiInputTextFlags InputFlags = ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCharFilter |
                                                   ImGuiInputTextFlags.CallbackCompletion | ImGuiInputTextFlags.CallbackHistory;

    public readonly Plugin Plugin;
    public IChatWindow MainWindow { get; set; }  // 可写：tab 跨窗口迁移（弹出合并）时更新归属

    public readonly SendHandler SendHandler;
    public readonly AutoCompleteHandler AutoCompleteHandler;
    public readonly PayloadHandler PayloadHandler;
    public readonly ChunkHandler ChunkHandler;

    public string ChatInput = string.Empty;

    public bool Activate;
    public bool InputFocused;

    public bool PlayedClosingSound = true;

    public long FrameTime; // set every frame
    public long LastActivityTime = Environment.TickCount64;

    public int CursorPos;

    // [InputScrollFix v2] 两步滚动修复状态（文本长度变化时触发）
    private int _lastScrollFixLen = -1;
    private bool _scrollFixStep2;
    public int ActivatePos = -1;

    public readonly string InputHandlerId;

    public InputHandler(IChatWindow mainWindow, Plugin plugin, string id)
    {
        MainWindow = mainWindow;
        Plugin = plugin;
        InputHandlerId = id;

        SendHandler = new SendHandler(plugin);
        ChunkHandler = new ChunkHandler(plugin);
        PayloadHandler = new PayloadHandler(this);
        AutoCompleteHandler = new AutoCompleteHandler(this);
    }

    public unsafe void DrawInputArea(Tab activeTab, float inputWidth, ref bool tellSpecial)
    {
        // 输入文字用独立字体（大小由"输入字体大小"设置控制），输入框高度随之自适应
        using var inputFont = Plugin.FontManager.InputFont.Push();
        // IME 候选字放大：缓存输入字体 ImFont*（AddText detour 在聚焦时临时换字体）
        Plugin.SetImeFont(Plugin.FontManager.InputFont.Available ? (nint)ImGui.GetFont().Handle : 0);

        var inputType = activeTab.CurrentChannel.UseTempChannel
            ? activeTab.CurrentChannel.TempChannel.ToChatType()
            : activeTab.CurrentChannel.Channel.ToChatType();

        var isCommand = ChatInput.Trim().StartsWith('/');
        if (isCommand)
        {
            var command = ChatInput.Split(' ')[0];
            if (Plugin.Commands.TextCommandChannels.TryGetValue(command, out var channel))
                inputType = channel;

            if (!IsValidCommand(command))
                inputType = ChatType.Error;
        }

        var inputColour = Plugin.Config.ChatColours.TryGetValue(inputType, out var inputCol) ? inputCol : inputType.DefaultColor();

        if (!isCommand && Plugin.ExtraChat.ChannelOverride is var (_, overrideColour))
            inputColour = overrideColour;

        if (isCommand && Plugin.ExtraChat.ChannelCommandColours.TryGetValue(ChatInput.Split(' ')[0], out var ecColour))
            inputColour = ecColour;

        var push = inputColour != null;
        using (ImRaii.PushColor(ImGuiCol.Text, push ? ColourUtil.RgbaToAbgr(inputColour!.Value) : 0, push))
        {
            var isChatEnabled = activeTab is { InputDisabled: false };
            if (isChatEnabled && Activate)
                ImGui.SetKeyboardFocusHere();

            var chatCopy = ChatInput;

            // 输入框背景透明度独立（InputAlpha，四透明度之一）；主窗口与 PopOut 统一跟随；
            // 停靠时不透明（窗口背景也不透明）
            var inputBgAlpha = ImGui.IsWindowDocked()
                ? 100f
                : Plugin.Config.InputAlpha;
            var bgAlphaF = inputBgAlpha / 100f;
            // 原生输入框贴图素材取消（素材不好）→ 恢复 FrameBg 颜色 + 描边
            // 自定义输入框背景：CustomInputBg 开启时用自定义 RGB（alpha 统一走 InputAlpha）
            var frameBgU32 = Plugin.Config.CustomInputBg && ColourUtil.RgbaToVector4(Plugin.Config.InputBgColor) is { } customBg
                ? ImGui.GetColorU32(new Vector4(customBg.X, customBg.Y, customBg.Z, bgAlphaF))
                : ImGui.GetColorU32(ImGuiCol.FrameBg, bgAlphaF);
            using (ImRaii.PushColor(ImGuiCol.FrameBg, frameBgU32, Plugin.Config.CustomInputBg || bgAlphaF < 1f))
            using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, frameBgU32, Plugin.Config.CustomInputBg || bgAlphaF < 1f))
            using (ImRaii.PushColor(ImGuiCol.FrameBgActive, frameBgU32, Plugin.Config.CustomInputBg || bgAlphaF < 1f))
            using (ImRaii.Disabled(!isChatEnabled))
            {
                var flags = InputFlags | (!isChatEnabled ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None);
                // 单行输入框贴字：InputText 高度 = FontSize + FramePadding.Y×2，把 Y Push 成 0 → 高度 = FontSize
                {
                    var inputPad = new Vector2(ImGui.GetStyle().FramePadding.X, 0f);
                    using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, inputPad))
                    {
                        ImGui.SetNextItemWidth(inputWidth);
                        ImGui.InputTextWithHint("##chat2-input", isChatEnabled ? "" : Language.ChatLog_DisabledInput, ref ChatInput, 500, flags, Callback);
                        // IME 候选字放大开关：输入框焦点时 AddText detour 放大候选文字
                        Plugin.SetImeActive(ImGui.IsItemActive());
                        // 候选字体句柄（临时 push 候选字体取 ImFont*，供 IME detour 换字体）
                        using (var imeFont = Plugin.FontManager.ImeCandidateFont.Push())
                            Plugin.SetImeFont(Plugin.FontManager.ImeCandidateFont.Available ? (nint)ImGui.GetFont().Handle : 0);
                    }
                }
            }
            var inputActive = ImGui.IsItemActive();
            InputFocused = isChatEnabled && inputActive;

            // 输入框轮廓描边（默认界面也描边，不再限于仿原生）：外圈 1px 黑线
            //（与背景交融）+ 内圈 1px 灰白线（可见边界），四角磨圆
            {
                var rMin = ImGui.GetItemRectMin();
                var rMax = ImGui.GetItemRectMax();
                var dl = ImGui.GetWindowDrawList();
                var grayCol = inputActive
                    ? ImGui.GetColorU32(new Vector4(0.85f, 0.85f, 0.85f, 0.6f))
                    : ImGui.GetColorU32(new Vector4(0.80f, 0.80f, 0.80f, 0.35f));
                var blackCol = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.45f));
                dl.AddRect(rMin - new Vector2(1f, 1f), rMax + new Vector2(1f, 1f), blackCol, 4f, ImDrawFlags.None, 1f);
                dl.AddRect(rMin, rMax, grayCol, 4f, ImDrawFlags.None, 1f);
            }



            if (ImGui.IsItemDeactivated())
            {
                if (ImGui.IsKeyDown(ImGuiKey.Escape))
                {
                    ChatInput = chatCopy;

                    if (activeTab.CurrentChannel.UseTempChannel)
                    {
                        activeTab.CurrentChannel.ResetTempChannel();
                        Plugin.Functions.Chat.SetChannelWithExtraChat(activeTab.CurrentChannel.Channel);
                    }
                }

                if (ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter))
                {
                    Plugin.CommandHelpWindow.IsOpen = false;
                    SendHandler.SendChatBox(activeTab, ref ChatInput, ref tellSpecial);

                    if (activeTab.CurrentChannel.UseTempChannel)
                    {
                        activeTab.CurrentChannel.ResetTempChannel();
                        Plugin.Functions.Chat.SetChannelWithExtraChat(activeTab.CurrentChannel.Channel);
                    }
                }
            }

            // Process keybinds that have modifiers while the chat is focused.
            if (inputActive)
            {
                Plugin.Functions.KeybindManager.HandleKeybinds(KeyboardSource.ImGui, true, true);
                LastActivityTime = FrameTime;
            }

            // Only trigger unfocused if we are currently not calling the auto complete
            if (!Activate && !inputActive && AutoCompleteHandler.AutoCompleteInfo == null)
            {
                if (Plugin.Config.PlaySounds && !PlayedClosingSound)
                {
                    PlayedClosingSound = true;
                    unsafe { UIGlobals.PlaySoundEffect(ChatCloseSfx); }
                }

                if (activeTab.CurrentChannel.UseTempChannel)
                {
                    activeTab.CurrentChannel.ResetTempChannel();
                    Plugin.Functions.Chat.SetChannelWithExtraChat(Plugin.CurrentTab.CurrentChannel.Channel);
                }
            }
        }
    }

    private bool IsValidCommand(string command)
    {
        return Plugin.CommandManager.Commands.ContainsKey(command) || Plugin.Commands.AllCommands.ContainsKey(command);
    }

    private unsafe int Callback(scoped ref ImGuiInputTextCallbackData data)
    {
        // We play the opening sound here only if closing sound has been played before
        if (Plugin.Config.PlaySounds && PlayedClosingSound)
        {
            PlayedClosingSound = false;
            UIGlobals.PlaySoundEffect(ChatOpenSfx);
        }

        CursorPos = data.CursorPos;
        // [InputScrollFix] 中文长文本输入框不自动滚动修复（v2，v1 no-op 无效）：
        // ImGui 源码 imgui_widgets.cpp L4806：Callback 返回后仅当
        // callback_data.CursorPos != utf8_cursor_pos 时 state->CursorFollow = true
        // （v1 的 no-op 赋值值相同 → 不触发 → 无效）；滚动执行在 L4975
        // if (render_cursor && state->CursorFollow) { 算 scroll_x; state->CursorFollow = false; }
        // 即滚动是"事件驱动"的：IME/TSF 上屏不走 ImGui 字符输入路径 → 永不置位 → 不滚动；
        // Backspace 走按键路径 → 置位 → 恢复（"删一字即恢复"）。
        // + 光标在末尾时，做"退1字符→下帧回末尾"两步移动（值确实变化 → 触发
        // 滚动跟随）；只在文本变化时触发（不变化不触发 → 打字期间不会每帧闪烁）。
        if (data.EventFlag == ImGuiInputTextFlags.CallbackAlways && data.BufTextLen > 0)
        {
            if (_scrollFixStep2)
            {
                // 第二步：光标回末尾（值变化 → 触发滚动到末尾）
                _scrollFixStep2 = false;
                data.CursorPos = data.BufTextLen;
            }
            else if (data.BufTextLen != _lastScrollFixLen)
            {
                _lastScrollFixLen = data.BufTextLen; // 文本变化（输入/删除）
                if (data.CursorPos >= data.BufTextLen)
                {
                    // 末尾 → 退 1 个 UTF-8 字符（不能 -1 字节：可能落在多字节字符中间）
                    var curText = MemoryHelper.ReadString((nint)data.Buf, data.BufTextLen);
                    var lastLen = 1;
                    if (curText.Length > 0)
                        lastLen = System.Text.Encoding.UTF8.GetByteCount(curText[^1].ToString());
                    _scrollFixStep2 = true;
                    data.CursorPos = Math.Max(0, data.BufTextLen - lastLen);
                }
            }
        }
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            if (data.CursorPos == 0)
            {
                AutoCompleteHandler.AutoCompleteInfo = new AutoCompleteInfo(string.Empty, data.CursorPos, data.CursorPos);
                AutoCompleteHandler.AutoCompleteOpen = true;
                AutoCompleteHandler.AutoCompleteSelection = 0;

                return 0;
            }

            int white;
            for (white = data.CursorPos - 1; white >= 0; white--)
                if (data.Buf[white] == ' ')
                    break;

            var start = data.Buf + white + 1;
            var end = data.CursorPos - white - 1;
            var utf8Message = Marshal.PtrToStringUTF8((nint)start, end);
            var correctedCursor = data.CursorPos - (end - utf8Message.Length);
            AutoCompleteHandler.AutoCompleteInfo = new AutoCompleteInfo(utf8Message, white + 1, correctedCursor);
            AutoCompleteHandler.AutoCompleteOpen = true;
            AutoCompleteHandler.AutoCompleteSelection = 0;
            return 0;
        }

        if (data.EventFlag == ImGuiInputTextFlags.CallbackCharFilter)
            if (!Plugin.Functions.Chat.IsCharValid((char) data.EventChar))
                return 1;

        if (Activate)
        {
            Activate = false;
            // Use UTF-8 byte count as fallback since ImGui CursorPos is in bytes,
            // not characters. C# string.Length is wrong for multi-byte text.
            data.CursorPos = ActivatePos > -1 ? ActivatePos : Encoding.UTF8.GetByteCount(ChatInput);
            data.SelectionStart = data.SelectionEnd = data.CursorPos;
            ActivatePos = -1;
        }

        Plugin.CommandHelpWindow.IsOpen = false;
        var text = MemoryHelper.ReadString((nint) data.Buf, data.BufTextLen);
        if (text.StartsWith('/'))
        {
            var command = text.Split(' ')[0];
            if (Plugin.Commands.AllCommands.TryGetValue(command, out var textCommand))
                Plugin.CommandHelpWindow.UpdateContent(textCommand.Description);
            else if (Plugin.CommandManager.Commands.TryGetValue(command, out var info) && info.ShowInHelp)
                Plugin.CommandHelpWindow.UpdateContent(info.HelpMessage);
        }

        if (data.EventFlag != ImGuiInputTextFlags.CallbackHistory)
            return 0;

        var prevPos = SendHandler.InputBacklogIdx;
        switch (data.EventKey)
        {
            case ImGuiKey.UpArrow:
                switch (SendHandler.InputBacklogIdx)
                {
                    case -1:
                        var offset = 0;

                        if (!string.IsNullOrWhiteSpace(ChatInput))
                        {
                            SendHandler.AddBacklog(ChatInput);
                            offset = 1;
                        }

                        SendHandler.InputBacklogIdx = SendHandler.InputBacklog.Count - 1 - offset;
                        break;
                    case > 0:
                        SendHandler.InputBacklogIdx--;
                        break;
                }
                break;
            case ImGuiKey.DownArrow:
                if (SendHandler.InputBacklogIdx != -1)
                    if (++SendHandler.InputBacklogIdx >= SendHandler.InputBacklog.Count)
                        SendHandler.InputBacklogIdx = -1;
                break;
        }

        if (prevPos == SendHandler.InputBacklogIdx)
            return 0;

        var historyStr = SendHandler.InputBacklogIdx >= 0 ? SendHandler.InputBacklog[SendHandler.InputBacklogIdx] : string.Empty;
        data.DeleteChars(0, data.BufTextLen);
        data.InsertChars(0, historyStr);

        return 0;
    }
}