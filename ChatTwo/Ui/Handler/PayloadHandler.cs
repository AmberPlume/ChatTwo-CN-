using System.Linq;
using System.Numerics;
using System.Text;
using ChatTwo.Code;
using ChatTwo.GameFunctions;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

using Action = System.Action;
using DalamudPartyFinderPayload = Dalamud.Game.Text.SeStringHandling.Payloads.PartyFinderPayload;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;
using ChatTwoPartyFinderPayload = ChatTwo.Util.PartyFinderPayload;

namespace ChatTwo.Ui.Handler;

public sealed class PayloadHandler
{
    // 原生菜单打开失败回退：无 ContentId/World 的目标（如部队下线消息的玩家名）游戏会拒绝打开，
    // 触发后等若干帧菜单仍未显示 → 静默放弃 + 复位标志（避免"点一下没反应"）。
    private (Chunk chunk, Payload payload)? PendingNativeMenuFallback;
    private int NativeMenuFallbackFrames;

    private InputHandler InputHandler { get; }

    public bool HandleTooltips;
    public uint HoveredItem;
    public uint HoverCounter;
    public uint LastHoverCounter;

    private const uint PopupSfx = 1;

    public PayloadHandler(InputHandler inputHandler)
    {
        InputHandler = inputHandler;
    }

    public void Draw()
    {
        VerifyNativeMenuFallback();
        CheckPendingMenu();   // 左键延迟打开菜单（松开后触发）

        if (HandleTooltips && ++HoverCounter - LastHoverCounter > 1)
        {
            GameFunctions.GameFunctions.CloseItemTooltip();
            HoveredItem = 0;
            HoverCounter = LastHoverCounter = 0;
            HandleTooltips = false;
        }
    }

    private void VerifyNativeMenuFallback()
    {
        if (PendingNativeMenuFallback is not { } pending)
            return;

        // 菜单已显示 → 原生打开成功，取消回退
        if (Plugin.IsNativeContextMenuVisible())
        {
            PendingNativeMenuFallback = null;
            return;
        }

        // 数帧后仍未显示（游戏拒绝打开）→ 静默放弃 + 复位标志（不残留 NoMouseInputs 导致穿透）。
        if (--NativeMenuFallbackFrames <= 0)
        {
            PendingNativeMenuFallback = null;
            Plugin.ContextMenuActive = false;
            Plugin.ChatTwoMenuSession = false;
        }
    }

    public unsafe void Click(Chunk chunk, Payload? payload, ImGuiMouseButton button)
    {
        if (Plugin.Config.PlaySounds)
            UIGlobals.PlaySoundEffect(PopupSfx);

        switch (button)
        {
            // 消息区左右键功能一致；唯一区别是菜单打开时机：
            // - 右键：按下即打开（松开不触发菜单项）
            // - 左键：松开后打开（按下即打开会让松开落在菜单上触发菜单项/关闭 → 闪没）
            case ImGuiMouseButton.Left:
                HandlePayloadClick(chunk, payload, delayMenu: true);
                break;
            case ImGuiMouseButton.Right:
                HandlePayloadClick(chunk, payload, delayMenu: false);
                break;
        }
    }

    public void Hover(Payload payload)
    {
        var hoverSize = 350f * ImGuiHelpers.GlobalScale;

        switch (payload)
        {
            case StatusPayload status:
                DoHover(() => HoverStatus(status), hoverSize);
                break;
            case ItemPayload item:
                if (Plugin.Config.NativeItemTooltips)
                {
                    if (!HandleTooltips || HoveredItem != item.RawItemId)
                    {
                        HandleTooltips = true;
                        HoveredItem = item.RawItemId;
                        HoverCounter = LastHoverCounter = 0;

                        GameFunctions.GameFunctions.OpenItemTooltip(item.RawItemId, item.Kind);
                    }
                    else
                    {
                        LastHoverCounter = HoverCounter;
                    }

                    return;
                }

                DoHover(() => HoverItem(item), hoverSize);
                break;
            case UriPayload uri:
                DoHover(() => HoverUri(uri), hoverSize);
                break;
        }
    }

    private void DoHover(Action inside, float width)
    {
        ImGui.SetNextWindowSize(new Vector2(width, -1f));

        using (ImRaii.Tooltip())
        using (ImRaii.TextWrapPos(0.0f))
        using (ImRaii.PushColor(ImGuiCol.Text, InputHandler.Plugin.DefaultText))
            inside();
    }

    /// <summary>
    /// 打开一级菜单前调用：把 ContextMenu addon 的 BlockedParentId 设为 ChatLog。
    /// Dalamud 的 OnMenuOpened 事件（hook AtkModuleVf22OpenAddonByAgent detour）里：
    /// AddonName = GetAddonById(GetAddonByName("ContextMenu")->BlockedParentId)
    /// 原生右键 ChatLog 时该字段=ChatLog id（右键链设置）；ChatTwo 用 OpenContextMenu 模拟
    /// 不经过右键链，需手动设，否则 AddonName≠"ChatLog" → DR/Allagan 的 switch 不识别 →
    /// 道具/玩家菜单项不注入（DR"物品搜索/市场搜索"会缺失）。
    /// </summary>
    private static unsafe void SetContextMenuBlockedParentToChatLog()
    {
        try
        {
            var mgr = RaptureAtkModule.Instance()->RaptureAtkUnitManager;
            var ctx = mgr.GetAddonByName("ContextMenu");
            if (ctx != null)
                ctx->BlockedParentId = (ushort)GameFunctions.GameFunctions.GetChatLogAddonId();
        }
        catch (Exception ex) { Plugin.Log.Debug($"[NativeCtxMenu] SetBlockedParent error {ex.Message}"); }
    }

    private static void InlineIcon(IDalamudTextureWrap icon)

    {
        var cursor = ImGui.GetCursorPos();
        const int maxIconSize = 32;
        // Keep the icons aspect ratio while also shrinking it down so its at most 32px wide/tall
        var iconRatio = icon.Size.X / icon.Size.Y;
        var x = Math.Min(maxIconSize, (int) (maxIconSize * iconRatio));
        var y = Math.Min(maxIconSize, (int) (maxIconSize / iconRatio));
        var size = ImGuiHelpers.ScaledVector2(x, y);

        ImGui.Image(icon.Handle, size);
        ImGui.SameLine();
        ImGui.SetCursorPos(cursor + new Vector2(size.X + 4, size.Y - ImGui.GetTextLineHeightWithSpacing()));
    }

    private void HoverStatus(StatusPayload status)
    {
        if (Plugin.TextureProvider.GetFromGameIcon(status.Status.Value.Icon).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        var builder = new SeStringBuilder();
        var nameValue = status.Status.Value.Name.ToString();
        switch (status.Status.Value.StatusCategory)
        {
            case 1:
                builder.AddUiForeground($"{SeIconChar.Buff.ToIconString()}{nameValue}", 517);
                break;
            case 2:
                builder.AddUiForeground($"{SeIconChar.Debuff.ToIconString()}{nameValue}", 518);
                break;
            default:
                builder.AddUiForeground(nameValue, 1);
                break;
        }

        var name = ChunkUtil.ToChunks(builder.BuiltString, ChunkSource.None, null);
        InputHandler.ChunkHandler.DrawChunks(name.ToList());
        ImGui.Separator();

        var desc = ChunkUtil.ToChunks(status.Status.Value.Description.ToDalamudString(), ChunkSource.None, null);
        InputHandler.ChunkHandler.DrawChunks(desc.ToList());
    }

    private void HoverItem(ItemPayload item)
    {
        if (item.Kind == ItemKind.EventItem)
        {
            HoverEventItem(item);
            return;
        }

        if (!item.Item.TryGetValue(out Item resolvedItem))
            return;

        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(resolvedItem.Icon, item.IsHQ)).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        var name = ChunkUtil.ToChunks(resolvedItem.Name.ToDalamudString(), ChunkSource.None, null);
        InputHandler.ChunkHandler.DrawChunks(name.ToList());
        ImGui.Separator();

        var desc = ChunkUtil.ToChunks(resolvedItem.Description.ToDalamudString(), ChunkSource.None, null);
        InputHandler.ChunkHandler.DrawChunks(desc.ToList());
    }

    private void HoverEventItem(ItemPayload payload)
    {
        if (!Sheets.EventItemSheet.TryGetRow(payload.RawItemId, out var itemRow))
            return;

        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(itemRow.Icon)).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        var name = ChunkUtil.ToChunks(itemRow.Name.ToDalamudString(), ChunkSource.None, null);
        InputHandler.ChunkHandler.DrawChunks(name.ToList());
        ImGui.Separator();

        if (!Sheets.EventItemHelpSheet.TryGetRow(payload.RawItemId, out var itemHelpRow))
            return;

        InputHandler.ChunkHandler.DrawChunks(ChunkUtil.ToChunks(itemHelpRow.Description.ToDalamudString(), ChunkSource.None, null).ToList());
    }

    private void HoverUri(UriPayload uri)
    {
        ImGui.TextUnformatted(string.Format(Language.Context_URLDomain, uri.Uri.Authority));
        ImGuiUtil.WarningText(Language.Context_URLWarning);
    }

    /// <summary>
    /// 消息区 payload 点击统一入口（左右键共用，重构）。
    /// 玩家/道具 → 原生菜单（左键延迟到松开后打开，避免松开点击落在菜单上触发菜单项）；
    /// 其他 payload（地图/任务/招募/成就/链接）→ 对应动作，左右键一致。
    /// </summary>
    private void HandlePayloadClick(Chunk chunk, Payload? payload, bool delayMenu)
    {
        switch (payload)
        {
            case PlayerPayload player:
                if (delayMenu)
                {
                    PendingMenu = (chunk, player);
                    PendingMenuDownPos = ImGui.GetIO().MousePos;
                    break;
                }
                OpenPlayerContextMenu(chunk, player);
                break;
            case ItemPayload item:
                if (delayMenu)
                {
                    PendingMenu = (chunk, item);
                    PendingMenuDownPos = ImGui.GetIO().MousePos;
                    break;
                }
                OpenItemContextMenu(item);
                break;
            case MapLinkPayload map:
                Plugin.GameGui.OpenMapWithMapLink(map);
                break;
            case QuestPayload quest:
                GameFunctions.GameFunctions.OpenQuestLog(quest.Quest);
                break;
            case DalamudLinkPayload link:
                ClickLinkPayload(chunk, payload, link);
                break;
            case DalamudPartyFinderPayload pf:
                if (pf.LinkType == DalamudPartyFinderPayload.PartyFinderLinkType.PartyFinderNotification)
                    GameFunctions.GameFunctions.OpenPartyFinder();
                else
                    GameFunctions.GameFunctions.OpenPartyFinder(pf.ListingId);
                break;
            case ChatTwoPartyFinderPayload pf:
                GameFunctions.GameFunctions.OpenPartyFinder(pf.Id);
                break;
            case AchievementPayload achievement:
                GameFunctions.GameFunctions.OpenAchievement(achievement.Id);
                break;
            case RawPayload raw:
                if (Equals(raw, ChunkUtil.PeriodicRecruitmentLink))
                    GameFunctions.GameFunctions.OpenPartyFinder();
                break;
            case UriPayload uri:
                WrapperUtil.TryOpenUri(uri.Uri);
                break;
            // 未知 payload 静默（不弹原版 ImGui 菜单）
            default:
                break;
        }
    }

    /// <summary>左键延迟打开的菜单（等左键松开且未拖拽 → CheckPendingMenu 打开）。</summary>
    private (Chunk chunk, Payload payload)? PendingMenu;
    private Vector2 PendingMenuDownPos;

    /// <summary>左键"延迟打开菜单"的触发入口（每帧由 Draw 调用）。
    /// 关联：Click(Left) → HandlePayloadClick(delayMenu:true) 记录 PendingMenu + 按下位置；
    /// 本方法在左键松开时检查：按下→松开距离 < 5px（未拖拽选字）才真正打开菜单。
    /// 左键按下即打开会让松开落在菜单上触发菜单项/关闭 → 闪没（游戏原生即松开才开）。
    /// 右键不走此路径（delayMenu:false 直接打开）。
    /// </summary>
    private void CheckPendingMenu()
    {
        if (PendingMenu is not { } pending)
            return;
        // 其他菜单已激活 → 丢弃（避免覆盖）
        if (Plugin.ContextMenuActive || Plugin.ChatTwoMenuSession)
        {
            PendingMenu = null;
            return;
        }
        var io = ImGui.GetIO();
        if (io.MouseReleased[(int)ImGuiMouseButton.Left] && !io.MouseDown[(int)ImGuiMouseButton.Left])
        {
            PendingMenu = null;
            // 按下→松开距离 < 5px = 快速点击（未拖拽选择文本）
            if (Vector2.Distance(PendingMenuDownPos, io.MousePos) < 5f)
            {
                switch (pending.payload)
                {
                    case PlayerPayload player:
                        OpenPlayerContextMenu(pending.chunk, player);
                        break;
                    case ItemPayload item:
                        OpenItemContextMenu(item);
                        break;
                }
            }
        }
    }

    /// <summary>打开玩家原生菜单（成功则登记待验证目标，失败静默）。</summary>
    private void OpenPlayerContextMenu(Chunk chunk, PlayerPayload player)
    {
        if (TryShowNativePlayerContextMenu(chunk, player))
        {
            // 记录待验证目标：菜单若未在数帧内显示（游戏拒绝，如无 ContentId/World 的目标），
            // 由 VerifyNativeMenuFallback 静默放弃 + 复位标志。
            PendingNativeMenuFallback = (chunk, player);
            NativeMenuFallbackFrames = 10;
        }
    }

    /// <summary>打开道具原生菜单（失败静默，起不再回退 ImGui 弹窗）。</summary>
    private void OpenItemContextMenu(ItemPayload item)
    {
        TryShowNativeItemContextMenu(item);
    }

    /// <summary>
    /// 计算菜单位置：ExperimentalMenuFollowMouse=true → 跟随鼠标（clamp 屏幕内）；
    /// false → 聊天框右侧固定（旧逻辑备份）。
    /// SetPosition 用逻辑坐标（与 MoveTooltip 一致）；ImGui.GetWindowPos/GetWindowSize 在聊天框
    /// 渲染上下文中返回聊天框位置。打开前调用 menuW/menuH 传 0（仅定位），打开后传真实尺寸。
    /// </summary>
    private static void ComputeMenuPos(int mouseX, int mouseY, int menuW, int menuH, out int gameX, out int gameY)
    {
        var vp = ImGuiHelpers.MainViewport.Size;
        var vpW = (int)vp.X;
        var vpH = (int)vp.Y;
        if (Plugin.Config.ExperimentalMenuFollowMouse)
        {
            // 跟随鼠标（游戏原生跟手，clamp 到屏幕内）
            gameX = Math.Clamp(mouseX, 0, Math.Max(0, vpW - menuW));
            gameY = Math.Clamp(mouseY, 0, Math.Max(0, vpH - menuH));
        }
        else
        {
            // 聊天框右侧固定（旧逻辑备份）：默认放聊天框右侧 10px；右侧出屏翻转左侧；垂直/水平 clamp
            var chatPos = ImGui.GetWindowPos();
            var chatSize = ImGui.GetWindowSize();
            gameX = (int)(chatPos.X + chatSize.X + 10);
            gameY = (int)chatPos.Y;
            if (gameX + menuW > vpW)
                gameX = (int)(chatPos.X - 10 - menuW);
            gameY = (int)Math.Clamp(gameY, 0, Math.Max(0, vpH - menuH));
            gameX = (int)Math.Clamp(gameX, 0, Math.Max(0, vpW - menuW));
        }
    }

    private void ClickLinkPayload(Chunk chunk, Payload payload, DalamudLinkPayload link)
    {
        if (chunk.GetSeString() is not { } source)
            return;

        var start = source.Payloads.IndexOf(payload);
        if (start == -1)
        {
            // 历史消息（SQLite 加载）的 chunk.Link 是 MessagePack 反序列化实例，
            // 与 ContentSource.Payloads 的元素不是同一引用，而 DalamudLinkPayload 未重写
            // Equals（引用比较）→ IndexOf 返回 -1 → 点击无效。
            // 回退：按 Plugin+CommandId 语义匹配链接起点。
            for (var i = 0; i < source.Payloads.Count; i++)
            {
                if (source.Payloads[i] is DalamudLinkPayload dl && dl.Plugin == link.Plugin && dl.CommandId == link.CommandId)
                {
                    start = i;
                    break;
                }
            }
        }
        if (start == -1)
            return;

        if (!Plugin.ChatGui.RegisteredLinkHandlers.TryGetValue((link.Plugin, link.CommandId), out var value))
        {
            Plugin.Log.Warning("Could not find DalamudLinkHandlers");
            return;
        }

        try
        {
            // 传完整消息而非链接段：原生点击时 handler 收到整条 SeString，而 OmniToolbox 的
            // 传送点链接结构是 [MapLink(坐标@消息开头) ... DalamudLink(传送点)]——坐标在链接段外，
            // 上游只传段导致 handler 解析不到坐标 → 传送不发起。
            // try 必须包在 RunOnTick 回调内部：RunOnTick 只是注册回调，value.Invoke 在下一帧执行，
            // 包在外面捕获不到 handler 异常（上游 bug，异常会被吞掉）。
            Plugin.Framework.RunOnTick(() =>
            {
                try
                {
                    value.Invoke(link.CommandId, source);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Error executing DalamudLinkPayload handler");
                }
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error executing DalamudLinkPayload handler");
        }
    }

    /// <summary>
    /// 触发原生玩家右键菜单（左右键共用的打开路径，由 Click → HandlePayloadClick → OpenPlayerContextMenu 调用）。
    /// 设置 AgentContext 目标数据后，用 OpenContextMenu 确保菜单实际显示（游戏内部重新定位 → 原生跟手）。
    /// 位置：打开前用 ComputeMenuPos 计算并 SetPosition（跟随鼠标 / 聊天框右侧固定）；
    /// 打开后不再每帧控制位置（由游戏原生跟手）。
    /// 菜单打开期间聊天框的点击穿透（NoMouseInputs）与挖洞（RenderHole）在 ChatLog.Window / RenderHole 处理。
    /// 若游戏拒绝打开（无 ContentId/World 的目标），由 VerifyNativeMenuFallback 数帧后静默放弃并复位标志。
    /// </summary>
    /// <returns>true 表示成功触发原生菜单，false 表示需要回退到 ImGui 弹窗</returns>

    private unsafe bool TryShowNativePlayerContextMenu(Chunk chunk, PlayerPayload player)
    {
        try
        {
            var agent = AgentContext.Instance();
            if (agent == null)
                return false;

            var validContentId = chunk.Message?.ContentId is not (null or 0);
            var world = player.World;
            var playerName = player.PlayerName;

            // 查找玩家对象（用于 TargetObjectId 和有效性检查）
            var foundChar = FindCharacterForPayload(player);
            // 只要有名字就尝试原生菜单——即使没有 ContentId/World/不在场
            // （如"部队成员下线"消息的玩家名，payload 只带名字）。
            // 游戏可能拒绝打开（目标无效），由 VerifyNativeMenuFallback 数帧后回退 ImGui 弹窗。
            if (string.IsNullOrEmpty(playerName))
            {
                Plugin.Log.Debug($"[NativeCtxMenu] Skipping player - empty name");
                return false;
            }

            Plugin.Log.Debug($"[NativeCtxMenu] Triggering player menu for '{playerName}' ContentId={chunk.Message?.ContentId ?? 0}");

            // 设置 ContextMenuHandler 静态上下文（OnMenuOpened 回调中读取）
            GameFunctions.ContextMenuHandler.CurrentTargetType = GameFunctions.ContextMenuHandler.MenuTargetType.Player;
            GameFunctions.ContextMenuHandler.CurrentChatType = chunk.Message?.Code.Type;
            GameFunctions.ContextMenuHandler.CurrentContentId = chunk.Message?.ContentId ?? 0;
            GameFunctions.ContextMenuHandler.CurrentMessageContent = chunk.Message?.ContentSource;
            GameFunctions.ContextMenuHandler.IsChatTwoTriggered = true;

            // 清除上次道具菜单的 ContextItemId 残留，防止 OnMenuOpened 误判为道具菜单
            // 同时清除 LinkedItem.LinkedItemQuality：DR 检查 *(uint*)(agent + 0x950)==3 跳过玩家菜单，
            // 0x950 即 LinkedItemQuality，残留 3（收藏品）会导致 DR 误判。
            // 还要清 [0x9C8]：道具菜单写入 *(uint*)(agent+0x9c8)=3，InventoryTools 用
            // GetObjectItemId("ChatLog",2504)==3 判断是否道具菜单，残留 3 会让玩家菜单被误判。
            unsafe
            {
                var chatLogAgent = AgentChatLog.Instance();
                if (chatLogAgent != null)
                {
                    chatLogAgent->ContextItemId = 0;
                    chatLogAgent->LinkedItem.LinkedItemQuality = 0;
                    *(uint*)((byte*)chatLogAgent + 0x9c8) = 0;
                }
            }

            // 计算菜单位置（跟随鼠标 vs 聊天框右侧固定）。
            // SetPosition 用逻辑坐标（与 MoveTooltip 一致），ImGui MousePos 即逻辑坐标，直接使用。
            var mousePos = ImGui.GetIO().MousePos;
            ComputeMenuPos((int)mousePos.X, (int)mousePos.Y, 0, 0, out var gameX, out var gameY);

            Plugin.Log.Debug($"[NativeCtxMenu] Menu position: ({gameX}, {gameY}), mouse=({mousePos.X:F0},{mousePos.Y:F0})");

            // 标记菜单激活，ChatLog.MoveContextMenu 会在 PreDraw 中移动菜单
            // （FrameworkUpdate 兜底复位用）
            Plugin.ContextMenuActive = true;
            Plugin.ContextMenuActivatedAt = Environment.TickCount64;
            // ChatTwo 菜单会话开始（二级菜单 MoveContextSubMenu 用它区分聊天框/背包等来源）
            Plugin.ChatTwoMenuSession = true;
            Plugin.ChatTwoMenuSessionAt = Environment.TickCount64;

            // 设置菜单位置提示
            agent->SetPosition(gameX, gameY);

            // SetChatInteractable(true)：原生聊天框必须始终隐藏，
            // 菜单在隐藏状态下可正常打开

            // 清除上次菜单的残留原生菜单项，防止显示缓存内容。
            // 顺带清掉 AgentContext 的 TargetContentId/TargetHomeWorldId！
            // 所以目标字段必须在 ClearMenu 之后设置。
            agent->ClearMenu();

            // ── 设置目标数据（必须在 ClearMenu 之后！）──
            if (validContentId)
                agent->TargetContentId = chunk.Message!.ContentId;
            // 设置目标账号 ID：TargetAccountId=0 会导致游戏生成 0 个原生 subcommand →
            // 注入"没有可以选择的指令"占位符（原生右键时游戏会填此字段）。
            // 账号 ID 从聊天消息拿（ContentIdResolver hook 已存进 Message.AccountId）
            if (chunk.Message is { AccountId: > 0 })
                agent->TargetAccountId = chunk.Message.AccountId;
            // TargetHomeWorldId 直接用世界行 ID：
            // 原生游戏玩家菜单此字段就是玩家的世界 ID（需 RowId != 0）
            agent->TargetHomeWorldId = (short)world.RowId;

            // 设置目标对象ID（游戏原生菜单构建器需要此字段判断玩家在场状态）
            if (foundChar != null)
                agent->TargetObjectId.ObjectId = foundChar.EntityId;

            // 设置目标名字
            if (!string.IsNullOrEmpty(playerName))
            {
                var nameBytes = Encoding.UTF8.GetBytes(playerName);
                fixed (byte* ptr = nameBytes)
                {
                    agent->TargetName.SetString(ptr);
                }
            }

            // ContextMenuTarget（InfoProxyCommonList.CharacterData）。
            // 游戏过滤读"右键目标内部状态"（原生右键链写入，插件触发缺失），ContextMenuTarget 对过滤无帮助也无害。
            var ctxTarget = &agent->ContextMenuTarget;
            ctxTarget->ContentId = chunk.Message?.ContentId ?? 0;
            if (chunk.Message is { AccountId: > 0 })
                ctxTarget->AccountId = chunk.Message.AccountId;
            ctxTarget->HomeWorld = (ushort)world.RowId;
            ctxTarget->CurrentWorld = (ushort)world.RowId;
            ctxTarget->State = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCommonList.CharacterData.OnlineStatus.Online;
            // Name（固定缓冲 @0x32，32 字节）：用指针直接写，避免只读属性限制
            if (!string.IsNullOrEmpty(playerName))
            {
                var nameBytes = Encoding.UTF8.GetBytes(playerName + "\0");
                fixed (byte* ptr = nameBytes)
                {
                    Buffer.MemoryCopy(ptr, (byte*)ctxTarget + 0x32, 32, Math.Min(nameBytes.Length, 32));
                }
            }
            // ：
            // 触发时设 ChatLog → OnMenuOpened 时 AddonName="ChatLog"（DR/Allagan 的 switch 靠它
            // 识别聊天框菜单，缺失则"物品搜索/市场搜索"等道具项不注入）。
            // 但 owner=ChatLog 会让插件二级菜单在 OpenAddon 时绑定 ChatLog → 隐藏即关。
            // 解法：MoveContextMenu（一级菜单 PreDraw）在注入完成后每帧清零 OwnerAddon →
            // 点二级菜单时 owner 已是 0 → 不绑定 → 显示正常。
            // 重开一级菜单时 ContextMenuHandler 会重新设回 ChatLog（保证重开也能注入）。
            agent->OwnerAddon = GameFunctions.GameFunctions.GetChatLogAddonId();

            // 完整复刻原生玩家菜单（eventId 来自 CurrentContextMenu+0x448 反汇编）。
            // eventId 对照：1=发送悄悄话 102=切换频道回复 12=组队邀请 75=好友申请 70=邀请新人
            // 48=队员招募 68=选中 69=从新人频道移除 8=查看铭牌。
            // eventId 是语义型，游戏按 eventId+目标状态自动过滤不适用项。
            agent->AddContextMenuItem(1, Language.Context_SendTell);
            agent->AddContextMenuItem(102, Language.Context_ReplyInSelectedChatMode);
            agent->AddContextMenuItem(12, Language.Context_InviteToParty);
            // ：游戏过滤在插件场景不生效 → 自己判断目标 ContentId 是否在好友列表（在则不显示）。
            var friendCid = chunk.Message?.ContentId ?? 0;
            var isFriend = friendCid != 0
                && GameFunctions.GameFunctions.GetFriends().Any(f => f.ContentId == friendCid);
            if (!isFriend)
                agent->AddContextMenuItem(75, Language.Context_SendFriendRequest);
            agent->AddContextMenuItem(70, Language.Context_InviteToNoviceNetwork);
            // 屏蔽机能：原生子菜单绑定右键事件流（不可控），改 C 前缀"屏蔽机能"子菜单项
            // （ContextMenuHandler 用 Dalamud OpenSubmenu 展开，绕开右键事件流）。
            // 子项：加入黑名单 / 加入屏蔽名单 / 记录屏蔽词（插件 handler）。
            agent->AddContextMenuItem(48, Language.Context_ViewRecruitment);
            agent->AddContextMenuItem(68, Language.Context_Target);
            agent->AddContextMenuItem(69, Language.Context_LeaveNoviceNetwork);
            agent->AddContextMenuItem(8, Language.Context_AdventurerPlate);

            // 打开菜单：用 OpenContextMenu（游戏原生流程），不用 OpenContextMenuForAddon（事件端字段被清）。
            // BlockedParentId=ChatLog：OnMenuOpened 里 AddonName=GetAddonById(BlockedParentId)，
            // ChatTwo 模拟不经过右键链需手动设，否则 AddonName≠"ChatLog" → 菜单项不注入。
            SetContextMenuBlockedParentToChatLog();
            // bindToOwner 必须为 false：原生聊天框全程隐藏，绑定到 owner(ChatLog) 会被游戏立即关闭。
            agent->OpenContextMenu(false, false);

            // 立即设置菜单位置，防止闪烁（PreDraw 要到下一帧才执行）。
            // （菜单会超出屏幕）：读 addon 渲染尺寸（×RootNode 缩放），按开关模式设置。
            try
            {
                var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
                if (ctxAddon != null && ctxAddon->IsReady)
                {
                    ushort w, h;
                    ctxAddon->GetSize(&w, &h, false);
                    var root = ctxAddon->RootNode;
                    var sx = root != null ? root->ScaleX : 1f;
                    var sy = root != null ? root->GetScaleY() : 1f;
                    var menuW = (int)(w * sx);
                    var menuH = (int)(h * sy);
                    ComputeMenuPos(gameX, gameY, menuW, menuH, out gameX, out gameY);
                    ctxAddon->SetPosition((short)gameX, (short)gameY);
                }
            }
            catch { /* ignore */ }

            Plugin.Log.Debug($"[NativeCtxMenu] OpenContextMenu for player '{playerName}'");

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[NativeCtxMenu] Error triggering player menu: {ex}");
            return false;
        }
    }

    /// <summary>
    /// 触发原生道具右键菜单。
    /// 设置 AgentChatLog.ContextItemId 后，先用 ReceiveEvent 触发游戏事件链
    /// （让 DR 等 OnMenuOpened 插件有机会添加菜单项），再用 OpenContextMenuForAddon
    /// 确保菜单实际显示。
    /// </summary>
    /// <returns>true 表示成功触发原生菜单，false 表示需要回退到 ImGui 弹窗</returns>
    private unsafe bool TryShowNativeItemContextMenu(ItemPayload item)
    {
        try
        {
            var agent = AgentContext.Instance();
            if (agent == null)
                return false;

            var chatLog = AgentChatLog.Instance();
            if (chatLog == null)
                return false;

            var itemId = item.RawItemId;
            Plugin.Log.Debug($"[NativeCtxMenu] Triggering item menu for itemId={itemId}");

            // 清除玩家菜单的静态上下文，设置道具菜单标志
            GameFunctions.ContextMenuHandler.CurrentTargetType = GameFunctions.ContextMenuHandler.MenuTargetType.Item;
            GameFunctions.ContextMenuHandler.CurrentChatType = null;
            GameFunctions.ContextMenuHandler.CurrentContentId = 0;
            GameFunctions.ContextMenuHandler.CurrentItemId = itemId;
            GameFunctions.ContextMenuHandler.IsChatTwoTriggered = true;

            // 设置 ContextItemId（关键：DailyRoutines 等插件依赖此字段识别道具）
            chatLog->ContextItemId = itemId;

            // 清除 AgentContext 玩家字段残留，防止 DR 等插件在道具菜单上添加玩家菜单项
            // （TargetName/TargetContentId 等不会在切换菜单类型时自动清除）
            agent->TargetContentId = 0;
            agent->TargetHomeWorldId = 0;
            agent->TargetObjectId.ObjectId = 0;
            var emptyName = new byte[] { 0 };
            fixed (byte* emptyPtr = emptyName)
            {
                agent->TargetName.SetString(emptyPtr);
            }

            // （同玩家菜单）：触发时设 ChatLog 供 OnMenuOpened 识别，
            // MoveContextMenu 每帧清零防二级菜单绑定。
            // 必须在 ClearMenu 之后设置！ClearMenu 会清掉目标字段区（含 OwnerAddon），
            // 先设会被清 0 → OnMenuOpened 时 AddonName 不是 ChatLog → DR 道具项不注入。

            // 计算菜单位置（跟随鼠标 vs 聊天框右侧固定）。
            // SetPosition 用逻辑坐标（与 MoveTooltip 一致），ImGui MousePos 即逻辑坐标，直接使用。
            var mousePos = ImGui.GetIO().MousePos;
            ComputeMenuPos((int)mousePos.X, (int)mousePos.Y, 0, 0, out var gameX, out var gameY);

            Plugin.Log.Debug($"[NativeCtxMenu] Item menu position: ({gameX}, {gameY}), mouse=({mousePos.X:F0},{mousePos.Y:F0})");

            // 标记菜单激活，ChatLog.MoveContextMenu 会在 PreDraw 中移动菜单
            // （FrameworkUpdate 兜底复位用）
            Plugin.ContextMenuActive = true;
            Plugin.ContextMenuActivatedAt = Environment.TickCount64;
            // ChatTwo 菜单会话开始（二级菜单 MoveContextSubMenu 用它区分聊天框/背包等来源）
            Plugin.ChatTwoMenuSession = true;
            Plugin.ChatTwoMenuSessionAt = Environment.TickCount64;

            // 设置菜单位置提示
            agent->SetPosition(gameX, gameY);

            // SetChatInteractable(true)：原生聊天框必须始终隐藏

            // 清除上次菜单的残留原生菜单项（同玩家菜单逻辑）
            agent->ClearMenu();

            // 在 ClearMenu 之后设置（ClearMenu 会清掉它，见上方注释）
            agent->OwnerAddon = GameFunctions.GameFunctions.GetChatLogAddonId();

            // 用 AddMenuItem + AgentChatLog 本体作 handler（根除快照）。
            // 是游戏常驻 agent，永远非空 → 无需快照/缓存/预热。
            // 字段：ACL[0x20]=0x51(addon id) ACL[0x9c0]=道具ID ACL[0x9c8]=0x3(类型) ACL[0x860]=0x6
            // 命令 ID 语义：0=属性对比 1=试穿 2=持有情况 3=展示 5=复制名 6=制作 9=幻影化
            var itemNativeAdded = false;
            var chatLogAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentChatLog.Instance();
            var handlerSnapshot = chatLogAgent != null ? (nint)chatLogAgent : 0;
            if (handlerSnapshot != 0 && item.Kind != ItemKind.EventItem && Sheets.ItemSheet.TryGetRow(item.ItemId, out var itemRow))
            {
                var itemHandler = (FFXIVClientStructs.FFXIV.Component.GUI.AtkModuleInterface.AtkEventInterface*)handlerSnapshot;
                var isEquipment = itemRow.EquipSlotCategory.RowId != 0;
                var items = new List<(string Text, int Cmd)>();
                if (isEquipment)
                {
                    // 装备（7 项）：对比/试穿/持有/展示/制作/幻影化/复制
                    items.Add((Language.Context_ItemComparison, 0x0000)); // 装备属性对比
                    items.Add((Language.Context_TryOn, 0x0001));          // 试穿
                    items.Add((Language.Context_SearchForItem, 0x0002));  // 查看持有情况
                    items.Add((Language.Context_Link, 0x0003));           // 展示道具属性
                    items.Add((Language.Context_SearchRecipes, 0x0006));  // 查看能制作什么
                    // 套装幻影化仅"无属性纯外观时装"才有（套装 = 一套时装，可打包套装幻影化）。
                    // 判定 = 无主属性(BaseParamValue 全 0) && 无攻击力(DamagePhys/Mag==0)。
                    // 反例：风化短剑（1 级武器 DamagePhys=8）、末世终迹套（有主属性）。
                    // 不是 LevelEquip==1（风化短剑也 1 级）；不是 IsGlamorous（几乎所有装备都可投影）。
                    if (itemRow.BaseParamValue.All(v => v == 0)
                        && itemRow.DamagePhys == 0
                        && itemRow.DamageMag == 0)
                        items.Add((Language.Context_ViewItemSet, 0x0009));    // 查看套装幻影化
                    items.Add((Language.Context_CopyItemName, 0x0005));   // 复制道具名（[self+0x9c0] 已写为当前道具 ID，动作可正常读）
                }
                else
                {
                    // 非装备（材料/杂货/家具等，4 项）：持有/展示/制作/复制
                    items.Add((Language.Context_SearchForItem, 0x0002));  // 查看持有情况
                    items.Add((Language.Context_Link, 0x0003));           // 展示道具属性
                    items.Add((Language.Context_SearchRecipes, 0x0006));  // 查看能制作什么
                    items.Add((Language.Context_CopyItemName, 0x0005));   // 复制道具名（[self+0x9c0] 已写为当前道具 ID，动作可正常读）
                }

                if (items.Count > 0)
                {
                    // 读"道具上下文"对象的 [self+0x9c0]（道具 ID），而非 ChatLog.ContextItemId。
                    // self = AgentChatLog；[0x9c0] 存道具 ID。把 [0x9c0] 写 RawItemId，[0x9c8] 写类型=3
                    //（反汇编 0xed8bdd：类型必须 ==3 才走复制路径）。
                    *(uint*)((byte*)handlerSnapshot + 0x9c0) = item.RawItemId;
                    *(uint*)((byte*)handlerSnapshot + 0x9c8) = 3;

                    foreach (var it in items)
                        agent->AddMenuItem(it.Text, itemHandler, 0x10000L | (uint)it.Cmd);
                    itemNativeAdded = true;
                }
            }

            GameFunctions.ContextMenuHandler.NativeItemMenuAdded = itemNativeAdded;

            // OpenContextMenuForAddon！它内部强制 closeExisting=true，会调 [vtable+0x28]
            // 清空手动加的项 → 占位符。改用 OpenContextMenu(false,false)（closeExisting=false 不清项）。
            // BlockedParentId=ChatLog（同玩家菜单，DR/Allagan 注入识别用）
            SetContextMenuBlockedParentToChatLog();
            agent->OpenContextMenu(false, false);

            // 立即设置菜单位置，防止闪烁
            // （菜单会超出屏幕）+ 开关模式
            try
            {
                var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
                if (ctxAddon != null && ctxAddon->IsReady)
                {
                    ushort w, h;
                    ctxAddon->GetSize(&w, &h, false);
                    var root = ctxAddon->RootNode;
                    var sx = root != null ? root->ScaleX : 1f;
                    var sy = root != null ? root->GetScaleY() : 1f;
                    var menuW = (int)(w * sx);
                    var menuH = (int)(h * sy);
                    ComputeMenuPos(gameX, gameY, menuW, menuH, out gameX, out gameY);
                    ctxAddon->SetPosition((short)gameX, (short)gameY);
                }
            }
            catch { /* ignore */ }

            Plugin.Log.Debug($"[NativeCtxMenu] OpenContextMenuForAddon for itemId={itemId}");

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[NativeCtxMenu] Error triggering item menu: {ex}");
            return false;
        }
    }

    private IPlayerCharacter? FindCharacterForPayload(PlayerPayload payload)
    {
        var worldId = payload.World.RowId;

        // 优先家服精确匹配，避免同名玩家误选
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter character)
                continue;

            if (character.Name.TextValue == payload.PlayerName && character.HomeWorld.RowId == worldId)
                return character;
        }

        // 回退：仅按名字匹配（覆盖跨区旅行者——家服不同但在场的情况）
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter character)
                continue;

            if (character.Name.TextValue == payload.PlayerName)
                return character;
        }

        return null;
    }

    private static bool IsFriend(string playerName, ushort worldId)
    {
        try
        {
            var friends = GameFunctions.GameFunctions.GetFriends();
            foreach (var friend in friends)
            {
                if (friend.HomeWorld == worldId)
                {
                    var nameSpan = friend.Name;
                    var nullIdx = nameSpan.IndexOf((byte)0);
                    var nameStr = System.Text.Encoding.UTF8.GetString(
                        nullIdx >= 0 ? nameSpan.Slice(0, nullIdx) : nameSpan);
                    if (nameStr == playerName)
                        return true;
                }
            }
        }
        catch
        {
            // 好友列表访问失败，默认为非好友
        }
        return false;
    }
}
