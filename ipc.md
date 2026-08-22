# Context Menu IPC Integration

If you want to display custom menu items in the chat context menu, you can use
Chat 2's IPC.

Here's an example.

```cs
public class ContextMenuIntegration {
    // This is used to register your plugin with the IPC. It will return an ID
    // that you should save for later.
    private ICallGateSubscriber<string> Register { get; }
    // This is used to unregister your plugin from the IPC. You should call this
    // when your plugin is unloaded.
    private ICallGateSubscriber<string, object?> Unregister { get; }
    // You should subscribe to this event in order to receive a notification
    // when Chat 2 is loaded or updated, so you can re-register.
    private ICallGateSubscriber<object?> Available { get; }
    // Subscribe to this to draw your custom context menu items.
    private ICallGateSubscriber<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?> Invoke { get; }

    // The registration ID.
    private string? _id;

    public ChatTwoIpc(DalamudPluginInterface @interface) {
        this.Register = @interface.GetIpcSubscriber<string>("ChatTwo.Register");
        this.Unregister = @interface.GetIpcSubscriber<string, object?>("ChatTwo.Unregister");
        this.Invoke = @interface.GetIpcSubscriber<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?>("ChatTwo.Invoke");
        this.Available = @interface.GetIpcSubscriber<object?>("ChatTwo.Available");
    }

    public void Enable() {
        // When Chat 2 becomes available (if it loads after this plugin) or when
        // Chat 2 is updated, register automatically.
        this.Available.Subscribe(() => this.Register());
        // Register if Chat 2 is already loaded.
        this.Register();

        // Listen for context menu events.
        this.Invoke.Subscribe(this.Integration);
    }

    private void Register() {
        // Register and save the registration ID.
        this._id = this.Register.InvokeFunc();
    }

    public void Disable() {
        if (this._id != null) {
            this.Unregister.InvokeAction(this._id);
            this._id = null;
        }

        this.Invoke.Unsubscribe(this.Integration);
    }

    private void Integration(string id, PlayerPayload? sender, ulong contentId, Payload? payload, SeString? senderString, SeString? content) {
        // Make sure the ID is the same as the saved registration ID.
        if (id != this._id) {
            return;
        }

        // Draw your custom menu items here.
        // sender is the first PlayerPayload contained in the sender SeString
        // contentId is the content ID of the message sender or 0 if not known
        // payload is the payload that was right-clicked, if any (excluding text)
        // senderString is the message sender SeString
        // content is the message content SeString
        if (ImGui.Selectable("Test plugin")) {
            PluginLog.Log($"hi!\nsender: {sender}\ncontent id: {contentId:X}\npayload: {payload}\nsender string: {senderString}\ncontent string: {content}");
        }
    }
}
```

# Chat Input IPC

If your plugin replaces or supplements the chat input area (e.g. a quick-chat
panel), you can read and submit the text currently typed into Chat 2's input
box. Chat 2 hides the native chat log addon, so the native `ChatLog` input
component (node id 5) is **not** usable — always go through these endpoints
instead.

- `ChatTwo.Input.Get`: call this function to retrieve the current input box
  text (`string`). Returns an empty string when the box is empty; the text is
  kept even when the input box does not have focus (Chat 2 preserves
  unsent text on blur).
- `ChatTwo.Input.Send`: submits the current input box text using Chat 2's full
  send pipeline — channel prefix, auto-translate, tell-special handling and
  input history — exactly as if the player pressed Enter, then clears the box.
  Returns `bool`: `true` when there was non-whitespace text to send, `false`
  when the box was empty (nothing is sent, the box is not cleared).

Both calls must be made from the game's main (framework) thread, e.g. inside
an `AddonLifecycle` callback or a `Framework.Update` handler — they are
synchronous and touch UI state directly.

Example usage:
```cs
public sealed class ChatInputIntegration {
    private ICallGateSubscriber<string> Get { get; }
    private ICallGateSubscriber<bool> Send { get; }

    public ChatInputIntegration(DalamudPluginInterface @interface) {
        this.Get  = @interface.GetIpcSubscriber<string>("ChatTwo.Input.Get");
        this.Send = @interface.GetIpcSubscriber<bool>("ChatTwo.Input.Send");
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
- `ChatTwo.GetChatInputState`: call this function to retrieve the current state.
- `ChatTwo.ChatInputStateChanged`: subscribe to this event to receive updates
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
        this.GetChatInputState = @interface.GetIpcSubscriber<(bool, bool, bool, bool, int, ChatType)>("ChatTwo.GetChatInputState");
        this.ChatInputStateChanged = @interface.GetIpcSubscriber<(bool, bool, bool, bool, int, ChatType)>("ChatTwo.ChatInputStateChanged");
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

If a plugin wants to display a toggle button next to Chat 2's input box (like
DailyRoutines' QuickChatPanel), it can subscribe to this endpoint and Chat 2
will show a lightning-bolt button in the input area's right icon row whenever
at least one subscriber is present. Clicking it fires the toggle.

- `ChatTwo.QuickChatPanel.Toggle`: subscribe to receive a notification when the
  user clicks the quick-panel button in Chat 2's input area. Chat 2 shows the
  button only while `SubscriptionCount > 0` (i.e. while your plugin is
  subscribed), so the button appears and disappears together with your module.

Example usage:
```cs
public sealed class QuickChatPanelIntegration {
    private ICallGateSubscriber<object?> Toggle { get; }

    public QuickChatPanelIntegration(DalamudPluginInterface @interface) {
        this.Toggle = @interface.GetIpcSubscriber<object?>("ChatTwo.QuickChatPanel.Toggle");
    }

    public void Enable() {
        // While subscribed, Chat 2 displays the quick-panel button in its input area.
        this.Toggle.Subscribe(OnToggle);
    }

    public void Disable() {
        this.Toggle.Unsubscribe(OnToggle);
    }

    private void OnToggle() {
        // Toggle your panel (e.g. an ImGui overlay).
        this.Overlay.IsOpen ^= true;
    }
}
```

All integrations are called inside of an ImGui `BeginMenu`.
