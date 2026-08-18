using System.Numerics;
using System.Text;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Ui.ChatLog;
using ChatTwo.Ui.Handler;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.ImGuiFontChooserDialog;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ChatTwo.Util;

public static class ImGuiUtil
{
    private static Plugin Plugin = null!;

    // Set by DrawMessageLog before rendering chunks, cleared after.
    // If non-null, WrapText records each chunk's screen rect + text here.
    public static TextSelectionState? CurrentSelection;

    public static void Initialize(Plugin plugin)
    {
        Plugin = plugin;
    }

    private static readonly ImGuiMouseButton[] Buttons =
    [
        ImGuiMouseButton.Left,
        ImGuiMouseButton.Middle,
        ImGuiMouseButton.Right
    ];

    private static Payload? Hovered;
    public static Payload? HoveredPayload => Hovered;
    private static Payload? LastLink;
    private static readonly List<(Vector2, Vector2)> PayloadBounds = [];

    public static void PostPayload(Chunk chunk, PayloadHandler? handler)
    {
        var payload = chunk.Link;
        if (payload != null && ImGui.IsItemHovered())
        {
            Hovered = payload;
            // !!! 链接 hover → 帧末切游戏原生手指（Clickable）；
            // 原 SetMouseCursor(Hand) 被 NoMouseCursorChange 禁用（实测链接不变手指）
            Plugin.AnyInteractiveHovered = true;
            handler?.Hover(payload);
        }
        else if (!ReferenceEquals(Hovered, payload))
        {
            Hovered = null;
        }

        if (handler == null)
            return;

        if (payload == null)
            return;

        foreach (var button in Buttons)
        {
            if (ImGui.IsItemClicked(button))
            {
                handler.Click(chunk, payload, button);
            }
        }
    }

    /// <summary>
    /// 聊天正文描边（仿游戏原生 Axis 字体的细黑描边）。8 方向 1px 黑色偏移副本先入
    /// drawlist → 正文 TextUnformatted 覆盖其上。布局/换行语义完全不变。
    /// </summary>
    private static bool TextOutlineEnabled => true;

    /// <summary>byte* 版本（WrapText 用）：画描边 + 正文 + 布局推进。</summary>
    public static unsafe void TextUnformattedOutline(byte* text, byte* textEnd)
    {
        var oldPos = ImGui.GetCursorScreenPos();
        var len = (int)(textEnd - text);
        if (len > 0 && TextOutlineEnabled)
        {
            var s = Encoding.UTF8.GetString(text, len);
            DrawOutline(oldPos, s);
        }

        ImGuiNative.TextUnformatted(text, textEnd);
    }

    /// <summary>string 版本（ChunkHandler 非 wrap 路径用）：画描边 + 正文 + 布局推进。</summary>
    public static void TextUnformattedOutline(string text)
    {
        var oldPos = ImGui.GetCursorScreenPos();
        if (text.Length > 0 && TextOutlineEnabled)
            DrawOutline(oldPos, text);

        ImGui.TextUnformatted(text);
    }

    /// <summary>在指定位置画 8 方向 0.5px 半透明黑色文字（描边底层）。0.5px 为实用下限，再小会模糊。
    /// 半透明灰（0x80000000）更接近 FFXIV Axis 原生观感（猜测：游戏描边是柔和灰而非纯黑）。
    /// !!! 试过 4 方向（上下左右）：实测视觉较差 → 回退 8 方向。</summary>
    private static void DrawOutline(Vector2 pos, string text)
    {
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var dl = ImGui.GetWindowDrawList();
        const float o = 0.5f;
        for (var dx = -o; dx <= o; dx += o)
        {
            for (var dy = -o; dy <= o; dy += o)
            {
                if (dx == 0 && dy == 0)
                    continue;
                dl.AddText(font, fontSize, pos + new Vector2(dx, dy), 0x80000000u, text);
            }
        }
    }

    public static unsafe void WrapText(string csText, Chunk chunk, PayloadHandler? handler, Vector4 defaultText, float lineWidth, float letterSpacing = 0f)
    {
        // !!! v1.40.17+ 正文字间距（要求）：非零且是消息内容时走自绘逐字符路径。
        // 换行/描边/点击命中/选字全部按间距补偿；时间戳与发送者名不受影响（调用方只给 Content 传间距）。
        if (letterSpacing != 0f && chunk.Source == ChunkSource.Content)
        {
            WrapTextSpaced(csText, chunk, handler, defaultText, lineWidth, letterSpacing);
            return;
        }

        void Text(byte* text, byte* textEnd)
        {
            var oldPos = ImGui.GetCursorScreenPos();

            TextUnformattedOutline(text, textEnd);
            PostPayload(chunk, handler);

            if (CurrentSelection != null)
            {
                var byteLen = (int)(textEnd - text);
                var line = byteLen > 0 ? System.Text.Encoding.UTF8.GetString(text, byteLen) : string.Empty;
                if (!string.IsNullOrEmpty(line))
                {
                    var itemSize = ImGui.GetItemRectSize();
                    // Compute per-character X positions along the line
                    var charX = new float[line.Length + 1];
                    charX[0] = 0f;
                    var font = ImGui.GetFont();
                    var fontSize = ImGui.GetFontSize();
                    for (var ci = 0; ci < line.Length; ci++)
                    {
                        var ch = line[ci];
                        // Handle surrogate pairs - measure the whole UTF-16 char
                        if (char.IsHighSurrogate(ch) && ci + 1 < line.Length && char.IsLowSurrogate(line[ci + 1]))
                        {
                            var pair = char.ToString(ch) + line[ci + 1];
                            var sz = ImGui.CalcTextSize(pair).X;
                            charX[ci + 1] = charX[ci] + sz;
                            charX[ci + 2] = charX[ci + 1]; // same boundary for low surrogate
                            ci++; // skip the low surrogate
                        }
                        else
                        {
                            var sz = ImGui.CalcTextSize(char.ToString(ch)).X;
                            charX[ci + 1] = charX[ci] + sz;
                        }
                    }
                    // Align last boundary with actual item width (in case of rounding)
                    charX[line.Length] = itemSize.X;
                    CurrentSelection.AddChunk(oldPos, oldPos + itemSize, line, charX);
                }
            }

            if (!ReferenceEquals(LastLink, chunk.Link))
                PayloadBounds.Clear();

            LastLink = chunk.Link;

            if (Hovered != null && ReferenceEquals(Hovered, chunk.Link))
            {
                defaultText.W = 0.25f;
                var actualCol = ColourUtil.Vector4ToAbgr(defaultText);
                ImGui.GetWindowDrawList().AddRectFilled(oldPos, oldPos + ImGui.GetItemRectSize(), actualCol);

                foreach (var (start, size) in PayloadBounds)
                    ImGui.GetWindowDrawList().AddRectFilled(start, start + size, actualCol);

                PayloadBounds.Clear();
            }

            if (Hovered == null && chunk.Link != null)
                PayloadBounds.Add((oldPos, ImGui.GetItemRectSize()));
        }

        if (csText.Length == 0)
            return;

        foreach (var part in csText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None))
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            fixed (byte* rawText = bytes)
            {
                var text = rawText;
                var textEnd = text + bytes.Length;

                // empty string (e.g. after splitting on \n)
                if (text == textEnd)
                {
                    ImGui.TextUnformatted("");
                    continue;
                }

                var widthLeft = ImGui.GetContentRegionAvail().X;
                var endPrevLine = ImGuiNative.CalcWordWrapPositionA(ImGui.GetFont().Handle, ImGuiHelpers.GlobalScale, text, textEnd, widthLeft);
                if (endPrevLine == null)
                    continue;

                var firstSpace = FindFirstSpace(text, textEnd);
                // !!! 修复：无空格文本（纯 CJK/连续字符）视为"按字符断行"。
                // 原版 firstSpace == textEnd（整个文本是一个"词"）时，若文本 ≤ 整行宽度但 > 当前
                // 剩余宽度（如 Sender 占宽后画 Content），会误判"词放不下整行 → 空一行再画"，
                // 导致"第一行空、全部内容挤到第二行"（实测）。中文没有空格分词，应直接按
                // 字符断行（properBreak=true → 正常 Text + while 推进），不触发空行分支。
                var properBreak = firstSpace <= endPrevLine || firstSpace == textEnd;
                if (properBreak)
                {
                    Text(text, endPrevLine);
                }
                else
                {
                    if (lineWidth == 0f)
                    {
                        ImGui.TextUnformatted("");
                    }
                    else
                    {
                        // check if the next bit is longer than the entire line width
                        var wrapPos = ImGuiNative.CalcWordWrapPositionA(ImGui.GetFont().Handle, ImGuiHelpers.GlobalScale, text, firstSpace, lineWidth);

                        // only go to next line is it's going to wrap at the space
                        if (wrapPos >= firstSpace)
                            ImGui.TextUnformatted("");
                    }
                }

                widthLeft = ImGui.GetContentRegionAvail().X;
                while (endPrevLine < textEnd)
                {
                    if (properBreak)
                        text = endPrevLine;

                    // skip a space at start of line
                    if (*text == ' ')
                        ++text;

                    var newEnd = ImGuiNative.CalcWordWrapPositionA(ImGui.GetFont().Handle, ImGuiHelpers.GlobalScale, text, textEnd, widthLeft);
                    if (properBreak && newEnd == endPrevLine)
                        break;

                    endPrevLine = newEnd;
                    if (endPrevLine == null)
                    {
                        ImGui.TextUnformatted("");
                        ImGui.TextUnformatted("");
                        break;
                    }

                    Text(text, endPrevLine);

                    if (!properBreak)
                    {
                        properBreak = true;
                        widthLeft = ImGui.GetContentRegionAvail().X;
                    }
                }
            }
        }
    }

    private static unsafe byte* FindFirstSpace(byte* text, byte* textEnd)
    {
        for (var i = text; i < textEnd; i++)
            if (char.IsWhiteSpace((char) *i))
                return i;

        return textEnd;
    }

    // ═══════════ v1.40.17+ 正文字间距（自绘逐字符路径，仅 Content chunk 且间距非零时启用） ═══════════

    /// <summary>UTF-8 字符的字节长度（按首字节判断，1~4 字节；防越界截断）。</summary>
    private static unsafe int Utf8CharLen(byte* p, byte* end)
    {
        var b = *p;
        if (b < 0x80) return 1;
        if ((b & 0xE0) == 0xC0) return Math.Min(2, (int)(end - p));
        if ((b & 0xF0) == 0xE0) return Math.Min(3, (int)(end - p));
        if ((b & 0xF8) == 0xF0) return Math.Min(4, (int)(end - p));
        return 1;
    }

    private static unsafe string Utf8CharString(byte* p, int len)
        => Encoding.UTF8.GetString(p, len);

    /// <summary>
    /// 带字间距的换行绘制（正文专用）：逐字符 AddText + 描边，按 (字宽 + 间距) 推进；
    /// 用 Dummy 注册 item（payload 点击/选字矩形/换行推进）。换行语义与原 WrapText 一致：
    /// 词内断在最后一个可容纳的空格后；无空格（纯 CJK）按字符断行；行首跳过空格。
    /// </summary>
    private static unsafe void WrapTextSpaced(string csText, Chunk chunk, PayloadHandler? handler, Vector4 defaultText, float lineWidth, float spacing)
    {
        foreach (var part in csText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None))
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            fixed (byte* rawText = bytes)
            {
                var text = rawText;
                var textEnd = text + bytes.Length;

                // 空行（按 \n 切分后）：与原实现一致，输出一个空 item
                if (text == textEnd)
                {
                    ImGui.TextUnformatted("");
                    continue;
                }

                var widthLeft = ImGui.GetContentRegionAvail().X;
                while (text < textEnd)
                {
                    // 行首跳过空格（词断行的尾随空格不占下一行）
                    if (*text == (byte)' ')
                        ++text;
                    if (text >= textEnd)
                        break;

                    var end = FindBreakSpaced(text, textEnd, widthLeft, spacing);
                    DrawLineSpaced(text, end, chunk, handler, defaultText, spacing);
                    text = end;

                    widthLeft = ImGui.GetContentRegionAvail().X;
                }
            }
        }
    }

    /// <summary>按 (字宽+间距) 累计找断行点：优先断在最后一个可容纳的空格后；否则按字符断行（至少推进 1 字符）。</summary>
    private static unsafe byte* FindBreakSpaced(byte* text, byte* textEnd, float maxWidth, float spacing)
    {
        var p = text;
        var w = 0f;
        byte* lastSpace = null;   // 位置 = 最后一个可容纳的空格之后（含尾随空格在行尾）
        while (p < textEnd)
        {
            var chLen = Utf8CharLen(p, textEnd);
            var chStr = Utf8CharString(p, chLen);
            var cw = ImGui.CalcTextSize(chStr).X;
            if (w + cw > maxWidth)
                break;
            w += cw + spacing;
            p += chLen;
            if (chStr == " ")
                lastSpace = p;
        }

        if (lastSpace != null && lastSpace < textEnd)
            return lastSpace;

        // 无空格（纯 CJK/超长单字符）：按字符断行；保证至少推进一个字符
        if (p == text)
            p = text + Utf8CharLen(text, textEnd);
        return p;
    }

    /// <summary>逐字符绘制一行（8 方向描边 + 正文，间距推进），Dummy 注册 item，随后处理 payload 点击/选字/hover 高亮。</summary>
    private static unsafe void DrawLineSpaced(byte* text, byte* textEnd, Chunk chunk, PayloadHandler? handler, Vector4 defaultText, float spacing)
    {
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var col = ImGui.GetColorU32(ImGuiCol.Text);   // DrawChunk 已 PushColor（chunk 颜色）→ 直接取当前
        var dl = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorScreenPos();
        var pos = startPos;
        var lineWidth = 0f;

        var p = text;
        while (p < textEnd)
        {
            var chLen = Utf8CharLen(p, textEnd);
            var chStr = Utf8CharString(p, chLen);
            var cw = ImGui.CalcTextSize(chStr).X;

            if (TextOutlineEnabled)
            {
                const float o = 0.5f;
                for (var dx = -o; dx <= o; dx += o)
                    for (var dy = -o; dy <= o; dy += o)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        dl.AddText(font, fontSize, pos + new Vector2(dx, dy), 0x80000000u, chStr);
                    }
            }
            dl.AddText(font, fontSize, pos, col, chStr);

            pos.X += cw + spacing;
            lineWidth += cw + spacing;
            p += chLen;
        }

        // 注册 item：payload 点击区域 + 选字矩形 + 换行推进（高度 = 行高，与原 TextUnformatted 一致）
        var lineHeight = ImGui.GetTextLineHeight();
        ImGui.Dummy(new Vector2(lineWidth, lineHeight));
        PostPayload(chunk, handler);

        // 选字：逐字符 X 位置（含间距；代理对按一个单位）
        if (CurrentSelection != null)
        {
            var line = Encoding.UTF8.GetString(text, (int)(textEnd - text));
            if (!string.IsNullOrEmpty(line))
            {
                var itemSize = new Vector2(lineWidth, lineHeight);
                var charX = new float[line.Length + 1];
                charX[0] = 0f;
                for (var ci = 0; ci < line.Length; ci++)
                {
                    var ch = line[ci];
                    if (char.IsHighSurrogate(ch) && ci + 1 < line.Length && char.IsLowSurrogate(line[ci + 1]))
                    {
                        var pair = char.ToString(ch) + line[ci + 1];
                        var sz = ImGui.CalcTextSize(pair).X;
                        charX[ci + 1] = charX[ci] + sz + spacing;
                        charX[ci + 2] = charX[ci + 1]; // same boundary for low surrogate
                        ci++; // skip the low surrogate
                    }
                    else
                    {
                        var sz = ImGui.CalcTextSize(char.ToString(ch)).X;
                        charX[ci + 1] = charX[ci] + sz + spacing;
                    }
                }
                charX[line.Length] = lineWidth; // 对齐实际行宽（含末字间距）
                CurrentSelection.AddChunk(startPos, startPos + itemSize, line, charX);
            }
        }

        // hover 高亮（链接底色），与原 Text() 路径一致
        if (!ReferenceEquals(LastLink, chunk.Link))
            PayloadBounds.Clear();
        LastLink = chunk.Link;

        if (Hovered != null && ReferenceEquals(Hovered, chunk.Link))
        {
            defaultText.W = 0.25f;
            var actualCol = ColourUtil.Vector4ToAbgr(defaultText);
            dl.AddRectFilled(startPos, startPos + new Vector2(lineWidth, lineHeight), actualCol);

            foreach (var (start, size) in PayloadBounds)
                dl.AddRectFilled(start, start + size, actualCol);

            PayloadBounds.Clear();
        }

        if (Hovered == null && chunk.Link != null)
            PayloadBounds.Add((startPos, new Vector2(lineWidth, lineHeight)));
    }

    public static bool IconButton(FontAwesomeIcon icon, string? id = null, string? tooltip = null, Dalamud.Interface.ManagedFontAtlas.IFontHandle? font = null)
    {
        var label = icon.ToIconString();
        if (id != null)
            label += $"##{id}";

        bool ret;
        using ((font ?? Plugin.FontManager.FontAwesome).Push())
            ret = ImGui.Button(label);

        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Tooltip(tooltip);

        return ret;
    }

    /// <summary>原生按钮音效类型（实测确认）：打开=23、再按关闭=24、隐藏/关闭/重置=25；
    /// 新增：tab/频道切换=1（游戏原生频道切换音效，确认）。</summary>
    public enum BtnSfx
    {
        /// <summary>无声（排除项：添加tab/搜索/关闭窗口/新人）</summary>
        None = -1,
        /// <summary>频道切换/tab 切换 SFX 1（游戏原生频道音效，确认）</summary>
        UiSwitch = 1,
        /// <summary>打开（设置/聊天记录入口/筛选面板展开）SFX 23</summary>
        Open = 23,
        /// <summary>再按一次关闭（筛选面板收起）SFX 24</summary>
        Close = 24,
        /// <summary>隐藏/关闭/重置筛选（chat-hide/window-close/date-clear/player-clear）SFX 25</summary>
        Dismiss = 25,
    }

    /// <summary>
    /// 原生贴图图标按钮。
    /// <para>
    /// 交互反馈（决策，替代"方形底框"）：不画 hover/active 背景矩形，
    /// 状态只靠图标本身——hover 轻微拉亮（tint 1.25）、按下下沉 1px（模拟原生按钮 pressed）。
    /// 点击时播放 <paramref name="sfx"/> 指定音效（默认打开音 23；排除项传 <see cref="BtnSfx.None"/>）。
    /// </para>
    /// </summary>
    public static bool NativeIconButton(
        Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? wrap,
        string id,
        string? tooltip = null,
        FontAwesomeIcon fallbackIcon = FontAwesomeIcon.Question,
        Vector2? size = null,
        BtnSfx sfx = BtnSfx.Open)
    {
        // 资源未到位：用 FontAwesome 兜底，保证布局不变（FontAwesome 路径不播声音，保持原行为）
        // !!! 大工程：NativeBackground=false（非原生模式）→ 强制 FontAwesome 素材
        if (wrap == null || !Plugin.Config.NativeBackground)
            return IconButton(fallbackIcon, id, tooltip, Plugin.FontManager.FontAwesomeSmall);

        var s = size ?? CalcIconButtonSize();
        var clicked = ImGui.InvisibleButton($"##{id}", s);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();

        // 状态反馈（无方形底框）：按下下沉 1px；hover/按下"亮起"用半透明白雾叠加在图标区域。
        // !!! 修复：原实现 tint=1.25/1.15 + ColorConvertFloat4ToU32——ImU32 每通道仅 8bit，
        // 1.25×255=318 被钳制回 255(=1.0) → hover/active 从未生效（实测"未看到任何变化"）。
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        float pressOffset = active ? 1f : 0f;
        // !!! 可点击元素 hover → 帧末切游戏手指光标（Plugin.UpdateCursorDecision）
        if (hovered)
            Plugin.AnyInteractiveHovered = true;

        // 图标绘制区：保持宽高比（contain）居中，不拉伸。
        // !!! 修复：之前直接拉伸到整个按钮 → 宽>高的符号（如放大镜 21x32）
        // 被横向压扁（实测"图标有点扁"）。改为按 wrap 原始宽高比等比缩放居中。
        var avail = max - min;
        var texSize = wrap.Size;  // 原始纹理尺寸
        var scale = Math.Min(avail.X / texSize.X, avail.Y / texSize.Y);
        var drawSize = texSize * scale;
        var imgMin = min + (avail - drawSize) / 2f + new Vector2(0f, pressOffset);
        var imgMax = imgMin + drawSize;
        dl.AddImage(wrap.Handle, imgMin, imgMax, Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)));

        // 白雾亮起：原生素材是圆形按钮 → 用圆形白雾贴合（方形白雾会露角）；
        // active 比 hover 稍暗一点体现"按进去"
        if (hovered || active)
        {
            var glowAlpha = active ? 0.10f : 0.16f;
            var center = (imgMin + imgMax) / 2f;
            var radius = Math.Min(drawSize.X, drawSize.Y) / 2f * 0.9f;  // 贴合圆按钮（略留边缘）
            dl.AddCircleFilled(center, radius, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, glowAlpha)), 24);
        }

        if (clicked && sfx != BtnSfx.None && Plugin.Config.PlaySounds)
            unsafe { UIGlobals.PlaySoundEffect((uint)sfx); }

        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Tooltip(tooltip);

        return clicked;
    }

    public static bool OptionCheckbox(ref bool value, string label, string? description = null)
    {
        var ret = ImGui.Checkbox(label, ref value);
        // !!! v1.40.17+ 要求：说明悬浮在选项本身上（勾选框/文字），无独立 ? 标记
        if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
            Tooltip(description);

        return ret;
    }

    /// <summary>
    /// 把说明悬浮到上一个控件/文本上（无独立 ? 标记，v1.40.17+ 要求：悬浮选项即出说明）。
    /// 必须在目标 item 绘制后立即调用（IsItemHovered 指向上一个 item）。
    /// </summary>
    public static void TooltipOnLastItem(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (ImGui.IsItemHovered())
            Tooltip(text);
    }

    public static void HelpText(string text)
    {
        using (ImRaii.TextWrapPos(0.0f))
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled]))
            ImGui.TextUnformatted(text);
    }

    public static void WarningText(string text, bool wrap = true)
    {
        var style = StyleModel.GetConfiguredStyle() ?? StyleModel.GetFromCurrent();
        var dalamudOrange = style.BuiltInColors?.DalamudOrange;

        using (ImRaii.TextWrapPos(wrap ? 0.0f : ImGui.GetFontSize() * 35.0f))
        using (ImRaii.PushColor(ImGuiCol.Text, dalamudOrange ?? Vector4.Zero, dalamudOrange != null))
            ImGui.TextUnformatted(text);
    }

    public static ImRaii.ComboDisposable BeginComboVertical(string label, string previewValue, ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        return ImRaii.Combo($"##{label}", previewValue, flags);
    }

    public static bool DragFloatVertical(string label, ref float value, float vSpeed = 1.0f, float vMin = float.MinValue, float vMax = float.MaxValue, string? format = null, ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        return ImGui.DragFloat($"##{label}", ref value, vSpeed, vMin, vMax, format, flags);
    }

    public static bool DragFloatVertical(string label, string description, ref float value, float vSpeed = 1.0f, float vMin = float.MinValue, float vMax = float.MaxValue, string? format = null, ImGuiSliderFlags flags = ImGuiSliderFlags.None)
    {
        ImGui.TextUnformatted(label);
        if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
            Tooltip(description);   // 悬浮标签名
        ImGui.SetNextItemWidth(-1);
        var r = ImGui.DragFloat($"##{label}", ref value, vSpeed, vMin, vMax, format, flags);
        if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
            Tooltip(description);   // 悬浮滑条本身

        return r;
    }

    public static bool InputIntVertical(string label, string description, ref int value, int step = 1, int stepFast = 100, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        ImGui.TextUnformatted(label);
        if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
            Tooltip(description);   // 悬浮标签名
        ImGui.SetNextItemWidth(-1);
        var r = ImGui.InputInt($"##{label}", ref value, step, stepFast, flags: flags);
        if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
            Tooltip(description);   // 悬浮输入框本身

        return r;
    }

    public static void Tooltip(string tooltip)
    {
        using (ImRaii.Tooltip())
        using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 35.0f))
            ImGui.TextUnformatted(tooltip);
    }

    public static SingleFontChooserDialog? FontChooser(string label, SingleFontSpec font, bool checkbox, ref bool checkboxValue, Predicate<IFontFamilyId>? exclusion = null, string? preview = null)
    {
        using var id = ImRaii.PushId(label);

        ImGui.TextUnformatted(label);
        if (checkbox)
        {
            ImGui.Checkbox("##enabled", ref checkboxValue);
            ImGui.SameLine();
        }

        var fontFamily = font.FontId.Family.EnglishName;
        var fontStyle = font.FontId.EnglishName;
        fontStyle = fontStyle.Equals(fontFamily) ? "" : $" - {fontStyle}";

        var buttonText = $"{fontFamily}{fontStyle} ({font.SizePt}pt)";
        if (!ImGui.Button($"{buttonText}##{label}"))
            return null;

        var chooser = SingleFontChooserDialog.CreateAuto((UiBuilder) Plugin.Interface.UiBuilder);
        chooser.SelectedFont = font;
        if (exclusion is not null)
            chooser.FontFamilyExcludeFilter = exclusion;
        if (preview is not null)
            chooser.PreviewText = preview;

        return chooser;
    }

    public static void FontSizeCombo(string label, ref float currentSize)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo($"##{label}", $"{currentSize:###.##}pt");
        if (!combo.Success)
            return;

        // 每 2pt 一级（8~48），去掉 Axis 字体特有的零散数值（9.6/18.4/23/34 等）
        for (var size = 8f; size <= 48f; size += 2f)
            if (ImGui.Selectable($"{size:###.##}pt", currentSize.Equals(size)))
                currentSize = size;
    }

    public static bool Button(string id, FontAwesomeIcon icon, bool disabled)
    {
        using (ImRaii.Disabled(disabled))
            return ImGuiComponents.IconButton(id, icon);
    }

    public static bool CtrlShiftButton(string label, string tooltip = "")
    {
        var ctrlShiftHeld = ImGui.GetIO() is { KeyCtrl: true, KeyShift: true };

        bool ret;
        using (ImRaii.Disabled(!ctrlShiftHeld))
            ret = ImGui.Button(label) && ctrlShiftHeld;

        if (tooltip.Length != 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Tooltip(tooltip);

        return ret;
    }


    public static void DrawArrows(ref int selected, int min, int max, float spacing, int id = 0, string? tooltipLeft = null, string? tooltipRight = null)
    {
        // Prevents changing values from triggering EndDisable
        var isMin = selected == min;
        var isMax = selected == max;

        ImGui.SameLine(0, spacing);
        using (ImRaii.Disabled(isMin))
        {
            if (IconButton(FontAwesomeIcon.ArrowLeft, id.ToString()))
                selected--;
        }

        if (tooltipLeft != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltipLeft);

        ImGui.SameLine(0, spacing);

        using (ImRaii.Disabled(isMax))
        {
            if (IconButton(FontAwesomeIcon.ArrowRight, id+1.ToString()))
                selected++;
        }

        if (tooltipRight != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltipRight);
    }

    public static void WrappedTextWithColor(Vector4 color, string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextWrapped(text);
    }

    public static void CenterText(string text, float indent = 0.0f)
    {
        indent *= ImGuiHelpers.GlobalScale;
        ImGui.SameLine(((ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X) * 0.5f) + indent);
        ImGui.TextUnformatted(text);
    }

    public static bool TryToImGui(this VirtualKey key, out ImGuiKey result)
    {
        result = key switch
        {
            VirtualKey.NO_KEY => ImGuiKey.None,
            VirtualKey.BACK => ImGuiKey.Backspace,
            VirtualKey.TAB => ImGuiKey.Tab,
            VirtualKey.RETURN => ImGuiKey.Enter,
            VirtualKey.SHIFT => ImGuiKey.ModShift,
            VirtualKey.CONTROL => ImGuiKey.ModCtrl,
            VirtualKey.MENU => ImGuiKey.ModAlt,
            VirtualKey.PAUSE => ImGuiKey.Pause,
            VirtualKey.CAPITAL => ImGuiKey.CapsLock,
            VirtualKey.ESCAPE => ImGuiKey.Escape,
            VirtualKey.SPACE => ImGuiKey.Space,
            VirtualKey.PRIOR => ImGuiKey.PageUp,
            VirtualKey.NEXT => ImGuiKey.PageDown,
            VirtualKey.END => ImGuiKey.End,
            VirtualKey.HOME => ImGuiKey.Home,
            VirtualKey.LEFT => ImGuiKey.LeftArrow,
            VirtualKey.UP => ImGuiKey.UpArrow,
            VirtualKey.RIGHT => ImGuiKey.RightArrow,
            VirtualKey.DOWN => ImGuiKey.DownArrow,
            VirtualKey.SNAPSHOT => ImGuiKey.PrintScreen,
            VirtualKey.INSERT => ImGuiKey.Insert,
            VirtualKey.DELETE => ImGuiKey.Delete,
            VirtualKey.KEY_0 => ImGuiKey.Key0,
            VirtualKey.KEY_1 => ImGuiKey.Key1,
            VirtualKey.KEY_2 => ImGuiKey.Key2,
            VirtualKey.KEY_3 => ImGuiKey.Key3,
            VirtualKey.KEY_4 => ImGuiKey.Key4,
            VirtualKey.KEY_5 => ImGuiKey.Key5,
            VirtualKey.KEY_6 => ImGuiKey.Key6,
            VirtualKey.KEY_7 => ImGuiKey.Key7,
            VirtualKey.KEY_8 => ImGuiKey.Key8,
            VirtualKey.KEY_9 => ImGuiKey.Key9,
            VirtualKey.A => ImGuiKey.A,
            VirtualKey.B => ImGuiKey.B,
            VirtualKey.C => ImGuiKey.C,
            VirtualKey.D => ImGuiKey.D,
            VirtualKey.E => ImGuiKey.E,
            VirtualKey.F => ImGuiKey.F,
            VirtualKey.G => ImGuiKey.G,
            VirtualKey.H => ImGuiKey.H,
            VirtualKey.I => ImGuiKey.I,
            VirtualKey.J => ImGuiKey.J,
            VirtualKey.K => ImGuiKey.K,
            VirtualKey.L => ImGuiKey.L,
            VirtualKey.M => ImGuiKey.M,
            VirtualKey.N => ImGuiKey.N,
            VirtualKey.O => ImGuiKey.O,
            VirtualKey.P => ImGuiKey.P,
            VirtualKey.Q => ImGuiKey.Q,
            VirtualKey.R => ImGuiKey.R,
            VirtualKey.S => ImGuiKey.S,
            VirtualKey.T => ImGuiKey.T,
            VirtualKey.U => ImGuiKey.U,
            VirtualKey.V => ImGuiKey.V,
            VirtualKey.W => ImGuiKey.W,
            VirtualKey.X => ImGuiKey.X,
            VirtualKey.Y => ImGuiKey.Y,
            VirtualKey.Z => ImGuiKey.Z,
            VirtualKey.LWIN => ImGuiKey.LeftSuper,
            VirtualKey.RWIN => ImGuiKey.RightSuper,
            VirtualKey.NUMPAD0 => ImGuiKey.Keypad0,
            VirtualKey.NUMPAD1 => ImGuiKey.Keypad1,
            VirtualKey.NUMPAD2 => ImGuiKey.Keypad2,
            VirtualKey.NUMPAD3 => ImGuiKey.Keypad3,
            VirtualKey.NUMPAD4 => ImGuiKey.Keypad4,
            VirtualKey.NUMPAD5 => ImGuiKey.Keypad5,
            VirtualKey.NUMPAD6 => ImGuiKey.Keypad6,
            VirtualKey.NUMPAD7 => ImGuiKey.Keypad7,
            VirtualKey.NUMPAD8 => ImGuiKey.Keypad8,
            VirtualKey.NUMPAD9 => ImGuiKey.Keypad9,
            VirtualKey.MULTIPLY => ImGuiKey.KeypadMultiply,
            VirtualKey.ADD => ImGuiKey.KeypadAdd,
            VirtualKey.SUBTRACT => ImGuiKey.KeypadSubtract,
            VirtualKey.DECIMAL => ImGuiKey.KeypadDecimal,
            VirtualKey.DIVIDE => ImGuiKey.KeypadDivide,
            VirtualKey.F1 => ImGuiKey.F1,
            VirtualKey.F2 => ImGuiKey.F2,
            VirtualKey.F3 => ImGuiKey.F3,
            VirtualKey.F4 => ImGuiKey.F4,
            VirtualKey.F5 => ImGuiKey.F5,
            VirtualKey.F6 => ImGuiKey.F6,
            VirtualKey.F7 => ImGuiKey.F7,
            VirtualKey.F8 => ImGuiKey.F8,
            VirtualKey.F9 => ImGuiKey.F9,
            VirtualKey.F10 => ImGuiKey.F10,
            VirtualKey.F11 => ImGuiKey.F11,
            VirtualKey.F12 => ImGuiKey.F12,
            VirtualKey.NUMLOCK => ImGuiKey.NumLock,
            VirtualKey.SCROLL => ImGuiKey.ScrollLock,
            VirtualKey.OEM_NEC_EQUAL => ImGuiKey.KeypadEqual,
            VirtualKey.LSHIFT => ImGuiKey.LeftShift,
            VirtualKey.RSHIFT => ImGuiKey.RightShift,
            VirtualKey.LCONTROL => ImGuiKey.LeftCtrl,
            VirtualKey.RCONTROL => ImGuiKey.RightCtrl,
            VirtualKey.LMENU => ImGuiKey.LeftAlt,
            VirtualKey.RMENU => ImGuiKey.RightAlt,
            VirtualKey.OEM_1 => ImGuiKey.Semicolon,
            VirtualKey.OEM_PLUS => ImGuiKey.Equal,
            VirtualKey.OEM_COMMA => ImGuiKey.Comma,
            VirtualKey.OEM_MINUS => ImGuiKey.Minus,
            VirtualKey.OEM_PERIOD => ImGuiKey.Period,
            VirtualKey.OEM_2 => ImGuiKey.Slash,
            VirtualKey.OEM_3 => ImGuiKey.GraveAccent,
            VirtualKey.OEM_4 => ImGuiKey.LeftBracket,
            VirtualKey.OEM_5 => ImGuiKey.Backslash,
            VirtualKey.OEM_6 => ImGuiKey.RightBracket,
            VirtualKey.OEM_7 => ImGuiKey.Apostrophe,
            _ => 0,
        };

        return result != 0 || key == VirtualKey.NO_KEY;
    }

    public static void ChannelSelector(string headerText, Dictionary<ChatType, (ChatSource Source, ChatSource Target)> chatCodes, string? tooltip = null)
    {
        var spacing = 3.0f * ImGuiHelpers.GlobalScale;

        using var channelNode = ImRaii.TreeNode(headerText);
        if (!channelNode.Success)
            return;

        // !!! v1.40.17+ 说明悬浮在标题上（如"计入未读的频道"），hover 标题即显示，不占版面
        if (tooltip != null && ImGui.IsItemHovered())
            Tooltip(tooltip);

        foreach (var (header, types) in ChatTypeExt.SortOrder)
        {
            using var pushedId = ImRaii.PushId(header);

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Check))
            {
                foreach (var type in types)
                    chatCodes.TryAdd(type, (ChatSourceExt.All, ChatSourceExt.All));
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Language.ChannelSelector_Select);

            ImGui.SameLine(0, spacing);

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
            {
                foreach (var type in types)
                    chatCodes.Remove(type);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Language.ChannelSelector_Unselect);

            ImGui.SameLine(0, spacing);

            using var headerNode = ImRaii.TreeNode(header);
            if (!headerNode.Success)
                continue;

            foreach (var type in types)
            {
                if (type.IsGm())
                    continue;

                var enabled = chatCodes.ContainsKey(type);
                if (ImGui.Checkbox($"##{type.Name()}", ref enabled))
                {
                    if (enabled)
                        chatCodes[type] = (ChatSourceExt.All, ChatSourceExt.All);
                    else
                        chatCodes.Remove(type);
                }

                ImGui.SameLine();

                if (!type.HasSource())
                {
                    ImGui.TextUnformatted(type.Name());
                    continue;
                }

                using var typeNode = ImRaii.TreeNode($"{type.Name()}");
                if (!typeNode.Success)
                    continue;

                ImGui.Text(Language.ImGuiUtil_ChannelSelector_Source);
                ImGui.SameLine(400.0f * ImGuiHelpers.GlobalScale);
                ImGui.Text(Language.ImGuiUtil_ChannelSelector_Target);

                chatCodes.TryGetValue(type, out var sourcesEnum);
                var sources = (uint)sourcesEnum.Source;
                var targets = (uint)sourcesEnum.Target;

                foreach (var kind in Enum.GetValues<ChatSource>().Where(s => s != ChatSource.None))
                {
                    if (ImGui.CheckboxFlags($"{kind.Name()}##source", ref sources, (uint)kind))
                        chatCodes[type] = ((ChatSource)sources, sourcesEnum.Target);

                    ImGui.SameLine(400.0f * ImGuiHelpers.GlobalScale);

                    if (ImGui.CheckboxFlags($"{kind.Name()}##target", ref targets, (uint)kind))
                        chatCodes[type] = (sourcesEnum.Source, (ChatSource)targets);
                }
            }
        }
    }

    public static void ExtraChatSelector(string headerText, ref bool all, HashSet<Guid> extraChatChannels)
    {
        if (Plugin.ExtraChat.ChannelNames.Count <= 0)
            return;

        using var extraTree = ImRaii.TreeNode(headerText);
        if (!extraTree.Success)
            return;

        ImGui.Checkbox(Language.Options_Tabs_ExtraChatAll, ref all);
        ImGui.Separator();

        using var _ = ImRaii.Disabled(all);
        foreach (var (id, name) in Plugin.ExtraChat.ChannelNames)
        {
            var enabled = extraChatChannels.Contains(id);
            if (!ImGui.Checkbox($"{name}##ec-{id}", ref enabled))
                continue;

            if (enabled)
                extraChatChannels.Add(id);
            else
                extraChatChannels.Remove(id);
        }
    }

    public static Vector2 CalcIconButtonSize()
    {
        using (Plugin.FontManager.FontAwesomeSmall.Push())
            return ImGui.CalcTextSize(FontAwesomeIcon.Cog.ToIconString()) + ImGui.GetStyle().FramePadding * 2;
    }
}
