using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

public sealed class Display : ISettingsTab
{
    private Configuration Mutable { get; }

    public string Name => Language.Options_Display_Tab + "###tabs-display";

    public Display(Configuration mutable)
    {
        Mutable = mutable;
    }

    public void Draw(bool changed)
    {
        using var wrap = ImRaii.TextWrapPos(0.0f);

        ImGuiUtil.OptionCheckbox(ref Mutable.HideChat, Language.Options_HideChat_Name, Language.Options_HideChat_Description);
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideDuringCutscenes, Language.Options_HideDuringCutscenes_Name, string.Format(Language.Options_HideDuringCutscenes_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideWhenNotLoggedIn, Language.Options_HideWhenNotLoggedIn_Name, string.Format(Language.Options_HideWhenNotLoggedIn_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideWhenUiHidden, Language.Options_HideWhenUiHidden_Name, string.Format(Language.Options_HideWhenUiHidden_Description, Plugin.PluginName));
        ImGui.Spacing();

        ImGuiUtil.OptionCheckbox(ref Mutable.HideInLoadingScreens, Language.Options_HideInLoadingScreens_Name, string.Format(Language.Options_HideInLoadingScreens_Description, Plugin.PluginName));
        ImGui.Spacing();

        // "在战斗中隐藏" / "非活动时隐藏"已删除：保持关闭

        ImGui.Separator();
        ImGui.Spacing();

        // ═══════════════ 窗口 ═══════════════
        // 以下项从"窗口调整"页挪入（基础设置统一管窗口行为）
        // 显示时间戳：已移至"消息设置"页的时间戳菜单（v1.40.17+）
        // 仿原生窗口：已移至"窗口调整"页（v1.40.17+，改名"仿原生窗口"）

        // 锁定窗口移动（锁按钮从工具栏移除，改回设置项；
        // 开启后只有消息区不可拖动（选字防误拖），窗口其他区域仍可拖）
        ImGuiUtil.OptionCheckbox(ref Mutable.MoveLocked, Language.Options_MoveLocked_Name, Language.Options_MoveLocked_Description);
        ImGui.Spacing();

        // 标签页位置：仿原生窗口（NativeBackground）下固定底部（三段式贴图只支持底部），选项置灰。
        // !!! Disabled 必须限制在块级作用域——用 using var 会延到方法末尾，把下面所有设置项一起置灰
        using (ImRaii.Disabled(Mutable.NativeBackground))
        {
            using (var tabCombo = ImGuiUtil.BeginComboVertical(Language.Options_TabPosition_Name, Mutable.TabPosition.Name()))
            {
                if (tabCombo.Success)
                {
                    // 顶部选项已移除（窗口背景完全透明后顶部标签页无原生布局支撑）
                    foreach (var tabPos in Enum.GetValues<TabPosition>())
                    {
                        if (tabPos == TabPosition.Top)
                            continue;
                        if (ImGui.Selectable(tabPos.Name(), Mutable.TabPosition == tabPos))
                            Mutable.TabPosition = tabPos;
                    }
                }
            }
            ImGuiUtil.TooltipOnLastItem(Language.Options_TabPosition_Description);   // 悬浮下拉框即出说明
        }
        ImGui.Spacing();

        // 未读消息提示方式（放在基础设置）：高亮/呼吸/无
        var currentUnread = Mutable.UnreadNotifyMode;
        using (var combo = ImRaii.Combo(Language.Options_UnreadNotifyMode_Name, currentUnread.Name()))
        {
            if (combo)
            {
                foreach (UnreadNotifyMode mode in Enum.GetValues<UnreadNotifyMode>())
                    if (ImGui.Selectable(mode.Name(), currentUnread == mode))
                        Mutable.UnreadNotifyMode = mode;
            }
        }
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();

        // 保持输入焦点（与显示时间戳互换位置）
        ImGuiUtil.OptionCheckbox(ref Mutable.KeepInputFocus, Language.Options_KeepInputFocus_Name, Language.Options_KeepInputFocus_Description);
        ImGui.Spacing();

        // 定型文排序调整（原"定型文列表排序"，改名）
        ImGuiUtil.OptionCheckbox(ref Mutable.SortAutoTranslate, Language.Options_SortAutoTranslate_Name, Language.Options_SortAutoTranslate_Description);
        ImGui.Spacing();

        // "现代化布局/更紧凑的现代布局/隐藏重复的时间戳"已删除：保持传统时间戳样式
        // "折叠重复消息"及其子选项已删除

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 字体相关设置已拆分到独立"字体设置"页（v1.40.11+）

        ImGui.Spacing();

        // ═══════════════ 从原版 Chat Two 迁移 ═══════════════
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("从原版 Chat Two 迁移");
        ImGuiUtil.TooltipOnLastItem("迁移原版 Chat Two（InternalName=ChatTwo）的设置与聊天历史到本插件。设置立即生效；聊天历史在重启游戏后自动导入。");
        DrawMigrationSection();
        ImGui.Spacing();
    }

    // 从原版 ChatTwo 迁移配置/历史（原版配置: pluginConfigs/ChatTwo.json，库: pluginConfigs/ChatTwo/chat-sqlite.db）
    // 原库/目标库在运行中都可能被插件连接占用，不能文件复制；配置也不能整文件覆盖
    // （会丢 CN 特有字段）。配置 = 白名单字段合并立即生效；历史 = 写标记重启后在线导入。
    private void DrawMigrationSection()
    {
        var parentDir = Plugin.Interface.ConfigDirectory.Parent;
        if (parentDir == null)
            return;

        var srcConfig = Path.Combine(parentDir.FullName, "ChatTwo.json");
        var srcDb = Path.Combine(parentDir.FullName, "ChatTwo", "chat-sqlite.db");

        if (Plugin.Config.MigratedFromChatTwo)
        {
            var pending = Plugin.Config.PendingDbImportSource;
            ImGui.TextColored(new System.Numerics.Vector4(0.49f, 0.78f, 0.49f, 1f),
                string.IsNullOrEmpty(pending)
                    ? "已从原版 Chat Two 迁移设置。"
                    : "设置已迁移。聊天历史将在重启游戏后自动导入。");

            // 补救入口（防重复仍有效：重新迁移需二次确认；取消待导入用于放弃失败重试）
            if (ImGui.Button("重新迁移"))
                ImGui.OpenPopup("chat2-re-migrate");
            if (!string.IsNullOrEmpty(pending))
            {
                ImGui.SameLine();
                if (ImGui.Button("取消待导入"))
                {
                    Plugin.Config.PendingDbImportSource = null;
                    Mutable.PendingDbImportSource = null;
                    Plugin.Interface.SavePluginConfig(Plugin.Config);
                }
            }

            DrawReMigratePopup(srcConfig, srcDb);
            return;
        }

        if (!File.Exists(srcConfig))
        {
            ImGui.TextUnformatted("未找到原版配置（pluginConfigs/ChatTwo.json）。");
            return;
        }

        var alsoDb = _migrateAlsoDb;
        ImGui.Checkbox("同时迁移聊天历史（数据库）", ref alsoDb);
        _migrateAlsoDb = alsoDb;
        if (alsoDb && !File.Exists(srcDb))
            ImGui.TextUnformatted("未找到原版聊天历史数据库（pluginConfigs/ChatTwo/chat-sqlite.db），本次仅迁移设置。");

        if (ImGui.Button("迁移设置"))
            DoMigrate(srcConfig, srcDb);
    }

    // 重新迁移二次确认弹窗（防止误点反复合并配置/反复写导入标记）
    private void DrawReMigratePopup(string srcConfig, string srcDb)
    {
        using var popup = ImRaii.Popup("chat2-re-migrate");
        if (!popup)
            return;

        if (!File.Exists(srcConfig))
        {
            ImGui.TextUnformatted("未找到原版配置（pluginConfigs/ChatTwo.json），无法重新迁移。");
            ImGui.Spacing();
            if (ImGui.Button("关闭"))
                ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.TextUnformatted("重新从原版 Chat Two 迁移？");
        ImGui.TextUnformatted("将再次合并原版设置（CN 特有设置不受影响）。");
        if (_migrateAlsoDb)
        {
            if (File.Exists(srcDb))
                ImGui.TextUnformatted("聊天历史将重新标记导入（重启后生效）。");
            else
                ImGui.TextUnformatted("未找到原版聊天历史数据库，本次仅迁移设置。");
        }

        ImGui.Spacing();
        if (ImGui.Button("确认迁移"))
        {
            DoMigrate(srcConfig, srcDb);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消"))
            ImGui.CloseCurrentPopup();
    }

    // 迁移核心：白名单字段合并进当前配置（不覆盖 CN 特有字段），立即生效并持久化；
    // Mutable（设置窗口副本）同步合并，防止用户随后点"保存"把迁移结果回滚。
    // 聊天历史只写待导入标记，重启后由 MessageManager 用 SQLite Online Backup 导入。
    private void DoMigrate(string srcConfig, string srcDb)
    {
        ChatTwoMigrator.MergeConfigFrom(Plugin.Config, srcConfig);
        ChatTwoMigrator.MergeConfigFrom(Mutable, srcConfig);
        Plugin.Config.MigratedFromChatTwo = true;
        Mutable.MigratedFromChatTwo = true;
        Plugin.Interface.SavePluginConfig(Plugin.Config);

        if (_migrateAlsoDb && File.Exists(srcDb))
        {
            Plugin.Config.PendingDbImportSource = srcDb;
            Plugin.Interface.SavePluginConfig(Plugin.Config);
        }
    }

    private bool _migrateAlsoDb = true;
}
