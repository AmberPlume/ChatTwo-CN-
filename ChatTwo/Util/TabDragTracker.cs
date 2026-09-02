using System.Numerics;

namespace ChatTwo.Util;

/// <summary>tab 拖拽结束结果。</summary>
public enum DragEndResult
{
    None,      // 非松手
    Completed, // 正常拖拽结束（调用方执行终点逻辑）
    Stale,     // 拖拽中断过（隐藏等）→ 取消
}

/// <summary>
/// tab 拖拽判定状态机（浏览器式：600ms 长按 + 移动 10px）。
/// 主窗口拖出（native/legacy）与 Popout 拖拽共用；key = tab 标识（主窗口 int 索引 / Popout Guid）。
/// 只做判定不画 UI；终点语义由调用方在 Completed 时执行。
/// 约束须为 struct：TKey? 只有在 struct 约束下才是 Nullable<TKey>（notnull 约束时 ? 仅注解，
/// 值类型 key 的 null 判断会失真——CS8073，见 v1.41.3.8 修复）。
/// </summary>
public class TabDragTracker<TKey> where TKey : struct
{
    public const long LongPressMs = 600;
    public const float DragThresholdPx = 10f;

    public readonly Dictionary<TKey, long> PressStart = [];
    public readonly Dictionary<TKey, Vector2> PressPos = [];
    public TKey? Dragging;
    public long LastProcessed; // 拖拽最近处理时刻（中断检测：隐藏期 Draw 中断 → 松手判 Stale）

    /// <summary>按下（按住第一帧）→ 记录时刻/位置。</summary>
    public void TrackPress(bool itemActive, TKey id, long now, Vector2 mousePos)
    {
        if (itemActive && !PressStart.ContainsKey(id) && Dragging == null)
        {
            PressStart[id] = now;
            PressPos[id] = mousePos;
        }
    }

    /// <summary>长按达标 + 移动（拖拽手势）→ 进入拖拽；返回是否本帧开始拖。</summary>
    public bool TryBeginDrag(TKey id, bool leftDown, long now, Vector2 mousePos, float scale)
    {
        if (Dragging != null || !leftDown || !PressStart.TryGetValue(id, out var downAt))
            return false;
        if (now - downAt < LongPressMs || (mousePos - PressPos[id]).Length() <= DragThresholdPx * scale)
            return false;
        Dragging = id;
        LastProcessed = now;
        return true;
    }

    /// <summary>拖拽中且左键仍按住 → 更新最近处理时刻（中断检测用）。</summary>
    public void UpdateActive(TKey id, bool leftDown, long now)
    {
        if (Dragging != null && Dragging.Equals(id) && leftDown)
            LastProcessed = now;
    }

    /// <summary>松手处理。Completed = 正常结束（执行终点）；Stale = 中断过（取消）。</summary>
    public DragEndResult TryEndDrag(TKey id, bool leftDown, long now)
    {
        if (leftDown || !PressStart.Remove(id))
            return DragEndResult.None;
        PressPos.Remove(id);
        if (Dragging == null || !Dragging.Equals(id))
            return DragEndResult.None;
        var stale = now - LastProcessed > 500;
        Dragging = default;
        return stale ? DragEndResult.Stale : DragEndResult.Completed;
    }

    public void Clear()
    {
        PressStart.Clear();
        PressPos.Clear();
        Dragging = default;
    }
}
