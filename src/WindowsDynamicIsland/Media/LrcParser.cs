using System.Globalization;
using System.Text.RegularExpressions;
using WindowsDynamicIsland.Core.Models;

namespace WindowsDynamicIsland.Media;

/// <summary>
/// Parser for the LRC synchronized-lyrics format: lines like <c>[01:23.45]text</c>, several time tags per line,
/// metadata tags (<c>[ar:…]</c>) ignored, <c>[offset:+/-ms]</c> honoured. Pure, allocation-light, never throws.
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return result;

        var offset = TimeSpan.Zero;
        foreach (var rawLine in lrc.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var off = OffsetTag().Match(line);
            if (off.Success && int.TryParse(off.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
            {
                // Positive offset = lyrics appear earlier.
                offset = TimeSpan.FromMilliseconds(-ms);
                continue;
            }

            var matches = TimeTag().Matches(line);
            if (matches.Count == 0) continue;

            var last = matches[^1];
            var text = line[(last.Index + last.Length)..].Trim();

            foreach (Match m in matches)
            {
                var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = 0.0;
                if (m.Groups[3].Success)
                {
                    var f = m.Groups[3].Value;
                    fraction = int.Parse(f, CultureInfo.InvariantCulture) / Math.Pow(10, f.Length);
                }
                var time = TimeSpan.FromSeconds(minutes * 60 + seconds + fraction) + offset;
                if (time < TimeSpan.Zero) time = TimeSpan.Zero;
                result.Add(new LyricLine(time, text));
            }
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    /// <summary>Index of the line being sung at <paramref name="position"/> (−1 before the first line).</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }
}
