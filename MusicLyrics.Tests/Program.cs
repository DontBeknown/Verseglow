using MusicLyrics;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
}

var factory = Preset.BuiltIns[0];
var colors = factory with { AccentColors = ["#112233", "#334455"] };
Check(LyricVisual.WordVisible(0, 0, 4) && !LyricVisual.WordVisible(.24, 1, 4) &&
    LyricVisual.WordVisible(.25, 1, 4) && LyricVisual.WordVisible(.75, 3, 4), "Word reveal spans the cue");

var settingsFile = Path.GetTempFileName();
try
{
    File.WriteAllText(settingsFile, """
        {"ActivePreset":"Frosted Cinema","Custom":[{"Name":"Legacy","Effect":4,"FontSize":42,"Color":"#FFFFFF","Background":0.5,"Duration":170,"Scale":0.92}]}
        """);
    var settings = Preferences.Load(settingsFile);
    Check(settings.Presets.Single(p => p.Name == settings.ActivePreset).Effect == Entrance.Cinema,
        "Legacy settings use the updated Frosted Cinema factory effect");
    var legacy = settings.Custom.Single() with { Effect = Entrance.Fade };
    settings.UpdatePreset(legacy);
    settings.ResetPreset(legacy);
    Check(settings.Custom.Single().FontSize == factory.FontSize, "Legacy custom preset resets to its base");

    var edited = colors with { FontSize = 40, Effect = Entrance.WordByWord };
    settings.UpdatePreset(edited);
    var copy = edited.Duplicate("Copy") with { FontSize = 51 };
    settings.Custom.Add(copy);
    settings.ActivePreset = copy.Name;
    settings.Save(settingsFile);
    settings = Preferences.Load(settingsFile);
    Check(settings.ActivePreset == copy.Name && settings.Presets.First().FontSize == 40 &&
        settings.Presets.First().Effect == Entrance.WordByWord && settings.Custom.Last().FontSize == 51,
        "Built-in overrides and custom presets survive restart");
    settings.ResetPreset(settings.Presets.First());
    Check(settings.Presets.First() == factory && settings.Custom.Last().FontSize == 51,
        "Built-in reset affects only that preset");
    settings.ResetPreset(settings.Custom.Last());
    var reset = settings.Custom.Last();
    Check(reset.Name == copy.Name && reset.FontSize == 40 && reset.Effect == Entrance.WordByWord &&
        reset.AccentColors.SequenceEqual(colors.AccentColors), "Custom reset restores its creation snapshot");
}
finally { File.Delete(settingsFile); }

var lines = Lyrics.Parse("[00:03.00]last\n[offset:+100]\n[00:01.1][00:02.02]first\n[99:99]bad");
Check(lines.Count == 3 && lines[0] == new LyricLine(1200, "first") &&
    lines[1].AtMs == 2120 && lines[2].AtMs == 3100, "LRC timestamps, sorting and offset");
Check(Lyrics.Parse("[ar:Artist]\nplain").Count == 0, "Missing timing");
Check(Lyrics.Parse("[00:01]bonjour\n[la:fr-CA]\n[00:01]salut").Single().Language == "fr-CA",
    "Preserve the LRC language when combining simultaneous lines");
Check(Lyrics.Parse("[00:00]start\n[00:01.234]end")[1].AtMs == 1234, "Millisecond precision");
Check(Lyrics.Parse("[٠٠:00]bad\n[00:01.٢]bad\n[00:02]good").Single().Text == "good",
    "Non-ASCII timestamp digits are ignored");
Check(Lyrics.At(lines, 1199) == -1 && Lyrics.At(lines, 1200) == 0 &&
    Lyrics.At(lines, 3100) == 2 && Lyrics.At(lines, 1900) == 0, "Boundaries and backward seek");
Check(Lyrics.Offset(10000, 100) == 10000 && Lyrics.Offset(-10000, -100) == -10000 &&
    Lyrics.Offset(0, -100) == -100, "Offset step and limits");

var clock = new PlaybackClock();
clock.Anchor(1400, 1, 1000);
Check(clock.Position(1100) == 1500, "Position advances while playing");
clock.Anchor(1500, 0, 1100);
Check(clock.Position(1700) == 1500, "Position stays fixed while paused");
clock.Anchor(600, 1, 1700);
Check(clock.Position(1700) == 600, "Backward seek replaces anchor");
clock.AnchorSample(600, 0, 100, true, 1800);
clock.AnchorSample(600, 1, 60100, true, 61800);
clock.AnchorSample(600, 1, 60200, true, 61900);
Check(clock.Position(61900) == 800, "Repeated timeline sample excludes time spent paused");
clock.AnchorSample(2000, 1, 50, false, 62000);
Check(clock.Position(62000) == 2050, "Fresh playing sample projects reported position");
clock.Clear();
Check(clock.Position(1700) is null, "Missing timeline clears position");

var choice = new LyricSelection();
var old = choice.Begin();
var current = choice.Begin();
Check(!choice.TrySet(old, lines, manual: false), "Late result after track change");
Check(choice.TrySet(current, lines, manual: true), "Manual choice applies");
Check(!choice.TrySet(current, [], manual: false), "Automatic result cannot replace manual choice");
Check(choice.Lines.Count == 3, "Current lyrics remain installed");

var track = new Track("Song", "Artist", "Album", 180);
Check(PlaybackSource.Label("F0DC299D809B9700", "Zen") == "Zen", "Resolve Zen's Windows media ID");
Check(PlaybackSource.IsBrowser("Zen"), "Zen uses browser matching");
Check(PlaybackSource.Label("SpotifyAB.SpotifyMusic_test!Spotify") == "Spotify", "Spotify stays supported");
Check(PlaybackSource.Label("MSEdge") == "Edge" && PlaybackSource.Label("Chrome") == "Chrome",
    "Existing browsers stay supported");
Check(PlaybackSource.Label("ExampleApp", "Example") is null, "Unrelated sessions stay out of automatic selection");
var video = Lyrics.BrowserTrack(new Track("OneRepublic - Counting Stars", "OneRepublicVEVO", "", 257));
Check(video is { Title: "Counting Stars", Artist: "OneRepublic" }, "VEVO channel metadata uses the recording artist");
Check(Lyrics.BrowserTrack(new Track("Other Artist - Song", "Uploader", "", 180)).Title == "Other Artist - Song",
    "Uploader metadata cannot silently replace the artist");
using (var online = new Lrclib(null))
{
    Check(!online.Enabled, "Online lookups are off until configured");
    Check(!online.SetContact("invalid") && !online.Enabled, "Invalid contact cannot enable lookups");
    Check(online.SetContact(" https://example.com/musiclyrics ") && online.Enabled &&
        online.Contact == "https://example.com/musiclyrics", "Valid URL enables online lookups");
    Check(!online.SetContact("https://example.com/\r\nInjected: value") && online.Enabled,
        "Invalid header leaves the previous configuration intact");
    Check(online.SetContact(""), "Clearing the contact is a valid setting");
    Check(!online.Enabled && (await online.SearchAsync("Song", "Artist", "", default)).Count == 0,
        "Disabling online lookups prevents requests");
}
Check(Lyrics.BrowserTitle("Artist - Song (Official Music Video)", "Artist") == "Song",
    "Known YouTube title packaging can be removed");
Check(Lyrics.BrowserTitle("Dominic Fike \"Babydoll\" (Official Audio)", "Dominic Fike") == "Babydoll",
    "Quoted YouTube title is searched without the duplicate artist");
Check(Lyrics.BrowserTitle("Artist “Song” (Live)", "Artist") == "Song (Live)",
    "Quotes can be removed while preserving the recording qualifier");
Check(Lyrics.BrowserTitle("Artist – Song (Official Video) (4K Remaster)", "Artist") ==
    "Song (4K Remaster)", "Remaster qualifier survives title normalization");
Check(Lyrics.BrowserTitle("Other Artist - Song (Live)", "Artist") == "Other Artist - Song (Live)",
    "Mismatched artist prefix and recording qualifier survive");
var exact = new LyricResult(1, "Song", "Artist", "Album", 182, "[00:01]line");
Check(Lyrics.Match(track, [exact with { Title = "Artist - Song (Official Lyric Video)" }],
    browser: true)?.Id == 1, "Browser and provider metadata use the same title normalization");
Check(Lyrics.Match(track, [exact]) == exact, "Two seconds is inclusive");
var handler = new FixtureLyricsHandler();
using (var online = new Lrclib(null, handler))
{
    await online.SearchAsync("Song", "Artist", "Album", default);
    Check(handler.LastUri is null, "No requests are sent before opt-in");
    online.SetContact("https://example.com/musiclyrics");
    var spotify = await online.SearchAsync(track.Title, track.Artist, track.Album, default);
    Check(Lyrics.Match(track, spotify)?.Id == 42 && handler.LastUri?.Query ==
        "?track_name=Song&artist_name=Artist&album_name=Album", "Spotify metadata produces a timed lookup");
    Check(handler.UserAgent == "MusicLyrics/1.0 (https://example.com/musiclyrics)", "Lookup identifies the client");
    var youtube = Lyrics.BrowserTrack(new Track("Artist - Song (Official Video)", "ArtistVEVO", "", 180));
    var browserResults = await online.SearchAsync(youtube.Title, youtube.Artist, youtube.Album, default);
    Check(Lyrics.Match(youtube, browserResults, browser: true)?.Id == 42 && handler.LastUri?.Query ==
        "?track_name=Song&artist_name=Artist", "YouTube metadata produces a timed lookup");
}
Check(Lyrics.Match(track, [exact with { Duration = 182.001 }]) is null, "Over two seconds rejected");
Check(Lyrics.Match(track, [exact, exact with { Id = 2 }]) is null, "Ambiguous timed recordings rejected");
Check(Lyrics.Match(track, [exact with { Title = "Song (Live)" }]) is null, "Recording qualifier preserved");
Check(Lyrics.Match(track, [exact with { Duration = null }]) is null, "Known duration requires comparable result");
Check(Lyrics.Match(track with { Album = "" }, [exact]) == exact, "Album is optional");
Check(!Lyrics.DurationMatches(track with { Duration = 178 }, exact), "Late duration invalidates automatic match");

var fitted = new OverlayRect(-500, 600, 500, 500).Fit(300, 400);
Check(fitted == new OverlayRect(0, 0, 300, 400), "Oversized layout fits");
Check(new OverlayRect(400, 300, 200, 100).Fit(500, 350) ==
    new OverlayRect(300, 250, 200, 100), "Layout clamped");

var uiThread = new Thread(() =>
{
    var settings = new Preferences();
    var applied = 0;
    using var provider = new Lrclib(null);
    using var player = new Player(Dispatcher.CurrentDispatcher, provider, null);
    var window = new SettingsWindow(player, settings, () => applied++, _ => true, () => { }, () => { });
    try
    {
        var combos = Descendants(window).OfType<ComboBox>().ToArray();
        var presets = combos.Single(combo => combo.Items.Contains("Frosted Cinema"));
        presets.SelectedItem = "Frosted Cinema";
        Check(settings.ActivePreset == "Frosted Cinema" && applied == 1, "Preset selection applies immediately");
        var palettes = combos.Single(combo => combo.Items.Contains("Monochrome"));
        palettes.SelectedItem = "Cool";
        Check(settings.Presets.Last().AccentColors.Length == 3 && applied == 2, "Palette selection applies immediately");
        var entrance = combos.Single(combo => combo.SelectedValue is Entrance);
        entrance.SelectedValue = Entrance.WordByWord;
        Check(settings.Presets.Last().Effect == Entrance.WordByWord && applied == 3, "Word entrance applies immediately");
        Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "Reset to default"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(settings.Presets.Last().Effect == Entrance.Cinema && settings.Presets.Last().AccentColors.Length == 0 &&
            applied == 4, "Reset applies the selected preset's defaults immediately");
    }
    finally { window.CloseForExit(); }

    var visual = new LyricVisual();
    var bounds = new Rect(0, 0, 340, 100);
    var cinema = Preset.BuiltIns.Last();
    void Render(Preset preset, bool animate, string text = "The lyric line", int elapsed = 0, double span = 2400, long cue = 0)
    {
        var started = Stopwatch.GetTimestamp() - Stopwatch.Frequency * elapsed / 1000;
        visual.SetLine(text, preset, animate, started, span, cue: cue);
        visual.Measure(bounds.Size);
        visual.Arrange(bounds);
        visual.UpdateLayout();
    }
    byte[] Rasterize()
    {
        var bitmap = new RenderTargetBitmap((int)bounds.Width, (int)bounds.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }
    int BrightPixels()
    {
        var pixels = Rasterize();
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 2] > 180) count++;
        return count;
    }

    var accented = Preset.BuiltIns.First(p => p.Effect == Entrance.Fade) with { AccentColors = ["#FF0000"] };
    Render(accented, false, cue: 1600);
    var accentedLine = Rasterize();
    var pixels = Enumerable.Range(0, accentedLine.Length / 4).Select(i => i * 4);
    Check(pixels.Any(i => accentedLine[i + 2] > 180 && accentedLine[i + 1] < 30) &&
        pixels.Any(i => accentedLine[i + 2] > 180 && accentedLine[i + 1] > 180),
        "One lyric line renders both accent and main color words");
    Render(accented, false, cue: 1600);
    Check(Rasterize().SequenceEqual(accentedLine), "Accented words stay stable when the current cue redraws");
    Check(Enumerable.Range(1601, 20).Any(cue =>
    {
        Render(accented, false, cue: cue);
        return !Rasterize().SequenceEqual(accentedLine);
    }), "Different cues can accent different words");
    Render(accented, false, "Solo");
    var solo = Rasterize();
    Check(pixels.Any(i => solo[i + 2] > 180) &&
        pixels.Where(i => solo[i + 2] > 180).All(i => solo[i + 1] > 180), "Single word cues keep the main color");

    Render(cinema, true);
    if (Native.AnimationsEnabled())
    {
        Check(VisualTreeHelper.GetChildrenCount(visual) == 1 &&
            VisualTreeHelper.GetChild(visual, 0) is Visual text &&
            VisualTreeHelper.GetEffect(text) is BlurEffect { Radius: > 0 },
            "Cinema starts with a text-only blur");
        Render(cinema with { Effect = Entrance.Fade }, true);
        Check(VisualTreeHelper.GetChildrenCount(visual) == 0, "Soft Fade has no text blur");

        var wordByWord = Preset.BuiltIns.First(p => p.Effect == Entrance.Fade) with { Effect = Entrance.WordByWord };
        const string wrapped = "The words wrap onto more than one line";
        bounds = new Rect(0, 0, 240, 170);
        Render(wordByWord, true, wrapped, elapsed: -500);
        var firstWord = BrightPixels();
        Render(wordByWord, false, wrapped);
        var fullLine = BrightPixels();
        Check(firstWord > 0 && firstWord < fullLine, "A wrapped lyric starts with one word and can redraw fully");
        Render(wordByWord, true, wrapped, elapsed: 100, span: 80);
        Check(BrightPixels() == fullLine, "An elapsed short cue renders fully");
        Render(wordByWord, true, "موسيقى جديدة", elapsed: -500);
        Check(BrightPixels() == 0, "Unsupported word segmentation starts with a whole-line fade");
        Render(wordByWord, false, "موسيقى جديدة");
        Check(BrightPixels() > 0, "The fallback preserves the lyric text");
    }
    else Console.WriteLine("Skipped animated WPF checks because Windows animations are disabled.");
    Render(cinema, false);
    Check(VisualTreeHelper.GetChildrenCount(visual) == 0, "Cinema redraws sharp when animation is skipped");
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
uiThread.Join();

Console.WriteLine($"Passed {checks} offline checks.");

static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
{
    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
    {
        yield return child;
        foreach (var descendant in Descendants(child)) yield return descendant;
    }
}

sealed class FixtureLyricsHandler : HttpMessageHandler
{
    public Uri? LastUri { get; private set; }
    public string? UserAgent { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUri = request.RequestUri;
        UserAgent = request.Headers.UserAgent.ToString();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [{"id":42,"trackName":"Song","artistName":"Artist","albumName":"Album",
                  "duration":180,"syncedLyrics":"[00:01.000]Fixture lyric"}]
                """)
        });
    }
}
