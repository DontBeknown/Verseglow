using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;

namespace MusicLyrics;

internal static class LanguageTypography
{
    public static readonly Dictionary<string, CultureInfo> Languages = CultureInfo
        .GetCultures(CultureTypes.NeutralCultures).Where(c => c.Name.Length > 0)
        .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    // Windows ICU supplies the writing systems for each locale, including multi-script languages.
    [DllImport("icu.dll", CharSet = CharSet.Ansi)]
    private static extern int uscript_getCode(string locale, [Out] int[] scripts, int capacity, ref int error);
    [DllImport("icu.dll")]
    private static extern int uscript_getScript(int scalar, ref int error);

    internal static bool HasThai(string text) => text.Any(c => c is >= '\u0E01' and <= '\u0E5B');
    internal static readonly FontFamily Kanit = new(new Uri("pack://application:,,,/Verseglow;component/"), "./Fonts/#Kanit");

    public static void Apply(FormattedText layout, string text, Dictionary<string, string> fonts, string? language)
    {
        if (fonts.Count == 0 && !HasThai(text)) return;
        var scripts = new Dictionary<int, string?>();
        foreach (var (tag, family) in fonts)
        {
            var codes = new int[16];
            int error = 0, count = uscript_getCode(tag, codes, codes.Length, ref error);
            if (error > 0) continue;
            foreach (var code in codes.Take(count))
                if (!scripts.TryAdd(code, family) && scripts[code] != family) scripts[code] = null;
        }
        if (language is not null)
        {
            var family = fonts.Where(entry => language.Equals(entry.Key, StringComparison.OrdinalIgnoreCase) ||
                    language.StartsWith(entry.Key + "-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.Key.Length).Select(entry => entry.Value).FirstOrDefault();
var codes = new int[16];
            int error = 0, count = uscript_getCode(language, codes, codes.Length, ref error);
            if (error <= 0)
                foreach (var code in codes.Take(count)) scripts[code] = family;
        }
        // ICU USCRIPT_THAI is 38. Keep any explicit font override.
        const int thaiScript = 38;
        if (scripts.GetValueOrDefault(thaiScript) is null) scripts[thaiScript] = "Kanit Bold";
        int start = 0, offset = 0;
        string? current = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var error = 0;
            var script = uscript_getScript(rune.Value, ref error);
            var next = script is 0 or 1 ? current : scripts.GetValueOrDefault(script);
            if (next != current)
            {
                ApplyRun();
                current = next;
                start = offset;
            }
            offset += rune.Utf16SequenceLength;
        }
        ApplyRun();

        void ApplyRun()
        {
            if (current is null) return;
            var family = current is "Kanit" or "Kanit Bold" ? Kanit : new FontFamily(current);
            layout.SetFontFamily(family, start, offset - start);
            if (current == "Kanit Bold")
                layout.SetFontWeight(System.Windows.FontWeights.Bold, start, offset - start);
        }
    }
}
