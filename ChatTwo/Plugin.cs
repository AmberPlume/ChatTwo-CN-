using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using ChatTwo.Ipc;
using ChatTwo.Resources;
using ChatTwo.Ui;
using ChatTwo.Ui.ChatLog;
using ChatTwo.Util;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChatTwo;

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class Plugin : IDalamudPlugin
{
    public const string PluginName = "Chat 2";

    // !!! 精细鼠标光标（游戏原生）：NoMouseCursorChange 下 ImGui 不碰光标；
    // 可点击元素（按钮/tab/链接）hover 时显示**游戏自己的光标句柄**（原生手指）。
    // 方案：hook user32 SetCursor（游戏设光标必经）——detour 里鼠标在聊天窗口可点击
    // 元素上时把句柄换成游戏手指（Cursor+0x70）再放行：完全同步、无闪烁、无音效。
    // 句柄偏移扫描确认：Arrow=+0x18 / Clickable=+0x70（结构固定，句柄值每进程变）。
    internal static bool CursorInChatWindow;
    internal static bool AnyInteractiveHovered;
    // !!! 统一方案：hook user32 SetCursor（游戏设光标必经），detour 按缓存状态换句柄。
    // 手指句柄取游戏 Cursor+0x70（游戏原生手指）；聊天窗口内非可点击/离开窗口时透传，由游戏自管。
    // !!! volatile 缓存为主线程帧末写、游戏线程 detour 读；工作 flag 帧末清空，
    // 而游戏线程 SetCursor 常在清空后发生 → detour 直接读工作 flag 会恒 false。
    private static volatile bool _detourInChat;
    private static volatile bool _detourClickable;
    private static nint _clickCursor;
    private static nint _arrowCursor;
    private static bool _lastClickableForSfx;

    // !!! 主动 SetCursor：游戏在 ImGui 窗口（聊天框）上方静止时完全不调 user32.SetCursor
    //（实测 4.7s 静止 hover 0 次调用）→ detour 无触发时机。主线程在 hover 状态沿主动调一次
    // SetCursor：上升沿设手指句柄、下降沿设箭头句柄。游戏静止不覆盖 → 设置后保持、不闪烁。
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetCursor(nint hCursor);


    /// <summary>标记鼠标在本聊天窗口内（三窗口共用；帧末 UpdateCursorDecision 统一决策光标）。
    /// !!! 用鼠标位置 vs 窗口矩形判定（不用 IsWindowHovered——按下瞬间它返回 false，
    /// 会导致点击时手指消失）；与 hover/焦点/弹窗无关。</summary>
    internal static void MarkCursorInChatWindow()
    {
        var mp = ImGui.GetIO().MousePos;
        var wMin = ImGui.GetWindowPos();
        var wMax = wMin + ImGui.GetWindowSize();
        if (mp.X >= wMin.X && mp.X <= wMax.X && mp.Y >= wMin.Y && mp.Y <= wMax.Y)
            CursorInChatWindow = true;
    }

    /// <summary>帧末：把本帧 ImGui 计算的 hover 状态缓存给 detour，并在状态沿主动调 SetCursor。
    /// !!! 游戏在 ImGui 窗口（聊天框）上方静止时不调 user32.SetCursor（实测 4.7s 0 次）→
    /// 仅靠 detour 被动拦截无触发时机 → 上升沿主动设手指句柄、下降沿主动设箭头句柄；
    /// 游戏静止不覆盖 → 设置后保持、不闪烁。</summary>
    internal static void UpdateCursorDecision()
    {
        var inChat = CursorInChatWindow;
        var clickable = inChat && AnyInteractiveHovered;
        // 手指状态 false→true（进入可点击元素）时播一次游戏原生悬停音（SetCursorType 触发）；
        // 只在进入时调一次——不会连续响
        if (!_lastClickableForSfx && clickable)
        {
            PlayHoverSfx();
            // 上升沿：先同步缓存再主动设手指（detour 会读缓存再换一次，无害）
            _detourInChat = true;
            _detourClickable = true;
            if (_clickCursor != 0)
                SetCursor(_clickCursor);
        }
        else if (_lastClickableForSfx && !clickable)
        {
            // 下降沿：同步缓存 + 主动恢复箭头（detour 透传）
            _detourInChat = false;
            _detourClickable = false;
            if (_arrowCursor != 0)
                SetCursor(_arrowCursor);
        }
        _lastClickableForSfx = clickable;
        _detourInChat = inChat;
        _detourClickable = clickable;
        CursorInChatWindow = false;
        AnyInteractiveHovered = false;
    }

    /// <summary>游戏原生悬停音：调 SetCursorType 让游戏播"进入可点击区域"的音效（原生行为）。
    /// 仅显示层 hook 控制光标，此调用只播音效/同步内部状态，不影响手指显示。</summary>
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

    // !!! hook user32 SetCursor：游戏设光标必经此处，detour 按缓存状态换句柄
    //（观察版 hook 曾验证此路径有效；用 GetProcAddress 拿导出地址，非游戏模块内偏移）
    private static Hook<SetCursorDelegate>? _setCursorHook;
    private delegate nint SetCursorDelegate(nint hCursor);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint GetModuleHandle(string moduleName);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string procName);

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
            // 缓存游戏手指句柄（右键等场景，游戏手指即此）与箭头句柄；主动 SetCursor 状态沿用
            unsafe
            {
                var cp = Cursor.Instance();
                if (cp != null)
                {
                    _clickCursor = *(nint*)((byte*)cp + 0x70);
                    _arrowCursor = *(nint*)((byte*)cp + 0x18);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[CursorHook] init failed: {ex.Message}");
        }
    }

    /// <summary>鼠标在聊天窗口内且 hover 可点击元素 → 换成游戏手指句柄；否则原样透传（游戏自管箭头）。
    /// 兜底路径：游戏主动调 SetCursor 时也按缓存换句柄；常态由主线程状态沿主动推。
    /// !!! detour 必须极简（任意线程可能调用）：无分配、只读 volatile bool + 一次字段读取。</summary>
    private static unsafe nint SetCursorDetour(nint hCursor)
    {
        if (_detourInChat && _detourClickable)
        {
            if (_clickCursor != 0)
                return _setCursorHook!.Original(_clickCursor);
        }
        return _setCursorHook!.Original(hCursor);
    }

    // === 输入法候选放大（只 hook 绘制层：AddText 放大字 + AddRectFilled 放大框；
    // !!! 实测：CalcTextSize hook（布局放大）会触发 ImGui Fallback "Debug##Default" 窗口，
    // 绘制层 hook 无副作用）===
    // !!! 结构（卫月 DalamudIme.Draw 源码确认，勿改前提）：
    //   候选框(AddRectFilled#1) → 拼音框(AddRectFilled#2) → 拼音文字(AddText composition)
    //   → 分隔线(AddLine) → 候选词×N(AddText，强制 "N. " 前缀，含英文候选)
    //   → 分隔线(AddLine) → 页码(AddText "1/9 (1/1)")
    // !!! 方案（用户决策）：拼音框/拼音文字/分隔线全部移出屏幕（不显示）；
    //   候选框放大 + 候选词放大（相对候选框左下锚点映射）+ 页码保留（位置跟随、字体不放大）
    private static Hook<AddTextDelegate>? _imeAddTextHook;
    private static Hook<AddRectFilledDelegate>? _imeRectHook;
    private static Hook<AddLineDelegate>? _imeLineHook;
    private static bool _imeActive;
    private static nint _imeFont;
    private static nint _foregroundDl;
    // 候选活跃标志：AddText 前台触发 = 候选在绘制；AddRectFilled（候选框，画在文字前）
    // 用"上一帧候选活跃"决定是否放大（避免聚焦时放大其他前台矩形）
    private static bool _candidateThisFrame;
    private static bool _candidatePrev;
    // 放大比例 = 候选字体 px / 卫月默认 16px
    private static float _imeScale = 1f;
    // 本帧候选框已放大标记（后续 AddRectFilled = 拼音框 → 移出屏幕）
    private static bool _imeRectDone;
    // 候选框锚点 = 原始框**左下角（pMin.X, pMax.Y - offset）**——映射基准不含拼音空隙
    //（候选词/页码 = anchor - (原始距框底 - 拼音空隙) × yCoeff，左/下边固定向上排列）
    private static System.Numerics.Vector2 _imeAnchor;
    // 本帧原始候选框底（AddRectFilled 记录——AddText 同帧映射用：候选词距框底距离 = pMax.Y - pos.Y）
    private static System.Numerics.Vector2 _imeOrigMax;
    // 整体上移偏移：卫月候选框基于游戏窗口定位（输入框在 tab 内时框向下展开、
    // 超出 tab 底部、拼音盖输入框）→ 用拼音框位置（= 输入框光标处）做参考，
    // 框底上移到拼音框顶上方。固定偏移（不进映射，无反馈）
    private static float _imeOffset;
    // 界面缩放（卫月默认字体 px / 16，AddTextDetour 未 push 时记录）——
    // 页码不放大，字高 = 16×uiScale，留白必须按真实字高算（否则页码被框底切）
    private static float _imeUiScale = 1f;
    private static float _imePinyinTop;      // 本帧拼音框顶（AddRectFilled 第二矩形记录）
    private static float _imePinyinTopPrev;  // 上一帧拼音框顶（候选框上移参考）
    // 跨帧数据：候选词首行/页码原始 pos.Y、首行原始 X、最右内容 mapped X（框贴合内容用）
    private static float _imeFirstOrigY;
    private static float _imeFirstOrigYPrev;
    private static float _imeFirstOrigX;
    private static float _imeFirstOrigXPrev;
    private static float _imeMaxRightMappedX;
    private static float _imeMaxRightMappedXPrev;
    private static float _imeLastPageOrigY;
    private static float _imeLastPageOrigYPrev;
    private static bool _imeFirstDoneThisFrame;
    private static bool _imePageDoneThisFrame;
    private delegate void AddTextDelegate(nint drawList, System.Numerics.Vector2 pos, uint col, nint textBegin, nint textEnd);
    private delegate void AddRectFilledDelegate(nint drawList, System.Numerics.Vector2 pMin, System.Numerics.Vector2 pMax, uint col, float rounding, uint flags);
    private delegate void AddLineDelegate(nint drawList, System.Numerics.Vector2 p1, System.Numerics.Vector2 p2, uint col, float thickness);
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igPushFont(nint font);
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igPopFont();
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern float igGetFontSize();
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igCalcTextSize(out System.Numerics.Vector2 pOut, nint text, nint textEnd, byte hidden, float wrapWidth);
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
    private static extern bool ImFont_IsLoaded(nint font);

    private void InitImeZoomHook()
    {
        try
        {
            var cimgui = GetModuleHandle("cimgui.dll");
            // !!! 导出名是 ImDrawList_AddText_Vec2（无 font 参数的 AddText，卫月候选用）
            var addr = GetProcAddress(cimgui, "ImDrawList_AddText_Vec2");
            if (addr != 0)
            {
                _imeAddTextHook = GameInteropProvider.HookFromAddress<AddTextDelegate>(addr, AddTextDetour);
                _imeAddTextHook.Enable();
            }
            // 候选框（AddRectFilled，pMin/pMax 值传递——detour 改参数传给 Original）
            var rectAddr = GetProcAddress(cimgui, "ImDrawList_AddRectFilled");
            if (rectAddr != 0)
            {
                _imeRectHook = GameInteropProvider.HookFromAddress<AddRectFilledDelegate>(rectAddr, AddRectFilledDetour);
                _imeRectHook.Enable();
            }
            // 分隔线（AddLine）：候选 UI 整体上移时跟随
            var lineAddr = GetProcAddress(cimgui, "ImDrawList_AddLine");
            if (lineAddr != 0)
            {
                _imeLineHook = GameInteropProvider.HookFromAddress<AddLineDelegate>(lineAddr, AddLineDetour);
                _imeLineHook.Enable();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[ImeZoom] init failed: {ex.Message}");
        }
    }

    private static unsafe void AddLineDetour(nint drawList, System.Numerics.Vector2 p1, System.Numerics.Vector2 p2, uint col, float thickness)
    {
        // 大开关关闭：完全透传（卫月原始 IME）
        if (!Plugin.Config.ModifyImeCandidate)
        {
            _imeLineHook!.Original(drawList, p1, p2, col, thickness);
            return;
        }
        if (_candidatePrev && _imeActive && drawList == _foregroundDl)
            return;
        _imeLineHook!.Original(drawList, p1, p2, col, thickness);
    }

    private static unsafe void AddRectFilledDetour(nint drawList, System.Numerics.Vector2 pMin, System.Numerics.Vector2 pMax, uint col, float rounding, uint flags)
    {
        // 大开关关闭：完全透传（卫月原始 IME）
        if (!Plugin.Config.ModifyImeCandidate)
        {
            _imeRectHook!.Original(drawList, pMin, pMax, col, rounding, flags);
            return;
        }
        // 候选活跃帧 + 前台 dl = 卫月候选绘制（候选框先画、拼音框后画）
        if (_candidatePrev && _imeActive && drawList == _foregroundDl && _imeScale > 1.001f)
        {
            var newAlpha = (uint)(255f * Plugin.Config.ImeCandidateAlpha / 100f);
            var colA = (col & 0x00FFFFFF) | (newAlpha << 24);
            if (_imeRectDone)
            {
                // 第二个矩形 = 拼音框（composition 区域，画在输入框光标处）：
                // 记录其顶部位置（下一帧候选框上移参考 = 输入框位置）→ 不画（拼音已移出屏幕）
                _imePinyinTop = pMin.Y;
                return;
            }
            _imeRectDone = true;
            var w = pMax.X - pMin.X;
            var h = pMax.Y - pMin.Y;
            // !!! 记录本帧原始候选框底（AddText 映射用：原始距框底距离 = pMax.Y - pos.Y）
            _imeOrigMax = pMax;
            // 整体上移：卫月框基于游戏窗口定位，框底上移到拼音框顶（= 输入框）上方 24px
            var offset = _imePinyinTopPrev > 0
                ? System.Math.Max(0f, pMax.Y - (_imePinyinTopPrev - 24f))
                : 0f;
            _imeOffset = offset;
            var yCoeff = 0.5f * _imeScale;  // 用户指定行距系数 0.5（可能有轻微重叠，用户接受）
            // 拼音空隙（原始像素）：页码底到原始框底 = 卫月给拼音留的空间。
            // !!! 页码不放大，真实字高 = 16×uiScale（界面缩放）——留白不足会被框底切
            var pinyinGap = _imeLastPageOrigYPrev > 0
                ? System.Math.Max(0f, pMax.Y - _imeLastPageOrigYPrev - 16f * _imeUiScale)
                : h * 0.36f;
            // 内容高（映射后）：首行到页码底 + 页码真实字高
            float contentH;
            if (_imeFirstOrigYPrev > 0 && _imeLastPageOrigYPrev > _imeFirstOrigYPrev)
                contentH = (_imeLastPageOrigYPrev - _imeFirstOrigYPrev + 16f * _imeUiScale) * yCoeff;
            else
                contentH = (h - pinyinGap) * yCoeff;  // fallback：原始高 - 拼音空隙
            var pad = 4f * _imeScale;
            // !!! 框左/顶贴字（用跨帧 Prev 数据，稳定不跳变）：
            // 框左 = 首行 mappedX - 2px（靠近字但不贴死；条件修正——之前比较 maxRight 与
            // firstOrigX×scale 单位不一致，条件时灵时不灵 → 框左在贴字/fallback 间跳变）
            float boxLeft, boxRight;
            if (_imeFirstOrigXPrev > 0 && _imeMaxRightMappedXPrev > 0)
            {
                boxLeft = _imeAnchor.X + (_imeFirstOrigXPrev - pMin.X) * _imeScale - 6f;  // 边距 6px（原 2px 太贴，略微留白便于阅读）
                boxRight = _imeMaxRightMappedXPrev + pad;
            }
            else
            {
                // 首帧 fallback
                boxLeft = pMin.X;
                boxRight = pMin.X + w * _imeScale;
            }
            // 锚点 = 框**左下角**（左/下边固定，向上扩展——用户要求"左边和下边是起始边"）：
            // 框底 = 锚点（贴输入框上方 24px），框顶 = 锚点 - 内容高
            _imeAnchor = new System.Numerics.Vector2(pMin.X, pMax.Y - offset);
            float newTop;
            if (_imeFirstOrigYPrev > 0)
            {
                // 框顶贴首行字顶（首行 mappedY = 锚点 - (原始距框底 - 拼音空隙)×yCoeff）
                var pinyinGapForTop = _imeLastPageOrigYPrev > 0
                    ? System.Math.Max(0f, pMax.Y - _imeLastPageOrigYPrev - 16f * _imeUiScale)
                    : 0f;
                var firstMappedY = _imeAnchor.Y - (pMax.Y - _imeFirstOrigYPrev - pinyinGapForTop) * yCoeff;
                newTop = firstMappedY - 6f;  // 边距 6px（原 2px 太贴，略微留白便于阅读）
            }
            else
            {
                // fallback
                newTop = _imeAnchor.Y - contentH - pad * 2f;
            }
            var newMin = new System.Numerics.Vector2(boxLeft, newTop);
            var newMax = new System.Numerics.Vector2(boxRight, _imeAnchor.Y);
            _imeRectHook!.Original(drawList, newMin, newMax, colA, rounding, flags);
            return;
        }
        _imeRectHook!.Original(drawList, pMin, pMax, col, rounding, flags);
    }

    /// <summary>候选词文本特征："数字. " 前缀（卫月格式 "{l+1}. {candidate}"）——
    /// 拼音（纯字母）、页码（"1/9 (1/2)"）无此前缀 → 跳过；英文候选词（"1. word"）保留。</summary>
    private static unsafe bool IsCandidateText(nint textBegin, nint textEnd)
    {
        var p = textBegin;
        if (p == 0)
            return false;
        if (*(byte*)p < (byte)'0' || *(byte*)p > (byte)'9')
            return false;
        p++;
        // 可能两位数字（如 "10. "）
        if ((textEnd == 0 || p < textEnd) && *(byte*)p >= (byte)'0' && *(byte*)p <= (byte)'9')
            p++;
        if (textEnd != 0 && p >= textEnd)
            return false;
        if (*(byte*)p != (byte)'.')
            return false;
        p++;
        if (textEnd != 0 && p >= textEnd)
            return false;
        return *(byte*)p == (byte)' ';
    }

    /// <summary>页码文本特征：含 "/" 且主要由数字/斜杠/空格/括号组成（如 "1/9 (1/2)"）。</summary>
    private static unsafe bool IsPageNumber(nint textBegin, nint textEnd)
    {
        if (textBegin == 0)
            return false;
        var hasSlash = false;
        for (var p = textBegin; ; p++)
        {
            if (textEnd != 0 ? p >= textEnd : *(byte*)p == 0)
                break;
            var b = *(byte*)p;
            if (b >= (byte)'0' && b <= (byte)'9')
                continue;
            if (b == (byte)'/' || b == (byte)' ' || b == (byte)'(' || b == (byte)')')
            {
                if (b == (byte)'/')
                    hasSlash = true;
                continue;
            }
            return false;
        }
        return hasSlash;
    }

    private static unsafe void AddTextDetour(nint drawList, System.Numerics.Vector2 pos, uint col, nint textBegin, nint textEnd)
    {
        // 大开关关闭：完全透传（卫月原始 IME）
        if (!Plugin.Config.ModifyImeCandidate)
        {
            _imeAddTextHook!.Original(drawList, pos, col, textBegin, textEnd);
            return;
        }
        // 前台 dl + 输入框聚焦 + 字体有效 = 卫月候选绘制
        // !!! 主条件不能含 _candidatePrev——它是本 detour 自己设置的，作为条件会死锁
        //（候选词永远透传 → 标志永远 false → 拼音也不移出）。候选词/页码用前缀/斜杠
        // 判断已足够精确；拼音移出屏幕用 _candidatePrev（候选词已设标志 → 下一帧生效）
        if (_imeActive && _imeFont != 0 && ImFont_IsLoaded(_imeFont) && drawList == _foregroundDl)
        {
            // !!! 记录界面缩放（此时未 PushFont，当前字体 = 卫月默认，含界面缩放）——
            // 页码不放大（字高 = 16×uiScale），候选字体倍数 _imeScale 与此无关
            _imeUiScale = igGetFontSize() / 16f;
            if (IsCandidateText(textBegin, textEnd))
            {
                // 候选词（卫月强制 "N. " 前缀，英文候选也带 → 前缀判断不误杀英文）：
                // 放大（候选字号）+ 位置相对候选框左下锚点映射（框先画、字后画，同帧锚点已就绪）
                _candidateThisFrame = true;
                igPushFont(_imeFont);
                try
                {
                    _imeScale = igGetFontSize() / 16f;
                    // !!! 映射基准 = 锚点（框左下，原始框底上移 offset）：
                    // mappedY = anchor.Y - (原始距框底 - 拼音空隙) × yCoeff
                    var newPos = _imeAnchor.X != 0 || _imeAnchor.Y != 0
                        ? MapImePos(pos)
                        : pos;
                    // 记录首行原始 pos.X/Y（下一帧框左/框顶贴合用）
                    if (!_imeFirstDoneThisFrame)
                    {
                        _imeFirstDoneThisFrame = true;
                        _imeFirstOrigY = pos.Y;
                        _imeFirstOrigX = pos.X;
                    }
                    // 框右最右累加：mappedX + 文字宽（PushFont 后用候选字体量取）
                    igCalcTextSize(out var tSize, textBegin, textEnd, 0, -1f);
                    var candRight = newPos.X + tSize.X;
                    if (candRight > _imeMaxRightMappedX) _imeMaxRightMappedX = candRight;
                    _imeAddTextHook!.Original(drawList, newPos, col, textBegin, textEnd);
                }
                finally
                {
                    igPopFont();
                }
                return;
            }
            if (IsPageNumber(textBegin, textEnd))
            {
                // 页码：位置跟随映射（字体不放大）
                var newPos = _imeAnchor.X != 0 || _imeAnchor.Y != 0
                    ? MapImePos(pos)
                    : pos;
                // 记录页码原始 pos.Y（下一帧框底/拼音空隙计算）
                _imeLastPageOrigY = pos.Y;
                _imePageDoneThisFrame = true;
                // 页码也累加框右最右（页码不放大，用默认字体量取）
                igCalcTextSize(out var pSize, textBegin, textEnd, 0, -1f);
                var pageRight = newPos.X + pSize.X;
                if (pageRight > _imeMaxRightMappedX) _imeMaxRightMappedX = pageRight;
                _imeAddTextHook!.Original(drawList, newPos, col, textBegin, textEnd);
                return;
            }
            // 拼音（composition 纯字母串）：移出屏幕——需要 _candidatePrev 把关
            //（上一帧有候选词 = 这是卫月候选绘制，不是其他插件前台文字；首帧拼音闪一帧可接受）
            if (_candidatePrev)
            {
                _imeAddTextHook!.Original(drawList,
                    new System.Numerics.Vector2(pos.X, pos.Y - 10000f), col, textBegin, textEnd);
                return;
            }
        }
        _imeAddTextHook!.Original(drawList, pos, col, textBegin, textEnd);
    }

    /// <summary>输入框激活状态（InputText 焦点，DrawInputArea 里记录）——IME 候选放大开关。</summary>
    public static void SetImeActive(bool active) => _imeActive = active;
    /// <summary>缓存输入字体 ImFont*（DrawInputArea 里 InputFont push 时记录，候选放大用）。</summary>
    public static void SetImeFont(nint font) => _imeFont = font;

    /// <summary>候选 UI 位置映射：锚点（框左下 = 原始框底上移 offset）为基准，
    /// 内容相对原始框底的距离（扣除拼音空隙）按 (scale, yCoeff) 缩放。
    /// 拼音空隙 = 原始页码底到原始框底（卫月给拼音留的空间，放大时剔除）。
    /// 左/下边固定（锚点），内容向上排列——用户要求"左边和下边是起始边"。</summary>
    private static unsafe System.Numerics.Vector2 MapImePos(System.Numerics.Vector2 pos)
    {
        var yCoeff = 0.5f * _imeScale;  // 用户指定行距系数 0.5
        var origBottom = _imeOrigMax.Y > 0 ? _imeOrigMax.Y : pos.Y + 200f;
        // !!! 页码留白 = 16×_imeUiScale（页码真实字高，含界面缩放——16px 固定留白会被切）
        var pinyinGap = _imeLastPageOrigYPrev > 0
            ? System.Math.Max(0f, origBottom - _imeLastPageOrigYPrev - 16f * _imeUiScale)
            : 0f;
        var mappedY = _imeAnchor.Y - (origBottom - pos.Y - pinyinGap) * yCoeff;
        var mappedX = _imeAnchor.X + (pos.X - _imeAnchor.X) * _imeScale;
        return new System.Numerics.Vector2(mappedX, mappedY);
    }

    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IDalamudPluginInterface Interface { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IKeyState KeyState { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static IPartyList PartyList { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] public static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] public static INotificationManager Notification { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static ISeStringEvaluator Evaluator { get; private set; } = null!;
    [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] public static IPartyFinderGui PartyFinderGui { get; private set; } = null!;

    public static Configuration Config = null!;
    public static FileDialogManager FileDialogManager { get; private set; } = null!;

    /// <summary>原生右键菜单是否激活（非null表示激活），由 PayloadHandler 设置，ChatLog.PreDraw 使用</summary>
    public static bool ContextMenuActive;

    /// <summary>ChatTwo 触发的菜单会话是否进行中（新增）。
    /// 区分"ChatTwo 触发的菜单"与"游戏原生/背包等触发的菜单"：
    /// OwnerAddon 恒 0 后两者无法区分（背包二级菜单的 OwnerAddon 也是 0），
    /// 而二级菜单（AddonContextSub）应只对 ChatTwo 会话移动位置。
    /// PayloadHandler 触发时置 true；一级菜单关闭且无二级菜单显示时置 false。</summary>
    public static bool ChatTwoMenuSession;

    // !!! 兜底复位（实测：左键点击玩家后聊天框穿透保持，直到原生菜单开关才恢复）：
    // 菜单标志置位但菜单（或二级菜单）持续不可见超时 → FrameworkUpdate 强制复位，防 NoMouseInputs 残留。
    public static long ContextMenuActivatedAt;
    public static long ChatTwoMenuSessionAt;

    public readonly WindowSystem WindowSystem = new(PluginName);
    public SettingsWindow SettingsWindow { get; }
    public ChatLog ChatLog { get; }
    public DbViewer DbViewer { get; }
    public SearchWindow SearchWindow { get; }
    public CommandHelpWindow CommandHelpWindow { get; }

    public Commands Commands { get; }
    public GameFunctions.GameFunctions Functions { get; }
    public GameFunctions.ContextMenuHandler ContextMenuHandler { get; }
    public MessageManager MessageManager { get; }
    public IpcManager Ipc { get; }
    public ExtraChat ExtraChat { get; }
    public TypingIpc TypingIpc { get; }
    public FontManager FontManager { get; }

    public int DeferredSaveFrames = -1;

    public DateTime GameStarted { get; }

    public Vector4 DefaultText = Vector4.Zero;

    // Tab management needs to happen outside the chatlog window class for access reasons
    public int LastTab { get; set; }
    public int? WantedTab { get; set; }
    public Tab CurrentTab
    {
        get
        {
            var i = LastTab;
            return i > -1 && i < Config.Tabs.Count ? Config.Tabs[i] : new Tab();
        }
    }

    public Plugin()
    {
        try
        {
            GameStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime();

            // !!! 鼠标指针保持游戏原生：禁止 ImGui 修改 OS 光标。ImGui 默认在
            // hover 窗口时把光标设成 Windows 箭头（覆盖 FFXIV 游戏指针）；SetMouseCursor(None)
            // 方案实测指针直接消失（ImGui 的 None 会调 SetCursor(NULL) 隐藏）。NoMouseCursorChange
            // 让 ImGui 完全不碰光标 → 游戏原生指针全程保持。
            ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;

            // !!! hook user32 SetCursor：游戏设光标必经，detour 换手指句柄实现稳定原生手指
            InitCursorHook();
            // IME 候选字放大（只 hook 绘制层，候选框/布局不动——CalcTextSize 会触发 Fallback）
            InitImeZoomHook();

            // !!! 音效观察 hook（复测）

            // !!! 观察游戏 UI 音效（定位原生按钮点击音效，替换 BtnSfx 手动试听值）

            Config = Interface.GetPluginConfig() as Configuration ?? new Configuration();
            // 四透明度迁移：新字段（背景/标签页/输入框透明度）首次复制消息区透明度
            Config.EnsureAlphaMigration();

            // 以下选项已锁定，不再显示在设置界面中：
            // 强制开启：播放音效 / 显示新人频道加入按钮 / 显示隐藏按钮 / 显示原始道具帮助
            Config.PlaySounds = true;
            Config.ShowNoviceNetwork = true;
            Config.ShowHideButton = true;
            Config.NativeItemTooltips = true;
            // 强制关闭：显示聊天窗口标题栏 / 显示弹出标签页标题栏
            Config.ShowTitleBar = false;
            Config.ShowPopOutTitleBar = false;
            // 频道切换策略：灵活模式（体感灵活，保留）。
            // !!! 原版遗留：L133 曾有"偏好页已删除：热键固定严格模式"的覆盖行，与这里的 Flexible
            // 冲突（后写覆盖先写），且与体感（按住 W 移动时也能切频道）矛盾 → 已删除 Strict 覆盖行。
            // Flexible：修饰键"包含"即触发（Ctrl+R、Ctrl+Shift+R 都行）；Strict 需"完全相等"。
            Config.KeybindMode = KeybindMode.Flexible;
            // 中文适配：界面语言固定简体中文。
            // !!! 不能设为 None：None 会跟随 Interface.UiLanguage（国服卫月返回 "en"），界面会变英文
            Config.LanguageOverride = LanguageOverride.ChineseSimplified;
            // 命令帮助方向功能已从设置移除，保持关闭
            Config.CommandHelpSide = CommandHelpSide.None;
            // 已删除的设置项锁定默认值（等效于功能关闭）
            // !!! v1.40.17 清理：PrettierTimestamps/MoreCompactPretty/HideSameTimestamps（原作者
            // "现代化布局"表格渲染）字段已删除，时间戳统一走 DrawTimestampInline 行内渲染
            Config.HideInBattle = false;            // 在战斗中隐藏聊天窗口
            Config.HideWhenInactive = false;        // 非活动时隐藏（已从设置移除）
            Config.InactivityHideActiveDuringBattle = false;
            // 采集/制作消息不记录（设置项已从历史记录页移除）
            Config.DatabaseGatherCraftMessages = false;
            // 未读模式固定为"未看过的"（设置项已从标签页页删除）
            foreach (var tab in Config.Tabs)
            {
                tab.HideInBattle = false;
                tab.UnreadMode = UnreadMode.Unseen;
            }

            if (Config.Tabs.Count == 0)
                Config.Tabs.Add(TabsUtil.VanillaGeneral);

            LanguageChanged(Interface.UiLanguage);
            ImGuiUtil.Initialize(this);

            FileDialogManager = new FileDialogManager();

            // This is called by followup functions if the player is already logged in

            Commands = new Commands();
            Functions = new GameFunctions.GameFunctions(this);

            ContextMenuHandler = new GameFunctions.ContextMenuHandler(this);
            Ipc = new IpcManager();

            TypingIpc = new TypingIpc(this);
            ExtraChat = new ExtraChat();
            FontManager = new FontManager();

            CensorFilter.Initialize();

            MessageManager = new MessageManager(this); // Does it require UI?

            ChatLog = new ChatLog(this);
            SettingsWindow = new SettingsWindow(this);
            DbViewer = new DbViewer(this);
            SearchWindow = new SearchWindow(this);
            CommandHelpWindow = new CommandHelpWindow(ChatLog);

            WindowSystem.AddWindow(ChatLog);
            WindowSystem.AddWindow(SettingsWindow);
            WindowSystem.AddWindow(DbViewer);
            WindowSystem.AddWindow(SearchWindow);
            WindowSystem.AddWindow(CommandHelpWindow);

            FontManager.BuildFonts();

            // 原生 UI 图标（聊天记录窗口工具栏使用的 FFXIV 原生 PNG）。
            // !!! 修复：恢复构造函数直接 Load（首次成功验证过的逻辑， 日志
            // search=True）。之前改懒加载时把这里的调用删了、又没给 NativeIcons 设 _tp，
            // 导致 EnsureLoaded 永远不加载 → 全部回退 FontAwesome（实测图标消失）。
            // 失败时按钮回退到 FontAwesome，不会阻塞插件启动。
            NativeIcons.Load(TextureProvider);

            Interface.UiBuilder.DisableCutsceneUiHide = true;
            Interface.UiBuilder.DisableGposeUiHide = true;

            // let all the other components register, then initialize commands
            Commands.Initialise();

            if (Interface.Reason is not PluginLoadReason.Boot)
                MessageManager.FilterAllTabsAsync();

            Framework.Update += FrameworkUpdate;
            Interface.UiBuilder.Draw += Draw;
            Interface.LanguageChanged += LanguageChanged;

            #if !DEBUG
            // Avoid 300ms hitch when sending first message by preloading the
            // auto-translate cache. Don't do this in debug because it makes
            // profiling difficult.
            AutoTranslate.PreloadCache();
            #endif
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Plugin load threw an error, turning off plugin");
            Dispose();

            // Re-throw the exception to fail the plugin load.
            throw;
        }
    }

    // Suppressing this warning because Dispose() is called in Plugin() if the
    // load fails, so some values may not be initialized.
    [SuppressMessage("ReSharper", "ConditionalAccessQualifierIsNonNullableAccordingToAPIContract")]
    public void Dispose()
    {
        Interface.LanguageChanged -= LanguageChanged;
        Interface.UiBuilder.Draw -= Draw;
        Framework.Update -= FrameworkUpdate;
        GameFunctions.GameFunctions.SetChatInteractable(true);

        _setCursorHook?.Dispose();
        _imeAddTextHook?.Dispose();
        _imeRectHook?.Dispose();
        _imeLineHook?.Dispose();
        _setCursorHook = null;

        WindowSystem?.RemoveAllWindows();
        ChatLog?.Dispose();
        DbViewer?.Dispose();
        SettingsWindow?.Dispose();




        TypingIpc?.Dispose();
        ExtraChat?.Dispose();
        Ipc?.Dispose();
        ContextMenuHandler?.Dispose();
        MessageManager?.DisposeAsync().AsTask().Wait();
        Functions?.Dispose();
        Commands?.Dispose();
        NativeIcons.DisposeAll();
    }

    private unsafe void Draw()
    {
        // 前台 dl 缓存（IME 候选放大 detour 比较用）+ 候选活跃标志转移
        //（AddText 本帧触发 → 下一帧 AddRectFilled 候选框放大用）
        _foregroundDl = (nint)ImGui.GetForegroundDrawList().Handle;
        _candidatePrev = _candidateThisFrame;
        _candidateThisFrame = false;
        _imeRectDone = false;
        // 拼音框顶转移（候选框整体上移参考——卫月框基于游戏窗口定位，需上移贴输入框）
        _imePinyinTopPrev = _imePinyinTop;
        _imePinyinTop = 0f;
        // 首行/页码原始 pos.Y 转移（下一帧候选框框高计算——去除拼音空隙）
        if (_imeFirstDoneThisFrame) _imeFirstOrigYPrev = _imeFirstOrigY;
        if (_imeFirstDoneThisFrame) _imeFirstOrigXPrev = _imeFirstOrigX;
        if (_imePageDoneThisFrame) _imeLastPageOrigYPrev = _imeLastPageOrigY;
        _imeMaxRightMappedXPrev = _imeMaxRightMappedX;
        _imeMaxRightMappedX = 0f;
        _imeFirstDoneThisFrame = false;
        _imePageDoneThisFrame = false;

        ChatLog.BeginFrame();

        if (Config.HideInLoadingScreens && Condition[ConditionFlag.BetweenAreas])
        {
            ChatLog.FinalizeFrame();
            TypingIpc.Update();
            return;
        }

        ChatLog.IsHidden = HideStateHelper.HideStateCheck(ChatLog, Config.HideInBattle, Config.HideDuringCutscenes, Config.HideWhenNotLoggedIn, ChatLog.InputHandler.Activate);

        Interface.UiBuilder.DisableUserUiHide = !Config.HideWhenUiHidden;
        DefaultText = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

        // 窗口级字体用 SettingsFont（固定大小，设置里可调）：
        // 否则卫月标题栏等窗口 chrome 会随主字体缩放（实测，还会挡设置页保存按钮）。
        // 聊天消息/输入框等需要主字体的地方在各窗口内容里自行 Push（见 ChatLog.Draw / Popout.Draw）
        using (FontManager.SettingsFont.Push())
            WindowSystem.Draw();

        // !!! 光标决策（窗口 Draw 里置位的标志在此统一处理）
        UpdateCursorDecision();

        ChatLog.FinalizeFrame();
        TypingIpc.Update();

        FileDialogManager.Draw();
    }


    public void SaveConfig()
    {
        Interface.SavePluginConfig(Config);
    }

    public void LanguageChanged(string langCode)
    {
        var info = Config.LanguageOverride is LanguageOverride.None
            ? new CultureInfo(langCode)
            : new CultureInfo(Config.LanguageOverride.Code());

        Language.Culture = info;
    }

    private static readonly string[] ChatAddonNames =
    [
        "ChatLog",
        "ChatLogPanel_0",
        "ChatLogPanel_1",
        "ChatLogPanel_2",
        "ChatLogPanel_3",
    ];

    private void FrameworkUpdate(IFramework framework)
    {
        if (DeferredSaveFrames >= 0 && DeferredSaveFrames-- == 0)
            SaveConfig();

        // !!! 实验：注释"屏幕外可见"hack，干净验证 OwnerAddon=0（bindToOwner=false 等效）是否单独有效。
        // if (GameFunctions.GameFunctions.IsNativeSubContextMenuVisible())
        // {
        // GameFunctions.GameFunctions.KeepChatVisibleOffscreen();
        // return;
        // }
        GameFunctions.GameFunctions.RestoreChatPosition(); // 保留：恢复位置兜底（防 ChatLog 卡屏幕外）

        // !!! 兜底复位：菜单标志残留（打开失败/关闭路径异常）会让 NoMouseInputs
        // 持续生效 → 聊天框穿透（实测：左键点击玩家后穿透保持，直到原生菜单开关才恢复）。
        // 标志置位后菜单（或二级菜单）持续不可见超过 1s → 强制复位两个标志。
        // 必须放在 HideChat return 之前（可能未隐藏原生聊天框，此兜底需每帧执行）。
        try
        {
            var menuVisible = IsNativeContextMenuVisible() || IsNativeSubContextMenuVisible();
            if (ContextMenuActive && !menuVisible && Environment.TickCount64 - ContextMenuActivatedAt > 1000)
                ContextMenuActive = false;
            if (ChatTwoMenuSession && !menuVisible && Environment.TickCount64 - ChatTwoMenuSessionAt > 1000)
                ChatTwoMenuSession = false;
        }
        catch (Exception ex) { Plugin.Log.Debug($"[CtxFallback] error {ex.Message}"); }

        if (!Config.HideChat)
            return;

        // 菜单激活时不隐藏聊天框面板（bindToOwner=true 的子菜单展开需要访问聊天框）。
        // !!! 必须同时检查 ContextMenu addon 是否真的可见：
        // 游戏复用 ContextMenu addon（关闭后只是隐藏不销毁，PreFinalize 事件不触发），
        // ContextMenuActive 会残留 true；且打开失败（目标无效，如无 ContentId 的玩家）时
        // addon 根本不会显示。两种情况都会导致原生聊天框永不隐藏（实测）。
        // 菜单不可见（打开失败或已关闭）→ 恢复隐藏。
        if (ContextMenuActive && IsNativeContextMenuVisible())
            return;

        foreach (var name in ChatAddonNames)
            if (GameFunctions.GameFunctions.IsAddonInteractable(name))
                GameFunctions.GameFunctions.SetAddonInteractable(name, false);
    }

    public static bool IsNativeContextMenuVisible()
    {
        try
        {
            unsafe
            {
                var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
                return ctxAddon != null && ctxAddon->IsVisible;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>二级菜单（AddonContextSub）是否可见（兜底复位用）。</summary>
    public static unsafe bool IsNativeSubContextMenuVisible()
    {
        try
        {
            var addon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("AddonContextSub");
            return addon != null && addon->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    public static bool InBattle => Condition[ConditionFlag.InCombat];
    public static bool GposeActive => Condition[ConditionFlag.WatchingCutscene];
    public static bool CutsceneActive => Condition[ConditionFlag.OccupiedInCutSceneEvent] || Condition[ConditionFlag.WatchingCutscene78];
}
