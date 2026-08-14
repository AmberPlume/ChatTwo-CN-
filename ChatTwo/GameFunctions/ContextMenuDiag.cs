// ═══════════════════════════════════════════════════════════════════════
// 右键菜单逆向诊断（可选编译）
// ═══════════════════════════════════════════════════════════════════════
// 本文件集中存放 ChatTwoCN 开发过程中的逆向验证代码（hook + dump）。
//
// 启用方法：csproj 的 <DefineConstants> 加 ENABLE_CTX_DIAG（或 Debug 构建自动启用）。
// 平时 Release 构建不包含本文件任何代码（零开销、零日志噪音）。
//
// 为什么保留而不是删除（2026-08-14 决定）：
//   游戏版本更新后，以下逆向结论需要重新验证，这些 hook/dump 就是现成工具：
//   1. handler 身份是否仍 = AgentChatLog.Instance()（HandlerID 诊断）
//   2. AddMenuItem 调用参数（AMDI hook）——生成层是否变化
//   3. eventId/hParam 语义（MenuDump）——玩家/道具菜单项是否变化
//   4. AgentContext 关键字段（B2Diag/CtxDiag）——0x1418/0x1430/0x12D0 偏移是否变化
//   5. 二级菜单 addon 名（AllAddonDiag）——AddonContextSub 是否仍是容器
//
// ⚠️ 已知崩溃源（勿重新启用）：
//   GenHook（0xed6060）：delegate 已补第 5 栈参数但 Original 后仍崩（04:01 实测），
//   禁用。需要抓生成器时用 [AMDI]（AddMenuItem hook）代替。
// ═══════════════════════════════════════════════════════════════════════
#if ENABLE_CTX_DIAG
using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Hooking;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using Dalamud.Game;
using Dalamud.Utility.Signatures;
using System.Numerics;
using ChatTwo.Util;

namespace ChatTwo.GameFunctions;

public sealed partial class ContextMenuHandler
{
    // ────────────────────────────────────────────────────────────────────
    // 诊断 hook 字段
    // ────────────────────────────────────────────────────────────────────

    [Signature("40 57 48 83 EC 40 48 8B 51 18 48 8B F9 48 85 D2 0F 84 83 03 00 00 48 8D 4A 40", DetourName = nameof(MenuGenDetour))]
    private Hook<MenuGenDelegate>? MenuGenHook = null!;
    private delegate void MenuGenDelegate(nint self);

    [Signature("40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 C8 FD FF FF 48 81 EC 68 03 00 00 48 8B 05", DetourName = nameof(CtxReceiveEventDetour))]
    private Hook<CtxReceiveEventDelegate>? CtxReceiveEventHook = null!;
    private delegate nint CtxReceiveEventDelegate(nint self, nint returnValue, nint values, uint valueCount, ulong eventKind);

    private Hook<AddMenuItemDelegate>? AddMenuItemHook = null!;
    private delegate void AddMenuItemDelegate(nint thisPtr, nint text, nint handler, long handlerParam, byte disabled, byte submenu);

    // ⚠️ GenHook（0xed6060）= 崩溃源，禁用。留注释记录签名与坑。
    // [Signature("48 89 5C 24 20 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 30 FA FF FF 48 81 EC D0 06 00 00", DetourName = nameof(GenDetour))]
    // private Hook<GenDelegate>? GenHook = null!;
    // // ⚠️ 必须有第 5 参数（栈参数，入口读 [rbp+0x630]）！只声明 4 参数 → Original 丢栈参 → 崩溃（03:56 实测）
    // private delegate void GenDelegate(nint self, nint a2, nint a3, nint a4, nint a5);

    // ────────────────────────────────────────────────────────────────────
    // hook 初始化 / 释放
    // ────────────────────────────────────────────────────────────────────

    public void InitDiagnostics()
    {
        Plugin.GameInteropProvider.InitializeFromAttributes(this);
        if (MenuGenHook != null)
        {
            MenuGenHook.Enable();
            Plugin.Log.Error($"[B2Hook] 0x4b0e70 hook 已启用 addr=0x{(nint)MenuGenHook.Address:X}");
        }
        else
        {
            Plugin.Log.Error("[B2Hook] 0x4b0e70 hook 初始化失败（签名未命中）");
        }
        if (CtxReceiveEventHook != null)
        {
            CtxReceiveEventHook.Enable();
            Plugin.Log.Error($"[CtxEVT] 0x4e0a10 hook 已启用 addr=0x{(nint)CtxReceiveEventHook.Address:X}");
        }
        else
        {
            Plugin.Log.Error("[CtxEVT] 0x4e0a10 hook 初始化失败（签名未命中）");
        }

        // [AMDI] hook AddMenuItem（FFXIVClientStructs 解析地址），观测所有菜单项生成调用
        AddMenuItemHook = Plugin.GameInteropProvider.HookFromAddress<AddMenuItemDelegate>(
            (nint)FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentContext.Addresses.AddMenuItem.Value, AddMenuItemDetour);
        AddMenuItemHook.Enable();
        Plugin.Log.Error($"[AMDI] AddMenuItem hook 已启用 addr=0x{(nint)AgentContext.Addresses.AddMenuItem.Value:X}");
    }

    public void DisposeDiagnostics()
    {
        MenuGenHook?.Dispose();
        CtxReceiveEventHook?.Dispose();
        AddMenuItemHook?.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────
    // detours
    // ────────────────────────────────────────────────────────────────────

    private void MenuGenDetour(nint self)
    {
        try
        {
            unsafe
            {
                var b = (byte*)self;
                var sub = *(nint*)(b + 0x18);
                Plugin.Log.Error($"[B2Hook] 生成器被调 self=0x{self:X} [self+0x18]=0x{sub:X} [self+0x10]=0x{*(nint*)(b + 0x10):X} [self+0x20]=0x{*(nint*)(b + 0x20):X} head={HexBytes(b, 32)}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[B2Hook] error {ex.Message}");
        }
        MenuGenHook!.Original(self);
    }

    private nint CtxReceiveEventDetour(nint self, nint returnValue, nint values, uint valueCount, ulong eventKind)
    {
        try
        {
            unsafe
            {
                if (values == 0)
                {
                    Plugin.Log.Error($"[CtxEVT] values=0 kind={eventKind} vc={valueCount}");
                }
                else
                {
                    var b = (byte*)values;
                    var evtType = *b;
                    Plugin.Log.Error($"[CtxEVT] evtType={evtType} kind={eventKind} vc={valueCount} " +
                                     $"v0=0x{*(nint*)(b):X} v8=0x{*(nint*)(b + 8):X} v10=0x{*(nint*)(b + 0x10):X} v20=0x{*(nint*)(b + 0x20):X} " +
                                     $"ret=0x{returnValue:X} self=0x{self:X}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[CtxEVT] error {ex.Message}");
        }
        return CtxReceiveEventHook!.Original(self, returnValue, values, valueCount, eventKind);
    }

    private void AddMenuItemDetour(nint thisPtr, nint text, nint handler, long handlerParam, byte disabled, byte submenu)
    {
        try
        {
            Plugin.Log.Error($"[AMDI] AddMenuItem this=0x{thisPtr:X} handler=0x{handler:X} param=0x{handlerParam:X} disabled={disabled} sub={submenu}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[AMDI] error {ex.Message}");
        }
        AddMenuItemHook!.Original(thisPtr, text, handler, handlerParam, disabled, submenu);
    }

    // ⚠️ 崩溃源，勿启用（见文件头注释）。保留签名备查。
    // private void GenDetour(nint self, nint a2, nint a3, nint a4, nint a5) { ... }

    private static unsafe string HexBytes(byte* p, int n)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < n; i++)
            sb.Append(p[i].ToString("X2"));
        return sb.ToString();
    }

    // ────────────────────────────────────────────────────────────────────
    // OnMenuOpened 诊断 dump（OnMenuOpened 开头调用）
    // ────────────────────────────────────────────────────────────────────

    public void DumpOnMenuOpened(IMenuOpenedArgs args)
    {
        DumpB2Fields(args);
        DumpContextFields(args);
        DumpNativeMenuItems(args);
        DumpHandlerId(args);
    }

    /// <summary>[B2Diag] dump B2 关键字段（原生 vs 插件对比）。</summary>
    private static unsafe void DumpB2Fields(IMenuOpenedArgs args)
    {
        try
        {
            var agent = AgentContext.Instance();
            if (agent == null)
                return;

            var b = (byte*)agent;
            var vtable1418 = *(nint*)(b + 0x1418);      // 内联 handler 的 vtable 指针
            var sub1430 = *(nint*)(b + 0x1430);          // 0x4b0e70 首读的 [this+0x18]
            var h12D0 = *(nint*)(b + 0x12D0);            // B2c 候选缓存
            Plugin.Log.Error($"[B2Diag] addon={args.AddonName} ours={IsChatTwoTriggered} vtable1418=0x{vtable1418:X} sub1430=0x{sub1430:X} h12D0=0x{h12D0:X}");
            if (sub1430 != 0)
            {
                var sb = (byte*)sub1430;
                Plugin.Log.Error($"[B2Diag]   sub1430->[+0x12d0]=0x{*(nint*)(sb + 0x12d0):X} sub1430->[+0x40]=0x{*(nint*)(sb + 0x40):X}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[B2Diag] error {ex.Message}");
        }
    }

    /// <summary>[CtxDiag] dump AgentContext 字段（对比原生 vs 手动）。</summary>
    private static unsafe void DumpContextFields(IMenuOpenedArgs args)
    {
        try
        {
            var agent = AgentContext.Instance();
            if (agent == null)
                return;

            var b = (byte*)agent;
            var cid = *(ulong*)(b + 0x470);
            var hw = *(short*)(b + 0x478);
            var aid = *(ulong*)(b + 0x480);
            var oid = *(uint*)(b + 0x488);
            var sex = *(byte*)(b + 0x48c);
            var mount = *(byte*)(b + 0x48d);
            var owner = *(byte*)(b + 0x48e);
            var curTarget = *(nint*)(b + 0x120);
            Plugin.Log.Error($"[CtxDiag] addon={args.AddonName} ours={IsChatTwoTriggered} ContentId={cid} HomeWorld={hw} AccountId={aid} ObjId={oid} Sex={sex} MountSeats={mount} Owner={owner} CurTargetPtr=0x{curTarget:X}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[CtxDiag] error {ex.Message}");
        }
    }

    /// <summary>[MenuDump] dump 原生菜单项 eventId+文本+handler+hParam。</summary>
    private static unsafe void DumpNativeMenuItems(IMenuOpenedArgs args)
    {
        try
        {
            var agent = AgentContext.Instance();
            if (agent == null)
                return;
            var ctx = agent->CurrentContextMenu;
            if (ctx == null)
                return;
            var menu = (byte*)ctx;
            var count = menu[0]; // CurrentEventIndex
            Plugin.Log.Error($"[MenuDump] addon={args.AddonName} ours={IsChatTwoTriggered} ctx=0x{(nint)ctx:X} mainCtx=0x{(nint)(&agent->MainContextMenu):X}");
            Plugin.Log.Error($"[MenuDump] count={count - 8} CurrentEventIndex={count} blockFlags={(byte)agent->ContextMenuBlockFunctionsFlags} menuIndex={agent->ContextMenuIndex} subMask=0x{menu[0x694 - 0x600 + 0x600 - 0x600]:X} disMask=0x{menu[0x690 - 0x600 + 0x600 - 0x600]:X}");
            // eventIds 数组完整内容（偏移 0x448，34 字节）
            var ids = new List<string>();
            for (var i = 0; i < 34; i++)
                ids.Add(menu[0x448 + i].ToString());
            Plugin.Log.Error($"[MenuDump] eventIds@0x448: {string.Join(",", ids)}");

            // 逐项 dump 文本（AtkValue stride 16，菜单项从 index 8 开始）+ eventId + handler + handlerParam
            for (var i = 8; i < count && i < 40; i++)
            {
                var atk = (AtkValue*)(menu + 8 + i * 16);
                var id = menu[0x448 + i];
                var handler = *(nint*)(menu + 0x470 + i * 8);
                var hParam = *(long*)(menu + 0x580 + i * 8);
                Plugin.Log.Error($"[MenuDump]   [{i - 8}] text='{atk->GetValueAsString()}' id={id} handler=0x{handler:X} hParam=0x{hParam:X}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[MenuDump] error {ex.Message}");
        }
    }

    /// <summary>[HandlerID] 对比菜单 handler 与常驻对象指针（2026-08-14 已证实 = AgentChatLog）。</summary>
    private static unsafe void DumpHandlerId(IMenuOpenedArgs args)
    {
        try
        {
            var agent = AgentContext.Instance();
            if (agent == null)
                return;
            var menu = agent->CurrentContextMenu;
            if (menu == null)
                return;
            var handler = *(nint*)((byte*)menu + 0x470 + 8 * 8);
            var chatLogAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentChatLog.Instance();
            var chatLogAddon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName("ChatLog");
            Plugin.Log.Error($"[HandlerID] handler=0x{handler:X} AgentChatLog=0x{(nint)chatLogAgent:X} AddonChatLog=0x{(nint)chatLogAddon:X} AgentCtx=0x{(nint)agent:X} matchACL={(nint)chatLogAgent == handler} matchAddon={(nint)chatLogAddon == handler} matchCtx={(nint)agent == handler}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[HandlerID] error {ex.Message}");
        }
    }
}
#endif
