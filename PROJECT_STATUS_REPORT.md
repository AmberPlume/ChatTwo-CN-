# ChatTwo 原生右键菜单集成 - 项目状态报告

## 一、项目目标

在 ChatTwo 插件聊天框中右键玩家名/道具链接时，触发 FFXIV 原生右键菜单（ContextMenu addon），满足：

1. **跟随聊天框位置**（仅聊天框触发的菜单）
2. **显示正确目标的内容**（非上次缓存的菜单）
3. **第三方插件菜单项正常出现**（DailyRoutines、Allagan Tools 等）

## 二、代码结构介绍

### 核心文件

| 文件路径 | 作用 |
|---|---|
| `ChatTwo/Ui/Handler/PayloadHandler.cs` | 右键事件入口，包含 `TryShowNativePlayerContextMenu`（L453）和 `TryShowNativeItemContextMenu`（L554）|
| `ChatTwo/Ui/ChatLog/ChatLog.Tooltip.cs` | 菜单位置跟随逻辑 `MoveContextMenu`（L124）、菜单关闭 `OnContextMenuClosed`（L178）、关闭菜单 `CloseNativeContextMenu`（L195）|
| `ChatTwo/Plugin.cs` | `ContextMenuActive` 字段（L57）、`FrameworkUpdate` 中 ContextMenuActive 时跳过隐藏游戏聊天框（L282）|
| `ChatTwo/GameFunctions/GameFunctions.cs` | `SetChatInteractable` 临时显示/隐藏游戏聊天框、`GetChatLogAddonId` 获取聊天框 addon ID |
| `ChatTwo/Ui/ChatLog/ChatLog.Window.cs` | 左键点击聊天框内容区域时调用 `CloseNativeContextMenu`（L869）|
| `ChatTwo/ContextMenuIntegration.cs` | 已简化为空壳（曾包含全局 Hook，已移除）|

### 关键函数

#### `TryShowNativePlayerContextMenu`（PayloadHandler.cs L453）
触发玩家右键菜单的流程：
1. 设置 `AgentContext` 目标字段：`TargetContentId`、`TargetHomeWorldId`、`TargetObjectId`、`TargetName`
2. 设置 `OwnerAddon` 为 ChatLog addon ID
3. 计算菜单位置（聊天框右侧）
4. 临时显示游戏聊天框面板（`SetChatInteractable(true)`）
5. 调用 `agent->OpenContextMenuForAddon(chatLogAddonId, true)` 显示菜单

#### `TryShowNativeItemContextMenu`（PayloadHandler.cs L554）
触发道具右键菜单的流程：
1. 设置 `AgentChatLog.ContextItemId`
2. 设置 `AgentContext.OwnerAddon` 为 ChatLog addon ID
3. 临时显示游戏聊天框面板
4. 调用 `agent->OpenContextMenuForAddon(chatLogAddonId, true)` 显示菜单

#### `MoveContextMenu`（ChatLog.Tooltip.cs L124）
PreDraw 回调，每帧将 ContextMenu addon 移动到聊天框右侧：
- 检查 `Plugin.ContextMenuActive` 是否为 true
- 检查 `agent->OwnerAddon == ChatLog addon id`（非聊天框菜单立即重置）
- 菜单激活期间保持游戏聊天框面板可见
- 使用 `addon->SetPosition()` 移动菜单（不能用直接赋值 X/Y）

## 三、开发进度

### 已解决

| 问题 | 解决方案 | 文件 |
|---|---|---|
| 菜单不跟随聊天框位置 | `MoveContextMenu` 在 PreDraw 中持续覆盖位置 | `ChatLog.Tooltip.cs` |
| 非聊天框菜单也跟随 | 检查 `agent->OwnerAddon != ChatLog addon id` 时重置 `ContextMenuActive=false` | `ChatLog.Tooltip.cs` L140 |
| 菜单关闭后仍跟随 | `OnContextMenuClosed` 设 `ContextMenuActive=false`；addon 不可见时也重置 | `ChatLog.Tooltip.cs` L156, L178 |
| `bindToOwner=true` 子菜单崩溃 | 临时显示游戏聊天框面板（`SetChatInteractable(true)`） | `PayloadHandler.cs` L516 |
| 小队列表菜单错位 | `MoveContextMenu` 检查 `OwnerAddon != ChatLog` 时立即重置 | `ChatLog.Tooltip.cs` L140 |
| 左键点击空白不关闭菜单 | 新增 `CloseNativeContextMenu()`（`FireCallbackInt(-1)`） | `ChatLog.Tooltip.cs` L195 |
| 第三方插件菜单项（DR/Allagan Tools） | `OpenContextMenuForAddon` 触发 `OnMenuOpened` | 自动 |

### 未解决：原生菜单项不构建

**现象**：
- 不先打开原生菜单 → ChatTwo 菜单为空（"没有可以选择的指令"）
- 先打开原生菜单 → ChatTwo 菜单显示**上次的内容**（如玩家显示道具菜单的"试穿"）

## 四、根因分析（已通过反编译确认）

### 游戏原生菜单构建流程

```
用户右键 ChatLog 中的玩家名
  ↓
ChatLog addon 的 C++ 代码：
  1. 设置 AgentContext 目标字段（TargetName/TargetContentId 等）
  2. 调用 AddContextMenuItem2(eventId, addonTextId, ...) 添加原生菜单项
     （发送悄悄话、邀请组队、查看信息等）
  3. 调用 OpenContextMenu 显示菜单
  ↓
Dalamud detour vtable[22]:
  - 触发 OnMenuOpened 事件
  - DailyRoutines/Allagan Tools 等插件追加自定义菜单项
```

### ChatTwo 的问题

ChatTwo 隐藏了原生 ChatLog addon，因此**步骤 2（构建原生菜单项）不会执行**。我们只在步骤 1（设置目标字段）和步骤 3（显示菜单）做了手动处理。

### 关键发现（反编译 Dalamud ContextMenu.cs）

```csharp
// vtable[22] detour
var count = (int)values[0].UInt;  // 原生菜单项数量
var menu = AgentContext.Instance()->CurrentContextMenu;
var handlers = menu->EventHandlers.Slice(7, count);
var ids = menu->EventIds.Slice(7, count);
```

- `values[0]` = 原生菜单项数量，由 `OpenContextMenuForAddon` 从 `ContextMenu` struct 读取构建
- `ClearMenu()` 清空 struct → `values[0]=0` → 空菜单
- `ReceiveEvent(eventKind=1)` 不构建原生菜单项，只播放声音
- `OpenContextMenuForAddon` 不重建菜单项，只从 struct 读取

### 已尝试方案及结果

| 调用方式 | 结果 |
|---|---|
| `ReceiveEvent(0) + OpenContextMenu(true,false)` | 菜单显示，但内容是上次的 |
| `ReceiveEvent(1)` 单独 | 只有声音，菜单不显示 |
| `ReceiveEvent(1) + OpenContextMenu(true,false)` | 菜单显示，内容是上次的 |
| `OpenContextMenu(false,true) + ReceiveEvent(1)` | 菜单显示，内容是上次的 |
| `ReceiveEvent(1) + OpenContextMenu(false,true)` | 菜单显示，内容是上次的 |
| `OpenContextMenuForAddon(id, true)` 单独 | 菜单显示，内容是上次的（或空） |
| `ClearMenu() + ReceiveEvent(1) + OpenContextMenuForAddon(id, true)` | **空菜单** |

### 第三方插件研究结论

研究了 DailyRoutines 和 OmenTools 源码：
- **DailyRoutines** 不构建原生菜单项，只订阅 `OnMenuOpened` 追加自定义项
- **DailyRoutines 的 `FastRidePillion.cs`** hook 了 `AgentContext.ReceiveEvent` 拦截点击事件，但不参与菜单构建
- **OmenTools 的 `ContextMenuItemManager`** 只读取 `AgentChatLog.ContextItemId`，不构建菜单项
- **两者都依赖游戏原生 ChatLog 先构建好原生菜单项**

参考资源：
- `E:\处理站\Claude\DailyRoutines.ModulesPublic-main\Interface\ExpandPlayerMenuSearch.cs` — 订阅 OnMenuOpened 的示例
- `E:\处理站\Claude\DailyRoutines.ModulesPublic-main\System\FastRidePillion.cs` — hook AgentContext.ReceiveEvent 的示例
- `E:\处理站\Claude\OmenTools-main\OmenTools-main\OmenService\Implementations\Managers\ContextMenuItemManager.cs` — 读取 AgentChatLog.ContextItemId 的示例

## 五、当前方案建议

### 方案A（推荐）：Dalamud ContextMenu API 添加自定义菜单项

**原理**：
- 订阅 Dalamud `OnMenuOpened` 事件
- 在事件回调中通过 `args.AddMenuItem()` 添加自定义菜单项
- 外观完全原生（通过游戏 `AddContextMenuItem` 添加到 struct，与原生菜单项一起渲染）
- 不是 ImGui 绘制

**优点**：
- 简单可靠，不依赖缓存
- DR、Allagan Tools 等插件菜单项自动出现
- ChatTwo 已有大部分实现（`Chat.SendTell`、`Context_SendTell`/`Context_CopyName` 本地化字符串）

**缺点**：
- 不会有"邀请组队"、"查看信息"等原生菜单项（除非自己实现）
- 需要实现菜单项的点击动作

**实现工作量**：
| 菜单项 | 文本 | 点击动作 | 实现难度 |
|---|---|---|---|
| 发送悄悄话 | `Context_SendTell`（已有） | 打开悄悄话输入框 | 已实现 |
| 复制名字 | `Context_CopyName`（已有） | `ImGui.SetClipboardText(name)` | 简单 |
| 邀请组队 | 需新增 | 调用组队邀请 API | 中等 |
| 查看信息 | 需新增 | 调用 Examine API | 中等 |

### 方案B：手动调用 `AddContextMenuItem2`

**原理**：手动调用 `AgentContext.AddContextMenuItem2(eventId, addonTextId, ...)` 添加原生菜单项

**优点**：效果最原生，真正的原生菜单项

**缺点**：
- 需要硬编码 eventId/addonTextId 列表
- 需要处理各种条件（是否在队伍、是否好友等）
- 工作量大

### 方案C：触发原生 ChatLog 流程

**原理**：通过调用 ChatLog addon 的 ReceiveEvent/FireCallback 模拟右键点击事件，让游戏原生 C++ 代码执行菜单构建

**优点**：最理想，完全原生

**缺点**：最复杂，需要逆向分析 ChatLog 的事件参数格式

## 六、建议的下一步

采用**方案A**：

1. **在 `PayloadHandler.cs` 的 `TryShowNativePlayerContextMenu` 中**：
   - 保留 `OpenContextMenuForAddon` 触发 `OnMenuOpened`（让 DR 等插件菜单项出现）
   - 在 `Plugin.cs` 初始化时订阅 `Plugin.ContextMenu.OnMenuOpened` 事件
   - 在事件回调中通过 `args.AddMenuItem()` 添加"发送悄悄话"和"复制名字"

2. **参考 ChatTwo 已有的 ImGui fallback 菜单**（PayloadHandler.cs L741-L753）：
   ```csharp
   // 发送悄悄话（非自己）
   if (!isSelf && ImGui.Selectable(Language.Context_SendTell))
   {
       InputHandler.ChatInput = $"/tell {player.PlayerName}";
       if (world.Value.IsPublic)
           InputHandler.ChatInput += $"@{world.Value.Name}";
       InputHandler.ChatInput += " ";
       InputHandler.Activate = true;
   }

   // 复制名字
   if (ImGui.Selectable(Language.Context_CopyItemName))
       ImGui.SetClipboardText(player.PlayerName);
   ```

3. **复用已有实现**：
   - `Chat.SendTell`（Chat.cs L508）已实现发送悄悄话
   - `Context_SendTell`/`Context_CopyName` 本地化字符串已存在
   - DR 等插件菜单项自动出现，不需要额外工作

## 七、DLL 输出路径

`e:\处理站\Claude\ChatTwo-main\ChatTwo\bin\Release\ChatTwo.dll`

构建命令：`cd "e:\处理站\Claude\ChatTwo-main\ChatTwo" ; dotnet build -c Release`

测试：用 `/xlreload` 重新加载插件

## 八、关键硬约束（来自项目记忆）

- 右键玩家名/道具链接时，调用 `AgentContext.ReceiveEvent(eventData, atkValues, 1, 0)` 触发原生菜单，使用 `agent->SetPosition(gameX, gameY)` 设置菜单位置
- 必须使用非空 AtkValue 事件数据调用 ReceiveEvent，参考 AgentItemDetail 用法（`stackalloc AtkValue[1]`，`Int=-1`）
- 道具右键菜单需设置 `AgentChatLog.ContextItemId`，确保 DailyRoutines 等插件能识别道具
- 所有 Hook 回调必须包含三层 try/catch 异常处理，防止游戏崩溃
- 必须修复 `AtkUnitBasePtr` 到 `AtkUnitBase*` 的类型转换问题，通过 `args.Addon.Address` 获取原始指针再转换
- 原生菜单触发失败时，回退到 ImGui 弹窗（仅显示发送悄悄话和复制名字）
- 菜单位置计算方式：`(聊天框X + 聊天框宽度 + 10) / 全局缩放 = 游戏UI坐标X`，`聊天框Y / 全局缩放 = 游戏UI坐标Y`
- `eventKind=1` 用于触发上下文菜单，`eventKind=0` 会触发默认操作（查看冒险者铭牌）
- 对不在当前世界的玩家调用 ReceiveEvent 会导致时空指针崩溃，需通过 ObjectTable 检查玩家在场状态
- 菜单位置更新必须使用 `AtkUnitBase->SetPosition()` 而非直接赋值 X/Y
- `OpenContextMenu/OpenContextMenuForAddon` 会绕过游戏菜单创建流程，导致 Dalamud 的 OnMenuOpened 事件不触发（**注：此约束已过时，OpenContextMenuForAddon 内部调用 vtable[22] 会触发 OnMenuOpened**）
