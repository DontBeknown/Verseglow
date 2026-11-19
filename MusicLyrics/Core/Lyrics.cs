using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MusicLyrics;

internal sealed record LyricLine(long AtMs, string Text, string? Language = null);
internal sealed record Track(string Title, string Artist, string Album, double? Duration);
internal sealed record LyricResult(long Id, string Title, string Artist, string Album,
    double? Duration, string? TimedLyrics);

internal static partial class Lyrics
{
    [GeneratedRegex(@"\G\[([0-9]{1,6}):([0-5][0-9])(?:[.:]([0-9]{1,3}))?\]")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"(?im)^\s*\[offset\s*:\s*([+-]?\d+)\s*\]\s*$")]
    private static partial Regex FileOffset();

    [GeneratedRegex(@"(?i)\s*(?:\((?:official (?:(?:music|lyric) )?video|official audio)\)|\[(?:official (?:(?:music|lyric) )?video|official audio)\])(?=\s*(?:\(|\[|$))")]
    private static partial Regex VideoLabel();

    public static string BrowserTitle(string title, string artist)
    {
        var trimmed = VideoLabel().Replace(title, "").Trim();
        if (trimmed.Length == 0) return title;
        title = trimmed;
        if (artist.Length > 0)
        {
            foreach (var separator in new[] { " - ", " – ", " — " })
            {
                var prefix = artist + separator;
                if (!title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                title = title[prefix.Length..];
                break;
            }
            var quotedPrefix = artist + " ";
            if (title.StartsWith(quotedPrefix, StringComparison.OrdinalIgnoreCase) && title.Length > quotedPrefix.Length)
            {
                var quote = title[quotedPrefix.Length];
                if (quote is '"' or '“')
                {
                    var end = title.LastIndexOf(quote == '"' ? '"' : '”');
                    if (end > quotedPrefix.Length + 1)
                        title = title[(quotedPrefix.Length + 1)..end] + title[(end + 1)..];
                }
            }
        }
        return title.Trim();
    }

    public static Track BrowserTrack(Track track)
    {
        var artist = track.Artist.EndsWith("VEVO", StringComparison.Ordinal) ? track.Artist[..^4].TrimEnd() :
            track.Artist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ? track.Artist[..^8].TrimEnd() :
            track.Artist;
        var title = BrowserTitle(track.Title, artist);
        return track with { Title = title, Artist = artist.Length > 0 ? artist : track.Artist };
    }

    public static Track BrowserFallback(Track raw)
    {
        var track = BrowserTrack(raw);
        var title = BrowserTitle(raw.Title, "");
        if (!Equal(title, track.Title)) return track;
        var parts = title.Split([" - ", " – ", " — "], 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts.All(part => part.Length > 0)
            ? track with { Artist = parts[0], Title = parts[1] }
            : track;
    }

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+")]
    private static partial Regex MetadataWords();

    public static LyricResult? BestMatch(Track track, IEnumerable<LyricResult> results)
    {
        return results.Where(result => !string.IsNullOrWhiteSpace(result.TimedLyrics))
            .OrderByDescending(result => 2 * Similarity(track.Title, BrowserTitle(result.Title, result.Artist)) +
                Similarity(track.Artist, result.Artist))
            .ThenByDescending(result => track.Album.Length > 0 && Equal(track.Album, result.Album))
            .ThenBy(result => Math.Abs((track.Duration - result.Duration) ?? double.PositiveInfinity))
            .ThenBy(result => result.Id)
            .FirstOrDefault(result => Parse(result.TimedLyrics!).Count > 0);
    }

    private static double Similarity(string left, string right)
    {
        if (Equal(left, right)) return 1;
        var expected = MetadataWords().Matches(left).Select(word => word.Value).ToArray();
        var actual = MetadataWords().Matches(right).Select(word => word.Value).ToArray();
        if (expected.Length == 0 || actual.Length == 0) return 0;
        if (expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase)) return 1;
        return (double)expected.Intersect(actual, StringComparer.OrdinalIgnoreCase).Count() /
            Math.Max(expected.Length, actual.Length);
    }

    public static IReadOnlyList<LyricLine> Parse(string text)
    {
        var language = Regex.Match(text, @"(?im)^\s*\[la:\s*([a-z]{2,3}(?:-[a-z0-9]{2,8})*)\s*\]\s*$").Groups[1].Value;
        var offset = FileOffset().Match(text);
        int.TryParse(offset.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var offsetMs);
        var lines = new List<LyricLine>();
        foreach (var row in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var content = row.Trim().TrimStart('\uFEFF');
            var stamps = Timestamp().Matches(content);
            if (stamps.Count == 0) continue;
            var last = stamps[^1];
            var words = content[(last.Index + last.Length)..].Trim();
            foreach (Match stamp in stamps)
            {
                var minutes = long.Parse(stamp.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                var seconds = int.Parse(stamp.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
                var fraction = stamp.Groups[3].Value.PadRight(3, '0');
                var ms = int.Parse(fraction, CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(Math.Max(0, minutes * 60000 + seconds * 1000 + ms + offsetMs), words));
            }
        }
        if (lines.All(line => string.IsNullOrWhiteSpace(line.Text))) return [];
        return lines.OrderBy(line => line.AtMs).GroupBy(line => line.AtMs)
            .Select(group => new LyricLine(group.Key,
                string.Join("\n", group.Select(line => line.Text).Where(value => value.Length > 0).Distinct()), language.Length == 0 ? null : language))
            .ToArray();
    }

    public static int At(IReadOnlyList<LyricLine> lines, double position)
    {
        var low = 0;
        var high = lines.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (lines[middle].AtMs <= position) low = middle + 1;
            else high = middle;
        }
        return low - 1;
    }

    public static int Offset(int current, int delta) => Math.Clamp(current + delta, -10000, 10000);

    public static LyricResult? Match(Track track, IEnumerable<LyricResult> results, bool browser = false)
    {
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)) return null;
        var title = browser ? BrowserTitle(track.Title, track.Artist) : track.Title;
        var candidates = results.Where(result => Equal(title,
                browser ? BrowserTitle(result.Title, result.Artist) : result.Title) &&
            Equal(track.Artist, result.Artist) &&
            (string.IsNullOrWhiteSpace(track.Album) || Equal(track.Album, result.Album)) &&
            DurationMatches(track, result) &&
            result.TimedLyrics is { } lrc && Parse(lrc).Count > 0)
            .DistinctBy(result => result.Id).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static bool DurationMatches(Track track, LyricResult result) =>
        track.Duration is null || result.Duration is > 0 &&
        Math.Abs(track.Duration.Value - result.Duration.Value) <= 2;

    private static bool Equal(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}

internal sealed class LyricSelection
{
    public long Revision { get; private set; }
    public IReadOnlyList<LyricLine> Lines { get; private set; } = [];
    public bool IsManual { get; private set; }

    public long Begin()
    {
        Lines = [];
        IsManual = false;
        return ++Revision;
    }

    public bool TrySet(long revision, IReadOnlyList<LyricLine> lines, bool manual)
    {
        if (revision != Revision || IsManual && !manual || lines.Count == 0) return false;
        Lines = lines;
        IsManual = manual;
        return true;
    }

    public void ClearAutomatic()
    {
        if (!IsManual) Lines = [];
    }
}
