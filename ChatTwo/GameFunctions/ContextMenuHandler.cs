using System;
using System.Collections.Generic;
using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace ChatTwo.GameFunctions;

/// <summary>
/// 通过 Dalamud ContextMenu API 向 ChatLog 右键菜单注入自定义菜单项。
///
/// 工作原理：
/// PayloadHandler.TryShowNativePlayerContextMenu / TryShowNativeItemContextMenu
/// 设置 AgentContext 目标字段后调用 OpenContextMenuForAddon，内部触发
/// Dalamud 的 OnMenuOpened 事件。本处理器在事件回调中通过 args.AddMenuItem()
/// 添加自定义菜单项（发送悄悄话、复制名字、组队邀请等）。
/// 第三方插件（DailyRoutines、Allagan Tools）的菜单项也会在同一事件中添加。
/// </summary>
public sealed class ContextMenuHandler : IDisposable
{
    private Plugin Plugin { get; }

    /// <summary>
    /// ChatTwo 触发的菜单目标类型。PayloadHandler 在调用 OpenContextMenuForAddon
    /// 前设置，OnMenuOpened 回调中根据此字段决定调用 HandlePlayerMenu 还是 HandleItemMenu。
    /// 不依赖游戏状态（TargetName/ContextItemId 会残留上次的值），用此枚举确保分类正确。
    /// </summary>
    public enum MenuTargetType
    {
        None,
        Player,
        Item,
    }

    /// <summary>
    /// 当前菜单的目标类型，由 PayloadHandler 在触发原生菜单前设置。
    /// </summary>
    public static MenuTargetType CurrentTargetType;

    /// <summary>
    /// 当前右键目标的消息聊天类型，由 PayloadHandler 在触发原生菜单前设置。
    /// 用于"切换到相同频道回复"功能。
    /// </summary>
    public static ChatType? CurrentChatType;

    /// <summary>
    /// 当前右键目标的 ContentId，由 PayloadHandler 在触发原生菜单前设置。
    /// </summary>
    public static ulong CurrentContentId;

    /// <summary>
    /// 当前右键目标的原始道具 ID，由 PayloadHandler 在触发道具菜单前设置。
    /// </summary>
    public static uint CurrentItemId;

    /// <summary>
    /// 标记当前菜单由 ChatTwo 触发。防止向原生菜单注入 ChatTwo 菜单项。
    /// </summary>
    public static bool IsChatTwoTriggered;

    public ContextMenuHandler(Plugin plugin)
    {
        Plugin = plugin;
        Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
    {
        Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        // 只处理 ChatTwo 触发的菜单，防止污染原生菜单（小队列表、好友列表等）
        if (!IsChatTwoTriggered)
            return;

        Plugin.Log.Information($"[ContextMenuHandler] OnMenuOpened fired: TargetType={CurrentTargetType}, ItemId={CurrentItemId}, AddonName={args.AddonName}");

        try
        {
            // 用 PayloadHandler 设置的标志位决定菜单类型，不依赖游戏状态
            // （TargetName 和 ContextItemId 都会残留上次的值，不可靠）
            switch (CurrentTargetType)
            {
                case MenuTargetType.Player:
                    if (args.Target is MenuTargetDefault menuTarget)
                        HandlePlayerMenu(args, menuTarget);
                    else
                        Plugin.Log.Warning("[ContextMenuHandler] Expected MenuTargetDefault for Player menu");
                    break;

                case MenuTargetType.Item:
                    if (CurrentItemId != 0)
                        HandleItemMenu(args, CurrentItemId);
                    else
                        Plugin.Log.Warning("[ContextMenuHandler] CurrentItemId is 0 for Item menu");
                    break;

                default:
                    Plugin.Log.Warning($"[ContextMenuHandler] Unknown CurrentTargetType: {CurrentTargetType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ContextMenuHandler] OnMenuOpened error");
        }
        finally
        {
            // 清理所有静态状态
            IsChatTwoTriggered = false;
            CurrentTargetType = MenuTargetType.None;
            CurrentChatType = null;
            CurrentContentId = 0;
            CurrentItemId = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 道具菜单
    // ═══════════════════════════════════════════════════════════════

    private void HandleItemMenu(IMenuOpenedArgs args, uint rawItemId)
    {
        // 从 rawItemId 判断道具类型和实际 ID
        var kind = rawItemId switch
        {
            < 500_000 => ItemKind.Normal,
            < 1_000_000 => ItemKind.Collectible,
            < 2_000_000 => ItemKind.Hq,
            _ => ItemKind.EventItem,
        };

        var itemId = kind switch
        {
            ItemKind.Normal => rawItemId,
            ItemKind.Collectible => rawItemId - 500_000,
            ItemKind.Hq => rawItemId - 1_000_000,
            _ => rawItemId - 2_000_000,
        };

        // 获取道具名称和属性
        string itemName;
        bool isEquipment = false;
        bool isMaterial = false;

        if (kind == ItemKind.EventItem)
        {
            if (!Sheets.EventItemSheet.TryGetRow(itemId, out var eventItem))
                return;
            itemName = eventItem.Name.ToString();
        }
        else
        {
            if (!Sheets.ItemSheet.TryGetRow(itemId, out var itemRow))
                return;
            itemName = itemRow.Name.ToString();
            isEquipment = itemRow.EquipSlotCategory.RowId != 0;
            isMaterial = itemRow.ItemSearchCategory.Value.Category == 3;
        }

        // 装备类：装备属性对比、试穿（仅普通道具，非事件道具）
        if (kind != ItemKind.EventItem && isEquipment)
        {
            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_ItemComparison,
                Priority = 200,
                OnClicked = _ => Context.OpenItemComparison(rawItemId),
            });

            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_TryOn,
                Priority = 190,
                OnClicked = _ => Context.TryOn(rawItemId, 0),
            });
        }

        // 查看持有情况
        args.AddMenuItem(new MenuItem
        {
            PrefixChar = 'C',
            Name = Language.Context_SearchForItem,
            Priority = 180,
            OnClicked = _ => Context.SearchForItem(rawItemId),
        });

        // 展示道具属性
        args.AddMenuItem(new MenuItem
        {
            PrefixChar = 'C',
            Name = Language.Context_Link,
            Priority = 170,
            OnClicked = _ =>
            {
                GameFunctions.OpenItemTooltip(rawItemId, kind);
                Context.LinkItem(rawItemId);
            },
        });

        // 查看能制作什么（仅材料类）
        if (kind != ItemKind.EventItem && isMaterial)
        {
            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_SearchRecipes,
                Priority = 160,
                OnClicked = _ => Context.SearchForRecipesUsingItem(itemId),
            });
        }

        // 复制道具名
        args.AddMenuItem(new MenuItem
        {
            PrefixChar = 'C',
            Name = Language.Context_CopyItemName,
            Priority = 10,
            OnClicked = _ => ImGui.SetClipboardText(itemName),
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // 玩家菜单
    // ═══════════════════════════════════════════════════════════════

    private void HandlePlayerMenu(IMenuOpenedArgs args, MenuTargetDefault target)
    {
        var playerName = target.TargetName ?? string.Empty;
        var worldId = (ushort)(target.TargetHomeWorld.IsValid ? target.TargetHomeWorld.RowId : 0);
        var contentId = target.TargetContentId != 0 ? target.TargetContentId : CurrentContentId;

        // 兜底：从静态字段获取 ContentId
        if (contentId == 0)
            contentId = CurrentContentId;

        // 判断是否自己
        var isSelf = contentId != 0 && contentId == Plugin.PlayerState.ContentId;
        if (!isSelf && playerName == Plugin.PlayerState.CharacterName)
            isSelf = true;

        // 获取世界信息（worldName 用于跨服 tell 的 @世界名 后缀）
        // ⚠️ 不要用 WorldRow.IsPublic 判断跨服！CN 世界行 IsPublic=false。
        // 也不要仅用"家服 != 本地家服"判断——跨区旅行者（家服不同但当前在你世界）
        // 会被误判为跨服；反过来自己跨区后邀请留在家服的玩家会被误判为同服。
        // 正确做法：目标在 ObjectTable（在场）→ 同世界处理；
        // 不在场时对比"目标家服 vs 本地当前世界"决定同服/跨服邀请。
        var worldName = string.Empty;
        if (worldId != 0 && Sheets.WorldSheet.TryGetRow(worldId, out var worldRow))
            worldName = worldRow.Name.ToString();
        var localCurrentWorldId = (ushort)(Plugin.ObjectTable.LocalPlayer?.CurrentWorld.RowId ?? 0);

        // 查找玩家对象（用于选中、组队等）：优先家服精确匹配，回退仅名字匹配
        var foundChar = FindCharacter(playerName, worldId);

        // 队伍状态
        var party = Plugin.PartyList;
        var leader = party.Length > 0 ? party[(int)party.PartyLeaderIndex]?.ContentId ?? 0 : 0;
        var isLeader = party.Length == 0 || Plugin.PlayerState.ContentId == leader;
        var isInParty = false;
        foreach (var member in party)
        {
            if (member.Name.TextValue == playerName && member.World.RowId == worldId)
            {
                isInParty = true;
                break;
            }
        }

        var inInstance = GameFunctions.IsInInstance();
        var inPartyInstance = false;
        if (Sheets.TerritorySheet.TryGetRow(Plugin.ClientState.TerritoryType, out var territory))
            inPartyInstance = territory.TerritoryIntendedUse.RowId is 41 or 47 or 48 or 52 or 53 or 61;

        // 1. 发送悄悄话（非自己）
        if (!isSelf)
        {
            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_SendTell,
                Priority = 500,
                OnClicked = _ =>
                {
                    var input = $"/tell {playerName}";
                    // FFXIV 的 /tell 名字 只匹配当前世界，同服玩家若当前不在你的世界也会失败。
                    // 加 @家服名 按角色路由，对同服/跨服/跨区旅行者都有效（用户实测同服玩家也必须带 @）
                    if (!string.IsNullOrEmpty(worldName))
                        input += $"@{worldName}";
                    input += " ";
                    Plugin.ChatLog.InputHandler.ChatInput = input;
                    Plugin.ChatLog.InputHandler.Activate = true;
                },
            });
        }

        // 2. 切换到相同频道回复
        if (CurrentChatType != null)
        {
            var inputChannel = CurrentChatType.Value.ToInputChannel();
            if (inputChannel != null)
            {
                args.AddMenuItem(new MenuItem
                {
                    PrefixChar = 'C',
                Name = Language.Context_ReplyInSelectedChatMode,
                    Priority = 490,
                    OnClicked = _ =>
                    {
                        Plugin.Functions.Chat.SetChannelWithExtraChat(inputChannel.Value);
                        Plugin.ChatLog.InputHandler.Activate = true;
                    },
                });
            }
        }

        // 3. 发送组队邀请（非自己、非已组队、无队伍或自己是队长）
        var canSendInvite = !isSelf && !isInParty && (party.Length == 0 || isLeader);
        if (canSendInvite)
        {
            if (inInstance && inPartyInstance)
            {
                // 副本内：直接邀请
                if (contentId != 0)
                {
                    args.AddMenuItem(new MenuItem
                    {
                        PrefixChar = 'C',
                Name = Language.Context_InviteToParty,
                        Priority = 480,
                        OnClicked = _ => Party.InviteInInstance(contentId),
                    });
                }
            }
            else
            {
                // 非副本：单个"发送组队邀请"，点击时按目标状态自动选择邀请方式
                // （与原生菜单一致：游戏根据目标当前位置决定普通小队/跨服小队）
                args.AddMenuItem(new MenuItem
                {
                    PrefixChar = 'C',
                    Name = Language.Context_InviteToParty,
                    Priority = 480,
                    OnClicked = _ =>
                    {
                        if (contentId == 0)
                            return;

                        if (foundChar != null)
                        {
                            // 目标在场（当前世界=本地，含跨区旅行者）：普通小队邀请
                            Party.InviteSameWorld(playerName, (ushort)foundChar.CurrentWorld.RowId, contentId);
                        }
                        else if (worldId != 0 && worldId == localCurrentWorldId)
                        {
                            // 目标家服 == 本地当前世界（自己跨区时也适用）：普通邀请
                            Party.InviteSameWorld(playerName, worldId, contentId);
                        }
                        else
                        {
                            // 跨服（目标家服 != 本地当前世界）：跨服邀请（世界传 0 由游戏解析）
                            Party.InviteOtherWorld(contentId, 0);
                        }
                    },
                });
            }
        }

        // 4. 提拔/踢出（队长且目标在队伍中）
        if (isLeader && isInParty && (!inInstance || inPartyInstance))
        {
            // 需要找到 member 的 ContentId
            ulong memberContentId = 0;
            foreach (var member in party)
            {
                if (member.Name.TextValue == playerName && member.World.RowId == worldId)
                {
                    memberContentId = member.ContentId;
                    break;
                }
            }

            if (memberContentId != 0)
            {
                args.AddMenuItem(new MenuItem
                {
                    PrefixChar = 'C',
                Name = Language.Context_Promote,
                    Priority = 470,
                    OnClicked = _ => Party.Promote(playerName, memberContentId),
                });

                args.AddMenuItem(new MenuItem
                {
                    PrefixChar = 'C',
                Name = Language.Context_KickFromParty,
                    Priority = 460,
                    OnClicked = _ => Party.Kick(playerName, memberContentId),
                });
            }
        }

        // 5. 发送好友申请（非自己、非好友）
        if (!isSelf && !IsFriend(playerName, worldId))
        {
            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_SendFriendRequest,
                Priority = 450,
                OnClicked = _ => Plugin.Functions.SendFriendRequest(playerName, worldId),
            });
        }

        // 6. 邀请到新人频道（导师）
        if (!isSelf && GameFunctions.IsMentor())
        {
            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_InviteToNoviceNetwork,
                Priority = 440,
                OnClicked = _ => Context.InviteToNoviceNetwork(playerName, worldId),
            });
        }

        // 7. 屏蔽机能（非自己）— 展开为子菜单
        if (!isSelf)
        {
            var blockItems = new List<MenuItem>();

            blockItems.Add(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_AddToBlacklist,
                Priority = 100,
                OnClicked = _ => Plugin.Functions.AddToBlacklist(playerName, worldId),
            });

            blockItems.Add(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_AddToMuteList,
                Priority = 90,
                OnClicked = _ =>
                {
                    // AddToMuteList 需要 accountId 和 contentId，使用 contentId 兜底
                    Plugin.Functions.AddToMuteList(0, contentId, playerName, (short)worldId);
                },
            });

            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_BlockFunctions,
                Priority = 430,
                IsSubmenu = true,
                OnClicked = clickArgs =>
                {
                    try
                    {
                        clickArgs.OpenSubmenu(Language.Context_BlockFunctions, blockItems);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning(ex, "[ContextMenuHandler] OpenSubmenu failed, adding flat items");
                        // 降级：直接添加为扁平菜单项（此时菜单已打开，无法再添加，仅记录日志）
                    }
                },
            });
        }

        // 8. 队员招募（非自己）
        if (!isSelf)
        {
            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_ViewRecruitment,
                Priority = 420,
                OnClicked = _ => OpenPartyFinderSearchByCreator(playerName, contentId),
            });
        }

        // 9. 选中（非自己、玩家在场）
        if (!isSelf && foundChar != null)
        {
            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_Target,
                Priority = 410,
                OnClicked = _ => Plugin.TargetManager.Target = foundChar,
            });
        }

        // 10. 查看冒险者铭牌（非自己、有 ContentId）
        if (!isSelf && contentId != 0)
        {
            args.AddMenuItem(new MenuItem
            {
                PrefixChar = 'C',
                Name = Language.Context_AdventurerPlate,
                Priority = 400,
                OnClicked = _ =>
                {
                    if (!GameFunctions.TryOpenAdventurerPlate(contentId))
                        WrapperUtil.AddNotification(Language.Context_AdventurerPlateError, NotificationType.Warning);
                },
            });
        }

        // 11. 复制名字
        args.AddMenuItem(new MenuItem
        {
            PrefixChar = 'C',
            Name = Language.Context_CopyName,
            Priority = 10,
            OnClicked = _ => ImGui.SetClipboardText(playerName),
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // 辅助方法
    // ═══════════════════════════════════════════════════════════════

    private static IPlayerCharacter? FindCharacter(string playerName, ushort worldId)
    {
        // 优先家服精确匹配，避免同名玩家误选
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter character)
                continue;

            if (character.Name.TextValue == playerName && character.HomeWorld.RowId == worldId)
                return character;
        }

        // 回退：仅按名字匹配（覆盖跨区旅行者——家服不同但在场的情况）
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter character)
                continue;

            if (character.Name.TextValue == playerName)
                return character;
        }

        return null;
    }

    private static bool IsFriend(string playerName, ushort worldId)
    {
        try
        {
            var friends = GameFunctions.GetFriends();
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

    /// <summary>
    /// 打开招募面板并通过 PartyFinderGui.ReceiveListing 搜索该玩家的招募。
    /// 1 秒内未找到则提示"该玩家当前没有进行招募"。
    /// </summary>
    private static void OpenPartyFinderSearchByCreator(string playerName, ulong contentId)
    {
        try
        {
            var found = false;
            var searchName = playerName;
            var searchCid = contentId;
            var startTick = Environment.TickCount64;

            void OnListingReceived(Dalamud.Game.Gui.PartyFinder.Types.IPartyFinderListing listing, Dalamud.Game.Gui.PartyFinder.Types.IPartyFinderListingEventArgs listingArgs)
            {
                if (found) return;

                var match = searchCid != 0 && listing.ContentId == searchCid
                            || listing.Name.ToString() == searchName;
                if (!match) return;

                found = true;
                Plugin.PartyFinderGui.ReceiveListing -= OnListingReceived;

                try
                {
                    GameFunctions.OpenPartyFinder((uint)listing.Id);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "OpenListing failed");
                }
            }

            Plugin.PartyFinderGui.ReceiveListing += OnListingReceived;
            GameFunctions.OpenPartyFinder();
            Plugin.Log.Information($"[ViewRecruitment] Searching for [{playerName}] ContentId={contentId:X}");

            Plugin.Framework.Update += OnUpdate;

            void OnUpdate(IFramework _)
            {
                if (found || Environment.TickCount64 - startTick < 1000)
                    return;

                Plugin.Framework.Update -= OnUpdate;
                Plugin.PartyFinderGui.ReceiveListing -= OnListingReceived;
                WrapperUtil.AddNotification("该玩家当前没有进行招募", NotificationType.Info);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "ViewRecruitment failed");
        }
    }
}
