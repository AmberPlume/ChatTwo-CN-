using System.Numerics;
using ChatTwo.Resources;
using ChatTwo.Ui.SettingsTabs;
using ChatTwo.Util;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui;

public sealed class SettingsWindow : Window
{
    private readonly Plugin Plugin;

    private Configuration Mutable { get; }
    private List<ISettingsTab> Tabs { get; }
    private int CurrentTab;

    public SettingsWindow(Plugin plugin) : base($"{Language.Settings_Title.Format(Plugin.PluginName)}###chat2-settings")
    {
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(475, 600),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        Plugin = plugin;
        Mutable = new Configuration();

        Tabs =
        [
            new Display(Mutable),
            new Font(Mutable),
            new ChatLogConfig(Plugin, Mutable),
            new ChatColours(Plugin, Mutable),
            new Tabs(Plugin, Mutable),
            new Database(Plugin, Mutable),
            new Experimental(Mutable),   // 设置页（菜单跟随鼠标开关等）
        // 偏好页已删除：语言/命令帮助方向/热键模式选项已移除（频道切换策略固定"灵活"，见 Plugin.cs）
        // 字体页已合并到显示页
        ];

        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        Initialise();

        Plugin.Commands.Register("/chat2", "Perform various actions with Chat 2.").Execute += Command;
        Plugin.Interface.UiBuilder.OpenConfigUi += Toggle;
    }

    public void Dispose()
    {
        Plugin.Interface.UiBuilder.OpenConfigUi -= Toggle;
        Plugin.Commands.Register("/chat2").Execute -= Command;
    }

    /// <summary>
    /// 窗口操作（弹出/收回 tab）与设置窗口 Mutable 副本同步：
    /// 否则设置打开期间收回 tab，保存设置会把 PopOut 写回 true（tab 又弹出）。
    /// </summary>
    public void SyncTabPopOut(Guid tabIdentifier, bool popOut)
    {
        foreach (var tab in Mutable.Tabs)
        {
            if (tab.Identifier == tabIdentifier)
            {
                tab.PopOut = popOut;
                break;
            }
        }
    }

    private void Command(string command, string args)
    {
        // /chat2 search：打开聊天记录搜索窗口；无参数：设置窗口
        if (!string.IsNullOrWhiteSpace(args) && args.Trim().Equals("search", StringComparison.InvariantCultureIgnoreCase))
        {
            Plugin.SearchWindow.Toggle();
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
            Toggle();
    }

    private void Initialise()
    {
        Mutable.UpdateFrom(Plugin.Config, false);
    }

    public override void Draw()
    {
        if (ImGui.IsWindowAppearing())
            Initialise();

        // 设置界面使用独立字体（"设置界面字体大小"控制），不受聊天主字体影响
        using var settingsFont = Plugin.FontManager.SettingsFont.Push();

        using (var table = ImRaii.Table("##chat2-settings-table", 2))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("tab", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("settings", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextColumn();

                var changed = false;
                for (var i = 0; i < Tabs.Count; i++)
                {
                    if (!ImGui.Selectable($"{Tabs[i].Name}###tab-{i}", CurrentTab == i))
                        continue;

                    CurrentTab = i;
                    changed = true;
                }

                ImGui.TableNextColumn();

                var style = ImGui.GetStyle();
                var height = ImGui.GetContentRegionAvail().Y - style.FramePadding.Y * 2 - style.ItemSpacing.Y - style.ItemInnerSpacing.Y * 2 - ImGui.CalcTextSize("A").Y;

                using var child = ImRaii.Child("##chat2-settings", new Vector2(-1, height));
                if (child.Success)
                    Tabs[CurrentTab].Draw(changed);
            }
        }

        ImGui.Separator();

        var save = ImGui.Button(Language.Settings_Save);

        ImGui.SameLine();

        if (ImGui.Button(Language.Settings_SaveAndClose)) {
            save = true;
            IsOpen = false;
        }

        ImGui.SameLine();

        if (ImGui.Button(Language.Settings_Discard)) {
            IsOpen = false;
        }

        if (!save)
            return;

        // calculate all conditions before updating config
        var hideChanged = !Mutable.HideChat && Mutable.HideChat != Plugin.Config.HideChat;
        var languageChanged = Mutable.LanguageOverride != Plugin.Config.LanguageOverride;
        var fontSizeChanged = Math.Abs(Mutable.FontSizeV2 - Plugin.Config.FontSizeV2) > 0.001
                          || Math.Abs(Mutable.InputFontSize - Plugin.Config.InputFontSize) > 0.001
                          || Math.Abs(Mutable.SettingsFontSize - Plugin.Config.SettingsFontSize) > 0.001
                          || Math.Abs(Mutable.InputAreaScale - Plugin.Config.InputAreaScale) > 0.001
                          || Math.Abs(Mutable.TabScale - Plugin.Config.TabScale) > 0.001
                          || Math.Abs(Mutable.TabFontSizePt - Plugin.Config.TabFontSizePt) > 0.001
                          || Math.Abs(Mutable.ChannelNameFontSizePt - Plugin.Config.ChannelNameFontSizePt) > 0.001
                          || Math.Abs(Mutable.ImeCandidateFontSizePt - Plugin.Config.ImeCandidateFontSizePt) > 0.001
                          // 额外字符集：参与字体 atlas 构建（FontManager 读取），变化必须重建
                          || Mutable.ExtraGlyphRanges != Plugin.Config.ExtraGlyphRanges
                          // 自定义字体：字体族变化也要重建（字号统一由 FontSizeV2 控制，不比较 SizePt）
                          || Mutable.GlobalFontV2.FontId.EnglishName != Plugin.Config.GlobalFontV2.FontId.EnglishName
                          || Mutable.JapaneseFontV2.FontId.EnglishName != Plugin.Config.JapaneseFontV2.FontId.EnglishName
                          || Mutable.FontsEnabled != Plugin.Config.FontsEnabled;

        // 条件化：只有影响消息内容/过滤/布局的设置变化才清空重载
        //（字体变化→消息高度缓存失效必须重算；语言/会话范围/定型文排序→文本内容变化）。
        // 纯视觉设置（透明度/颜色/开关/缩放等）跳过 → 保存瞬间无加载感。
        var messagesNeedReload = fontSizeChanged
                              || languageChanged
                              || Mutable.FilterIncludePreviousSessions != Plugin.Config.FilterIncludePreviousSessions
                              || Mutable.SortAutoTranslate != Plugin.Config.SortAutoTranslate;

        Plugin.Config.UpdateFrom(Mutable, true);

        // save after 60 frames have passed, which should hopefully not
        // commit any changes that cause a crash
        Plugin.DeferredSaveFrames = 60;

        if (messagesNeedReload)
        {
            Plugin.MessageManager.ClearAllTabs();
            Plugin.MessageManager.FilterAllTabsAsync();
        }

        if (fontSizeChanged)
            Plugin.FontManager.BuildFonts();

        if (languageChanged)
            Plugin.LanguageChanged(Plugin.Interface.UiLanguage);

        if (hideChanged)
            GameFunctions.GameFunctions.SetChatInteractable(true);

        Initialise();
    }
}
