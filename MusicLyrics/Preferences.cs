using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Screen = System.Windows.Forms.Screen;

namespace MusicLyrics;

internal enum Entrance { Fade, Pop, Ripple, Instant, Stack, Tilt, Fill, WordByWord, Cinema }

internal enum ExitTransition { None, Fade, Lift, Recede }

internal sealed record Preset(string Name, Entrance Effect, double FontSize, string Color,
    double Background, int Duration, double Scale)
{
    public const int MaxAccentColors = 12;
    public string? BasedOn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Preset? ResetDefaults { get; init; }
    public string? Font { get; init; }
    public Dictionary<string, string> LanguageFonts { get; init; } = [];
    public double Outline { get; init; }
    public string OutlineColor { get; init; } = "#201C28";
    public bool Blur { get; init; }
    public ExitTransition Exit { get; init; } = Effect == Entrance.Instant ? ExitTransition.None : ExitTransition.Fade;
    public bool Scatter { get; init; }
    public double Tilt { get; init; }
    public double StageStackSpeed { get; init; } = 1;
    public string[] AccentColors { get; init; } = [];
    public int MaxLineColors { get; init; } = 2;
    public int MaxHighlightWords { get; init; } = 1;
    public int? HighlightPercent { get; init; }
    // Read absolute sizes from older presets, including their saved reset defaults.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HighlightFontSize
    {
        get => null;
        init
        {
            if (value is int size)
                HighlightPercent ??= size is >= 12 and <= 100 && FontSize is >= 12 and <= 100
                    ? (int)Math.Round(100 * size / FontSize) : 0;
        }
    }
    public bool RandomPalettePerSong { get; init; }

    public Preset ForSong(Track? track)
    {
        if (!RandomPalettePerSong) return this;
        var song = (uint)HashCode.Combine(track?.Title, track?.Artist, track?.Album);
        var colors = Palettes[song % Palettes.Length].Colors;
        return this with { Color = colors[0], AccentColors = colors[1..] };
    }

    internal static readonly (string Name, string[] Colors)[] Palettes =
    [
        ("Warm", ["#FFF4DE", "#FFE58A", "#FFA69A", "#FFD0A1", "#FF826F", "#FFB347", "#F69CBA", "#FFD764"]),
        ("Cool", ["#EAF6FF", "#A7DDFF", "#A8F0DD", "#C9BBFF", "#70C8FF", "#87A5FF", "#74E5E7", "#DBAFFF"]),
        ("Ocean", ["#E3FAFF", "#45D9FF", "#79B7FF", "#4BE0C0", "#9FF4EB", "#62ACDF", "#92CEFF", "#45C8CC"]),
        ("Cherry Blossom", ["#FFF2F5", "#FF86AF", "#FFB3CB", "#EA8AA8", "#FFCCD7", "#EBA4D8", "#FF9B98", "#F27CAA"]),
        ("Citrus", ["#FDFFD8", "#C5EE51", "#FFDA52", "#FFAC52", "#9EE579", "#FFE783", "#E4F783", "#FFBE81"]),
        ("Midnight Gold", ["#E5E6FF", "#FFCD57", "#EDB74B", "#FFF0A6", "#DBA76C", "#F8DD87", "#A1AFF3", "#B8A7DD"]),
        ("Mint Chocolate", ["#FFE4CC", "#70ECC2", "#A0F2D3", "#D8AE90", "#67CFB4", "#EFC59A", "#B0DDAD", "#C79585"]),
        ("Orchid", ["#FFF0FD", "#CD96FF", "#E7B0FF", "#F18EDD", "#AB9DF8", "#F8B4DD", "#D783EF", "#BFB5FF"]),
        ("Monochrome", ["#FFFFFF", "#EBEBEB", "#D7D7D7", "#C4C4C4", "#F5F5F5", "#B6B6B6", "#A8A8A8", "#999999"])
    ];
    public static readonly Preset[] BuiltIns =
    [
        new("Stage Stack", Entrance.Stack, 34, "#F4ECFF", .58, 170, .92),
        new("Violet Tilt", Entrance.Tilt, 32, "#DBBCFF", .58, 190, .92),
        new("Karaoke Fill", Entrance.Fill, 34, "#CDA6FF", .68, 180, .96),
        new("Soft Fade", Entrance.Fade, 30, "#FFFFFF", .60, 160, .96),
        new("Gentle Pop", Entrance.Pop, 30, "#FFFFFF", .60, 180, .96),
        new("Word Ripple", Entrance.Ripple, 30, "#FFFFFF", .60, 220, .96),
        new("Instant", Entrance.Instant, 30, "#FFFFFF", .60, 160, .96),
        new("Paper Pop", Entrance.Pop, 34, "#FFE58A", .30, 170, .92)
        { Font = "Impact", Outline = 2, Blur = true, Scatter = true, Tilt = 3, AccentColors = ["#FFA69A", "#FFF4DE"] },
        new("Frosted Cinema", Entrance.Cinema, 32, "#FFF4DE", .45, 240, .96)
        { Font = "Georgia", Outline = 1, OutlineColor = "#29272B", Blur = true }
    ];

    public Preset Duplicate(string name) => this with
    {
        Name = name,
        ResetDefaults = this with { ResetDefaults = null }
    };

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Enum.IsDefined(Effect) && Enum.IsDefined(Exit) &&
        FontSize is >= 12 and <= 100 && ValidColor(Color) && Background is >= 0 and <= 1 &&
        Outline is >= 0 and <= 6 && Tilt is >= 0 and <= 15 && ValidColor(OutlineColor) &&
        MaxLineColors is >= 1 and <= MaxAccentColors + 1 && MaxHighlightWords is >= 0 and <= 32 &&
        HighlightPercent is null or (>= 12 and <= 833) &&
        AccentColors is { Length: <= MaxAccentColors } && AccentColors.All(ValidColor) &&
        (Font is null || ValidFont(Font)) && LanguageFonts is not null &&
        LanguageFonts.All(kv => LanguageTypography.Languages.ContainsKey(kv.Key) && ValidFont(kv.Value)) &&
        Duration is >= 80 and <= 250 && Scale is >= .80 and <= 1 && StageStackSpeed is >= 1 and <= 4;

    public static bool ValidColor(string? color) => color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
    private static bool ValidFont(string? font) => !string.IsNullOrWhiteSpace(font) && font.Length <= 150 &&
        font.IndexOfAny(['/', '\\', ':', '#']) < 0;
}

internal sealed record Hotkey(uint Modifiers, uint Key)
{
    public static readonly Hotkey Default = new(3, 0x4C);
    public bool IsValid => Modifiers is > 0 and <= 7 &&
        (Key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 or >= 0x70 and <= 0x7A);

    public override string ToString()
    {
        var key = Key >= 0x70 ? $"F{Key - 0x6F}" : ((char)Key).ToString();
        return ((Modifiers & 2) != 0 ? "Ctrl+" : "") + ((Modifiers & 1) != 0 ? "Alt+" : "") +
            ((Modifiers & 4) != 0 ? "Shift+" : "") + key;
    }
}

internal sealed record Placement(string Monitor, OverlayRect Rect)
{
    public static Placement Default(Screen screen)
    {
        var scale = Native.Scale(screen);
        var width = screen.Bounds.Width / scale;
        var height = screen.Bounds.Height / scale;
        var boxWidth = Math.Min(850, width * .8);
        return new Placement(screen.DeviceName,
            new OverlayRect((width - boxWidth) / 2, height - 160, boxWidth, 120).Fit(width, height));
    }

    public (Screen Screen, Placement Placement) Resolve()
    {
        var screen = Screen.AllScreens.FirstOrDefault(candidate => candidate.DeviceName == Monitor) ??
            Screen.PrimaryScreen ?? Screen.AllScreens.First();
        var scale = Native.Scale(screen);
        return (screen, new Placement(screen.DeviceName,
            Rect.Fit(screen.Bounds.Width / scale, screen.Bounds.Height / scale)));
    }
}

internal sealed class Preferences
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicLyrics");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    public Placement? Layout { get; set; }
    public Hotkey Shortcut { get; set; } = Hotkey.Default;
    public List<Preset> Custom { get; set; } = [];
    public Dictionary<string, Preset> BuiltInOverrides { get; set; } = [];
    public string ActivePreset { get; set; } = Preset.BuiltIns[0].Name;
    public string? OnlineLyricsContact { get; set; }
    [JsonIgnore]
    public IEnumerable<Preset> Presets => Preset.BuiltIns.Select(p => BuiltInOverrides.GetValueOrDefault(p.Name, p)).Concat(Custom);

    public void UpdatePreset(Preset preset)
    {
        if (Preset.BuiltIns.Any(p => p.Name == preset.Name)) BuiltInOverrides[preset.Name] = preset;
        else
        {
            var index = Custom.FindIndex(p => p.Name == preset.Name);
            if (index >= 0) Custom[index] = preset;
        }
    }

    public void ResetPreset(Preset preset)
    {
        if (BuiltInOverrides.Remove(preset.Name) || Preset.BuiltIns.Any(p => p.Name == preset.Name)) return;
        var baseline = preset.ResetDefaults ?? LegacyBaseline(preset);
        UpdatePreset(baseline with { Name = preset.Name, BasedOn = preset.BasedOn, ResetDefaults = preset.ResetDefaults });
    }

    private static Preset LegacyBaseline(Preset preset) => Preset.BuiltIns.FirstOrDefault(p => p.Name == preset.BasedOn) ??
        Preset.BuiltIns.FirstOrDefault(p => p.Effect == preset.Effect) ?? Preset.BuiltIns[0];

    public static Preferences Load(string? filePath = null)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(filePath ?? FilePath)) ?? new();
            settings.Shortcut = settings.Shortcut is { IsValid: true } ? settings.Shortcut : Hotkey.Default;
            settings.BuiltInOverrides = (settings.BuiltInOverrides ?? []).Where(kv =>
                kv.Value is { IsValid: true, ResetDefaults: null } && kv.Value.Name == kv.Key &&
                Preset.BuiltIns.Any(p => p.Name == kv.Key)).ToDictionary();
            // Earlier versions allowed custom presets to use the two new gallery names.
            var usedNames = (settings.Custom ?? []).OfType<Preset>().Select(p => p.Name)
                .Concat(Preset.BuiltIns.Select(p => p.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (settings.Custom is { } custom)
            {
                for (var i = 0; i < custom.Count; i++)
                {
                    var preset = custom[i];
                    if (preset is not { IsValid: true } || !(preset.Name.Equals("Paper Pop", StringComparison.OrdinalIgnoreCase) ||
                        preset.Name.Equals("Frosted Cinema", StringComparison.OrdinalIgnoreCase))) continue;
                    var name = preset.Name + " (custom)";
                    for (var suffix = 2; !usedNames.Add(name); suffix++) name = $"{preset.Name} (custom {suffix})";
                    custom[i] = preset with { Name = name };
                    if (settings.ActivePreset == preset.Name) settings.ActivePreset = name;
                }
            }
            settings.Custom = (settings.Custom ?? []).Where(p => p is { IsValid: true } &&
                !Preset.BuiltIns.Any(b => string.Equals(b.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.ResetDefaults is { IsValid: true, ResetDefaults: null }
                    ? p : p with { ResetDefaults = null, BasedOn = LegacyBaseline(p).Name }).ToList();
            if (!settings.Presets.Any(p => p.Name == settings.ActivePreset)) settings.ActivePreset = Preset.BuiltIns[0].Name;
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string? filePath = null)
    {
        filePath = Path.GetFullPath(filePath ?? FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporary = filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, filePath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
