# Chat Input IPC

If your plugin replaces or supplements the chat input area (e.g. a quick-chat
panel), you can read and submit the text currently typed into Chat 2's input
box. Chat 2 hides the native chat log addon, so the native `ChatLog` input
component (node id 5) is **not** usable — always go through these endpoints
instead.

- `ChatTwoCN.Input.Get`: call this function to retrieve the current input box
  text (`string`). Returns an empty string when the box is empty; the text is
  kept even when the input box does not have focus (Chat 2 preserves
  unsent text on blur).
- `ChatTwoCN.Input.Send`: submits the current input box text using Chat 2's full
  send pipeline — channel prefix, auto-translate, tell-special handling and
  input history — exactly as if the player pressed Enter, then clears the box.
  Returns `bool`: `true` when there was non-whitespace text to send, `false`
  when the box was empty (nothing is sent, the box is not cleared).
- `ChatTwoCN.GetChatWindowRect`: call this function to retrieve the chat main
  window's screen rectangle as a `(float X, float Y, float W, float H)` tuple
  (ImGui coordinates, updated every frame). Third-party panels can follow the
  window position with it — e.g. DailyRoutines' QuickChatPanel attaches next to
  the chat window and stays clamped to the work area.

Both calls must be made from the game's main (framework) thread, e.g. inside
an `AddonLifecycle` callback or a `Framework.Update` handler — they are
synchronous and touch UI state directly.

Example usage:
```cs
public sealed class ChatInputIntegration {
    private ICallGateSubscriber<string> Get { get; }
    private ICallGateSubscriber<bool> Send { get; }

    public ChatInputIntegration(DalamudPluginInterface @interface) {
        this.Get  = @interface.GetIpcSubscriber<string>("ChatTwoCN.Input.Get");
        this.Send = @interface.GetIpcSubscriber<bool>("ChatTwoCN.Input.Send");
    }

    // Replace your native-ChatLog-based "is there text in the input box" check
    // (node 5 / SearchNodeById(16)) with this:
    public bool IsAnyTextInBlock()
        => !string.IsNullOrWhiteSpace(this.Get.InvokeFunc());

    // Replace your native input-component send+clear with this. Returns false
    // when the box was empty, mirroring your previous SendChatboxMessage().
    public bool SendChatboxMessage()
        => this.Send.InvokeFunc();
}
```

# Typing State IPC

If you need to know whether the player is currently interacting with Chat 2's
input box, subscribe to the typing IPC.
- `ChatTwoCN.GetChatInputState`: call this function to retrieve the current state.
- `ChatTwoCN.ChatInputStateChanged`: subscribe to this event to receive updates
  whenever the state changes (and once immediately after subscribing).
Both IPC endpoints use the same tuple payload:
```
(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType)
```
- `InputVisible`: `true` when Chat 2 is not hidden by user/cutscene/battle
  settings.
- `InputFocused`: `true` while the Chat 2 input box currently has keyboard focus.
- `HasText`: `true` when the input buffer contains more than whitespace.
- `IsTyping`: convenience flag (`InputFocused && HasText`).
- `TextLength`: length of the raw input buffer.
- `ChannelType`: the `ChatTwo.Code.ChatType` representing the channel/mode that
  will be used if the buffer is submitted. This value comes from the current
  tab's `UsedChannel` (`ChatTwo/Configuration.cs`) which the plugin keeps in
  sync by hooking the in-game shell (`ChatTwo/GameFunctions/Chat.cs`) and by
  resolving temporary overrides inside the chat UI
  (`ChatTwo/Ui/ChatLogWindow.cs:597`). `InputChannel` values are converted into
  the exported `ChatType` via `ChatTwo/Code/InputChannelExt.ToChatType`.
Example usage:
```cs
public sealed class TypingIntegration {
    private ICallGateSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType)> GetChatInputState { get; }
    private ICallGateSubscriber<(bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType)> ChatInputStateChanged { get; }
    public TypingIntegration(DalamudPluginInterface @interface) {
        this.GetChatInputState = @interface.GetIpcSubscriber<(bool, bool, bool, bool, int, ChatType)>("ChatTwoCN.GetChatInputState");
        this.ChatInputStateChanged = @interface.GetIpcSubscriber<(bool, bool, bool, bool, int, ChatType)>("ChatTwoCN.ChatInputStateChanged");
    }
    public void Enable() {
        this.ChatInputStateChanged.Subscribe(OnChatInputStateChanged);
        // Optionally poll the current state on enable.
        var state = this.GetChatInputState.InvokeFunc();
        PluginLog.Information($"Initial typing state: {state}");
    }
    public void Disable() {
        this.ChatInputStateChanged.Unsubscribe(OnChatInputStateChanged);
    }

    private void OnChatInputStateChanged((bool InputVisible, bool InputFocused, bool HasText, bool IsTyping, int TextLength, ChatType ChannelType) state) {
        if (state.IsTyping) {
            // Show typing indicator.
        } else {
            // Hide typing indicator.
        }
    }
}
```

# Quick Chat Panel IPC

Chat 2 shows a quick-panel button (bubble icon) in the input area's right icon
row whenever the DailyRoutines QuickChatPanel module is enabled. Clicking the
button invokes the module's toggle endpoint, which is exposed through the
DailyRoutines IPC attribute system (`IPCAttributeRegistry`).

- `DailyRoutines.Modules.QuickChatPanel.Toggle` (provided by DailyRoutines'
  QuickChatPanel module): call it to toggle the panel. The button is only shown
  while this provider exists (i.e. while the module is enabled), so it appears
  and disappears together with the module.

Chat 2 detects the module by checking `ICallGateSubscriber.HasFunction` and
calls `InvokeAction()` on click — no subscription needed on the panel side.
