using System.Numerics;
using Dalamud.Hooking;

namespace ChatTwo;

public sealed partial class Plugin
{
    // IME 候选放大：hook 绘制层（AddText/AddRectFilled/AddLine）。
    // 卫月候选结构：候选框→拼音框→拼音文字→分隔线→候选词×N→分隔线→页码。
    // 方案：拼音相关全部移出屏幕；候选框/候选词放大；页码位置跟随字体不放大。
    // 帧状态统一存放于 ImeFrameState，本文件仅含 hook 安装与 detour 逻辑。

    // 卫月默认字号 16px（ImeFrameState.Scale/UiScale 计算基准）。
    private const float DefaultFontSize = 16f;
    // scale 大于此阈值才放大（避免 1.0 时无意义重绘）。
    private const float ScaleEnableThreshold = 1.001f;
    // yCoeff = scale * 0.5（垂直方向取一半，因水平不变）。
    private const float YCoeffFactor = 0.5f;
    // 候选框底上移到拼音框顶上方距离。
    private const float PinyinOffsetAbove = 24f;
    // 框左右边距（像素）。
    private const float BoxEdgeMargin = 6f;
    // 框内容 padding（以 scale 计）。
    private const float ContentPadScale = 4f;
    // 拼音空隙 fallback 比例（无跨帧页码数据时占框高比例）。
    private const float PinyinGapFallbackRatio = 0.36f;
    // 拼音移出屏幕的 Y 偏移。
    private const float PinyinClearYOffset = 10000f;
    // origBottom fallback（ImeFrameState.OrigMax 未记录时假定框底位置）。
    private const float OrigBottomFallback = 200f;
    // 颜色 alpha 通道掩码（取 RGB 部分）。
    private const uint ColorRgbMask = 0x00FFFFFF;
    // 颜色 alpha 取值上限（0-255）。
    private const float ColorAlphaMax = 255f;
    // ImeCandidateAlpha 配置百分比上限（0-100）。
    private const float AlphaPercentMax = 100f;

    private static Hook<AddTextDelegate>? _imeAddTextHook;
    private static Hook<AddRectFilledDelegate>? _imeRectHook;
    private static Hook<AddLineDelegate>? _imeLineHook;
    private delegate void AddTextDelegate(nint drawList, Vector2 pos, uint col, nint textBegin, nint textEnd);
    private delegate void AddRectFilledDelegate(nint drawList, Vector2 pMin, Vector2 pMax, uint col, float rounding, uint flags);
    private delegate void AddLineDelegate(nint drawList, Vector2 p1, Vector2 p2, uint col, float thickness);
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igPushFont(nint font);
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igPopFont();
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern float igGetFontSize();
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern void igCalcTextSize(out Vector2 pOut, nint text, nint textEnd, byte hidden, float wrapWidth);
    [System.Runtime.InteropServices.DllImport("cimgui.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
    private static extern bool ImFont_IsLoaded(nint font);

    private void InitImeZoomHook()
    {
        try
        {
            var cimgui = GetModuleHandle("cimgui.dll");
            // 导出名 ImDrawList_AddText_Vec2（无 font 参数，卫月候选用）。
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

    private static unsafe void AddLineDetour(nint drawList, Vector2 p1, Vector2 p2, uint col, float thickness)
    {
        // 大开关关闭：完全透传（卫月原始 IME）
        if (Plugin.Config == null || !Plugin.Config.ModifyImeCandidate)
        {
            _imeLineHook!.Original(drawList, p1, p2, col, thickness);
            return;
        }
        if (ImeFrameState.CandidatePrev && ImeFrameState.Active && drawList == ImeFrameState.ForegroundDl)
            return;
        _imeLineHook!.Original(drawList, p1, p2, col, thickness);
    }

    private static unsafe void AddRectFilledDetour(nint drawList, Vector2 pMin, Vector2 pMax, uint col, float rounding, uint flags)
    {
        // 大开关关闭：完全透传（卫月原始 IME）
        if (Plugin.Config == null || !Plugin.Config.ModifyImeCandidate)
        {
            _imeRectHook!.Original(drawList, pMin, pMax, col, rounding, flags);
            return;
        }
        // 候选活跃帧 + 前台 dl = 卫月候选绘制（候选框先画、拼音框后画）
        if (ImeFrameState.CandidatePrev && ImeFrameState.Active && drawList == ImeFrameState.ForegroundDl && ImeFrameState.Scale > ScaleEnableThreshold)
        {
            var newAlpha = (uint)(ColorAlphaMax * Plugin.Config.ImeCandidateAlpha / AlphaPercentMax);
            var colA = (col & ColorRgbMask) | (newAlpha << 24);
            // 第二个矩形 = 拼音框：记录顶位置（下一帧上移参考）→ 不画。
            if (ImeFrameState.RectDone)
            {
                ImeFrameState.PinyinTop = pMin.Y;
                return;
            }
            ImeFrameState.RectDone = true;
            var w = pMax.X - pMin.X;
            var h = pMax.Y - pMin.Y;
            ImeFrameState.OrigMax = pMax;  // AddText 映射用（距框底距离 = pMax.Y - pos.Y）
            // 框底上移到拼音框顶上方 PinyinOffsetAbove。
            var offset = ImeFrameState.PinyinTopPrev > 0
                ? System.Math.Max(0f, pMax.Y - (ImeFrameState.PinyinTopPrev - PinyinOffsetAbove))
                : 0f;
            ImeFrameState.Offset = offset;
            var yCoeff = YCoeffFactor * ImeFrameState.Scale;
            // 拼音空隙 = 页码底到框底（页码字高 = DefaultFontSize × uiScale）。
            var pinyinGap = ImeFrameState.LastPageOrigYPrev > 0
                ? System.Math.Max(0f, pMax.Y - ImeFrameState.LastPageOrigYPrev - DefaultFontSize * ImeFrameState.UiScale)
                : h * PinyinGapFallbackRatio;
            // 内容高（首行到页码底 + 页码字高）。
            float contentH;
            if (ImeFrameState.FirstOrigYPrev > 0 && ImeFrameState.LastPageOrigYPrev > ImeFrameState.FirstOrigYPrev)
                contentH = (ImeFrameState.LastPageOrigYPrev - ImeFrameState.FirstOrigYPrev + DefaultFontSize * ImeFrameState.UiScale) * yCoeff;
            else
                contentH = (h - pinyinGap) * yCoeff;
            var pad = ContentPadScale * ImeFrameState.Scale;
            // 框左/顶贴字（用跨帧 Prev 数据稳定不跳变）。
            float boxLeft, boxRight;
            if (ImeFrameState.FirstOrigXPrev > 0 && ImeFrameState.MaxRightMappedXPrev > 0)
            {
                boxLeft = ImeFrameState.Anchor.X + (ImeFrameState.FirstOrigXPrev - pMin.X) * ImeFrameState.Scale - BoxEdgeMargin;
                boxRight = ImeFrameState.MaxRightMappedXPrev + pad;
            }
            else
            {
                // 首帧 fallback。
                boxLeft = pMin.X;
                boxRight = pMin.X + w * ImeFrameState.Scale;
            }
            // 锚点 = 框左下角；框顶 = 锚点 - 内容高。
            ImeFrameState.Anchor = new Vector2(pMin.X, pMax.Y - offset);
            float newTop;
            if (ImeFrameState.FirstOrigYPrev > 0)
            {
                var pinyinGapForTop = ImeFrameState.LastPageOrigYPrev > 0
                    ? System.Math.Max(0f, pMax.Y - ImeFrameState.LastPageOrigYPrev - DefaultFontSize * ImeFrameState.UiScale)
                    : 0f;
                var firstMappedY = ImeFrameState.Anchor.Y - (pMax.Y - ImeFrameState.FirstOrigYPrev - pinyinGapForTop) * yCoeff;
                newTop = firstMappedY - BoxEdgeMargin;
            }
            else
            {
                newTop = ImeFrameState.Anchor.Y - contentH - pad * 2f;
            }
            var newMin = new Vector2(boxLeft, newTop);
            var newMax = new Vector2(boxRight, ImeFrameState.Anchor.Y);
            _imeRectHook!.Original(drawList, newMin, newMax, colA, rounding, flags);
            return;
        }
        _imeRectHook!.Original(drawList, pMin, pMax, col, rounding, flags);
    }

    /// <summary>候选词文本特征："数字. " 前缀（卫月格式 "N. {candidate}"）。</summary>
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

    private static unsafe void AddTextDetour(nint drawList, Vector2 pos, uint col, nint textBegin, nint textEnd)
    {
        // 大开关关闭：完全透传。
        if (Plugin.Config == null || !Plugin.Config.ModifyImeCandidate)
        {
            _imeAddTextHook!.Original(drawList, pos, col, textBegin, textEnd);
            return;
        }
        // 前台 dl + 输入框聚焦 + 字体有效 = 卫月候选绘制。
        // CandidatePrev（它是本 detour 设置的，会死锁）。
        if (ImeFrameState.Active && ImeFrameState.Font != 0 && ImFont_IsLoaded(ImeFrameState.Font) && drawList == ImeFrameState.ForegroundDl)
        {
            // 记录界面缩放（未 PushFont，当前 = 卫月默认含缩放）。
            ImeFrameState.UiScale = igGetFontSize() / DefaultFontSize;
            if (IsCandidateText(textBegin, textEnd))
            {
                // 候选词：放大 + 锚点映射。
                ImeFrameState.CandidateThisFrame = true;
                igPushFont(ImeFrameState.Font);
                try
                {
                    ImeFrameState.Scale = igGetFontSize() / DefaultFontSize;
                    var newPos = ImeFrameState.Anchor.X != 0 || ImeFrameState.Anchor.Y != 0
                        ? MapImePos(pos)
                        : pos;
                    // 记录首行原始 pos（下一帧框贴合用）。
                    if (!ImeFrameState.FirstDoneThisFrame)
                    {
                        ImeFrameState.FirstDoneThisFrame = true;
                        ImeFrameState.FirstOrigY = pos.Y;
                        ImeFrameState.FirstOrigX = pos.X;
                    }
                    // 框右最右累加。
                    igCalcTextSize(out var tSize, textBegin, textEnd, 0, -1f);
                    var candRight = newPos.X + tSize.X;
                    if (candRight > ImeFrameState.MaxRightMappedX) ImeFrameState.MaxRightMappedX = candRight;
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
                // 页码：位置跟随映射，字体不放大。
                var newPos = ImeFrameState.Anchor.X != 0 || ImeFrameState.Anchor.Y != 0
                    ? MapImePos(pos)
                    : pos;
                ImeFrameState.LastPageOrigY = pos.Y;
                ImeFrameState.PageDoneThisFrame = true;
                igCalcTextSize(out var pSize, textBegin, textEnd, 0, -1f);
                var pageRight = newPos.X + pSize.X;
                if (pageRight > ImeFrameState.MaxRightMappedX) ImeFrameState.MaxRightMappedX = pageRight;
                _imeAddTextHook!.Original(drawList, newPos, col, textBegin, textEnd);
                return;
            }
            // 拼音：移出屏幕（用 CandidatePrev 把关，首帧闪一帧可接受）。
            if (ImeFrameState.CandidatePrev)
            {
                _imeAddTextHook!.Original(drawList,
                    new Vector2(pos.X, pos.Y - PinyinClearYOffset), col, textBegin, textEnd);
                return;
            }
        }
        _imeAddTextHook!.Original(drawList, pos, col, textBegin, textEnd);
    }

    /// <summary>输入框激活状态（InputText 焦点，DrawInputArea 里记录）——IME 候选放大开关。</summary>
    public static void SetImeActive(bool active) => ImeFrameState.Active = active;
    /// <summary>缓存输入字体 ImFont*（DrawInputArea 里 InputFont push 时记录，候选放大用）。</summary>
    public static void SetImeFont(nint font) => ImeFrameState.Font = font;

    /// <summary>候选 UI 位置映射：以锚点为基准，内容相对原始框底距离按 (scale, yCoeff) 缩放。</summary>
    private static unsafe Vector2 MapImePos(Vector2 pos)
    {
        var yCoeff = YCoeffFactor * ImeFrameState.Scale;
        var origBottom = ImeFrameState.OrigMax.Y > 0 ? ImeFrameState.OrigMax.Y : pos.Y + OrigBottomFallback;
        // 拼音空隙 = 页码底到框底（页码字高 = DefaultFontSize × uiScale）。
        var pinyinGap = ImeFrameState.LastPageOrigYPrev > 0
            ? System.Math.Max(0f, origBottom - ImeFrameState.LastPageOrigYPrev - DefaultFontSize * ImeFrameState.UiScale)
            : 0f;
        var mappedY = ImeFrameState.Anchor.Y - (origBottom - pos.Y - pinyinGap) * yCoeff;
        var mappedX = ImeFrameState.Anchor.X + (pos.X - ImeFrameState.Anchor.X) * ImeFrameState.Scale;
        return new Vector2(mappedX, mappedY);
    }
}
