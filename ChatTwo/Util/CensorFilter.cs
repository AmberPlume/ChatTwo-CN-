using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DalamudGameSeString = Dalamud.Game.Text.SeStringHandling.SeString;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace ChatTwo.Util;

public static unsafe class CensorFilter
{
    private delegate void GetFilteredUtf8StringDelegate(nint vulgarInstance, Utf8String* str);

    private static GetFilteredUtf8StringDelegate? _getFiltered;
    private static nint _vulgarInstanceOffset;
    private static bool _initialized;
    private static bool _available;

    public static bool IsAvailable => _available;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var getFilteredAddr = SigScan("48 89 74 24 ?? 57 48 83 EC ?? 48 83 79 ?? ?? 48 8B FA 48 8B F1 0F 84 ?? ?? ?? ?? 48 89 5C 24");
            if (getFilteredAddr == nint.Zero)
            {
                Plugin.Log.Warning("[CensorFilter] GetFilteredUtf8String not found");
                return;
            }

            _getFiltered = Marshal.GetDelegateForFunctionPointer<GetFilteredUtf8StringDelegate>(getFilteredAddr);

            var vulgarSigAddr = SigScan("48 8B 81 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B D3");
            if (vulgarSigAddr == nint.Zero)
            {
                Plugin.Log.Warning("[CensorFilter] VulgarInstanceOffset not found");
                return;
            }

            byte* p = (byte*)vulgarSigAddr;
            _vulgarInstanceOffset = BitConverter.ToInt32(new byte[] { p[3], p[4], p[5], p[6] }, 0);

            _available = true;
            Plugin.Log.Information($"[CensorFilter] Ready! FilterFunc=0x{getFilteredAddr:X} VulgarOff=0x{_vulgarInstanceOffset:X}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[CensorFilter] Init failed");
        }
    }

    public static string GetFiltered(string input)
    {
        if (!_available || string.IsNullOrEmpty(input)) return input;

        try
        {
            var utf8String = Utf8String.FromString(input);
            var vulgarInstance = Marshal.ReadIntPtr((nint)Framework.Instance() + _vulgarInstanceOffset);
            _getFiltered!(vulgarInstance, utf8String);
            var result = utf8String->ToString();
            utf8String->Dtor(true);
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[CensorFilter] GetFiltered failed");
            return input;
        }
    }

    public static DalamudGameSeString ProcessContent(DalamudGameSeString input)
    {
        if (!_available || input == null || input.Payloads.Count == 0) return input!;

        // Use SeStringBuilder to construct the output, same approach as
        // DailyRoutines' HighlightCensorship. The builder's AddUiForeground
        // correctly wraps text with push/pop color payloads that the game's
        // color stack handles properly.
        bool anyChanged = false;
        var builder = new SeStringBuilder();

        foreach (var payload in input.Payloads)
        {
            if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload textPayload)
            {
                var text = textPayload.Text;
                if (string.IsNullOrEmpty(text))
                {
                    builder.Add(textPayload);
                    continue;
                }

                var filtered = GetFiltered(text);
                if (filtered == text)
                {
                    builder.Add(textPayload);
                }
                else
                {
                    anyChanged = true;
                    var subSegments = SplitByCensor(text, filtered);
                    foreach (var (subText, subCensored) in subSegments)
                    {
                        if (subCensored)
                        {
                            // AddUiForeground wraps text with UIForeground push/pop payloads,
                            // correctly handling the game's color stack (same as DailyRoutines).
                            builder.AddUiForeground(subText, 17);
                        }
                        else
                        {
                            builder.AddText(subText);
                        }
                    }
                }
            }
            else
            {
                // Preserve all non-text payloads (icon, color, link, etc.)
                builder.Add(payload);
            }
        }

        if (!anyChanged) return input;

        return builder.Build();
    }

    /// <summary>
    /// Splits the original text into (text, isCensored) segments by comparing the
    /// original and filtered strings at the CHARACTER level. The game's censor
    /// function replaces each censored CHARACTER with a single '*', not each byte.
    /// For example, "牛牛" (2 chars) → "**" (2 chars), not "******" (6 chars).
    /// The byte-level comparison previously used would fail because a 3-byte
    /// Chinese character replaced by a single '*' byte shifts all subsequent
    /// byte offsets, causing incorrect segment boundaries.
    /// </summary>
    private static List<(string text, bool isCensored)> SplitByCensor(string original, string filtered)
    {
        var result = new List<(string, bool)>();

        if (string.IsNullOrEmpty(original))
            return result;

        if (string.IsNullOrEmpty(filtered) || original == filtered)
        {
            result.Add((original, false));
            return result;
        }

        // Character-level walk: the game replaces each censored character
        // with a single '*'. Walk through both strings simultaneously:
        // - If filtered[filtIdx] == original[i], the character is unchanged (not censored)
        // - If filtered[filtIdx] == '*' and original[i] != '*', the character was censored
        int filtIdx = 0;
        bool insideCensor = false;
        int runStart = 0;

        for (int i = 0; i < original.Length; i++)
        {
            bool isCensored;
            if (filtIdx < filtered.Length && filtered[filtIdx] == original[i])
            {
                // Character unchanged - not censored
                isCensored = false;
                filtIdx++;
            }
            else if (filtIdx < filtered.Length && filtered[filtIdx] == '*')
            {
                // Character was censored - replaced with '*'
                // (original[i] != '*' is already guaranteed by the first branch)
                isCensored = true;
                filtIdx++;
            }
            else
            {
                // Fallback: assume not censored (shouldn't normally happen)
                isCensored = false;
            }

            if (isCensored && !insideCensor)
            {
                if (i > runStart) result.Add((original[runStart..i], false));
                insideCensor = true;
                runStart = i;
            }
            else if (!isCensored && insideCensor)
            {
                result.Add((original[runStart..i], true));
                insideCensor = false;
                runStart = i;
            }
        }

        if (insideCensor)
            result.Add((original[runStart..], true));
        else if (runStart < original.Length)
            result.Add((original[runStart..], false));

        return result;
    }

    private static nint SigScan(string pattern)
    {
        try
        {
            var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[parts.Length];
            var mask = new bool[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "??")
                {
                    mask[i] = false;
                }
                else
                {
                    bytes[i] = Convert.ToByte(parts[i], 16);
                    mask[i] = true;
                }
            }

            var mainModule = System.Diagnostics.Process.GetCurrentProcess().MainModule;
            if (mainModule == null) return nint.Zero;

            byte* start = (byte*)mainModule.BaseAddress;
            byte* end = start + mainModule.ModuleMemorySize;

            for (byte* p = start; p <= end - bytes.Length; p++)
            {
                bool match = true;
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (mask[i] && p[i] != bytes[i])
                    {
                        match = false;
                        break;
                    }
                }

                if (match) return (nint)p;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"[CensorFilter] SigScan({pattern}) failed");
        }

        return nint.Zero;
    }
}
