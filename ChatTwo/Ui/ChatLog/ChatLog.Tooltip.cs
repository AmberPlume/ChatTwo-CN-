using System.Numerics;
using ChatTwo.GameFunctions;
using ChatTwo.Util;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Interface.Utility;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChatTwo.Ui.ChatLog;

public partial class ChatLog
{
    // ===== OpenAddonByAgent hook（vtable 槽 22）：OpenContextMenu 前设 BlockedParentId 会被游戏覆盖 =====
    // 2026-08-15 诊断 [BPItem] 铁证：OpenContextMenu 内部把 ContextMenu->BlockedParentId 清成 0
    // （bindToOwner=false 不绑定）→ Dalamud OnMenuOpened 的 AddonName = GetAddonById(BlockedParentId)
    // = 空 → DR/Allagan 的 switch 不识别 → 道具/玩家菜单项不注入。
    // 本 detour 在 Dalamud 的 detour 之前执行（我们后 hook → 链上前置）：先把 BlockedParentId 设为
    // ChatLog，再调链 → Dalamud detour 读到 ChatLog → AddonName="ChatLog" → 插件项正常注入。
    // 仅 ChatTwo 菜单会话（Plugin.ContextMenuActive）且打开 "ContextMenu" 时设置，不影响背包等原生场景。
    private Hook<OpenAddonByAgentDelegate>? _openAddonByAgentHook;
    private unsafe delegate ushort OpenAddonByAgentDelegate(
        AtkModule* module, byte* addonName, int valueCount, AtkValue* values,
        AgentInterface* agent, nint a7, bool a8);

    private unsafe void InitOpenAddonByAgentHook()
    {
        try
        {
            var atk = (AtkModule*)RaptureAtkModule.Instance();
            var vtable = *(nint**)atk;
            var addr = vtable[22];
            _openAddonByAgentHook = Plugin.GameInteropProvider.HookFromAddress<OpenAddonByAgentDelegate>(addr, OpenAddonByAgentDetour);
            _openAddonByAgentHook.Enable();
            Plugin.Log.Debug($"[OpenAddonByAgent] hook 启用 addr=0x{addr:X}");
        }
        catch (Exception ex) { Plugin.Log.Debug($"[OpenAddonByAgent] init error {ex.Message}"); }
    }

    private unsafe ushort OpenAddonByAgentDetour(
        AtkModule* module, byte* addonName, int valueCount, AtkValue* values,
        AgentInterface* agent, nint a7, bool a8)
    {
        try
        {
            // ⚠️ 会话残留防护（2026-08-15 00:28 诊断 [SubSession] 实锤）：ChatTwo 菜单关闭路径
            // 不总是触发 MoveContextMenu 的 PreDraw（ContextMenu addon 可能被销毁），导致
            // ChatTwoMenuSession 残留 → 背包二级菜单误移。此处是所有菜单打开的必经点：
            // 非 ChatTwo 会话（ContextMenuActive=false）的菜单打开 → 强制复位会话标志。
            if (!Plugin.ContextMenuActive)
                Plugin.ChatTwoMenuSession = false;

            // 注入阶段（OnMenuOpened）：仅 ChatTwo 会话打开 ContextMenu 时，把 BlockedParentId
            // 设为 ChatLog → Dalamud 的 AddonName=GetAddonById(BlockedParentId)="ChatLog" →
            // DR/Allagan 的 switch 识别 → 菜单项注入。背包等原生场景不设置。
            var name = addonName != null ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)addonName) ?? string.Empty : "(null)";
            if (Plugin.ContextMenuActive && name == "ContextMenu")
            {
                var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
                if (ctxAddon != null)
                    ctxAddon->BlockedParentId = (ushort)ChatTwo.GameFunctions.GameFunctions.GetChatLogAddonId();
            }
        }
        catch (Exception ex) { Plugin.Log.Debug($"[OpenAddonByAgent] error {ex.Message}"); }
        var result = _openAddonByAgentHook!.Original(module, addonName, valueCount, values, agent, a7, a8);

        // ⚠️ 注入完成（OnMenuOpened 已触发，DR 项已加）后立即清 BlockedParentId：
        // 游戏对"阻塞父"的检查在菜单打开时立即执行（用户实测 PreDraw 清零来不及，当帧闪没），
        // 必须在 Original 返回后、游戏后续检查前清成 0，否则隐藏的 ChatLog 持续阻塞菜单。
        // MoveContextMenu 的 PreDraw 清零保留作兜底。
        try
        {
            if (Plugin.ContextMenuActive)
            {
                var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
                if (ctxAddon != null && ctxAddon->BlockedParentId == ChatTwo.GameFunctions.GameFunctions.GetChatLogAddonId())
                    ctxAddon->BlockedParentId = 0;
            }
        }
        catch (Exception ex) { Plugin.Log.Debug($"[OpenAddonByAgent] post-clear error {ex.Message}"); }
        return result;
    }

    // ===== SetPosition hook（正式功能）：拦截游戏对 ItemDetail/ActionDetail 的每帧位置覆盖 =====
    private Hook<SetPosDelegate>? _setPosHook;
    private unsafe delegate void SetPosDelegate(nint thisPtr, short x, short y);

    private unsafe void InitSetPosHook()
    {
        try
        {
            var addon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ItemDetail");
            if (addon == null)
                return;
            var setPos = addon->VirtualTable->SetPosition;
            if (setPos == null)
                return;
            _setPosHook = Plugin.GameInteropProvider.HookFromAddress<SetPosDelegate>((nint)setPos, SetPosDetour);
            _setPosHook.Enable();
        }
        catch (Exception ex) { Plugin.Log.Debug($"[SetPosHook] init error {ex.Message}"); }
    }

    private void SetPosDetour(nint thisPtr, short x, short y)
    {
        try
        {
            unsafe
            {
                var addon = (AtkUnitBase*)thisPtr;
                var name = addon->NameString;
                if (name == "ItemDetail" || name == "ActionDetail")
                {
                    // ⚠️ 根治闪帧：游戏每帧通过 SetPosition 覆盖 tooltip 位置（跟随鼠标），
                    // 我们在 detour 里把坐标替换为我们算好的"聊天框旁"位置，游戏怎么算都白搭，
                    // 当帧渲染即为我们的位置 → 零闪帧（2026-08-14 [SPDiag] 铁证：游戏每帧调 SetPosition）。
                    if (TryComputeTooltipPos(addon, out var nx, out var ny))
                    {
                        _setPosHook!.Original(thisPtr, (short)nx, (short)ny);
                        return;
                    }
                }
            }
        }
        catch { }
        _setPosHook!.Original(thisPtr, x, y);
    }
    private unsafe void MoveTooltip(AddonEvent type, AddonArgs args)
    {
        var atk = args.Addon;
        if (atk.IsNull)
            return;
        var atkBase = (AtkUnitBase*)atk.Address;
        if (atkBase->WindowNode == null)
            return;
        if (!atkBase->IsVisible)
            return;

        if (!TryComputeTooltipPos(atkBase, out var newX, out var newY))
            return;

        atkBase->SetPosition((short)newX, (short)newY);
    }

    /// <summary>
    /// 计算 tooltip 应放置的位置（聊天框旁，与菜单同款逻辑）。
    /// 供 MoveTooltip（PreDraw/PostShow）与 SetPosition hook detour 复用。
    /// </summary>
    private unsafe bool TryComputeTooltipPos(AtkUnitBase* atkBase, out int newX, out int newY)
    {
        newX = newY = 0;

        // Only move if the user has the "Next to Cursor" option selected
        if (!Plugin.GameConfig.TryGet(UiControlOption.DetailTrackingType, out uint selected) || selected != 0)
            return false;

        if (LastViewport != ImGuiHelpers.MainViewport.Handle)
            return false;

        // Only move tooltips triggered from the chat window
        var mousePos = ImGui.GetMousePos();
        var chatRect = new MathUtil.Rectangle(LastWindowPos, LastWindowSize);
        if (!chatRect.Contains(mousePos))
            return false;

        var component = atkBase->WindowNode->AtkResNode;
        var atkSize = new Vector2(component.GetWidth() * component.ScaleX, component.GetHeight() * component.GetScaleY());

        var viewportSize = ImGuiHelpers.MainViewport.Size;
        var iAtkW = (int)atkSize.X;
        var iAtkH = (int)atkSize.Y;
        var iVpW = (int)viewportSize.X;
        var iVpH = (int)viewportSize.Y;

        // Determine which side of the screen the chat window is on,
        // using the center of the chat window for robustness.
        var chatCenterX = chatRect.X + chatRect.Width / 2;
        var chatCenterY = chatRect.Y + chatRect.Height / 2;
        var isChatLeft = chatCenterX < iVpW / 2;
        var isChatTop = chatCenterY < iVpH / 2;

        // Horizontal: place tooltip on the side of the cursor opposite to chat
        if (isChatLeft)
            newX = (int)mousePos.X + 10;
        else
            newX = (int)mousePos.X - 10 - iAtkW;

        // If the preferred side goes off-screen, flip to the other side
        if (newX + iAtkW > iVpW)
            newX = (int)mousePos.X - 10 - iAtkW;
        else if (newX < 0)
            newX = (int)mousePos.X + 10;

        // Vertical: if chat is at the bottom, place the tooltip above the cursor
        // so it doesn't extend below the screen. If chat is at the top, place
        // below the cursor.
        if (isChatTop)
            newY = (int)mousePos.Y + 10;
        else
            newY = (int)mousePos.Y - 10 - iAtkH;

        // If the preferred vertical side goes off-screen, flip
        if (newY + iAtkH > iVpH)
            newY = (int)mousePos.Y - 10 - iAtkH;
        else if (newY < 0)
            newY = (int)mousePos.Y + 10;

        // Clamp to screen bounds
        newX = Math.Clamp(newX, 0, iVpW - iAtkW);
        newY = Math.Clamp(newY, 0, iVpH - iAtkH);

        // If the tooltip overlaps with the chat window, try the opposite
        // vertical position relative to the cursor
        var newRect = new MathUtil.Rectangle(newX, newY, iAtkW, iAtkH);
        if (chatRect.HasOverlap(newRect))
        {
            newY = isChatTop
                ? (int)mousePos.Y - 10 - iAtkH  // above cursor
                : (int)mousePos.Y + 10;          // below cursor

            newY = Math.Clamp(newY, 0, iVpH - iAtkH);
            newRect = new MathUtil.Rectangle(newX, newY, iAtkW, iAtkH);
        }

        // Final fallback: place at the edge of the screen next to the chat,
        // clamped to screen bounds
        if (chatRect.HasOverlap(newRect))
        {
            newX = isChatLeft ? chatRect.SizeX + 10 : chatRect.X - 10 - iAtkW;
            newY = isChatTop ? chatRect.SizeY + 10 : chatRect.Y - 10 - iAtkH;

            newX = Math.Clamp(newX, 0, iVpW - iAtkW);
            newY = Math.Clamp(newY, 0, iVpH - iAtkH);
        }

        return true;
    }

    /// <summary>AddonContextSub（二级菜单 addon）当前是否可见。</summary>
    private static unsafe bool IsAddonContextSubVisible()
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

    /// <summary>
    /// PreDraw 回调：将 ContextMenu addon 移动到聊天框右侧。
    /// 与 MoveTooltip 相同的方式，使用 LastWindowPos/LastWindowSize 实时计算位置，
    /// 确保菜单跟随聊天框移动。
    /// 仅当 Plugin.ContextMenuActive 为 true 时移动（即菜单由 ChatTwo 触发）。
    /// </summary>
    private unsafe void MoveContextMenu(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!Plugin.ContextMenuActive)
                return;

            // ⚠️ 不调用 SetChatInteractable(true)：原生聊天框必须始终隐藏（用户要求）。
            // 之前担心 bindToOwner 子菜单需要访问聊天框，实测菜单在隐藏状态下可正常打开。

            // 菜单来源判断：不再依赖 OwnerAddon（2026-08-14 15:17 根治：ChatTwo 触发菜单时
            // OwnerAddon 恒为 0，避免其他插件 OpenSubmenu 的二级菜单被隐藏的 ChatLog 关闭）。
            // 改由 Plugin.ContextMenuActive（触发时置 true、菜单关闭/隐藏时置 false）+ IsVisible 判断。

            var addonPtr = args.Addon.Address;
            if (addonPtr == nint.Zero)
                return;

            var addon = (AtkUnitBase*)addonPtr;
            if (!addon->IsVisible)
            {
                // 菜单已隐藏但未销毁（游戏复用 ContextMenu addon，PreFinalize 不会触发）
                // 重置激活状态，防止后续非聊天框触发的菜单被错误移动
                Plugin.ContextMenuActive = false;
                // 一级菜单关闭：若二级菜单（AddonContextSub）也不可见 → ChatTwo 会话结束。
                // ⚠️ 展开二级时一级也会被游戏 Hide，但此时二级可见 → 保留会话（不误清）。
                if (!IsAddonContextSubVisible())
                    Plugin.ChatTwoMenuSession = false;
                return;
            }

            // ⚠️ OwnerAddon 时分复用（2026-08-14 23:49）：注入阶段（OnMenuOpened）由 PayloadHandler
            // 设为 ChatLog（DR/Allagan 靠 AddonName="ChatLog" 识别注入），此处（渲染阶段，注入已完成）
            // 每帧清零 → 用户点二级菜单时 OpenAddon 读到 owner=0 → 不绑定 ChatLog → 二级菜单正常显示。
            // 仅清 ChatLog 来源，不影响小队/背包等其他来源（那些来源 OwnerAddon 本就是其他 addon 或 0）。
            try
            {
                var agent = AgentContext.Instance();
                if (agent != null && agent->OwnerAddon == GameFunctions.GameFunctions.GetChatLogAddonId())
                    agent->OwnerAddon = 0;
            }
            catch (Exception ex) { Plugin.Log.Debug($"[NativeCtxMenu] owner-clear error {ex.Message}"); }

            // ⚠️ BlockedParentId 时分复用（2026-08-15 00:19）：OpenAddonByAgent detour 为 DR/Allagan
            // 注入设 BlockedParentId=ChatLog（OnMenuOpened 的 AddonName 来源），但它同时是"阻塞父"
            // 字段——ChatLog 隐藏（ChatTwo 常态）会持续阻塞菜单 → 一级菜单闪没（用户实测，打开原生
            // 聊天框才保持显示）。此处（渲染阶段，注入已完成）清 0 → 显示期间不被阻塞。
            try
            {
                if (addon->BlockedParentId == GameFunctions.GameFunctions.GetChatLogAddonId())
                    addon->BlockedParentId = 0;
            }
            catch (Exception ex) { Plugin.Log.Debug($"[NativeCtxMenu] blockedparent-clear error {ex.Message}"); }

            // 使用 LastWindowPos/LastWindowSize（每帧在 ImGui Begin/End 中更新）
            // 注意：SetPosition 用的是逻辑坐标（与 MoveTooltip 一致），不要再除以 globalScale，
            // 否则菜单位置会缩水（1.5 倍缩放时弹到聊天框中央）
            var chatRect = new MathUtil.Rectangle(LastWindowPos, LastWindowSize);

            // 菜单尺寸（逻辑坐标）
            var root = addon->RootNode;
            if (root == null)
                return;
            var atkSize = new Vector2(root->GetWidth() * root->ScaleX, root->GetHeight() * root->GetScaleY());
            var menuW = (int)atkSize.X;
            var menuH = (int)atkSize.Y;

            var viewportSize = ImGuiHelpers.MainViewport.Size;
            var vpW = (int)viewportSize.X;
            var vpH = (int)viewportSize.Y;

            // 默认放在聊天框右侧
            var newX = (int)(chatRect.X + chatRect.Width + 10);
            var newY = (int)chatRect.Y;

            // 右侧超出屏幕 → 翻转到聊天框左侧
            if (newX + menuW > vpW)
                newX = (int)(chatRect.X - 10 - menuW);

            // 垂直方向超出屏幕 → 靠边对齐
            newY = Math.Clamp(newY, 0, Math.Max(0, vpH - menuH));

            // 水平方向最终 Clamp 到屏幕内
            newX = Math.Clamp(newX, 0, Math.Max(0, vpW - menuW));

            addon->SetPosition((short)newX, (short)newY);

            // ===== SubMenuDiag 已确认是误报（二级菜单功能正常但 PreDraw 读不到），删除减少日志噪音 =====
        }
        catch (Exception ex)
        {
            try { Plugin.Log.Debug($"[NativeCtxMenu] MoveContextMenu error: {ex.Message}"); }
            catch { /* 最后兜底 */ }
        }
    }

    /// <summary>
    /// PreDraw/PostShow 回调：将 AddonContextSub（二级菜单）移动到聊天框外。
    /// 二级菜单是独立的 addon（2026-08-14 二进制确认，非 ContextMenu 子节点）。
    /// ⚠️ 关键（2026-08-14 05:49 用户实测）：
    ///   - 主菜单在二级菜单展开时会自动关闭（游戏行为）
    ///   - PreDraw 触发时 addon 可能尚未显示（IsVisible=false）→ 只靠 PreDraw 会"闪一下消失"
    ///   - 用 PostShow（显示完成瞬间）立即 SetPosition（当帧生效），与一级菜单"打开后立即 SetPosition"同款防闪
    ///   - 不依赖主菜单 ContextMenu 可见性（它已关闭）、不依赖 Plugin.ContextMenuActive（已被置 false）
    /// </summary>
    private unsafe void MoveContextSubMenu(AddonEvent type, AddonArgs args)
    {
        try
        {
            // ⚠️ 仅 ChatTwo 触发的菜单会话才移动二级菜单（2026-08-14 23:25 用户反馈：
            // 背包原生右键的二级菜单也被移到聊天框右侧）。ChatTwo 会话标志由 PayloadHandler
            // 触发时置 true，一级菜单关闭且二级不可见时清 false（且 OpenAddonByAgent detour
            // 对非 ChatTwo 会话强制复位，防残留）；背包等原生场景该标志恒 false →
            // 二级菜单保持游戏原生位置（跟随一级菜单旁）。
            if (!Plugin.ChatTwoMenuSession)
                return;

            var addonPtr = args.Addon.Address;
            if (addonPtr == nint.Zero)
                return;

            var addon = (AtkUnitBase*)addonPtr;

            // 检查菜单来源：仅聊天框触发的菜单才跟随位置。
            // ⚠️ 2026-08-14：ChatTwo 触发菜单时 OwnerAddon 恒为 0（最终方案，见 MEMORY.md），
            //    OwnerAddon 为 0（解绑态）或 ChatLog 都是聊天框来源 → 允许；其他来源（小队/背包）停止跟随。
            var agent = AgentContext.Instance();
            if (agent != null)
            {
                var chatLogAddonId = GameFunctions.GameFunctions.GetChatLogAddonId();
                // ⚠️ 通用修复（2026-08-14 15:12 用户实测）：二级菜单显示期间清零 OwnerAddon。
                // 原生聊天框隐藏（ChatTwo 常态）会让游戏检查 owner(ChatLog) 可见性失败并关闭
                // AddonContextSub —— 对 ChatTwo 自己以及 DR 等任何插件 OpenSubmenu 的二级菜单都生效。
                // 本回调注册在 PostShow + PreDraw（每帧触发），清零后游戏后续 owner 检查必读到 0。
                // 仅清 ChatLog 来源，不影响小队/背包等其他来源的子菜单。
                if (agent->OwnerAddon == chatLogAddonId)
                    agent->OwnerAddon = 0;
                if (agent->OwnerAddon != 0 && agent->OwnerAddon != chatLogAddonId)
                    return;
            }

            // 使用 LastWindowPos/LastWindowSize（每帧在 ImGui Begin/End 中更新）
            var chatRect = new MathUtil.Rectangle(LastWindowPos, LastWindowSize);

            // 二级菜单尺寸
            var root = addon->RootNode;
            if (root == null)
                return;
            var subSize = new Vector2(root->GetWidth() * root->ScaleX, root->GetHeight() * root->GetScaleY());
            var menuW = (int)subSize.X;
            var menuH = (int)subSize.Y;

            var viewportSize = ImGuiHelpers.MainViewport.Size;
            var vpW = (int)viewportSize.X;
            var vpH = (int)viewportSize.Y;

            // 默认放在聊天框右侧（与主菜单相同位置逻辑）
            var newX = (int)(chatRect.X + chatRect.Width + 10);
            var newY = (int)chatRect.Y;

            // 右侧超出屏幕 → 翻转到聊天框左侧
            if (newX + menuW > vpW)
                newX = (int)(chatRect.X - 10 - menuW);

            // 垂直方向超出屏幕 → 靠边对齐
            newY = Math.Clamp(newY, 0, Math.Max(0, vpH - menuH));

            // 水平方向最终 Clamp 到屏幕内
            newX = Math.Clamp(newX, 0, Math.Max(0, vpW - menuW));

            addon->SetPosition((short)newX, (short)newY);
        }
        catch (Exception ex)
        {
            try { Plugin.Log.Debug($"[NativeCtxMenu] MoveContextSubMenu error: {ex.Message}"); }
            catch { /* 最后兜底 */ }
        }
    }

    /// <summary>
    /// PreFinalize 回调：ContextMenu 关闭时清理活动标记。
    /// </summary>
    private static void OnContextMenuClosed(AddonEvent type, AddonArgs args)
    {
        try
        {
            Plugin.ContextMenuActive = false;
        }
        catch (Exception ex)
        {
            try { Plugin.Log.Debug($"[NativeCtxMenu] OnContextMenuClosed error: {ex.Message}"); }
            catch { /* 最后兜底 */ }
        }
    }

    /// <summary>
    /// 关闭原生上下文菜单（模拟游戏原生聊天框左键点击空白处关闭菜单的行为）。
    /// 通过 FireCallbackInt(-1) 让 ContextMenu addon 自行关闭。
    /// </summary>
    internal static unsafe void CloseNativeContextMenu()
    {
        try
        {
            var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
            if (ctxAddon != null && ctxAddon->IsVisible)
                ctxAddon->FireCallbackInt(-1);
            Plugin.ContextMenuActive = false;
        }
        catch (Exception ex)
        {
            try { Plugin.Log.Debug($"[NativeCtxMenu] CloseNativeContextMenu error: {ex.Message}"); }
            catch { /* 最后兜底 */ }
        }
    }
}