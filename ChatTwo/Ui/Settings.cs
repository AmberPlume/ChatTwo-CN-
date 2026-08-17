using System.Numerics;
using ChatTwo.Resources;
using ChatTwo.Ui.SettingsTabs;
using ChatTwo.Util;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ChatTwo.Ui;

public sealed class SettingsWindow : Window
{
    private readonly Plugin Plugin;

    // SFX 批量试听状态（/chat2 sfxscan <起> <止>，每 500ms 播一个，2026-08-17 调试用）
    private uint? _sfxScanCurrent;
    private uint _sfxScanEnd;
    private long _sfxScanNextTick;

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
            new Experimental(Mutable),   // ⚠️ 2026-08-15 18:05 实验功能设置页（菜单跟随鼠标开关等）
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

        // /chat2 sfx <id>：播放指定 UI 音效（SFX 试听调试命令，2026-08-17 加；
        // 用于挑选按钮点击音，找到合适的 id 后固化到 ImGuiUtil.ButtonClickSfx 并移除本命令）
        if (!string.IsNullOrWhiteSpace(args) && args.Trim().StartsWith("sfx", StringComparison.InvariantCultureIgnoreCase))
        {
            var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && uint.TryParse(parts[1], out var sfxId))
            {
                unsafe { UIGlobals.PlaySoundEffect(sfxId); }
                Plugin.ChatGui.Print($"ChatTwoCN: playing SFX #{sfxId}");
            }
            else
            {
                Plugin.ChatGui.Print("ChatTwoCN: usage /chat2 sfx <id>（例如 /chat2 sfx 17）");
            }
            return;
        }

        // /chat2 sfxscan <起> <止>：每 500ms 依次播放一段区间并回显 id（批量试听）
        if (!string.IsNullOrWhiteSpace(args) && args.Trim().StartsWith("sfxscan", StringComparison.InvariantCultureIgnoreCase))
        {
            var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && uint.TryParse(parts[1], out var sfxStart) && uint.TryParse(parts[2], out var sfxEnd))
            {
                _sfxScanCurrent = sfxStart;
                _sfxScanEnd = Math.Max(sfxStart, sfxEnd);
                _sfxScanNextTick = Environment.TickCount64;
                Plugin.ChatGui.Print($"ChatTwoCN: scanning SFX #{sfxStart}~#{_sfxScanEnd}（每 0.5s 一个，输 /chat2 sfxstop 停止）");
            }
            else
            {
                Plugin.ChatGui.Print("ChatTwoCN: usage /chat2 sfxscan <起始id> <结束id>（例如 /chat2 sfxscan 1 60）");
            }
            return;
        }

        // /chat2 sfxstop：停止批量试听
        if (!string.IsNullOrWhiteSpace(args) && args.Trim().Equals("sfxstop", StringComparison.InvariantCultureIgnoreCase))
        {
            _sfxScanCurrent = null;
            Plugin.ChatGui.Print("ChatTwoCN: sfx scan stopped");
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
            Toggle();
    }

    /// <summary>SFX 批量试听驱动（由 Plugin.FrameworkUpdate 每帧调用，不依赖设置窗口打开）：
    /// 每 500ms 播一个并回显当前 id，直到播完或 /chat2 sfxstop。</summary>
    public void UpdateSfxScan()
    {
        if (_sfxScanCurrent is not { } scanCur)
            return;
        var now = Environment.TickCount64;
        if (now < _sfxScanNextTick)
            return;
        _sfxScanNextTick = now + 500;
        unsafe { UIGlobals.PlaySoundEffect(scanCur); }
        Plugin.ChatGui.Print($"ChatTwoCN: SFX #{scanCur}");
        if (scanCur >= _sfxScanEnd)
        {
            _sfxScanCurrent = null;
            Plugin.ChatGui.Print("ChatTwoCN: sfx scan done");
        }
        else
        {
            _sfxScanCurrent = scanCur + 1;
        }
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
                          // 自定义字体：字体族变化也要重建（字号统一由 FontSizeV2 控制，不比较 SizePt）
                          || Mutable.GlobalFontV2.FontId.EnglishName != Plugin.Config.GlobalFontV2.FontId.EnglishName
                          || Mutable.JapaneseFontV2.FontId.EnglishName != Plugin.Config.JapaneseFontV2.FontId.EnglishName
                          || Mutable.FontsEnabled != Plugin.Config.FontsEnabled;

        Plugin.Config.UpdateFrom(Mutable, true);

        // save after 60 frames have passed, which should hopefully not
        // commit any changes that cause a crash
        Plugin.DeferredSaveFrames = 60;
        Plugin.MessageManager.ClearAllTabs();
        Plugin.MessageManager.FilterAllTabsAsync();

        if (fontSizeChanged)
            Plugin.FontManager.BuildFonts();

        if (languageChanged)
            Plugin.LanguageChanged(Plugin.Interface.UiLanguage);

        if (hideChanged)
            GameFunctions.GameFunctions.SetChatInteractable(true);

        Initialise();
    }
}
