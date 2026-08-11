using System.Net.Http;
using Dalamud;
using Dalamud.Interface;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;

namespace ChatTwo;

public class FontManager
{
    private static readonly HttpClient HttpClient = new();

    public IFontHandle Axis = null!;
    public IFontHandle AxisItalic = null!;

    public IFontHandle RegularFont = null!;
    public IFontHandle? ItalicFont;

    /// <summary>小号字体（约 0.85 倍），用于标签页文字、输入框频道名等需要缩小的 UI 元素。</summary>
    public IFontHandle SmallFont = null!;

    /// <summary>输入框字体，大小由设置中的"输入字体大小"控制，输入框高度随之自适应。</summary>
    public IFontHandle InputFont = null!;

    /// <summary>设置界面字体，大小由"设置界面字体大小"控制（独立于聊天主字体）。</summary>
    public IFontHandle SettingsFont = null!;

    /// <summary>标签页字体：固定大小（12pt），不随"字体大小"设置变化。</summary>
    public IFontHandle TabFont = null!;

    public IFontHandle FontAwesome = null!;

    /// <summary>小号 FontAwesome 图标字体（约 0.8 倍），用于输入框左侧的频道切换气泡按钮。</summary>
    public IFontHandle FontAwesomeSmall = null!;

    public readonly byte[] GameSymFont;

    private ushort[] Ranges = [];
    private ushort[] JpRange = [];

    public static readonly HashSet<float> AxisFontSizeList =
    [
        9.6f, 10f, 12f, 14f, 16f,
        18f, 18.4f, 20f, 23f, 34f,
        36f, 40f, 45f, 46f, 68f, 90f,
    ];

    public FontManager()
    {
        var filePath = Path.Combine(Plugin.Interface.ConfigDirectory.FullName, "FFXIV_Lodestone_SSF.ttf");
        if (File.Exists(filePath))
        {
            GameSymFont = File.ReadAllBytes(filePath);
        }
        else
        {
            GameSymFont = HttpClient.GetAsync("https://img.finalfantasyxiv.com/lds/pc/global/fonts/FFXIV_Lodestone_SSF.ttf")
                .Result
                .Content
                .ReadAsByteArrayAsync()
                .Result;

            Dalamud.Utility.FilesystemUtil.WriteAllBytesSafe(filePath, GameSymFont);
        }
    }

    private unsafe void SetUpRanges()
    {
        ushort[] BuildRange(IReadOnlyList<ushort>? chars, params nint[] ranges)
        {
            var builder = new ImFontGlyphRangesBuilderPtr(ImGuiNative.ImFontGlyphRangesBuilder());
            // text
            foreach (var range in ranges)
                builder.AddRanges((ushort*)range);

            // chars
            if (chars != null)
            {
                for (var i = 0; i < chars.Count; i += 2)
                {
                    if (chars[i] == 0)
                        break;

                    for (var j = (uint) chars[i]; j <= chars[i + 1]; j++)
                        builder.AddChar((ushort) j);
                }
            }

            // Ingame supported ranges
            var reader = new FdtReader(Plugin.DataManager.GetFile("common/font/axis_12.fdt")!.Data);
            foreach (var c in reader.Glyphs)
                builder.AddChar(c.Char);

            // various symbols
            // French
            // Romanian
            // builder.AddText("←→↑↓《》■※☀★★☆♥♡ヅツッシ☀☁☂℃℉°♀♂♠♣♦♣♧®©™€$£♯♭♪✓√◎◆◇♦■□〇●△▽▼▲‹›≤≥<«“”─＼～");
            builder.AddText("Œœ");
            builder.AddText("ĂăÂâÎîȘșȚț");

            // "Enclosed Alphanumerics" (partial) https://www.compart.com/en/unicode/block/U+2460
            for (var i = 0x2460; i <= 0x24B5; i++)
                builder.AddChar((char) i);

            builder.AddChar('⓪');
            return builder.BuildRangesToArray();
        }

        var ranges = new List<nint> { (nint)ImGui.GetIO().Fonts.GetGlyphRangesDefault() };
        foreach (var extraRange in Enum.GetValues<ExtraGlyphRanges>())
            if (Plugin.Config.ExtraGlyphRanges.HasFlag(extraRange))
                ranges.Add(extraRange.Range());

        Ranges = BuildRange(null, ranges.ToArray());
        JpRange = BuildRange(GlyphRangesJapanese.GlyphRanges);
    }

    public void BuildFonts()
    {
        SetUpRanges();

        // 字体体系（用户要求）：
        // - 主字体由"自定义字体"（GlobalFontV2）选择，日文补充字体用 JapaneseFontV2
        // - 字号统一由"字体大小"(FontSizeV2) 控制（忽略 GlobalFontV2.SizePt），符号字体并入主字体
        var mainFontId = Plugin.Config.GlobalFontV2.FontId;
        var jpFontId = Plugin.Config.JapaneseFontV2.FontId;
        var baseSizePt = Plugin.Config.FontSizeV2;
        // 输入区缩放（用户要求与卫月全局字体比例同逻辑：重建字体时字号乘比例，
        // 这样 drawList 手动渲染的文字（tab 文字）也自然缩放）
        var inputScale = Plugin.Config.InputAreaScale;

        Axis = Plugin.Interface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, SizeInPx(baseSizePt)));
        AxisItalic = Plugin.Interface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, SizeInPx(baseSizePt))
        {
            SkewStrength = SizeInPx(baseSizePt) / 6
        });

        // 通用图标字体：跟随"设置界面字体大小"（设置页导入/导出/颜色图标与界面文字同比例，不随主字体变）
        FontAwesome = Plugin.Interface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk => tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = SizeInPx(Plugin.Config.SettingsFontSize) }));
            e.OnPostBuild(tk => tk.FitRatio(tk.Font));
        });

        // 输入区图标字体：固定 12px（气泡/齿轮/隐藏/新人按钮同尺寸，不随主字体设置变化）
        FontAwesomeSmall = Plugin.Interface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
        {
            e.OnPreBuild(tk => tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = 12f * inputScale * 0.9f }));
            e.OnPostBuild(tk => tk.FitRatio(tk.Font));
        });

        RegularFont = Plugin.Interface.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(
                tk =>
                {
                    var config = new SafeFontConfig {SizePt = baseSizePt, GlyphRanges = Ranges};
                    config.MergeFont = mainFontId.AddToBuildToolkit(tk, config);

                    config.SizePt = baseSizePt;
                    config.GlyphRanges = JpRange;
                    jpFontId.AddToBuildToolkit(tk, config);

                    // 符号字体并入主字体（跟随主字号）
                    tk.AddGameSymbol(config);

                    tk.Font = config.MergeFont;
                }
            ));

        // 斜体消息统一用 AxisItalic（不再单独构建 ItalicFont）
        ItalicFont = null;

        // 小号字体：固定 12pt（输入框频道名等 UI 元素，不随主字体设置变化）
        var smallFontSizePt = 12f * inputScale * 0.9f;  // 频道名（输入框附近），×0.9 缩小
        SmallFont = Plugin.Interface.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(
                tk =>
                {
                    var config = new SafeFontConfig {SizePt = smallFontSizePt, GlyphRanges = Ranges};
                    config.MergeFont = mainFontId.AddToBuildToolkit(tk, config);

                    config.SizePt = smallFontSizePt;
                    config.GlyphRanges = JpRange;
                    jpFontId.AddToBuildToolkit(tk, config);

                    tk.AddGameSymbol(config);

                    tk.Font = config.MergeFont;
                }
            ));

        // 输入框字体：大小只由"输入字体大小"设置控制（不乘 inputScale —— 输入区缩放不动输入框大小，
        // 只放大频道名/标签页/图标等其他 UI，输入框位置随布局自然变化）
        InputFont = Plugin.Interface.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(
                tk =>
                {
                    var config = new SafeFontConfig {SizePt = Plugin.Config.InputFontSize, GlyphRanges = Ranges};
                    config.MergeFont = mainFontId.AddToBuildToolkit(tk, config);

                    config.SizePt = Plugin.Config.InputFontSize;
                    config.GlyphRanges = JpRange;
                    jpFontId.AddToBuildToolkit(tk, config);

                    tk.AddGameSymbol(config);

                    tk.Font = config.MergeFont;
                }
            ));

        // 设置界面字体：大小由"设置界面字体大小"设置控制（独立于聊天主字体）
        SettingsFont = Plugin.Interface.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(
                tk =>
                {
                    var config = new SafeFontConfig {SizePt = Plugin.Config.SettingsFontSize, GlyphRanges = Ranges};
                    config.MergeFont = mainFontId.AddToBuildToolkit(tk, config);

                    config.SizePt = Plugin.Config.SettingsFontSize;
                    config.GlyphRanges = JpRange;
                    jpFontId.AddToBuildToolkit(tk, config);

                    tk.AddGameSymbol(config);

                    tk.Font = config.MergeFont;
                }
            ));

        // 标签页字体：固定 12pt（不随"字体大小"变化）
        var tabFontSizePt = 12f * inputScale;
        TabFont = Plugin.Interface.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(
                tk =>
                {
                    var config = new SafeFontConfig {SizePt = tabFontSizePt, GlyphRanges = Ranges};
                    config.MergeFont = mainFontId.AddToBuildToolkit(tk, config);

                    config.SizePt = tabFontSizePt;
                    config.GlyphRanges = JpRange;
                    jpFontId.AddToBuildToolkit(tk, config);

                    tk.AddGameSymbol(config);

                    tk.Font = config.MergeFont;
                }
            ));
    }

    public static float SizeInPt(float px) => (float) (px * 3.0 / 4.0);
    public static float SizeInPx(float pt) => (float) (pt * 4.0 / 3.0);
    public static float GetFontSize() => SizeInPx(Plugin.Config.FontSizeV2);
}
