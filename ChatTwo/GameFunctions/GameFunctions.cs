using System.Globalization;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Utility;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace ChatTwo.GameFunctions;

public unsafe class GameFunctions : IDisposable
{
    #region Hooks
    [Signature("E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B D0 49 8D 4F", DetourName = nameof(ResolveTextCommandPlaceholderDetour))]
    private Hook<ResolveTextCommandPlaceholderDelegate>? ResolveTextCommandPlaceholderHook = null!;
    private delegate nint ResolveTextCommandPlaceholderDelegate(nint a1, byte* placeholderText, byte a3, byte a4);
    #endregion

    private Plugin Plugin { get; }
    public KeybindManager KeybindManager { get; }
    public Chat Chat { get; }

    public GameFunctions(Plugin plugin)
    {
        Plugin = plugin;
        KeybindManager = new KeybindManager(plugin);
        Chat = new Chat(Plugin);

        Plugin.GameInteropProvider.InitializeFromAttributes(this);

        ResolveTextCommandPlaceholderHook?.Enable();
    }

    public void Dispose()
    {
        Chat.Dispose();
        KeybindManager.Dispose();

        ResolveTextCommandPlaceholderHook?.Dispose();

        Marshal.FreeHGlobal(PlaceholderNamePtr);
    }

    public void SendFriendRequest(string name, ushort world)
    {
        ListCommand(name, world, "friendlist");
    }

    public void AddToBlacklist(string name, ushort world)
    {
        ListCommand(name, world, "blist");
    }

    public void AddToMuteList(ulong accountId, ulong contentId, string name, short worldId)
    {
        AgentMutelist.Instance()->Add(accountId, contentId, name, worldId);
    }

    public void AddToTermsList(SeString content)
    {
        AgentTermFilter.Instance()->OpenNewFilterWindow(content.EncodeWithNullTerminator());
    }

    private void ListCommand(string name, ushort world, string commandName)
    {
        var worldRow = Sheets.WorldSheet.GetRow(world);

        ReplacementName = $"{name}@{worldRow.Name.ToString()}";
        ChatBox.SendMessage($"/{commandName} add {Placeholder}");
    }

    private static T* GetAddon<T>(string name) where T : unmanaged
    {
        var addon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName(name);
        return addon != null && addon->IsReady ? (T*)addon : null;
    }

    public static void SetAddonInteractable(string name, bool interactable)
    {
        var addon = GetAddon<AtkUnitBase>(name);
        if (addon == null)
            return;
        addon->IsVisible = interactable;
    }

    public static void SetChatInteractable(bool interactable)
    {
        for (var i = 0; i < 4; i++)
            SetAddonInteractable($"ChatLogPanel_{i}", interactable);

        SetAddonInteractable("ChatLog", interactable);
    }

    public static bool IsAddonInteractable(string name)
    {
        var addon = GetAddon<AtkUnitBase>(name);
        return addon != null && addon->IsVisible;
    }

    // ═══════════════════════════════════════════════════════════════
    // 二级菜单"闪一下消失"根治（2026-08-14）
    // ═══════════════════════════════════════════════════════════════
    // 根因：游戏展开二级菜单（AddonContextSub）后检查 OwnerAddon（ChatLog）的 IsVisible，
    //       ChatTwo 的 FrameworkUpdate 每帧隐藏 ChatLog（SetAddonInteractable(name,false)），
    //       二级菜单展开时主菜单自动关闭 → ContextMenuActive=false → 下帧 ChatLog 被隐藏
    //       → 游戏检测 OwnerAddon 不可见 → 立即 Hide AddonContextSub（[HideDiag] 实测：
    //       Hide(AddonContextSub) menuIndex=1 owner=81，OSM 后 5ms 内被调）。
    // 修复：二级菜单展开期间，ChatLog 保持 IsVisible=true 但移到屏幕外（游戏看到"可见"
    //       通过检查，用户看不到任何闪现）；二级菜单关闭后恢复原位并交还正常隐藏逻辑。
    private static bool _chatOffscreen;
    private static short _chatSavedX;
    private static short _chatSavedY;

    public static bool IsNativeSubContextMenuVisible()
    {
        var addon = GetAddon<AtkUnitBase>("AddonContextSub");
        return addon != null && addon->IsVisible;
    }

    public static void KeepChatVisibleOffscreen()
    {
        var addon = GetAddon<AtkUnitBase>("ChatLog");
        if (addon == null)
            return;
        if (!_chatOffscreen)
        {
            _chatSavedX = addon->X;
            _chatSavedY = addon->Y;
            _chatOffscreen = true;
        }
        // 游戏要求 OwnerAddon（ChatLog）可见才保持二级菜单 → IsVisible=true
        if (!addon->IsVisible)
            addon->IsVisible = true;
        // 移到屏幕外（short 范围），避免原版聊天框闪现
        if (addon->X != 9999 || addon->Y != 9999)
            addon->SetPosition(9999, 9999);
    }

    public static void RestoreChatPosition()
    {
        if (!_chatOffscreen)
            return;
        _chatOffscreen = false;
        var addon = GetAddon<AtkUnitBase>("ChatLog");
        if (addon != null)
        {
            addon->SetPosition(_chatSavedX, _chatSavedY);
            // IsVisible 交还 FrameworkUpdate 的正常隐藏逻辑（HideChat=true 时下帧会隐藏）
        }
    }

    public static uint GetChatLogAddonId()
    {
        // 优先返回 "ChatLog" 而不是 "ChatLogPanel_0"：
        // OwnerAddon 决定了 Dalamud OnMenuOpened 事件中的 AddonName。
        // DR/Allagan Tools 等插件只识别 "ChatLog"（switch case 里没有 "ChatLogPanel_X"），
        // 用 ChatLogPanel_0 会导致它们的菜单项被跳过（落入 default: return false）。
        var addon = GetAddon<AtkUnitBase>("ChatLog");
        if (addon == null)
            addon = GetAddon<AtkUnitBase>("ChatLogPanel_0");
        return addon != null ? (uint)addon->Id : 0;
    }

    public static void OpenItemTooltip(uint id, ItemKind itemKind)
    {
        var atkStage = AtkStage.Instance();
        var agent = AgentItemDetail.Instance();
        var addon = GetAddon<AtkUnitBase>("ItemDetail");

        // atkStage ain't gonna be null or we have bigger problems
        if (agent == null || addon == null)
            return;

        agent->DetailKind = itemKind == ItemKind.EventItem ? DetailKind.KeyItem : DetailKind.Item;
        agent->TypeOrId = id;
        agent->Index = 0;
        agent->Flag1 &= 0xEF;
        agent->ItemId = id;
        // agent->Flag2 = 1;
        // agent->Flag3 = 0;
        // TODO: Revert whenever CS is merged
        *(byte*)((nint)agent + 0x21A) = 1;
        *(byte*)((nint)agent + 0x21E) = 0;

        // This just probably needs to be set
        agent->AddonId = addon->Id;

        // Skips early return
        atkStage->TooltipManager.TooltipType |= 2;
        addon->Show(false, 15);
    }

    public static void CloseItemTooltip()
    {
        // hide addon first to prevent the "addon close" sound
        var addon = GetAddon<AtkUnitBase>("ItemDetail");
        if (addon != null)
            addon->Hide(true, false, 0);

        var agent = AgentItemDetail.Instance();
        if (agent != null)
        {
            var eventData = stackalloc AtkValue[1];
            var atkValues = stackalloc AtkValue[1];
            atkValues->Type = ValueType.Int;
            atkValues->Int = -1;
            agent->ReceiveEvent(eventData, atkValues, 1, 1);
        }
    }

    public static void OpenPartyFinder()
    {
        // this whole method: 6.05: 84433A (FF 97 ?? ?? ?? ?? 41 B4 01)
        var lfg = AgentLookingForGroup.Instance();
        if (lfg->IsAgentActive())
        {
            var addonId = lfg->GetAddonId();
            var atkModule = RaptureAtkModule.Instance();
            var atkModuleVtbl = (void**) atkModule->AtkModule.VirtualTable;
            var vf27 = (delegate* unmanaged<RaptureAtkModule*, ulong, ulong, byte>) atkModuleVtbl[27];
            vf27(atkModule, addonId, 1);
        }
        else
        {
            // 6.05: 8443DD
            if (*(uint*) ((nint) lfg + 0x2C20) > 0)
                lfg->Hide();
            else
                lfg->Show();
        }
    }

    public static bool IsMentor()
    {
        return PlayerState.Instance()->IsMentor();
    }

    public static InfoProxyCommonList.CharacterData[] GetFriends()
    {
        return InfoProxyFriendList.Instance()->CharDataSpan.ToArray();
    }

    public static void OpenQuestLog(RowRef<Quest> quest)
    {
        var splits = quest.Value.Id.ToString().Split("_");
        if (splits.Length != 2)
        {
            Plugin.ChatGui.Print("QuestId is wrongly formatted");
            return;
        }

        if (!uint.TryParse(splits[1], NumberStyles.Any, CultureInfo.InvariantCulture,  out var questId))
        {
            Plugin.ChatGui.Print("Unable to parse quest id");
            return;
        }

        AgentQuestJournal.Instance()->OpenForQuest(questId, 1);
    }

    public static void OpenPartyFinder(uint id)
    {
        AgentLookingForGroup.Instance()->OpenListing(id);
    }

    public static void OpenAchievement(uint id)
    {
        AgentAchievement.Instance()->OpenById(id);
    }

    public static bool IsInInstance()
    {
        return Plugin.Condition[ConditionFlag.BoundByDuty56];
    }

    public static bool TryOpenAdventurerPlate(ulong playerId)
    {
        try
        {
            AgentCharaCard.Instance()->OpenCharaCard(playerId);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Unable to open adventurer plate");
            return false;
        }
    }

    public static void ClickNoviceNetworkButton()
    {
        var agent = AgentChatLog.Instance();
        // case 3
        var value = new AtkValue { Type = ValueType.Int, Int = 3, };
        var result = 0;
        var vf0 = *(delegate* unmanaged<AgentChatLog*, int*, AtkValue*, ulong, ulong, int*>*) agent->VirtualTable;
        vf0(agent, &result, &value, 0, 0);
    }

    private readonly nint PlaceholderNamePtr = Marshal.AllocHGlobal(128);
    private readonly string Placeholder = $"<{Guid.NewGuid():N}>";
    private string? ReplacementName;

    private nint ResolveTextCommandPlaceholderDetour(nint a1, byte* placeholderText, byte a3, byte a4)
    {
        var placeholder = MemoryHelper.ReadStringNullTerminated((nint) placeholderText);
        if (ReplacementName == null || placeholder != Placeholder)
            return ResolveTextCommandPlaceholderHook!.Original(a1, placeholderText, a3, a4);

        MemoryHelper.WriteString(PlaceholderNamePtr, ReplacementName);
        ReplacementName = null;

        return PlaceholderNamePtr;
    }
}
