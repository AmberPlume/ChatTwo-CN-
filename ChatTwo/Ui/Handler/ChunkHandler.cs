using System.Numerics;
using ChatTwo.Code;
using ChatTwo.Util;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace ChatTwo.Ui.Handler;

public class ChunkHandler
{
    private readonly Plugin Plugin;

    public ChunkHandler(Plugin plugin)
    {
        Plugin = plugin;
    }

    public void DrawChunks(IReadOnlyList<Chunk> chunks, bool wrap = true, PayloadHandler? handler = null, float lineWidth = 0f)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i] is TextChunk text && string.IsNullOrEmpty(text.Content))
                continue;

            DrawChunk(chunks[i], wrap, handler, lineWidth);

            if (NextDrawable(chunks, i + 1) is { } next)
                SameLineIfFits(next);
        }
    }

    /// <summary>在给定 chunk 序列之前续行（发送者→正文、时间戳→发送者这类边界用）。</summary>
    public void SameLineBefore(IReadOnlyList<Chunk> chunks)
    {
        if (NextDrawable(chunks, 0) is { } next)
            SameLineIfFits(next);
        else
            ImGui.SameLine();
    }

    /// <summary>下一个真正会绘制的 chunk（空 TextChunk 不产生 item）。</summary>
    private static Chunk? NextDrawable(IReadOnlyList<Chunk> chunks, int from)
    {
        for (var i = from; i < chunks.Count; i++)
        {
            if (chunks[i] is TextChunk text && string.IsNullOrEmpty(text.Content))
                continue;
            return chunks[i];
        }

        return null;
    }

    /// <summary>
    /// !!! ImGui 的 SameLine 不做折行：ItemSize 已把光标推到下一行，SameLine 又把它拉回上一行末尾
    /// （CursorPosPrevLine），完全不看剩余宽度。折行只发生在单个 text/Image item 内部，所以窄窗口下
    /// chunk 边界会把 item 顶到右边界外侧——图标被裁一半；文字则拿到接近 0 的可用宽度，被
    /// CalcWordWrapPositionA 的"至少推进 1 字节"保底粘在上一行末尾（CJK 还会断在多字节字符中间）。
    /// 续行前测上一行剩余宽度：放不下下一个 chunk 的首个断行单元就让它另起一行。
    /// </summary>
    private void SameLineIfFits(Chunk next)
    {
        var minWidth = MinWidthOf(next);
        if (minWidth <= 0f)
        {
            ImGui.SameLine();
            return;
        }

        // !!! 不能用 GetContentRegionAvail：上一条 item 的 ItemSize 已把光标推到下一行行首，
        // avail 是整行宽。SameLine 粘到的位置 = 上一条 item 右边缘 + ItemSpacing
        // （ItemSize 里 CursorPosPrevLine.x = CursorPos.x + size.x，与 item 矩形右边缘同值）。
        var lineRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        var glueX = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X;
        if (lineRight - glueX >= minWidth)
            ImGui.SameLine();
    }

    /// <summary>下一个 chunk 的最小不可断宽度：文字=首个断行单元，图标=图标实际绘制宽。</summary>
    private float MinWidthOf(Chunk chunk)
    {
        switch (chunk)
        {
            case IconChunk icon:
                return IconUtil.GfdFileView.TryGetEntry((uint) icon.Icon, out var entry) ? IconSize(entry).X : 0f;
            case TextChunk text when !string.IsNullOrEmpty(text.Content):
                // 与 DrawChunk 同一套字间距口径：只有正文带字间距
                var letterSpacing = chunk.Source == ChunkSource.Content
                    ? Plugin.Config.MessageLetterSpacing * ImGuiHelpers.GlobalScale
                    : 0f;
                return ImGuiUtil.FirstUnitWidth(text.Content, letterSpacing);
            default:
                return 0f;
        }
    }

    /// <summary>图标绘制尺寸：随当前生效字号等比例缩放（与文字同高，跟随主字体和 UI 缩放）。</summary>
    private static Vector2 IconSize(GfdFileView.GfdEntry entry)
        => new Vector2(entry.Width, entry.Height) * (ImGui.GetFontSize() / entry.Height);

    public void DrawIcon(Chunk chunk, IconChunk icon, PayloadHandler? handler)
    {
        if (!IconUtil.GfdFileView.TryGetEntry((uint) icon.Icon, out var entry))
            return;

        var iconTexture = Plugin.TextureProvider.GetFromGame("common/font/fonticon_ps5.tex").GetWrapOrDefault();
        if (iconTexture == null)
            return;

        var texSize = new Vector2(iconTexture.Width, iconTexture.Height);
        var size = IconSize(entry);

        var uv0 = new Vector2(entry.Left, entry.Top + 170) * 2 / texSize;
        var uv1 = new Vector2(entry.Left + entry.Width, entry.Top + entry.Height + 170) * 2 / texSize;

        ImGui.Image(iconTexture.Handle, size, uv0, uv1);
        ImGuiUtil.PostPayload(chunk, handler);
    }

    public void DrawChunk(Chunk chunk, bool wrap = true, PayloadHandler? handler = null, float lineWidth = 0f)
    {
        if (chunk is IconChunk icon)
        {
            DrawIcon(chunk, icon, handler);
            return;
        }

        if (chunk is not TextChunk text)
            return;

        var color = text.Color;
        if (color == null && text.FallbackColor != null)
        {
            var type = text.FallbackColor.Value;
            color = Plugin.Config.ChatColours.TryGetValue(type, out var col)
                ? ColourUtil.RgbaToVector4(col)
                : ColourUtil.RgbaToVector4(type.DefaultColor());
        }

        using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, color);

        // 简化后不再单独构建 ItalicFont，斜体消息统一用 AxisItalic
        var disposableFont = Plugin.FontManager.AxisItalic;
        if (text.Italic)
            disposableFont.Push();

        // Check for contains here as sometimes there are multiple
        // TextChunks with the same PlayerPayload but only one has the name.
        // E.g. party chat with cross world players adds extra chunks.
        // // Note: This has been null before, I'm guessing due to some issues with
        // other plugins. New TextChunks will now enforce empty string in ctor,
        // but old ones may still be null.
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
        var content = text.Content ?? "";
        if (PlayerUtil.ScreenshotMode)
        {
            if (chunk.Link is PlayerPayload playerPayload)
                content = PlayerUtil.HidePlayerInString(content, playerPayload.PlayerName, playerPayload.World.RowId);
            else if (Plugin.PlayerState.IsLoaded)
                content = PlayerUtil.HidePlayerInString(content, Plugin.PlayerState.CharacterName, Plugin.PlayerState.HomeWorld.RowId);
        }

        if (wrap)
        {
            // 正文字间距：只给消息内容（Content）传间距，时间戳/发送者名保持原样
            var letterSpacing = chunk.Source == ChunkSource.Content
                ? Plugin.Config.MessageLetterSpacing * ImGuiHelpers.GlobalScale
                : 0f;
            ImGuiUtil.WrapText(content, chunk, handler, Plugin.DefaultText, lineWidth, letterSpacing);
        }
        else
        {
            ImGuiUtil.TextUnformattedOutline(content);
            ImGuiUtil.PostPayload(chunk, handler);
        }

        if (text.Italic)
            disposableFont.Pop();
    }
}