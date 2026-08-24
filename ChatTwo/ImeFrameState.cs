using System.Numerics;

namespace ChatTwo;

/// <summary>
/// IME 候选放大帧状态：所有 detour 读写的运行时状态集中在此处。
/// 帧末由 Plugin.Draw 调 <see cref="BeginFrame"/> 转移本帧 → Prev，供下一帧 detour 用。
/// </summary>
internal static class ImeFrameState
{
    // —— 输入参数（DrawInputArea 设置，跨帧稳定） ——
    public static bool Active;
    public static nint Font;
    public static nint ForegroundDl;

    // —— 本帧状态（detour 写入、BeginFrame 清零） ——
    public static bool CandidateThisFrame;
    public static bool RectDone;
    public static Vector2 Anchor;
    public static Vector2 OrigMax;
    public static float Offset;
    public static float UiScale = 1f;
    public static float Scale = 1f;
    public static float PinyinTop;
    public static float FirstOrigY;
    public static float FirstOrigX;
    public static float MaxRightMappedX;
    public static float LastPageOrigY;
    public static bool FirstDoneThisFrame;
    public static bool PageDoneThisFrame;

    // —— 上一帧状态（BeginFrame 时由本帧值转移，下一帧框贴合/上移用） ——
    public static bool CandidatePrev;
    public static float PinyinTopPrev;
    public static float FirstOrigYPrev;
    public static float FirstOrigXPrev;
    public static float MaxRightMappedXPrev;
    public static float LastPageOrigYPrev;

    /// <summary>帧末转移：本帧 → Prev，本帧清零。</summary>
    public static void BeginFrame()
    {
        CandidatePrev = CandidateThisFrame;
        CandidateThisFrame = false;
        RectDone = false;
        PinyinTopPrev = PinyinTop;
        PinyinTop = 0f;
        if (FirstDoneThisFrame)
        {
            FirstOrigYPrev = FirstOrigY;
            FirstOrigXPrev = FirstOrigX;
        }
        if (PageDoneThisFrame)
            LastPageOrigYPrev = LastPageOrigY;
        MaxRightMappedXPrev = MaxRightMappedX;
        MaxRightMappedX = 0f;
        FirstDoneThisFrame = false;
        PageDoneThisFrame = false;
    }
}
