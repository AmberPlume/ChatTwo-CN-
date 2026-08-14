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
    private readonly string PopupId = "chat2-context-popup";

    // 原生菜单打开失败时的回退验证：
    // 有些目标（如部队下线消息的玩家名）没有 ContentId/World，游戏可能拒绝打开原生菜单。
    // 触发后等若干帧，若菜单始终未显示则回退到 ImGui 弹窗（避免"点一下没反应"）。
    private (Chunk chunk, Payload payload)? PendingNativeMenuFallback;
    private int NativeMenuFallbackFrames;

    private InputHandler InputHandler { get; }
    private (Chunk, Payload?)? Popup { get; set; }

    public bool HandleTooltips;
    public uint HoveredItem;
    public uint HoverCounter;
    public uint LastHoverCounter;

    private const uint PopupSfx = 1;

    public PayloadHandler(InputHandler inputHandler)
    {
        InputHandler = inputHandler;
        PopupId += inputHandler.InputHandlerId;
    }

    public void Draw()
    {
        VerifyNativeMenuFallback();
        DrawPopups();

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

        // 数帧后仍未显示（游戏拒绝打开）→ 回退 ImGui 弹窗
        if (--NativeMenuFallbackFrames <= 0)
        {
            PendingNativeMenuFallback = null;
            Popup = pending;
            ImGui.OpenPopup(PopupId);
        }
    }

    private void DrawPopups()
    {
        if (Popup == null)
            return;

        var (chunk, payload) = Popup.Value;

        using var popup = ImRaii.Popup(PopupId);
        if (!popup.Success)
        {
            Popup = null;
            return;
        }

        using var id = ImRaii.PushId(PopupId);
        var drawn = false;
        switch (payload)
        {
            case PlayerPayload player:
                DrawPlayerPopup(chunk, player);
                drawn = true;
                break;
            case ItemPayload item:
                DrawItemPopup(item);
                drawn = true;
                break;
            case UriPayload uri:
                DrawUriPopup(uri);
                drawn = true;
                break;
            case StatusPayload status:
                DrawStatusPopup(status);
                drawn = true;
                break;
        }

        ContextFooter(drawn, chunk);
        Integrations(chunk, payload);
    }

    private void Integrations(Chunk chunk, Payload? payload)
    {
        var registered = InputHandler.Plugin.Ipc.Registered;
        if (registered.Count == 0)
            return;

        ImGui.Separator();

        var contentId = chunk.Message?.ContentId ?? 0;
        var sender = chunk.Message?.Sender.Select(c => c.Link).FirstOrDefault(p => p is PlayerPayload) as PlayerPayload;

        using var menu = ImRaii.Menu(Language.Context_Integrations);
        if (!menu.Success)
            return;

        var cursor = ImGui.GetCursorPos();
        foreach (var id in registered)
        {
            try
            {
                InputHandler.Plugin.Ipc.Invoke(id, sender, contentId, payload, chunk.Message?.SenderSource, chunk.Message?.ContentSource);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error executing integration");
            }
        }

        if (cursor == ImGui.GetCursorPos())
        {
            using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.Text("No integrations available");
        }
    }

    private void ContextFooter(bool didCustomContext, Chunk chunk)
    {
        ImRaii.MenuDisposable menu = default;
        if (didCustomContext)
        {
            ImGui.Separator();

            // Only place these menu items in a submenu if we've already drawn
            // custom context menu items based on the payload.
            //
            // It makes it much more convenient in the majority of cases to
            // copy the message content without having to open a submenu.
            menu = ImRaii.Menu(Plugin.PluginName);
            if (!menu.Success)
                return;
        }

        if (ImGui.Selectable(Language.Context_HideChat))
            InputHandler.MainWindow.CurrentHideState = HideState.User;

        if (chunk.Message is { } message)
        {
            if (ImGui.Selectable(Language.Context_Copy))
            {
                ImGui.SetClipboardText(StringifyMessage(message, true));
                WrapperUtil.AddNotification(Language.Context_CopySuccess, NotificationType.Info);
            }

            // Only show a separate "Copy content" option if the message has
            // Sender chunks, so it doesn't show for system messages.
            if (message.Sender.Count > 0 && ImGui.Selectable(Language.Context_CopyContent))
            {
                ImGui.SetClipboardText(StringifyMessage(message));
                WrapperUtil.AddNotification(Language.Context_CopyContentSuccess, NotificationType.Info);
            }

            using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled]);
            ImGui.TextUnformatted(message.Code.Type.Name());
        }

        menu.Dispose();
    }

    private static string StringifyMessage(Message? message, bool withSender = false)
    {
        if (message == null)
            return string.Empty;

        var chunks = withSender ? message.Sender.Concat(message.Content) : message.Content;
        return chunks.Where(chunk => chunk is TextChunk)
            .Cast<TextChunk>()
            .Select(text => text.Content)
            .Aggregate(string.Concat);
    }

    public unsafe void Click(Chunk chunk, Payload? payload, ImGuiMouseButton button)
    {
        if (Plugin.Config.PlaySounds)
            UIGlobals.PlaySoundEffect(PopupSfx);

        switch (button)
        {
            case ImGuiMouseButton.Left:
                LeftClickPayload(chunk, payload);
                break;
            case ImGuiMouseButton.Right:
                RightClickPayload(chunk, payload);
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
    ///   AddonName = GetAddonById(GetAddonByName("ContextMenu")->BlockedParentId)
    /// 原生右键 ChatLog 时该字段=ChatLog id（右键链设置）；ChatTwo 用 OpenContextMenu 模拟
    /// 不经过右键链，需手动设，否则 AddonName≠"ChatLog" → DR/Allagan 的 switch 不识别 →
    /// 道具/玩家菜单项不注入（用户实测 DR"物品搜索/市场搜索"缺失）。
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

    private void LeftClickPayload(Chunk chunk, Payload? payload)
    {
        switch (payload)
        {
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
            // 左键点击有 payload 的内容（玩家/道具等）→ 与右键一致弹菜单（模拟原生聊天框：
            // 原生左键点玩家名同样弹菜单）。原版为迁就文本选择 TEMPORARILY DISABLED 了 default，
            // 现按需求恢复；文本选择保留给"点击空白处"（见 DrawMessageLog 点击处理）。
            default:
                RightClickPayload(chunk, payload);
                break;
        }
    }

    private void ClickLinkPayload(Chunk chunk, Payload payload, DalamudLinkPayload link)
    {
        if (chunk.GetSeString() is not { } source)
            return;

        var start = source.Payloads.IndexOf(payload);
        var end = source.Payloads.IndexOf(RawPayload.LinkTerminator, start == -1 ? 0 : start);
        if (start == -1 || end == -1)
            return;

        var payloads = source.Payloads.Skip(start).Take(end - start + 1).ToList();
        if (!Plugin.ChatGui.RegisteredLinkHandlers.TryGetValue((link.Plugin, link.CommandId), out var value))
        {
            Plugin.Log.Warning("Could not find DalamudLinkHandlers");
            return;
        }

        try
        {
            // Running XivCommon SendChat instantly, without RunOnTick, leads to a game freeze, for whatever reason
            Plugin.Framework.RunOnTick(() => value.Invoke(link.CommandId, new SeString(payloads)));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error executing DalamudLinkPayload handler");
        }
    }

    private void RightClickPayload(Chunk chunk, Payload? payload)
    {
        switch (payload)
        {
            case PlayerPayload player:
                // 尝试触发原生玩家右键菜单
                if (TryShowNativePlayerContextMenu(chunk, player))
                {
                    // 记录待验证目标：菜单若未在数帧内显示（游戏拒绝，如无 ContentId/World 的目标），
                    // 由 VerifyNativeMenuFallback 回退到 ImGui 弹窗
                    PendingNativeMenuFallback = (chunk, payload);
                    NativeMenuFallbackFrames = 10;
                    return;
                }
                // 如果触发失败（如玩家名也为空），回退到 ImGui 弹窗（仅显示基本功能）
                Popup = (chunk, payload);
                ImGui.OpenPopup(PopupId);
                break;
            case ItemPayload item:
                // 尝试触发原生道具右键菜单
                if (TryShowNativeItemContextMenu(item))
                    return;
                // 回退
                Popup = (chunk, payload);
                ImGui.OpenPopup(PopupId);
                break;
            default:
                Popup = (chunk, payload);
                ImGui.OpenPopup(PopupId);
                break;
        }
    }

    /// <summary>
    /// 触发原生玩家右键菜单。
    /// 设置 AgentContext 目标数据后，先用 ReceiveEvent 触发游戏事件链（让 DR 等
    /// OnMenuOpened 插件有机会添加菜单项），再用 OpenContextMenu 确保菜单实际显示。
    /// 位置通过 AddonLifecycle.PreDraw 持续覆盖到聊天框右侧。
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
            // 同时清除 LinkedItem.LinkedItemQuality：
            // DR 的 ExpandPlayerMenuSearch 检查 *(uint*)(agent + 0x950) == 3 来跳过玩家菜单，
            // 0x950 正是 LinkedItemQuality 的位置。残留的链接道具品质（3=收藏品）会导致 DR 误判。
            unsafe
            {
                var chatLogAgent = AgentChatLog.Instance();
                if (chatLogAgent != null)
                {
                    chatLogAgent->ContextItemId = 0;
                    chatLogAgent->LinkedItem.LinkedItemQuality = 0;
                }
            }

            // 计算菜单位置：放在聊天框右侧（游戏UI坐标）
            var chatPos = ImGui.GetWindowPos();
            var chatSize = ImGui.GetWindowSize();
            // SetPosition 用逻辑坐标（与 MoveContextMenu/MoveTooltip 一致），不要除以 globalScale
            var gameX = (int)(chatPos.X + chatSize.X + 10);
            var gameY = (int)chatPos.Y;

            Plugin.Log.Debug($"[NativeCtxMenu] Menu position: ({gameX}, {gameY}), chatPos=({chatPos.X:F0},{chatPos.Y:F0}), chatSize=({chatSize.X:F0},{chatSize.Y:F0})");

            // 标记菜单激活，ChatLog.MoveContextMenu 会在 PreDraw 中移动菜单
            Plugin.ContextMenuActive = true;
            // ChatTwo 菜单会话开始（二级菜单 MoveContextSubMenu 用它区分聊天框/背包等来源）
            Plugin.ChatTwoMenuSession = true;

            // 设置菜单位置提示
            agent->SetPosition(gameX, gameY);

            // ⚠️ 不调用 SetChatInteractable(true)：原生聊天框必须始终隐藏（用户要求），
            // 菜单在隐藏状态下可正常打开

            // 清除上次菜单的残留原生菜单项，防止显示缓存内容。
            // ⚠️ ClearMenu 会顺带清掉 AgentContext 的 TargetContentId/TargetHomeWorldId！
            // 所以目标字段必须在 ClearMenu 之后设置（DIAG 证实：设置后过 ClearMenu 会变 0）
            agent->ClearMenu();

            // ── 设置目标数据（必须在 ClearMenu 之后！）──
            if (validContentId)
                agent->TargetContentId = chunk.Message!.ContentId;
            // 设置目标账号 ID：TargetAccountId=0 会导致游戏生成 0 个原生 subcommand →
            // 注入"没有可以选择的指令"占位符（原生右键时游戏会填此字段，我们之前漏了）。
            // 账号 ID 从聊天消息拿（ContentIdResolver hook 已存进 Message.AccountId）
            if (chunk.Message is { AccountId: > 0 })
                agent->TargetAccountId = chunk.Message.AccountId;
            // TargetHomeWorldId 直接用世界行 ID：
            // 原生游戏玩家菜单此字段就是玩家的世界 ID（DR 要求 RowId != 0）
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

            // ⚠️ 关键：填充 ContextMenuTarget（InfoProxyCommonList.CharacterData）。
            // 实验 08:06 证实：注释后游戏过滤也未恢复 → 游戏过滤读的是"右键目标内部状态"（原生右键链写入，
            // 插件触发缺失，8-13 探针"生效"是残留假象）→ ContextMenuTarget 对过滤无帮助也无害（保留）。
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
            // ⚠️ OwnerAddon 时分复用（2026-08-14 23:49 修订，替代 15:17 的恒 0 方案）：
            //   触发时设 ChatLog → OnMenuOpened 时 AddonName="ChatLog"（DR/Allagan 的 switch 靠它
            //   识别聊天框菜单，缺失则"物品搜索/市场搜索"等道具项不注入——用户实测回归）。
            //   但 owner=ChatLog 会让插件二级菜单在 OpenAddon 时绑定 ChatLog → 隐藏即关。
            //   解法：MoveContextMenu（一级菜单 PreDraw）在注入完成后每帧清零 OwnerAddon →
            //   用户点二级菜单时 owner 已是 0 → 不绑定 → 显示正常。
            //   返回重开一级菜单时 ContextMenuHandler 会重新设回 ChatLog（保证重开也能注入）。
            agent->OwnerAddon = GameFunctions.GameFunctions.GetChatLogAddonId();

            // ===== 完整复刻原生玩家菜单（正确 eventId，来自 CurrentContextMenu+0x448 反汇编确认）=====
            // 反汇编证实 AddContextMenuItem 完整签名：(eventId, text, disabled, submenu, copyText)。
            // eventId 对照（dump 读 0x448+8+i 确认）：
            //   1=发送悄悄话 102=切换频道回复 12=组队邀请 75=好友申请 70=邀请新人
            //   48=队员招募 68=选中 69=从新人频道移除 8=查看铭牌
            // ⚠️ 全部无脑加即可：玩家菜单 eventId 是语义型，游戏按 eventId+目标状态自动过滤不适用项
            //   （下线玩家只显示"邀请新人/选中"，非好友不显示"好友申请"等，游戏自己处理）
            agent->AddContextMenuItem(1, Language.Context_SendTell);
            agent->AddContextMenuItem(102, Language.Context_ReplyInSelectedChatMode);
            agent->AddContextMenuItem(12, Language.Context_InviteToParty);
            // ⚠️ 好友申请：实验证实（08:23~08:25）游戏过滤在插件场景不生效（AccountId 正常时
            // 好友申请也在）→ 自己判断：目标 ContentId 在好友列表 → 不加该项（原版行为）。
            // GetFriends 按 ContentId 匹配（历史消息 AccountId=0 不影响，ContentId 数据库里有）。
            var friendCid = chunk.Message?.ContentId ?? 0;
            var isFriend = friendCid != 0
                && GameFunctions.GameFunctions.GetFriends().Any(f => f.ContentId == friendCid);
            if (!isFriend)
                agent->AddContextMenuItem(75, Language.Context_SendFriendRequest);
            agent->AddContextMenuItem(70, Language.Context_InviteToNoviceNetwork);
            // 屏蔽机能：不做原生子菜单（二级菜单内容生成绑定右键事件流，无解）。
            // 改为 C 前缀"屏蔽机能"子菜单项（ContextMenuHandler.HandlePlayerMenu 用 Dalamud
            // OpenSubmenu 展开，走 RaptureAtkModule::OpenAddon 通道，绕开右键事件流）。
            // 子项：加入黑名单 / 加入屏蔽名单 / 记录屏蔽词（插件 handler）。
            agent->AddContextMenuItem(48, Language.Context_ViewRecruitment);
            agent->AddContextMenuItem(68, Language.Context_Target);
            agent->AddContextMenuItem(69, Language.Context_LeaveNoviceNetwork);
            agent->AddContextMenuItem(8, Language.Context_AdventurerPlate);

            // 打开菜单。之前用 OpenContextMenuForAddon 时事件端字段仍被清，
            // 改用 OpenContextMenu（游戏原生流程：设置字段 → 直接打开）。
            // ⚠️ 打开前把 ContextMenu addon 的 BlockedParentId 设为 ChatLog：
            // Dalamud 的 OnMenuOpened 里 AddonName = GetAddonById(ContextMenu->BlockedParentId)，
            // 原生右键 ChatLog 时它=ChatLog；ChatTwo 模拟不经过右键链，需手动设，
            // 否则 AddonName≠"ChatLog" → DR/Allagan 的 switch 不识别 → 菜单项不注入（实测）。
            SetContextMenuBlockedParentToChatLog();
            // ⚠️ bindToOwner 必须为 false：原生聊天框全程隐藏（IsVisible=false），
            // 菜单绑定到 owner(ChatLog) 会在打开后立即被游戏关闭（用户实测"菜单闪一下就消失"）
            agent->OpenContextMenu(false, false);

            // 立即设置菜单位置，防止闪烁（PreDraw 要到下一帧才执行）
            try
            {
                var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
                if (ctxAddon != null && ctxAddon->IsReady)
                    ctxAddon->SetPosition((short)gameX, (short)gameY);
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

            // ⚠️ OwnerAddon 时分复用（同玩家菜单）：触发时设 ChatLog 供 OnMenuOpened 识别
            //（DR/Allagan 道具项注入依赖 AddonName="ChatLog"），MoveContextMenu 每帧清零防二级菜单绑定。
            // ⚠️ 必须在 ClearMenu 之后设置！ClearMenu 会清掉 AgentContext 目标字段区（含 OwnerAddon 0xDF0），
            //   先设会被清成 0 → OnMenuOpened 时 AddonName 不是 ChatLog → DR 道具项不注入（用户实测回归）。

            // 计算菜单位置：放在聊天框右侧
            var chatPos = ImGui.GetWindowPos();
            var chatSize = ImGui.GetWindowSize();
            // SetPosition 用逻辑坐标（与 MoveContextMenu/MoveTooltip 一致），不要除以 globalScale
            var gameX = (int)(chatPos.X + chatSize.X + 10);
            var gameY = (int)chatPos.Y;

            Plugin.Log.Debug($"[NativeCtxMenu] Item menu position: ({gameX}, {gameY})");

            // 标记菜单激活，ChatLog.MoveContextMenu 会在 PreDraw 中移动菜单
            Plugin.ContextMenuActive = true;
            // ChatTwo 菜单会话开始（二级菜单 MoveContextSubMenu 用它区分聊天框/背包等来源）
            Plugin.ChatTwoMenuSession = true;

            // 设置菜单位置提示
            agent->SetPosition(gameX, gameY);

            // ⚠️ 不调用 SetChatInteractable(true)：原生聊天框必须始终隐藏（用户要求）

            // 清除上次菜单的残留原生菜单项（同玩家菜单逻辑）
            agent->ClearMenu();

            // ⚠️ OwnerAddon 必须在 ClearMenu 之后设置（ClearMenu 会清掉它，见上方注释）
            agent->OwnerAddon = GameFunctions.GameFunctions.GetChatLogAddonId();

            // ===== [FullReplicate-Item v3+] 用 AddMenuItem + AgentChatLog 本体作 handler（2026-08-14 根除快照）=====
            // ⭐ handler 身份确认：[HandlerID] 实测 handler == AgentChatLog.Instance()（matchACL=True）！
            //   AgentChatLog 是游戏常驻 agent（Instance() 由 FFXIVClientStructs 上游维护，非裸 RVA），
            //   游戏启动即有、永远非空 → 快照/缓存/预热全部不需要，道具菜单每次直接可用。
            // 字段验证：ACL[0x20]=0x51(addon id) ACL[0x9c0]=道具ID ACL[0x9c8]=0x3(类型) ACL[0x860]=0x6
            // 命令 ID 语义固定：0=属性对比 1=试穿 2=持有情况 3=展示 5=复制名 6=制作 9=幻影化
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
                    // 装备（原生 dump 确认 7 项）：对比/试穿/持有/展示/制作/幻影化/复制
                    items.Add((Language.Context_ItemComparison, 0x0000)); // 装备属性对比
                    items.Add((Language.Context_TryOn, 0x0001));          // 试穿
                    items.Add((Language.Context_SearchForItem, 0x0002));  // 查看持有情况
                    items.Add((Language.Context_Link, 0x0003));           // 展示道具属性
                    items.Add((Language.Context_SearchRecipes, 0x0006));  // 查看能制作什么
                                                                                // 套装幻影化仅"无属性纯外观时装"才有（用户澄清：套装=一套时装，可打包套装幻影化）。
                    // 判定 = 无主属性(BaseParamValue 全 0) && 无攻击力(DamagePhys/Mag==0)：
                    //   ✔ 花花公子帽/褶边裤（无属性无攻击，可套装幻影化）
                    //   ✘ 风化短剑（1级武器 DamagePhys=8，有属性→不可套装化）
                    //   ✘ 末世终迹套（90级战斗套有主属性）
                    // 注意：不是 LevelEquip==1（风化短剑也 1 级）；不是 IsGlamorous（几乎所有装备都可投影）
                    if (itemRow.BaseParamValue.All(v => v == 0)
                        && itemRow.DamagePhys == 0
                        && itemRow.DamageMag == 0)
                        items.Add((Language.Context_ViewItemSet, 0x0009));    // 查看套装幻影化
                    items.Add((Language.Context_CopyItemName, 0x0005));   // 复制道具名（[self+0x9c0] 已写为当前道具 ID，动作可正常读）
                }
                else
                {
                    // 非装备（材料/杂货/家具等，原生 dump 确认 4 项）：持有/展示/制作/复制
                    // 白银羽毛（杂货）原生菜单与材料一致（含"制作"项），游戏对非装备基本无条件显示这 4 项
                    items.Add((Language.Context_SearchForItem, 0x0002));  // 查看持有情况
                    items.Add((Language.Context_Link, 0x0003));           // 展示道具属性
                    items.Add((Language.Context_SearchRecipes, 0x0006));  // 查看能制作什么
                    items.Add((Language.Context_CopyItemName, 0x0005));   // 复制道具名（[self+0x9c0] 已写为当前道具 ID，动作可正常读）
                }

                if (items.Count > 0)
                {
                    // ⚠️ 关键修复（2026-08-14）：游戏"复制名"动作（hParam=0x10005）读"道具上下文"对象
                    // 的 [self+0x9c0] 字段（当前道具 ID，反汇编 0xed8bd5 确认），而非 ChatLog.ContextItemId！
                    // self = AgentChatLog（[HandlerID] 实测 matchACL=True）；[0x9c0] 存道具 ID。
                    // 修复：把 [0x9c0] 写为当前道具 RawItemId，且 [0x9c8] 写类型=3（反汇编 0xed8bdd：
                    // sub ecx,1; cmp ecx,2; jne return → 类型必须 ==3 才走复制路径）。
                    *(uint*)((byte*)handlerSnapshot + 0x9c0) = item.RawItemId;
                    *(uint*)((byte*)handlerSnapshot + 0x9c8) = 3;

                    foreach (var it in items)
                        agent->AddMenuItem(it.Text, itemHandler, 0x10000L | (uint)it.Cmd);
                    itemNativeAdded = true;
                }
            }

            GameFunctions.ContextMenuHandler.NativeItemMenuAdded = itemNativeAdded;

            // ⚠️ 关键：不能用 OpenContextMenuForAddon！反汇编(0x49dce0)证实它内部 mov r8b,1 强制 closeExisting=true，
            // 会调 [vtable+0x28] 清空菜单 → 把我们手动加的项全清掉 → 占位符。
            // 改用 OpenContextMenu(false,false)（closeExisting=false 不清项），与玩家菜单一致。
            // OwnerAddon 已在上面设置（OpenContextMenuForAddon 本质也只是设置 OwnerAddon 后跳到这里）
            // ⚠️ 打开前设 BlockedParentId=ChatLog（同玩家菜单，DR/Allagan 注入识别用）
            SetContextMenuBlockedParentToChatLog();
            agent->OpenContextMenu(false, false);

            // 立即设置菜单位置，防止闪烁
            try
            {
                var ctxAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ContextMenu");
                if (ctxAddon != null && ctxAddon->IsReady)
                    ctxAddon->SetPosition((short)gameX, (short)gameY);
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

    private void DrawItemPopup(ItemPayload payload)
    {
        if (payload.Kind == ItemKind.EventItem)
        {
            DrawEventItemPopup(payload);
            return;
        }

        if (!Sheets.ItemSheet.TryGetRow(payload.ItemId, out var itemRow))
            return;

        var hq = payload.Kind == ItemKind.Hq;
        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(itemRow.Icon, hq)).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        var name = itemRow.Name.ToDalamudString();
        // hq symbol
        if (hq)
            name.Payloads.Add(new TextPayload(" "));
        else if (payload.Kind == ItemKind.Collectible)
            name.Payloads.Add(new TextPayload(" "));

        InputHandler.ChunkHandler.DrawChunks(ChunkUtil.ToChunks(name, ChunkSource.None, null).ToList(), false);
        ImGui.Separator();

        // ═══════════════════════════════════════════════════════════
        // 硬编码菜单已注释 —— 改用原生右键菜单触发方案
        // 原生菜单通过 TryShowNativeItemContextMenu() 触发，
        // 此 fallback 仅在最简情况下显示
        // ═══════════════════════════════════════════════════════════

        // 复制名字
        if (ImGui.Selectable(Language.Context_CopyItemName))
            ImGui.SetClipboardText(name.TextValue);

    }

    private void DrawEventItemPopup(ItemPayload payload)
    {
        if (payload.Kind != ItemKind.EventItem)
            return;

        if (!Sheets.EventItemSheet.HasRow(payload.ItemId))
            return;

        var item = Sheets.EventItemSheet.GetRow(payload.ItemId);
        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Icon)).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        InputHandler.ChunkHandler.DrawChunks(ChunkUtil.ToChunks(item.Name.ToDalamudString(), ChunkSource.None, null).ToList(), false);
        ImGui.Separator();

        var realItemId = payload.RawItemId;
        if (ImGui.Selectable(Language.Context_Link))
            GameFunctions.Context.LinkItem(realItemId);

        if (ImGui.Selectable(Language.Context_CopyItemName))
            ImGui.SetClipboardText(item.Name.ToString());
    }

    private unsafe void DrawPlayerPopup(Chunk chunk, PlayerPayload player)
    {
        // Possible that GMs return a null payload
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (player == null)
            return;

        var world = player.World;
        if (chunk.Message?.Code.Type == ChatType.FreeCompanyLoginLogout)
            if (Plugin.PlayerState.HomeWorld.IsValid)
                world = Plugin.PlayerState.HomeWorld;

        var name = new List<Chunk> { new TextChunk(ChunkSource.None, null, player.PlayerName) };
        // ⚠️ 不要用 World.IsPublic（CN 世界行恒为 false）；fallback 弹窗无法查 ObjectTable，
        // 用"目标家服 != 本地当前世界"做展示与 tell 后缀的近似判断（本地用当前世界，
        // 避免自己跨区时把留在家服的玩家误判为同服）
        var localCurrentWorldId = (ushort)(Plugin.ObjectTable.LocalPlayer?.CurrentWorld.RowId ?? 0);
        var isDifferentWorld = world.RowId != 0 && world.RowId != localCurrentWorldId;
        if (isDifferentWorld)
        {
            name.AddRange([
                new IconChunk(ChunkSource.None, null, BitmapFontIcon.CrossWorld),
                new TextChunk(ChunkSource.None, null, world.Value.Name.ToString())
            ]);
        }

        InputHandler.ChunkHandler.DrawChunks(name, false);
        ImGui.Separator();

        // ═══════════════════════════════════════════════════════════
        // 硬编码菜单已注释 —— 改用原生右键菜单触发方案
        // 原生菜单通过 TryShowNativePlayerContextMenu() 触发，
        // 此 fallback 仅在最简情况下显示
        // ═══════════════════════════════════════════════════════════

        var validContentId = chunk.Message?.ContentId is not (null or 0);
        var isSelf = validContentId && chunk.Message!.ContentId == Plugin.PlayerState.ContentId;
        if (!isSelf && player.PlayerName == Plugin.PlayerState.CharacterName)
            isSelf = true;

        // 发送悄悄话（非自己）
        if (!isSelf && ImGui.Selectable(Language.Context_SendTell))
        {
            InputHandler.ChatInput = $"/tell {player.PlayerName}";
            // 无条件加 @世界名（按角色路由）：同服玩家若当前不在你的世界也必须带 @（用户实测）
            if (world.IsValid)
                InputHandler.ChatInput += $"@{world.Value.Name}";
            InputHandler.ChatInput += " ";
            InputHandler.Activate = true;
        }

        // 复制名字
        if (ImGui.Selectable(Language.Context_CopyItemName))
            ImGui.SetClipboardText(player.PlayerName);

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

    private void DrawUriPopup(UriPayload uri)
    {
        ImGui.TextUnformatted(string.Format(Language.Context_URLDomain, uri.Uri.Authority));
        ImGuiUtil.WarningText(Language.Context_URLWarning, false);
        ImGui.Separator();

        if (ImGui.Selectable(Language.Context_OpenInBrowser))
            WrapperUtil.TryOpenUri(uri.Uri);

        if (ImGui.Selectable(Language.Context_CopyLink))
        {
            ImGui.SetClipboardText(uri.Uri.ToString());
            WrapperUtil.AddNotification(Language.Context_CopyLinkNotification, NotificationType.Info);
        }
    }

    private void DrawStatusPopup(StatusPayload status)
    {
        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(status.Status.Value.Icon)).GetWrapOrDefault() is { } icon)
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

        InputHandler.ChunkHandler.DrawChunks(ChunkUtil.ToChunks(builder.BuiltString, ChunkSource.None, null).ToList(), false);
        ImGui.Separator();

        if (ImGui.Selectable(Language.Context_Link))
        {
            GameFunctions.Context.LinkStatus(status.Status.RowId);
            InputHandler.ChatInput += " <status>";
        }
    }
}
