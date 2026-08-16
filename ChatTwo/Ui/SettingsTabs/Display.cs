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

        // "在战斗中隐藏" / "非活动时隐藏"已删除：保持关闭（用户要求）

        ImGui.Separator();
        ImGui.Spacing();

        // ═══════════════ 窗口 ═══════════════
        // 以下项从"窗口调整"页挪入（用户要求：基础设置统一管窗口行为）
        // 显示时间戳：全局开关（各 tab 另有独立时间戳开关，两者叠加）
        ImGuiUtil.OptionCheckbox(ref Mutable.ShowTimestamp, Language.Options_ShowTimestamp_Name, Language.Options_ShowTimestamp_Description);
        ImGui.Spacing();

        // 仿原生界面背景（用户要求：放在标签页位置上面）
        // 仿原生界面（用户要求：去掉"背景"两字，观感更简洁）
        ImGuiUtil.OptionCheckbox(ref Mutable.NativeBackground, Language.Options_NativeBackground_Name, Language.Options_NativeBackground_Description);
        ImGui.Spacing();

        // 锁定窗口移动（用户 2026-08-17：锁按钮从工具栏移除，改回设置项；
        // 开启后只有消息区不可拖动（选字防误拖），窗口其他区域仍可拖）
        ImGuiUtil.OptionCheckbox(ref Mutable.MoveLocked, Language.Options_MoveLocked_Name, Language.Options_MoveLocked_Description);
        ImGui.Spacing();

        using (var tabCombo = ImGuiUtil.BeginComboVertical(Language.Options_TabPosition_Name, Mutable.TabPosition.Name()))
        {
            if (tabCombo.Success)
            {
                foreach (var tabPos in Enum.GetValues<TabPosition>())
                    if (ImGui.Selectable(tabPos.Name(), Mutable.TabPosition == tabPos))
                        Mutable.TabPosition = tabPos;
            }
        }
        ImGuiUtil.HelpText(Language.Options_TabPosition_Description);
        ImGui.Spacing();

        // 未读消息提示方式（用户要求放在基础设置）：高亮/呼吸/无
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

        // 保持输入焦点（与显示时间戳互换位置，用户要求）
        ImGuiUtil.OptionCheckbox(ref Mutable.KeepInputFocus, Language.Options_KeepInputFocus_Name, Language.Options_KeepInputFocus_Description);
        ImGui.Spacing();

        // 定型文排序调整（原"定型文列表排序"，改名）
        ImGuiUtil.OptionCheckbox(ref Mutable.SortAutoTranslate, Language.Options_SortAutoTranslate_Name, Language.Options_SortAutoTranslate_Description);
        ImGui.Spacing();

        // "现代化布局/更紧凑的现代布局/隐藏重复的时间戳"已删除：保持传统时间戳样式（用户要求）
        // "折叠重复消息"及其子选项已删除（用户要求）

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 字体相关设置已拆分到独立"字体设置"页（v1.40.11+，用户要求）

        ImGui.Spacing();

        // ═══════════════ 从原版 Chat Two 迁移 ═══════════════
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("从原版 Chat Two 迁移");
        ImGuiUtil.HelpText("复制原版 Chat Two（InternalName=ChatTwo）的设置与聊天历史到本插件。复制后需要重启游戏生效。");
        DrawMigrationSection();
        ImGui.Spacing();
    }

    // 从原版 ChatTwo 迁移配置/历史（原版配置: pluginConfigs/ChatTwo.json，库: pluginConfigs/ChatTwo/chat-sqlite.db）
    private void DrawMigrationSection()
    {
        var parentDir = Plugin.Interface.ConfigDirectory.Parent;
        if (parentDir == null)
            return;

        var srcConfig = Path.Combine(parentDir.FullName, "ChatTwo.json");
        var srcExists = File.Exists(srcConfig);

        if (Mutable.MigratedFromChatTwo)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.49f, 0.78f, 0.49f, 1f), "已迁移过，请重启游戏生效。");
            return;
        }

        if (!srcExists)
        {
            ImGui.TextUnformatted("未找到原版配置（pluginConfigs/ChatTwo.json）。");
            return;
        }

        var alsoDb = _migrateAlsoDb;
        ImGui.Checkbox("同时迁移聊天历史（数据库）", ref alsoDb);
        _migrateAlsoDb = alsoDb;

        if (ImGui.Button("迁移设置"))
        {
            try
            {
                // 备份当前配置
                var dstConfig = Plugin.Interface.ConfigFile.FullName;
                File.Copy(dstConfig, $"{dstConfig}.bak", true);

                // 复制原版配置
                File.Copy(srcConfig, dstConfig, true);

                // 可选：复制聊天历史数据库
                if (_migrateAlsoDb)
                {
                    var srcDb = Path.Combine(parentDir.FullName, "ChatTwo", "chat-sqlite.db");
                    var dstDb = Path.Combine(Plugin.Interface.ConfigDirectory.FullName, "chat-sqlite.db");
                    if (File.Exists(srcDb))
                    {
                        try { File.Copy(srcDb, dstDb, true); } catch { /* 数据库可能被占用，忽略 */ }
                    }
                }

                Mutable.MigratedFromChatTwo = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "迁移原版配置失败");
            }
        }
    }

    private bool _migrateAlsoDb = true;
}
