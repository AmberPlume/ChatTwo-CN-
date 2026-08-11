using System.Numerics;
using ChatTwo.GameFunctions;
using ChatTwo.Util;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Config;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChatTwo.Ui.ChatLog;

public partial class ChatLog
{
    private unsafe void MoveTooltip(AddonEvent type, AddonArgs args)
    {
        // Only move if the user has the "Next to Cursor" option selected
        if (!Plugin.GameConfig.TryGet(UiControlOption.DetailTrackingType, out uint selected) || selected != 0)
            return;

        if (LastViewport != ImGuiHelpers.MainViewport.Handle)
            return;

        // Only move tooltips triggered from the chat window
        var mousePos = ImGui.GetMousePos();
        var chatRect = new MathUtil.Rectangle(LastWindowPos, LastWindowSize);
        if (!chatRect.Contains(mousePos))
            return;

        var atk = args.Addon;
        if (atk.IsNull)
            return;

        var atkBase = (AtkUnitBase*)atk.Address;
        if (atkBase->WindowNode == null)
            return;

        if (!atkBase->IsVisible)
            return;

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

        int newX, newY;

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

        atkBase->SetPosition((short)newX, (short)newY);
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

            // 检查菜单来源：仅聊天框触发的菜单才跟随位置。
            // 如果 OwnerAddon 不是 ChatLog（如小队列表、背包等触发的菜单），停止跟随。
            var agent = AgentContext.Instance();
            if (agent != null)
            {
                var chatLogAddonId = GameFunctions.GameFunctions.GetChatLogAddonId();
                if (agent->OwnerAddon != chatLogAddonId)
                {
                    Plugin.ContextMenuActive = false;
                    return;
                }
            }

            var addonPtr = args.Addon.Address;
            if (addonPtr == nint.Zero)
                return;

            var addon = (AtkUnitBase*)addonPtr;
            if (!addon->IsVisible)
            {
                // 菜单已隐藏但未销毁（游戏复用 ContextMenu addon，PreFinalize 不会触发）
                // 重置激活状态，防止后续非聊天框触发的菜单被错误移动
                Plugin.ContextMenuActive = false;
                return;
            }

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
        }
        catch (Exception ex)
        {
            try { Plugin.Log.Debug($"[NativeCtxMenu] MoveContextMenu error: {ex.Message}"); }
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