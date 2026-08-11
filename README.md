# ChatTwo-CN

基于 [ChatTwo](https://github.com/SitiSchu/ChatTwo)（EUPL v1.2 许可）进行了一定程度的修改，对原生消息栏进行了拙劣的模仿。

主要改动方向：中文化、设置精简、输入框/字体行为调整、右键菜单（原生菜单项复刻）等。

## 已知问题

- **右键菜单的占位文本无法移除**：ChatTwo 隐藏了游戏原生聊天框（ChatLog），原生菜单项的构建流程也随之失效。我们通过 Dalamud ContextMenu API 注入了自定义菜单项，但当一个菜单没有任何可用项目时，游戏会显示"没有可以选择的指令"占位文本——该文本由原生 ChatLog 的菜单逻辑生成，而 ChatLog 已被隐藏且无法调用其清除逻辑，因此这一占位文本无法被移除。
- 其他：原生聊天框的某些行为（如输入法候选框位置、部分原生菜单联动）可能与原版存在差异。

## 构建

```sh
cd ChatTwo
dotnet build -c Release
```

输出：`ChatTwo/bin/Release/ChatTwo.dll`

## 许可

本项目基于 [ChatTwo](https://github.com/SitiSchu/ChatTwo) 修改，遵循 [EUPL v1.2](LICENCE)（copyleft）许可。
