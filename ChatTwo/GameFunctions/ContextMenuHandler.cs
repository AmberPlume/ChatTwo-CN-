using System;
using System.Collections.Generic;
using ChatTwo.Code;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Utility.Signatures;
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
public sealed partial class ContextMenuHandler : IDisposable
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
    /// 当前右键消息的全文（SeString），供"记录屏蔽词"填入屏蔽词窗口。
    /// </summary>
    public static SeString? CurrentMessageContent;

    /// <summary>
    /// 标记当前菜单由 ChatTwo 触发。防止向原生菜单注入 ChatTwo 菜单项。
    /// </summary>
    public static bool IsChatTwoTriggered;

    /// <summary>
    /// 标记当前道具菜单已由 PayloadHandler 加了原生项（装备/材料），
    /// HandleItemMenu 据此跳过 C 前缀自定义项，避免重复。
    /// </summary>
    public static bool NativeItemMenuAdded;

    public ContextMenuHandler(Plugin plugin)
    {
        Plugin = plugin;
        Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;

        // ===== [ENABLE_CTX_DIAG] 逆向诊断 hook（独立文件，编译开关控制，平时不启用）=====
#if ENABLE_CTX_DIAG
        InitDiagnostics();
#endif
    }

    public void Dispose()
    {
        Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;
#if ENABLE_CTX_DIAG
        DisposeDiagnostics();
#endif
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        // ===== [ENABLE_CTX_DIAG] 逆向诊断（独立文件，编译开关控制）=====
#if ENABLE_CTX_DIAG
        DumpOnMenuOpened(args);
#endif

        // 只处理 ChatTwo 触发的菜单，防止污染原生菜单（小队列表、好友列表等）
        if (!IsChatTwoTriggered)
            return;

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
            CurrentMessageContent = null;
            NativeItemMenuAdded = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 道具菜单
    // ═══════════════════════════════════════════════════════════════

    private void HandleItemMenu(IMenuOpenedArgs args, uint rawItemId)
    {
        // 已由 PayloadHandler 加原生项（含复制名）→ 跳过 C 前缀项，避免重复。
        // 复制名走原生动作（0x10005）：PayloadHandler 已把道具上下文 [self+0x9c0] 写为当前道具 ID，
        // 动作可正常读（2026-08-14 根因修复）。
        if (NativeItemMenuAdded)
            return;

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
        // ===== 屏蔽机能子菜单（Dalamud OpenSubmenu → RaptureAtkModule::OpenAddon 通道，
        // 直接打开 AddonContextSub，绕开右键事件流 —— 此前三条路失败皆因绑定右键链）。
        // 子项点击走插件 handler（AddToBlacklist/AddToMuteList/AddToTermsList），不依赖游戏动作上下文。=====
        {
            var pName = target.TargetName ?? string.Empty;
            var wId = (ushort)(target.TargetHomeWorld.IsValid ? target.TargetHomeWorld.RowId : 0);
            var cId = target.TargetContentId != 0 ? target.TargetContentId : CurrentContentId;
            if (cId == 0)
                cId = CurrentContentId;

            // 好友：屏蔽机能只有"记录屏蔽词"；陌生人：完整三项（黑名单/屏蔽名单/屏蔽词）。
            // 复用 GetFriends 按 ContentId 匹配（用户实测确认：好友仅记录屏蔽词）。
            var isFriend = cId != 0
                && GameFunctions.GetFriends().Any(f => f.ContentId == cId);

            var blockItems = new List<MenuItem>();

            if (!isFriend)
            {
                blockItems.Add(new MenuItem
                {
                    Name = Language.Context_AddToBlacklist,
                    Priority = 100,
                    OnClicked = _ => Plugin.Functions.AddToBlacklist(pName, wId),
                });

                blockItems.Add(new MenuItem
                {
                    Name = Language.Context_AddToMuteList,
                    Priority = 90,
                    OnClicked = _ => Plugin.Functions.AddToMuteList(0, cId, pName, (short)wId),
                });
            }

            // 记录屏蔽词：把当前右键消息全文填入屏蔽词窗口（AgentTermFilter.OpenNewFilterWindow）
            if (CurrentMessageContent != null)
            {
                blockItems.Add(new MenuItem
                {
                    Name = Language.Context_AddToTermsFilter,
                    Priority = 80,
                    OnClicked = _ => Plugin.Functions.AddToTermsList(CurrentMessageContent),
                });
            }

            // 返回：关闭二级菜单并重新打开一级菜单（原生返回=回到一级菜单）。
            // ⚠️ 经验：① IsSubmenu=true 会生成"右指"箭头（▶，子菜单指示），原生返回是"左指" →
            //   不用 IsSubmenu，Name 直接带左箭头 "←"（U+2190，CJK 字体必有字形；◁ U+25C1 无字形）；
            // ② 展开二级时游戏会关闭一级菜单（ContextMenu addon），Show() 不生效（内容/状态已被清）→
            //   用 AgentContext.OpenContextMenu 重新打开（右键时写入的目标字段/菜单结构仍在，未清）；
            // ③ OpenSubmenu 生成的子菜单不带自动返回（ReturnArrowMask 依赖原生右键标志 _gap_0x6BC，
            //   插件场景为 0）。Priority=MaxValue 保证升序排列中位于最底部。
            blockItems.Add(new MenuItem
            {
                Name = "← " + Language.Context_Back,
                Priority = int.MaxValue,
                OnClicked = _ =>
                {
                    unsafe
                    {
                        var mgr = RaptureAtkModule.Instance()->RaptureAtkUnitManager;
                        var sub = mgr.GetAddonByName("AddonContextSub");
                        if (sub != null)
                            sub->Hide(true, false, 0);
                        var agent = AgentContext.Instance();
                        if (agent != null)
                        {
                            // 重开一级菜单前恢复 OwnerAddon=ChatLog（时分复用：触发时设 ChatLog 供
                            // OnMenuOpened 注入，MoveContextMenu 渲染时清零防二级绑定）。
                            agent->OwnerAddon = ChatTwo.GameFunctions.GameFunctions.GetChatLogAddonId();
                            agent->OpenContextMenu(false, false);
                        }
                    }
                },
            });

            if (blockItems.Count > 0)
            {
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
                            Plugin.Log.Warning(ex, "[ContextMenuHandler] OpenSubmenu failed");
                        }
                    },
                });
            }

            return;
        }
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
