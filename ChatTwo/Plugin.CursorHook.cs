using Dalamud.Hooking;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChatTwo;

public sealed partial class Plugin
{
// Cursor 结构内字段偏移（句柄值每进程变，结构偏移固定）
    private const int CursorArrowOffset = 0x18;
    private const int CursorClickableOffset = 0x70;

    // 光标状态：Draw 阶段置位，UpdateCursorDecision 帧末消费。
    internal static bool CursorInChatWindow;
    internal static bool AnyInteractiveHovered;
// volatile 字段：detour 可能跨线程读
    private static volatile bool _detourInChat;
    private static volatile bool _detourClickable;
    private static nint _clickCursor;
    private static nint _arrowCursor;
    private static bool _lastClickableForSfx;

    // 上次设置的光标形状；仅在变化时 SetCursor（避免菜单关闭没人调用导致光标卡住）
    private enum CursorShape { None, Arrow, Clickable }
    private static CursorShape _lastSetCursorShape;

    // 状态沿主动调 SetCursor（游戏静止时不调 user32.SetCursor，detour 无触发时机）。
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetCursor(nint hCursor);

// 用矩形判定（而非 IsWindowHovered）
    internal static void MarkCursorInChatWindow()
    {
        var mp = ImGui.GetIO().MousePos;
        var wMin = ImGui.GetWindowPos();
        var wMax = wMin + ImGui.GetWindowSize();
        if (mp.X >= wMin.X && mp.X <= wMax.X && mp.Y >= wMin.Y && mp.Y <= wMax.Y)
            CursorInChatWindow = true;
    }

// 帧末消费 hover 标志：仅 clickable 上升沿播音效，按 wanted 差量 SetCursor
    internal static void UpdateCursorDecision()
    {
        var inChat = CursorInChatWindow;
        var clickable = inChat && AnyInteractiveHovered;

// clickable 上升沿播一次悬停音
        if (!_lastClickableForSfx && clickable)
            PlayHoverSfx();
        _lastClickableForSfx = clickable;

// 期望光标：聊天框外=不干预 / clickable=手指 / 其余聊天区=箭头
        CursorShape wanted;
        if (clickable) wanted = CursorShape.Clickable;
        else if (inChat) wanted = CursorShape.Arrow;
        else wanted = CursorShape.None;

// 形状变化时主动 SetCursor（避免 detour 无触发时机）
        if (wanted != _lastSetCursorShape)
        {
            _lastSetCursorShape = wanted;
            if (wanted == CursorShape.Clickable)
            {
                if (_clickCursor != 0) SetCursor(_clickCursor);
            }
            else if (wanted == CursorShape.Arrow)
            {
                if (_arrowCursor != 0) SetCursor(_arrowCursor);
            }
            // None：不主动设置，交给游戏/OS 默认行为。
        }

        _detourInChat = inChat;
        _detourClickable = clickable;
        CursorInChatWindow = false;
        AnyInteractiveHovered = false;
    }

    // 通过 SetCursorType 播放游戏原生悬停音
    private static unsafe void PlayHoverSfx()
    {
        try
        {
            var stage = AtkStage.Instance();
            if (stage != null)
                stage->AtkCursor.SetCursorType(AtkCursor.CursorType.Clickable, true);
        }
        catch (Exception ex)
        {
            Log.Error($"[Cursor] hover sfx failed: {ex.Message}");
        }
    }

// hook user32.SetCursor
    private static Hook<SetCursorDelegate>? _setCursorHook;
    private delegate nint SetCursorDelegate(nint hCursor);

    private void InitCursorHook()
    {
        try
        {
            var user32 = GetModuleHandle("user32.dll");
            var addr = GetProcAddress(user32, "SetCursor");
            if (addr == 0)
                return;
            _setCursorHook = GameInteropProvider.HookFromAddress<SetCursorDelegate>(addr, SetCursorDetour);
            _setCursorHook.Enable();
// 缓存句柄
            unsafe
            {
                var cp = Cursor.Instance();
                if (cp != null)
                {
                    _clickCursor = *(nint*)((byte*)cp + CursorClickableOffset);
                    _arrowCursor = *(nint*)((byte*)cp + CursorArrowOffset);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[CursorHook] init failed: {ex.Message}");
        }
    }

// 聊天窗口内 clickable → 强制手指句柄；否则透传。detour 不分配内存。
    private static unsafe nint SetCursorDetour(nint hCursor)
    {
        if (_detourInChat && _detourClickable)
        {
            if (_clickCursor != 0)
                return _setCursorHook!.Original(_clickCursor);
        }
        // 聊天框内 SetCursor(NULL) → 换箭头（菜单关闭瞬间游戏隐藏光标）
        if (hCursor == 0 && _detourInChat)
        {
            if (_arrowCursor != 0)
                return _setCursorHook!.Original(_arrowCursor);
        }
        return _setCursorHook!.Original(hCursor);
    }
}
