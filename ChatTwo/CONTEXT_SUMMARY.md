# ChatTwo 开发上下文总结

## 项目概述
ChatTwo 是一个 FFXIV 的 Dalamud 聊天插件，用 ImGui 替代游戏原生聊天窗口。

## 当前进行中的功能

### 1. 右键菜单（Context Menu）
**目标**：右键点击聊天框中的玩家名字或道具链接时，显示自定义 ImGui 菜单，包含原生菜单内容和插件添加的选项。

**核心实现**：
- `ContextMenuIntegration.cs`：通过 Hook `AddContextMenuItem` 捕获所有菜单项
- 两阶段捕获策略（玩家和道具菜单均使用）：
  - 阶段1：直接调用 `AddContextMenuItem` / `ReceiveEvent` 捕获原生项和 Hook 链上的插件项
  - 阶段2：延迟1帧让原生菜单打开（触发 `OnMenuOpened`），捕获使用 Dalamud ContextMenu API 的插件（DailyRoutines、Market Board 等）
- 玩家菜单：通过 `ReceiveEvent(null, null, 0, 0)` 触发原生菜单，延迟回调打开弹窗
- 道具菜单：通过 `AddItemContextMenuItems` 直接调用（`ReceiveEvent` 对道具会崩溃），延迟回调打开弹窗
- 道具菜单仅保留 6 项基础通用菜单（Try On、Item Comparison、Search for Recipes、Search for Item、Link、Copy Item Name）
- 已移除手写的 Examine 和 Copy Name 菜单项

**关键文件**：
- `ChatTwo\GameFunctions\ContextMenuIntegration.cs` - 核心捕获逻辑
- `ChatTwo\Ui\Handler\PayloadHandler.cs` - 右键事件处理和弹窗渲染
- `ChatTwo\GameFunctions\GameFunctions.cs` - 获取 ChatLog Addon ID

**已知问题**：
- 道具菜单仍可能无法捕获部分使用 Dalamud ContextMenu API 的插件（DailyRoutines、Market Board），因为这些插件可能需要 `OnMenuOpened` 事件触发，而 `AddItemContextMenuItems` 不一定会触发原生菜单打开

### 2. 屏蔽词过滤（Censor Filter）
**目标**：对玩家聊天频道的屏蔽词进行标红处理，不处理系统消息。

**核心实现**：
- `CensorFilter.cs`：Hook 游戏的 `GetFilteredUtf8String` 函数获取屏蔽后的文本
- 使用 `SeStringBuilder.AddUiForeground(text, 17)` 对标红段进行颜色标记（与 DailyRoutines 相同方案）
- `SplitByCensor`：字符级比对算法，同时遍历原始字符串和过滤后字符串，正确识别被屏蔽段
- 仅在 `MessageManager.ProcessMessage` 中对玩家消息（`chatCode.IsPlayerMessage()`）处理

**关键文件**：
- `ChatTwo\Util\CensorFilter.cs` - 屏蔽词检测和标红
- `ChatTwo\MessageManager.cs` - 消息处理入口

**已修复的 Bug**：
- 屏蔽词拆分错误（如"犎牛牛排"中"牛牛"被屏蔽导致"牛牛排"标红）
- 颜色无法恢复（后续文本全红）- 改用 `SeStringBuilder.AddUiForeground` 方案

### 3. 场景加载时输入打断
**目标**：场景加载/切换地图时，聊天框输入不被中断，IME 输入法状态保持。

**实现**：
- `Chat.cs` 的 `ChatLogRefreshDetour` 中检查 `InputFocused`，用户正在输入时跳过 `Activated` 调用

**关键文件**：
- `ChatTwo\GameFunctions\Chat.cs`

## 项目结构
```
ChatTwo-main\ChatTwo\
├── Util\
│   ├── CensorFilter.cs        # 屏蔽词过滤
│   ├── ChunkUtil.cs           # SeString → Chunk 转换
│   ├── DebugLog.cs            # 崩溃安全调试日志
│   └── ExtraPayload.cs        # ColorPayload 解析
├── GameFunctions\
│   ├── ContextMenuIntegration.cs  # 右键菜单捕获
│   ├── Chat.cs                # 聊天事件处理
│   ├── Context.cs             # 上下文操作（LinkItem 等）
│   └── GameFunctions.cs       # 游戏功能封装
├── Ui\
│   └── Handler\
│       └── PayloadHandler.cs  # 载荷交互处理（右键菜单、弹窗）
├── MessageManager.cs          # 消息管理
├── Message.cs                 # 消息数据结构
└── Plugin.cs                  # 插件入口
```

## 重要约束
- 屏蔽词过滤仅对玩家聊天频道生效（Say、Tell、Party、LS、CWLS、FC、Yell 等）
- 系统消息（System、BattleSystem、Error）不得处理
- 菜单捕获必须在非 ImGui 渲染阶段调用游戏函数
- 道具菜单不得调用 `agent->ReceiveEvent(null, null, 0, 1)`（会导致崩溃）
- 道具菜单捕获后必须立即重置 `OwnerAddon` 为 0（保持设置会导致崩溃）
- 使用 `AgentChatLog.Instance()->LinkItem(itemId)` 而非硬编码偏移量设置道具 ID

## 当前待修复问题
1. **道具菜单无法捕获 DailyRoutines/Market Board 等使用 OnMenuOpened 的插件**：`AddItemContextMenuItems` 不触发原生菜单打开，`ReceiveEvent` 对道具会崩溃。需要找到不崩溃的方式触发原生道具菜单打开。
2. **右键道具链接偶尔粘贴链接到聊天框**：已通过延迟1帧打开弹窗修复（待验证）。

## 测试验证
- 屏蔽词：测试"犎牛牛排"、"可靠的队友"、"山鸡革强袭手套"、"花花公子褶边裤"等案例
- 右键玩家名字：菜单应包含原生选项和插件选项
- 右键道具链接：菜单应包含基础选项，不崩溃
- 场景加载：输入不被中断，IME 状态保持
- 调试日志：`%APPDATA%\XIVLauncherCN\chat2_debug.log`