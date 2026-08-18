// ═══════════════════════════════════════════════════════════════════════
// 顶点挖洞（正式功能，正式化）——"原生菜单不被聊天框遮挡"的渲染层方案
// ═══════════════════════════════════════════════════════════════════════
// 思路：原生菜单（ContextMenu/AddonContextSub）画在 ImGui 之下（渲染管线决定）。
// 要让聊天框不遮挡菜单，可在 ImGui draw data 生成后、D3D 提交前，把聊天框 draw list 中
// "菜单圆角矩形区域"内的三角形剔除（顶点挖洞）→ 菜单完整透出。
//
// 唯一精确插入点：hook cimgui.dll 的 igRender（ImGui.Render 的 C 导出）——
// Original() 执行后 draw data 已生成（ImGui.GetDrawData()），D3D 提交尚未发生。
//
// 三道工序（HoleDrawList）：
// ① 文字退化：三角形三顶点都在菜单圆角矩形内 → 三索引同指（面积 0 不渲染）。
// ② 背景拆分：大 cmd（bbox 面积≥1600）→ 拆成"背景−圆角矩形"矩形子 cmd（clip 裁剪镂空）。
// ③ 跨边界字符：小 cmd 跨菜单边界 → 按"菜单内占比"分流（≥0.7 退化 / <0.7 拆分），预算耗尽退化兜底。
// 每帧临时修改，下一帧 ImGui 重建 draw data 自动恢复（菜单关闭后文字正常显示）。
//
// !!! 稳定性铁律（勿重蹈，详见"挖洞与菜单跟手交接.md"）：
// - 永不替换 CmdBuffer.Data 指针（AllocHGlobal 与 ImGui IM_FREE 分配器不匹配 → 堆损坏崩溃 0x12345679）
// - 原地拆分，subs 总数必须 ≤ Capacity 余量；所有索引访问加越界保护
// - draw data 修改后绝不按帧释放资源（Present detour 与 igRender 帧边界不可靠）
//
// 诊断部分（[Hole-Diag] dump / [Hole-Budget] 预算 / DumpNodeTree）保留在 #if ENABLE_CTX_DIAG 内
//（可选编译，平时关闭）；毒药 UserCallback 防御是防崩溃安全网，随正式功能一起生效。
// ═══════════════════════════════════════════════════════════════════════
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChatTwo.Ui.ChatLog;

/// <summary>
/// 顶点挖洞（正式功能）：hook igRender，在 draw data 生成后剔除聊天框内"菜单圆角矩形区域"的
/// 三角形，让原生右键菜单不被聊天框遮挡。
/// 关联：菜单打开 = PayloadHandler（ContextMenuActive/ChatTwoMenuSession 置 true）；
/// 点击穿透 = ChatLog.Window.PreOpenCheck 的 NoMouseInputs（[CtxClickPass]）；
/// Init/Dispose 由 ChatLog.Window 构造/析构调用（本文件已出宏，正式构建生效）。
/// </summary>
public static class RenderHole
{
    private static Hook<IgRenderDelegate>? _renderHook;
    private delegate void IgRenderDelegate();

#if ENABLE_CTX_DIAG
    private static int _frames;
    private static bool _diagDumped;

    // !!! 预算诊断：每个菜单会话（menuRects>0 首次）打印各 dl 的真实预算消耗，
    // 定位"文字进入/圆角降级"的资源分配瓶颈（availCmd 是硬上限，只能靠数据优化需求侧）。
    // 修复：按 dl OwnerName 隔离（全局 bool 会被主窗口抢先置 true → messages/bottom-log 不打）。
    private static readonly HashSet<string> _budgetDumpedDls = [];
#endif

    // !!! 稳定性重构：**永不扩容、永不替换 CmdBuffer.Data 指针**。
    // 原地拆分 + subs 总数 ≤ Capacity 余量（阶梯动态降级）。无分配器所有权问题。
    // 历史教训（勿重蹈）：
    // - 每帧 AllocHGlobal 新 buffer + 下一帧释放 → Present 时序竞争 → 崩溃
    // - 面积阈值 200 拆字符 → 负尺寸 clip → D3D 崩溃
    // - AllocHGlobal 替换 Data → ImGui 用 IM_FREE 释放不匹配 → 未定义行为

    public static void Dispose()
    {
        _renderHook?.Dispose();
        _renderHook = null;
    }

    public static void Init(Plugin plugin)
    {
        try
        {
            var hmod = GetModuleHandle("cimgui.dll");
            if (hmod == IntPtr.Zero)
            {
                Plugin.Log.Error("[Hole] cimgui.dll 未加载");
                return;
            }
            var addr = GetProcAddress(hmod, "igRender");
            if (addr == IntPtr.Zero)
            {
                Plugin.Log.Error("[Hole] igRender 导出未找到");
                return;
            }
            _renderHook = Plugin.GameInteropProvider.HookFromAddress<IgRenderDelegate>(addr, RenderDetour);
            _renderHook.Enable();
            Plugin.Log.Information($"[Hole] igRender hook 启用 addr=0x{addr:X}");
        }
        catch (Exception ex) { Plugin.Log.Error($"[Hole] init error {ex.Message}"); }
    }

    /// <summary>
    /// 判断 draw list 是否属于 ChatTwo 需要挖洞的窗口。
    /// 主聊天窗口 ID 以 "Chat 2###chat2" 开头；PopOut 弹出窗口 ID 为 "{tab.Name}##popout"
    /// （见 Popout.cs base($"{tab.Name}##popout")）→ 两者都参与挖洞：
    /// 在 PopOut 里右键玩家/道具打开原生菜单时，菜单下方的 PopOut 内容同样需要镂空。
    /// !!! 用 Contains("##popout") 而非 EndsWith：PopOut 内部的消息区/底部栏是 ImGui child，
    /// 其 OwnerName 带父窗口前缀（如 "{tab.Name}##popout##chat2-messages"），EndsWith 匹配不到
    /// → 内容全画在 child 里，挖洞只命中主窗口 dl 无效（实测 ）。
    /// </summary>
    private static bool IsChatTwoDrawList(string name) =>
        name.StartsWith("Chat 2###chat2") || name.Contains("##popout");

    private static unsafe void RenderDetour()
    {
        _renderHook!.Original();
        // !!! 起不分配/不释放任何原生 buffer（原地拆分，无 Present 时序问题）。
        try
        {
            var dd = ImGui.GetDrawData();
            if (dd.IsNull || dd.CmdListsCount <= 0)
                return;

            // !!! PoC-1 结论（11:22）：chat2 窗口 cmd=10，所有 cmd 的 ClipRect 都是整个窗口矩形
            //（cmd 按纹理/状态分组，非按区域）→ 改 ClipRect 会误伤整批 → 必须顶点/索引级剔除。
            // PoC-2：退化三角形技巧——三角形完全在菜单矩形内 → 三索引指向同一顶点（面积0不渲染）。
            // 原地改 IdxBuffer，零内存分配，菜单关闭时零开销（GetMenuRect 返回 null）。
#if ENABLE_CTX_DIAG
            // 前 30 帧 dump（诊断用，宏内）
            if (_frames++ < 30)
            {
                Plugin.Log.Error($"[Hole] draw data: lists={dd.CmdListsCount}");
                for (var i = 0; i < dd.CmdListsCount && i < 20; i++)
                {
                    var dl = new ImDrawListPtr(dd.CmdLists[i]);
                    if (dl.Handle == null)
                        continue;
                    var name = dl.OwnerName != null ? Marshal.PtrToStringUTF8((nint)dl.OwnerName) : "?";
                    Plugin.Log.Error($"[Hole]   [{i}] name='{name}' cmd={dl.CmdBuffer.Size} vtx={dl.VtxBuffer.Size} idx={dl.IdxBuffer.Size}");
                }
            }
#endif

            var menuRects = GetMenuRects();

#if ENABLE_CTX_DIAG
            // [Hole-Diag] 菜单打开会话首次 dump：菜单矩形 + chat2 主窗口 cmd 结构（找背景 cmd + 矩形偏差）
            if (menuRects.Count > 0 && !_diagDumped)
            {
                _diagDumped = true;
                var scale0 = AtkStage.Instance()->ScreenSizeScale;
                // 诊断：固定 dump 菜单 addon 的矩形（GetMenuRects 恒单矩形，这里额外看全调参）
                var mgr0 = RaptureAtkModule.Instance()->RaptureAtkUnitManager;
                var ctxD = mgr0.GetAddonByName("ContextMenu");
                var subD = mgr0.GetAddonByName("AddonContextSub");
                if (ctxD != null && ctxD->IsVisible)
                    Plugin.Log.Error($"[Hole-Diag] diag ContextMenu rect=({MenuRectOf(ctxD, scale0).X:0},{MenuRectOf(ctxD, scale0).Y:0},{MenuRectOf(ctxD, scale0).Z:0},{MenuRectOf(ctxD, scale0).W:0})");
                if (subD != null && subD->IsVisible)
                    Plugin.Log.Error($"[Hole-Diag] diag AddonContextSub rect=({MenuRectOf(subD, scale0).X:0},{MenuRectOf(subD, scale0).Y:0},{MenuRectOf(subD, scale0).Z:0},{MenuRectOf(subD, scale0).W:0})");
                for (var mi = 0; mi < menuRects.Count; mi++)
                {
                    var mr = menuRects[mi];
                    Plugin.Log.Error($"[Hole-Diag] menu[{mi}] rect=({mr.X:0},{mr.Y:0},{mr.Z:0},{mr.W:0}) scale={scale0}");
                }
                for (var i = 0; i < dd.CmdListsCount; i++)
                {
                    var dl = new ImDrawListPtr(dd.CmdLists[i]);
                    if (dl.Handle == null)
                        continue;
                    var name = dl.OwnerName != null ? Marshal.PtrToStringUTF8((nint)dl.OwnerName) : "?";
                    if (name == null || !IsChatTwoDrawList(name))
                        continue;
                    Plugin.Log.Error($"[Hole-Diag] dl '{name}' cmd={dl.CmdBuffer.Size} cap={dl.CmdBuffer.Capacity} vtx={dl.VtxBuffer.Size} idx={dl.IdxBuffer.Size}");
                    var dcmds = (ImDrawCmd*)dl.CmdBuffer.Data;
                    var didx = (ushort*)dl.IdxBuffer.Data;
                    var dvtx = (ImDrawVert*)dl.VtxBuffer.Data;
                    // ElemCount 分布统计 + 每个 cmd 的实际顶点 bbox（不只看 clip）
                    var elemDist = new System.Collections.Generic.Dictionary<uint, int>();
                    for (var c = 0; c < dl.CmdBuffer.Size; c++)
                    {
                        elemDist.TryGetValue(dcmds[c].ElemCount, out var cnt);
                        elemDist[dcmds[c].ElemCount] = cnt + 1;
                    }
                    var dist = string.Join(",", elemDist.Select(kv => $"{kv.Key}x{kv.Value}"));
                    Plugin.Log.Error($"[Hole-Diag]   elem分布: {dist}");
                    for (var c = 0; c < dl.CmdBuffer.Size && c < 20; c++)
                    {
                        // 取前 6 索引的 bbox（矩形判据）
                        if (dcmds[c].ElemCount == 6 || dcmds[c].ElemCount == 12)
                        {
                            float minx = float.MaxValue, miny = float.MaxValue, maxx = float.MinValue, maxy = float.MinValue;
                            for (var k = 0; k < 6 && (int)(dcmds[c].IdxOffset + k) < dl.IdxBuffer.Size; k++)
                            {
                                var vi = didx[dcmds[c].IdxOffset + k];
                                var p = dvtx[vi].Pos;
                                minx = Math.Min(minx, p.X); miny = Math.Min(miny, p.Y);
                                maxx = Math.Max(maxx, p.X); maxy = Math.Max(maxy, p.Y);
                            }
                            Plugin.Log.Error($"[Hole-Diag]   cmd[{c}] elem={dcmds[c].ElemCount} clip=({dcmds[c].ClipRect.X:0},{dcmds[c].ClipRect.Y:0},{dcmds[c].ClipRect.Z:0},{dcmds[c].ClipRect.W:0}) vtxBBox=({minx:0},{miny:0},{maxx:0},{maxy:0})");
                        }
                        else
                        {
                            Plugin.Log.Error($"[Hole-Diag]   cmd[{c}] elem={dcmds[c].ElemCount} clip=({dcmds[c].ClipRect.X:0},{dcmds[c].ClipRect.Y:0},{dcmds[c].ClipRect.Z:0},{dcmds[c].ClipRect.W:0})");
                        }
                    }
                }
            }
            if (menuRects.Count == 0)
            {
                _diagDumped = false;
                _budgetDumpedDls.Clear();   // !!! 按 dl 隔离：菜单关闭清空，下次菜单各 dl 再打
            }
#endif

            if (menuRects.Count == 0)
                return; // 菜单未打开 → 不挖洞

            for (var i = 0; i < dd.CmdListsCount; i++)
            {
                var dl = new ImDrawListPtr(dd.CmdLists[i]);
                if (dl.Handle == null)
                    continue;
                var name = dl.OwnerName != null ? Marshal.PtrToStringUTF8((nint)dl.OwnerName) : null;
                if (name == null || !IsChatTwoDrawList(name))
                    continue;
                HoleDrawList(dl, menuRects);
            }
        }
        catch (Exception ex) { Plugin.Log.Error($"[Hole] error {ex.Message}"); }
    }

    /// <summary>当前可见菜单的屏幕矩形（逻辑坐标 × UI scale × RootNode 缩放），恒返回 0 或 1 个。</summary>
    /// <remarks>!!! 修复（实测"圆角没了 + 二级文字进入"）：
    /// 之前返回所有可见菜单（一级 ContextMenu + 二级 AddonContextSub，过渡帧可能 2 个），
    /// 迭代减第二轮把第一轮圆角阶梯**直角化**（洞变直角），②b 字符迭代两次 overflow 跳过
    /// （文字进入菜单）。二级展开时一级已自动关闭 → **恒单矩形最稳**：优先二级，其次一级。
    /// !!! 放弃提示框挖洞（ItemDetail/ActionDetail 不再返回）——提示框位置
    /// 由智能放置控制（避开聊天框），不再需要挖洞；挖洞只服务原生右键菜单。</remarks>
    private static unsafe List<Vector4> GetMenuRects()
    {
        var rects = new List<Vector4>(1);
        try
        {
            var mgr = RaptureAtkModule.Instance()->RaptureAtkUnitManager;
            var scale = AtkStage.Instance()->ScreenSizeScale;
            // 二级菜单（AddonContextSub）优先：二级展开时一级已关闭，过渡帧两个都可见也只挖二级
            var sub = mgr.GetAddonByName("AddonContextSub");
            if (sub != null && sub->IsVisible)
            {
                rects.Add(MenuRectOf(sub, scale));
                return rects;
            }
            // 一级菜单（ContextMenu）
            var ctx = mgr.GetAddonByName("ContextMenu");
            if (ctx != null && ctx->IsVisible)
                rects.Add(MenuRectOf(ctx, scale));
        }
        catch { /* 忽略 */ }
        return rects;
    }

    /// <summary>菜单 addon → 屏幕矩形（含 RootNode 缩放； 实测漏乘导致挖洞只有 1/1.4）。
    /// 调用方：GetMenuRects（挖洞用）与 RenderDetour 的诊断 dump。</summary>
    private static unsafe Vector4 MenuRectOf(AtkUnitBase* addon, float scale)
    {
        ushort w, h;
        addon->GetSize(&w, &h, false); // 未缩放尺寸
        var root = addon->RootNode;
        var sx = root != null ? root->ScaleX : 1f;
        var sy = root != null ? root->GetScaleY() : 1f;
        var x = addon->X * scale;
        var y = addon->Y * scale;
        return new Vector4(x, y, x + w * scale * sx, y + h * scale * sy);
    }

    /// <summary>
    /// 挖洞：① 完全在任一菜单圆角矩形内的三角形退化（文字/小元素）→ ② 背景 cmd（大面积块）拆成
    /// "背景−所有菜单圆角矩形"的矩形子 cmd（复用原顶点+原索引，clip=子区域，渲染时裁剪）→ 背景精确镂空。
    /// !!! ：菜单是圆角矩形（实测四角磨圆），洞不能是直角矩形（四角外多挖）。
    /// !!! ：支持多个菜单矩形（一级 ContextMenu + 二级 AddonContextSub）——
    /// ②/②b 用"迭代减"：subs 从 {bbox} 开始，逐个矩形减（bg − A − B 的矩形分解）。
    /// 新 cmd 插到背景 cmd 之后（保持渲染顺序：背景在文字前）。零分配（写在 Capacity 余量内）。
    /// </summary>
    private static unsafe void HoleDrawList(ImDrawListPtr dl, List<Vector4> menuRects)
    {
        var cmds = (ImDrawCmd*)dl.CmdBuffer.Data;
        var idx = (ushort*)dl.IdxBuffer.Data;
        var vtx = (ImDrawVert*)dl.VtxBuffer.Data;
        if (cmds == null || idx == null || vtx == null)
            return;

        // 每个菜单的圆角矩形（内缩阴影，四边独立参数——14:07/ 实测微调值）。
        // !!! 二级菜单暂用同一组参数，实测观感不同再单独调。
        var rrs = new List<Vector4>(menuRects.Count);
        foreach (var mr in menuRects)
            rrs.Add(MenuRoundRect(mr));
        var radius = 11f;

        // ① 文字/小元素：三角形三个顶点都在**任一**菜单圆角矩形内 → 退化（面积 0 不渲染）
        var vtxCount = dl.VtxBuffer.Size;
        for (var i = 0; i + 2 < dl.IdxBuffer.Size; i += 3)
        {
            var i0 = idx[i];
            var i1 = idx[i + 1];
            var i2 = idx[i + 2];
            // !!! 越界保护（稳定性排查）：idx 值必须 < VtxBuffer.Size，否则读越界崩溃
            if (i0 >= vtxCount || i1 >= vtxCount || i2 >= vtxCount)
                continue;
            var p0 = vtx[i0].Pos;
            var p1 = vtx[i1].Pos;
            var p2 = vtx[i2].Pos;
            foreach (var rr in rrs)
            {
                if (InsideRoundRect(p0, rr, radius) && InsideRoundRect(p1, rr, radius) && InsideRoundRect(p2, rr, radius))
                {
                    idx[i] = i0;
                    idx[i + 1] = i0;
                    idx[i + 2] = i0;
                    break;
                }
            }
        }

        // ② 背景 cmd 拆分：先收集所有需要拆分的背景 cmd 与 subs，统一原地重建
        // !!! 稳定性重构：**永不替换 CmdBuffer.Data 指针**——
        // 扩容 AllocHGlobal 有分配器不匹配隐患（ImGui 每帧 Clear 后在新 Data 上 PushBack，
        // draw list 销毁时 IM_FREE 释放 AllocHGlobal → 未定义行为）。
        // 改为：subs 总数严格 ≤ Capacity 余量（原地拆分），圆角阶梯动态降级适配。
        var cmdSize = dl.CmdBuffer.Size;
        var availCmd = Math.Max(0, dl.CmdBuffer.Capacity - cmdSize);   // 可用 cmd 槽位（原地拆分上限）
        var splitPlan = new List<(int c, List<Vector4> subs)>();
        var totalSubs = 0;
        // 背景 cmd 候选（先收集 bbox，再统一分配阶梯预算）
        var bgCandidates = new List<(int c, Vector4 bbox)>();
        for (var c = 0; c < cmdSize; c++)
        {
            var cmd = &cmds[c];
            if (cmd->ElemCount < 6)
                continue;
            var bbox = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            for (var k = 0; k < cmd->ElemCount && (int)(cmd->IdxOffset + k) < dl.IdxBuffer.Size; k++)
            {
                var vi = idx[cmd->IdxOffset + k];
                if (vi >= vtxCount)
                    continue;   // !!! 越界保护（14:38）
                var p = vtx[vi].Pos;
                bbox.X = Math.Min(bbox.X, p.X);
                bbox.Y = Math.Min(bbox.Y, p.Y);
                bbox.Z = Math.Max(bbox.Z, p.X);
                bbox.W = Math.Max(bbox.W, p.Y);
            }
            if (bbox.Z <= bbox.X || bbox.W <= bbox.Y)
                continue;
            var area = (bbox.Z - bbox.X) * (bbox.W - bbox.Y);
            if (area < 1600f)
                continue;   // 只拆背景级大块（27x27 字符走②b）
            // 与任一菜单矩形外接框相交？
            var intersects = false;
            var fullyInside = false;
            foreach (var rr in rrs)
            {
                if (RectIntersects(bbox, rr))
                    intersects = true;
                if (RectContains(rr, bbox))
                    fullyInside = true;
            }
            if (!intersects)
                continue;
            if (fullyInside)
                continue;   // 完全在内 → ① 已退化
            bgCandidates.Add((c, bbox));
        }

        // ②b 跨边界字符/小元素：clip 矩形拆分（不圆角，防负尺寸崩溃）
        // !!! ：字符 cmd（elem=6，27x27）跨圆角矩形边界时，①退化不生效
        //（三顶点不全在内）→ 字体进入菜单。改为：把字符 clip 缩到"字符∩菜单外"的矩形
        //（1-4 个简单矩形，不做圆角阶梯，避免小 bbox 生成负尺寸）。
        // !!! 预算重构：先收集字符候选 → **②b 优先分配预算（保底 availCmd/3，
        // 文字排开最重要）** → 背景② 用剩余预算。此前② 占满 availCmd → ②b 跳过 → 文字
        // 进入菜单（实测"偶尔排不开文字"）。
        var charCandidates = new List<(int c, Vector4 bbox)>();
        for (var c = 0; c < cmdSize; c++)
        {
            var cmd = &cmds[c];
            if (cmd->ElemCount < 6)
                continue;
            // 只处理小 cmd（面积 < 1600，即非背景）
            var bbox = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            for (var k = 0; k < cmd->ElemCount && (int)(cmd->IdxOffset + k) < dl.IdxBuffer.Size; k++)
            {
                var vi = idx[cmd->IdxOffset + k];
                if (vi >= vtxCount)
                    continue;   // !!! 越界保护（14:38）
                var p = vtx[vi].Pos;
                bbox.X = Math.Min(bbox.X, p.X);
                bbox.Y = Math.Min(bbox.Y, p.Y);
                bbox.Z = Math.Max(bbox.Z, p.X);
                bbox.W = Math.Max(bbox.W, p.Y);
            }
            if (bbox.Z <= bbox.X || bbox.W <= bbox.Y)
                continue;
            var smallArea = (bbox.Z - bbox.X) * (bbox.W - bbox.Y);
            if (smallArea >= 1600f)
                continue;
            // 与任一菜单矩形相交？完全在内（①已退化）或不相交 → 跳过
            var intersects2 = false;
            var fullyInside2 = false;
            foreach (var rr in rrs)
            {
                if (RectIntersects(bbox, rr))
                    intersects2 = true;
                if (RectContains(rr, bbox))
                    fullyInside2 = true;
            }
            if (!intersects2)
                continue;
            if (fullyInside2)
                continue;
            charCandidates.Add((c, bbox));
        }

        // ── ②b 优先分配（文字排开保底）──
        // !!! 实测（[Hole-Budget]）：messages avail=651，跨边界字符 317 个，
        // charBudget=avail/3=217 只够拆 208 个 → charSkip=109 → 文字大量进入菜单。
        // 背景反而占 223 槽（steps=7 圆角饱满）。提为 **avail/2**：文字优先，背景 steps 降级
        //（7→~5，圆角略粗但可接受；文字排开是第一优先级）。
        // !!! 追加：预算耗尽时**剩余跨边界字符直接退化**（不拆、不进入菜单；
        // 代价：菜单边缘的字符整字消失——比"文字压在菜单上"视觉干净）。
        // !!! 方案3（确认）：字符按"菜单内占比"分流——占比 ≥70% → **直接退化**
        //（丢 ≤30% 视觉无感，0 槽省预算给真正需要的字符）；占比 <70% → **拆分**（保留大部分）。
        // !!! ：提示框场景 ②b 不保底（保底 1/4，背景优先——提示框大、跨边界字符 2 倍，
        // 保底 1/2 会把背景预算挤没）；菜单场景保底 1/2。
        // !!! 放弃提示框挖洞 → 不再区分提示框/菜单场景，②b 统一保底 1/2（文字优先）
        var charBudget = Math.Max(4, availCmd / 2);
        var charUsed = 0;      // ②b 实际消耗槽位
        var charAllocated = 0; // ②b 成功拆分的字符数
        foreach (var (c, bbox) in charCandidates)
        {
            // 方案3：字符大部分在菜单内 → 整字退化（省预算，丢的小部分视觉无感）
            if (MenuInsideRatio(bbox, rrs) >= 0.7f)
            {
                DegenerateCharCmd(c, cmds, idx, dl.IdxBuffer.Size);
                continue;
            }
            // 迭代减（简化直角补集，不圆角）—— 预算保护：超 8 个就放弃该字符
            var subs2 = new List<Vector4> { bbox };
            var overflow2 = false;
            foreach (var rr in rrs)
            {
                var next2 = new List<Vector4>(subs2.Count * 4);
                foreach (var sub in subs2)
                    next2.AddRange(RectComplementSimple(sub, rr));
                subs2 = next2;
                if (subs2.Count > 8)
                {
                    overflow2 = true;
                    break;
                }
            }
            if (overflow2 || subs2.Count == 0)
                continue;
            if (totalSubs + subs2.Count > charBudget)
                break;   // 保底预算用完 → 停（不再拆更多字符）
            splitPlan.Add((c, subs2));
            totalSubs += subs2.Count;
            charUsed += subs2.Count;
            charAllocated++;
        }
        // !!! 预算耗尽兜底：未拆分的跨边界字符全部退化（不渲染）→ 文字不进入菜单。
        //（占比≥70% 已退化，重复退化幂等无害）
        if (charAllocated < charCandidates.Count)
        {
            for (var j = charAllocated; j < charCandidates.Count; j++)
                DegenerateCharCmd(charCandidates[j].c, cmds, idx, dl.IdxBuffer.Size);
        }

        // ── 背景② 用剩余预算（steps 降级适配）──
        var bgBudget = availCmd - totalSubs;
        var steps = 8;
        while (steps > 1 && bgCandidates.Count * (4 + 4 * steps) > bgBudget)
            steps--;
        var bgUsed = 0;        // 背景实际消耗槽位
        var bgAllocated = 0;   // 背景成功拆分的 cmd 数
        var bgFallback = 0;    // fallback 直角次数
        foreach (var (c, bbox) in bgCandidates)
        {
            // 迭代减：bbox − 矩形1 − 矩形2 ...（正确分解"背景 − 所有菜单"）
            var subs = new List<Vector4> { bbox };
            var overflow = false;
            foreach (var rr in rrs)
            {
                var next = new List<Vector4>(subs.Count * 4);
                foreach (var sub in subs)
                    next.AddRange(RoundRectComplement(sub, rr, radius, steps));
                subs = next;
                if (subs.Count > availCmd)
                {
                    overflow = true;
                    break;
                }
            }
            if (overflow || subs.Count == 0)
                continue;
            if (totalSubs + subs.Count > availCmd)
            {
                // !!! 预算不足：fallback 直角补集（1-4 块）——至少挖掉不盖菜单
                //（bottom-log cap=8 场景：圆角阶梯 8 块放不下 → 直角挖掉）
                var simple = new List<Vector4> { bbox };
                var ovf = false;
                foreach (var rr in rrs)
                {
                    var nx = new List<Vector4>(simple.Count * 4);
                    foreach (var sub in simple)
                        nx.AddRange(RectComplementSimple(sub, rr));
                    simple = nx;
                    if (simple.Count > 4)
                    {
                        ovf = true;
                        break;
                    }
                }
                if (ovf || simple.Count == 0)
                    continue;
                if (totalSubs + simple.Count > availCmd)
                    continue;
                subs = simple;
                bgFallback++;
            }
            splitPlan.Add((c, subs));
            totalSubs += subs.Count;
            bgUsed += subs.Count;
            bgAllocated++;
        }

#if ENABLE_CTX_DIAG
        // !!! [Hole-Budget] 预算诊断（16:05）：每菜单会话每 dl 打印一次真实消耗。
        // availCmd=Capacity−Size 是硬上限；bg/char 消耗看清后按需调 charBudget 比例或 steps 策略。
        try
        {
            var dlName = dl.OwnerName != null ? Marshal.PtrToStringUTF8((nint)dl.OwnerName) ?? "?" : "?";
            if (!_budgetDumpedDls.Contains(dlName))
            {
                _budgetDumpedDls.Add(dlName);
                var finalEst = cmdSize + totalSubs - splitPlan.Count;
                Plugin.Log.Error($"[Hole-Budget] dl='{dlName}' avail={availCmd} bgCand={bgCandidates.Count} bgUsed={bgUsed} bgAlloc={bgAllocated} bgFallback={bgFallback} steps={steps} charCand={charCandidates.Count} charBudget={charBudget} charUsed={charUsed} charAlloc={charAllocated} charSkip={charCandidates.Count - charAllocated} totalSubs={totalSubs} final={finalEst}");
            }
        }
        catch { /* 诊断失败忽略 */ }
#endif
        if (splitPlan.Count == 0)
            return;

        var addedCount = 0;
        foreach (var (_, subs) in splitPlan)
            addedCount += subs.Count - 1;
        var finalSize = cmdSize + addedCount;
        // !!! 稳定性：finalSize 必须 ≤ Capacity（永不扩容，绝不替换 Data 指针）
        if (finalSize > dl.CmdBuffer.Capacity)
        {
            Plugin.Log.Error($"[Hole] finalSize {finalSize} > Capacity {dl.CmdBuffer.Capacity} 跳过（不应发生）");
            return;
        }

        // 原地从后往前填充（不越界，finalSize ≤ Capacity）
        var writeIdx = finalSize - 1;
        for (var c = cmdSize - 1; c >= 0; c--)
        {
            var plan = splitPlan.FirstOrDefault(p => p.c == c);
            if (plan.subs != null)
            {
                for (var s = plan.subs.Count - 1; s >= 0; s--)
                {
                    cmds[writeIdx].ClipRect = plan.subs[s];
                    cmds[writeIdx].TextureId = cmds[c].TextureId;
                    cmds[writeIdx].VtxOffset = cmds[c].VtxOffset;
                    cmds[writeIdx].IdxOffset = cmds[c].IdxOffset;
                    cmds[writeIdx].ElemCount = cmds[c].ElemCount;
                    cmds[writeIdx].UserCallback = null;
                    cmds[writeIdx].UserCallbackData = null;
                    writeIdx--;
                }
            }
            else
            {
                if (writeIdx != c)
                    cmds[writeIdx] = cmds[c];
                writeIdx--;
            }
        }
        dl.CmdBuffer = new ImVector<ImDrawCmd>(finalSize, dl.CmdBuffer.Capacity, dl.CmdBuffer.Data);

        // !!! [Hole-Diag] 崩溃防御（诊断 + 清零）：
        // 崩溃地址 0x12345679 固定 = 某处写入的"毒药"UserCallback 指针被 D3D 当回调调用
        //（RenderDrawDataInternal switch：非 0(Empty)/-8(ResetRenderState)/-1(blur) → 直接 call）
        // → 跳转非法地址 AccessViolation（实测 4 次，栈完全相同）。
        // 防御：修改后扫描，UserCallback 落在"绝不可能是合法代码指针"的范围 → 清零（宁可少画不崩）。
        try
        {
            for (var k = 0; k < finalSize; k++)
            {
                var cb = (long)cmds[k].UserCallback;
                if (cb == 0 || cb == -1 || cb == -8)   // 0=Empty, -1=blur, -8=ResetRenderState（已知合法值）
                    continue;
                if (cb == 0x12345679 || cb < 0x10000)  // 毒药值 / 低地址（不可能是代码段）
                {
                    Plugin.Log.Error($"[Hole-Diag] cmd[{k}] 毒药 UserCallback=0x{cb:X} 已清零 elem={cmds[k].ElemCount} clip=({cmds[k].ClipRect.X:0},{cmds[k].ClipRect.Y:0},{cmds[k].ClipRect.Z:0},{cmds[k].ClipRect.W:0})");
                    cmds[k].UserCallback = null;
                }
            }
        }
        catch { /* 防御失败忽略 */ }
    }

    /// <summary>菜单 addon 矩形 → 挖洞用的圆角矩形（内缩阴影，四边独立参数——实测微调值）。</summary>
    /// <remarks>!!! ：二级菜单（AddonContextSub）暂用同一组参数，实测观感不同再单独调。</remarks>
    private static Vector4 MenuRoundRect(Vector4 menuRect)
    {
        var insetTop = 7f;
        var insetLeft = 8f;
        var insetRight = 7f;
        var insetBottom = 11f;   // 确认：下方向上 0.5px（10.5→11）
        return new Vector4(menuRect.X + insetLeft, menuRect.Y + insetTop, menuRect.Z - insetRight, menuRect.W - insetBottom);
    }

    /// <summary>两矩形是否相交（a.Z &gt; b.X && a.X &lt; b.Z ... 外接框判定）。</summary>
    private static bool RectIntersects(Vector4 a, Vector4 b)
        => a.Z > b.X && a.X < b.Z && a.W > b.Y && a.Y < b.W;

    /// <summary>outer 是否完全包含 inner。</summary>
    private static bool RectContains(Vector4 outer, Vector4 inner)
        => inner.X >= outer.X && inner.Y >= outer.Y && inner.Z <= outer.Z && inner.W <= outer.W;

    /// <summary>退化字符 cmd：所有三角形的三索引指向同一顶点（面积 0 不渲染）。
    /// ②b 预算耗尽时对未拆分的跨边界字符调用——宁可整字消失，也不让文字压进菜单。</summary>
    private static unsafe void DegenerateCharCmd(int c, ImDrawCmd* cmds, ushort* idx, int idxBufferSize)
    {
        var cmd = &cmds[c];
        var start = (int)cmd->IdxOffset;
        var end = Math.Min(start + (int)cmd->ElemCount, idxBufferSize);
        for (var k = start; k + 2 < end; k += 3)
        {
            var i0 = idx[k];
            idx[k] = i0;
            idx[k + 1] = i0;
            idx[k + 2] = i0;
        }
    }

    /// <summary>字符 bbox 落在菜单内的面积占比（0~1）。字符小（27x27），用菜单外接矩形近似交集，圆角忽略。
    /// 方案3（17:51）：占比 ≥0.7 → 整字退化（丢 ≤30% 视觉无感）；&lt;0.7 → 拆分。</summary>
    private static float MenuInsideRatio(Vector4 bbox, List<Vector4> rrs)
    {
        var bboxArea = (bbox.Z - bbox.X) * (bbox.W - bbox.Y);
        if (bboxArea <= 0)
            return 0;
        var maxIn = 0f;
        foreach (var rr in rrs)
        {
            var w = Math.Min(bbox.Z, rr.Z) - Math.Max(bbox.X, rr.X);
            var h = Math.Min(bbox.W, rr.W) - Math.Max(bbox.Y, rr.Y);
            if (w > 0 && h > 0)
                maxIn = Math.Max(maxIn, w * h);
        }
        return maxIn / bboxArea;
    }

    /// <summary>背景矩形 − 菜单矩形 的简化直角补集（1-4 个矩形，不圆角；②b 字符拆分用）。
    /// 调用方：HoleDrawList 的②b 分配循环（跨边界字符的 clip 拆分）与背景②的预算耗尽直角 fallback。</summary>
    private static List<Vector4> RectComplementSimple(Vector4 bg, Vector4 rr)
    {
        var subs = new List<Vector4>(4);
        var my0 = Math.Max(rr.Y, bg.Y);
        var my1 = Math.Min(rr.W, bg.W);
        var mx0 = Math.Max(rr.X, bg.X);
        var mx1 = Math.Min(rr.Z, bg.Z);
        if (my0 > bg.Y && my0 - bg.Y >= 1f)
            subs.Add(new Vector4(bg.X, bg.Y, bg.Z, my0));                          // 上
        if (my1 < bg.W && bg.W - my1 >= 1f)
            subs.Add(new Vector4(bg.X, my1, bg.Z, bg.W));                          // 下
        if (mx0 > bg.X && mx0 - bg.X >= 1f && my1 > my0)
            subs.Add(new Vector4(bg.X, my0, mx0, my1));                            // 左
        if (mx1 < bg.Z && bg.Z - mx1 >= 1f && my1 > my0)
            subs.Add(new Vector4(mx1, my0, bg.Z, my1));                            // 右
        return subs;
    }

    /// <summary>点是否在圆角矩形内（含四角圆弧内部）。
    /// 调用方：HoleDrawList 的①退化循环（三个顶点都在内 → 三角形退化不渲染）。</summary>
    private static bool InsideRoundRect(Vector2 p, Vector4 rr, float r)
    {
        if (p.X < rr.X || p.X > rr.Z || p.Y < rr.Y || p.Y > rr.W)
            return false;
        // 四角圆弧：圆心在四角内侧 r 处
        var cx = p.X < rr.X + r ? rr.X + r : (p.X > rr.Z - r ? rr.Z - r : p.X);
        var cy = p.Y < rr.Y + r ? rr.Y + r : (p.Y > rr.W - r ? rr.W - r : p.Y);
        // 若点不在角区（中心十字带），直接在内
        if ((p.X >= rr.X + r && p.X <= rr.Z - r) || (p.Y >= rr.Y + r && p.Y <= rr.W - r))
            return true;
        var dx = p.X - cx;
        var dy = p.Y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>背景矩形 − 圆角矩形 的矩形分解（保留区，四角圆弧用阶梯近似）。
    /// 调用方：HoleDrawList 的背景②拆分（对每个背景 bbox 与每个菜单矩形做迭代减）。</summary>
    /// <remarks>!!! 加正尺寸防御：所有 subs 必须 X&lt;Z && Y&lt;W 且 ≥1px，
    /// 否则跳过（防止极端 bbox 生成负尺寸 clip → D3D 渲染崩溃）。</remarks>
    private static List<Vector4> RoundRectComplement(Vector4 bg, Vector4 rr, float r, int steps)
    {
        var subs = new List<Vector4>(12);
        void AddSub(Vector4 v)
        {
            if (v.Z - v.X >= 1f && v.W - v.Y >= 1f)
                subs.Add(v);
        }
        // 上下左右主条带（圆角矩形直边外的区域）
        if (rr.Y > bg.Y)
            AddSub(new Vector4(bg.X, bg.Y, bg.Z, rr.Y));                          // 上
        if (rr.W < bg.W)
            AddSub(new Vector4(bg.X, rr.W, bg.Z, bg.W));                          // 下
        if (rr.X > bg.X && rr.W > rr.Y)
            AddSub(new Vector4(bg.X, rr.Y, rr.X, rr.W));                          // 左
        if (rr.Z < bg.Z && rr.W > rr.Y)
            AddSub(new Vector4(rr.Z, rr.Y, bg.Z, rr.W));                          // 右

        // 四角圆弧外侧的小三角区（圆弧外、外接矩形内）→ 阶梯矩形近似
        // !!! 修复：每段用"近圆心端"的 yArc（圆弧 y 最大值，高估）
        // 作为矩形边界，确保覆盖整个段圆弧外区域（实测：段中点低估导致右上/右下出现"尖角矩形"）
        for (var i = 0; i < steps; i++)
        {
            // 左半边 x ∈ [rr.X, rr.X+r]，圆心 (rr.X+r)，近圆心端 = x1（更大 x）
            var x0L = rr.X + r * i / steps;
            var x1L = rr.X + r * (i + 1) / steps;
            // 左上角圆心 (rr.X+r, rr.Y+r)：用 x1L
            var yArcTL = YArcTop(x1L, rr.X + r, rr.Y + r, r);
            if (yArcTL > rr.Y)
                AddSub(new Vector4(x0L, rr.Y, x1L, yArcTL));
            // 左下角圆心 (rr.X+r, rr.W-r)：用 x1L
            var yArcBL = YArcBottom(x1L, rr.X + r, rr.W - r, r);
            if (yArcBL < rr.W)
                AddSub(new Vector4(x0L, yArcBL, x1L, rr.W));

            // 右半边 x ∈ [rr.Z-r, rr.Z]，圆心 (rr.Z-r)，近圆心端 = x0（更小 x）
            var x0R = rr.Z - r + r * i / steps;
            var x1R = rr.Z - r + r * (i + 1) / steps;
            // 右上角：用 x0R
            var yArcTR = YArcTop(x0R, rr.Z - r, rr.Y + r, r);
            if (yArcTR > rr.Y)
                AddSub(new Vector4(x0R, rr.Y, x1R, yArcTR));
            // 右下角：用 x0R
            var yArcBR = YArcBottom(x0R, rr.Z - r, rr.W - r, r);
            if (yArcBR < rr.W)
                AddSub(new Vector4(x0R, yArcBR, x1R, rr.W));
        }
        return subs;
    }

    /// <summary>上半圆弧在 x 处的 y（圆心 (cx,cy)，半径 r，圆弧 y ≤ cy）。
    /// 调用方：RoundRectComplement（每段圆弧外矩形边界，取"近圆心端" x）。</summary>
    private static float YArcTop(float x, float cx, float cy, float r)
    {
        var d = r * r - (x - cx) * (x - cx);
        return cy - (d > 0 ? MathF.Sqrt(d) : cy);
    }

    /// <summary>下半圆弧在 x 处的 y（圆心 (cx,cy)，半径 r，圆弧 y ≥ cy）。
    /// 调用方：RoundRectComplement（每段圆弧外矩形边界，取"近圆心端" x）。</summary>
    private static float YArcBottom(float x, float cx, float cy, float r)
    {
        var d = r * r - (x - cx) * (x - cx);
        return cy + (d > 0 ? MathF.Sqrt(d) : cy);
    }

#if ENABLE_CTX_DIAG
    /// <summary>递归 dump 菜单节点树（本地坐标 + 全局坐标估算），找可见背景/列表区。</summary>
    /// <remarks>!!! AtkResNode.Type：普通节点=NodeType 枚举（1..10）；组件节点 Type 是 ≥1000 的编码（如 1001），
    /// 判断组件用 GetNodeType()==Component(10000) 或 Type>=1000。RootNode.X/Y 已是全局坐标（=addon X/Y），
    /// 子节点 X/Y 是相对父偏移 → 全局 = RootNode.X + Σ(沿父链偏移)。</remarks>
    private static unsafe void DumpNodeTree(AtkUnitBase* addon, AtkResNode* node, int depth, int maxDepth)
    {
        if (node == null || depth > maxDepth)
            return;
        try
        {
            var s = new string(' ', depth * 2);
            var type = (int)node->Type;
            var vis = node->IsVisible() ? "V" : "H";
            // 全局坐标：从 RootNode 开始累加（RootNode.X 已含 addon 位置）
            var absX = node->X;
            var absY = node->Y;
            var n = node->ParentNode;
            while (n != null)
            {
                absX += n->X;
                absY += n->Y;
                n = n->ParentNode;
            }
            Plugin.Log.Error($"[Hole-Diag]   {s}[{depth}] type={type} {vis} local=({node->X:0},{node->Y:0}) size=({node->GetWidth():0}x{node->GetHeight():0}) sc=({node->ScaleX:0.00},{node->GetScaleY():0.00}) global=({absX:0},{absY:0})");
        }
        catch (Exception ex) { Plugin.Log.Error($"[Hole-Diag]   node dump error {ex.Message}"); }

        // 组件节点：内部还有一层 UldManager 子节点（如 List 组件内的元素）
        // !!! type>=1000 才是组件（1001 是组件编码，NodeType.Component=10000 是 GetNodeType 返回值）
        if ((int)node->Type >= 1000)
        {
            try
            {
                var compNode = (AtkComponentNode*)node;
                if (compNode->Component != null && compNode->Component->UldManager.RootNode != null)
                    DumpNodeTree(addon, compNode->Component->UldManager.RootNode, depth + 1, maxDepth);
            }
            catch (Exception ex) { Plugin.Log.Error($"[Hole-Diag]   comp expand error {ex.Message}"); }
        }

        // 兄弟链：先深度后广度（优先看主要节点）
        if (node->ChildNode != null && depth < maxDepth)
            DumpNodeTree(addon, node->ChildNode, depth + 1, maxDepth);
        if (node->NextSiblingNode != null)
            DumpNodeTree(addon, node->NextSiblingNode, depth, maxDepth);
    }
#endif


    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
}
