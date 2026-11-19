using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace MusicLyrics;

internal sealed class SettingsWindow : Window
{
    private sealed record DialogOwner(nint Handle) : Forms.IWin32Window;
    private sealed record SessionChoice(Session? Session, string Label);
    private sealed record MatchChoice(long Revision, LyricResult Result, string Label);
    private readonly Player player;
    private readonly Preferences preferences;
    private readonly Action save;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock shortcutStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock matchStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock presetStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock presetNotice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock offset = new();
    private readonly ComboBox sessions = new() { DisplayMemberPath = "Label" };
    private readonly ListBox searchResults = new() { DisplayMemberPath = "Label", MaxHeight = 120 };
    private readonly TextBox title = new() { MaxLength = 250 };
    private readonly TextBox artist = new() { MaxLength = 250 };
    private readonly TextBox shortcut = new() { IsReadOnly = true };
    private readonly ComboBox presets = new();
    private readonly ComboBox entrance = new() { DisplayMemberPath = "Label", SelectedValuePath = "Effect" };
    private readonly TextBox name = new() { MaxLength = 50 };
    private readonly TextBlock trackHeading = new() { FontSize = 21, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock trackArtist = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button overlayToggle;
    private readonly Slider font = Slider(12, 100, 1);
    private readonly ComboBox language = new() { DisplayMemberPath = "DisplayName" };
    private readonly ComboBox family = new();
    private readonly ComboBox maxLineColors = new() { ItemsSource = Enumerable.Range(1, Preset.MaxAccentColors + 1) };
    private readonly ComboBox maxHighlightWords = new() { ItemsSource = Enumerable.Range(0, 33) };
    private readonly ComboBox highlightSize = new();
    private readonly Slider outline = Slider(0, 6, .5);
    private readonly Slider tilt = Slider(0, 15, 1);
    private readonly CheckBox scatter = new() { Content = "Scatter short lines inside the overlay" };
    private readonly CheckBox blur = new() { Content = "Blur background behind lyrics" };
    private readonly ComboBox palette = new();
    private readonly WrapPanel swatches = new();
    private Button addColor = null!;
    private readonly Border previewBackdrop = new();
    private Button outlinePicker = null!;
    private readonly Slider background = Slider(0, 1, .05);
    private readonly Slider duration = Slider(80, 250, 1);
    private readonly Slider scale = Slider(.80, 1, .01);
    private readonly Slider stageStackSpeed = Slider(1, 4, .25);
    private readonly LyricVisual preview = new();
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private readonly TabControl tabs = new();
    private readonly TabItem presetTab = new() { Header = "Presets" };
    private readonly Button search;
    private readonly Button attach;
    private readonly Button retry;
    private readonly Button import;
    private readonly Button earlier;
    private readonly Button later;
    private bool updatingSessions;
    private bool loadingPreset;
    private bool searching;
    private long viewedRevision = -1;
    private int sampleIndex;
    private bool closing;
    private Hotkey draftShortcut;
    private Preset draft = Preset.BuiltIns[0];
    private static readonly string[] Samples =
    [
        "Follow the words wherever you go",
        "Keep the music close",
        "A longer line wraps inside the space you choose for your lyrics"
    ];

    public SettingsWindow(Player player, Preferences preferences, Action save, Func<Hotkey, bool> setShortcut,
        Action editPlacement, Action toggleOverlay)
    {
        this.player = player;
        this.preferences = preferences;
        this.save = save;
        draftShortcut = preferences.Shortcut;
        Title = "Verseglow";
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        FontFamily = new FontFamily("Segoe UI Variable Text");
        FontSize = 14;
        Width = Math.Min(1080, SystemParameters.WorkArea.Width);
        Height = Math.Min(740, SystemParameters.WorkArea.Height);
        MinWidth = 460;
        MinHeight = 510;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var playback = new StackPanel { Margin = new Thickness(18) };
        overlayToggle = Button("Hide overlay", toggleOverlay);
        playback.Children.Add(Card(trackHeading, trackArtist, status, notice,
            Row(overlayToggle, Button("Move or resize overlay…", editPlacement))));
        var advanced = new StackPanel { Margin = new Thickness(18) };
        var onlineContact = new TextBox { MaxLength = 250, Text = player.OnlineContact };
        var onlineStatus = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = "LRCLIB receives the contact and track metadata when lookup is enabled. Clear the contact to turn it off." };
        advanced.Children.Add(Field("LRCLIB contact _email or HTTPS project URL", onlineContact));
        advanced.Children.Add(onlineStatus);
        advanced.Children.Add(Button("Apply online lookup settings", () =>
        {
            if (!player.ConfigureOnline(onlineContact.Text))
            {
                onlineStatus.Text = "Enter a valid email or HTTPS project URL.";
                return;
            }
            preferences.OnlineLyricsContact = player.OnlineContact;
            onlineContact.Text = player.OnlineContact;
            searchResults.ItemsSource = null;
            matchStatus.Text = "";
            save();
            onlineStatus.Text = player.CanUseOnline ?
                "Automatic lookup is on. Clear the contact and apply to turn it off." :
                "Automatic lookup is off. LRCLIB receives the contact and track metadata when enabled.";
        }));

        sessions.SelectionChanged += (_, _) =>
        {
            if (!updatingSessions && sessions.SelectedItem is SessionChoice choice) player.Sessions.Select(choice.Session);
        };
        retry = Button("Retry lookup", async () => await player.RetryAsync());
        import = Button("Import LRC…", async () => await Import());

        search = Button("Search timed lyrics", async () => await Search());
        attach = Button("Use selected lyrics", AttachSelected);
        AutomationProperties.SetName(searchResults, "Timed lyric search results");
        searchResults.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(ItemsControl.HasItems))
            { Source = searchResults, Converter = new BooleanToVisibilityConverter() });
        var manualSearch = new StackPanel();
        manualSearch.Children.Add(Field("Track _title", title));
        manualSearch.Children.Add(Field("Track _artist", artist));
        manualSearch.Children.Add(Row(search, attach));
        manualSearch.Children.Add(searchResults);
        playback.Children.Add(Card(Heading("Lyrics"), Row(import, retry),
            new Expander { Header = "Find lyrics by title and artist", Content = manualSearch }, matchStatus));
        searchResults.SelectionChanged += (_, _) => RefreshPlayback();

        earlier = Button("Earlier 100 ms", () => player.Adjust(-100));
        later = Button("Later 100 ms", () => player.Adjust(100));
        playback.Children.Add(Card(Heading("Lyric timing"), offset, Row(earlier, later)));
        advanced.Children.Insert(0, Field("_Playback session", sessions));
        advanced.Children.Add(Heading("Overlay shortcut"));
        shortcut.Text = draftShortcut.ToString();
        shortcut.PreviewKeyDown += CaptureShortcut;
        advanced.Children.Add(Field("Press a new show/hide _shortcut", shortcut));
        advanced.Children.Add(Button("Apply shortcut", () =>
        {
            if (setShortcut(draftShortcut))
            {
                shortcut.Text = preferences.Shortcut.ToString();
                shortcutStatus.Text = "Shortcut saved.";
            }
            else shortcutStatus.Text = $"Shortcut {draftShortcut} is unavailable. Choose another.";
        }));

        advanced.Children.Add(shortcutStatus);

        var editor = new StackPanel { Margin = new Thickness(18, 12, 18, 16) };
        var stage = new DockPanel { Margin = new Thickness(12) };
        var previewToolbar = new DockPanel();
        var replay = Button("Replay preview", RestartPreview);
        DockPanel.SetDock(replay, Dock.Right);
        previewToolbar.Children.Add(replay);
        var previewTitle = Heading("Live preview");
        previewTitle.VerticalAlignment = VerticalAlignment.Center;
        previewToolbar.Children.Add(previewTitle);
        DockPanel.SetDock(previewToolbar, Dock.Top);
        stage.Children.Add(previewToolbar);
        previewBackdrop.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(23, 47, 52), 0), new(Color.FromRgb(44, 79, 81), .52),
            new(Color.FromRgb(26, 52, 59), .53), new(Color.FromRgb(17, 33, 41), 1)
        }, 62);
        stage.Children.Add(new Grid { ClipToBounds = true, Children = { previewBackdrop, preview } });
        editor.Children.Add(Field("_Preset", presets));
        editor.Children.Add(presetStatus);
        editor.Children.Add(presetNotice);

        entrance.ItemsSource = Enum.GetValues<Entrance>().Select(effect => new { Effect = effect, Label = effect switch
        {
            Entrance.Stack => "Stage Stack", Entrance.Tilt => "Violet Tilt", Entrance.Fill => "Karaoke Fill",
            Entrance.Ripple => "Word Ripple", Entrance.WordByWord => "Word by word", Entrance.Cinema => "Frosted Cinema",
            _ => effect.ToString()
        } }).ToArray();
        palette.ItemsSource = Preset.Palettes.Select(p => p.Name).Prepend("Custom").Append("Random per song").ToArray();
        addColor = Button("Add color…", () => PickPaletteColor(draft.AccentColors.Length + 1));
        var durationField = Field("Entrance _duration (ms)", duration);
        var scaleField = Field("Pop start _scale", scale);
        var stackSpeedField = Field("Stage Stack _speed (1×–4×)", stageStackSpeed);
        stageStackSpeed.AutoToolTipPrecision = 2;
        var languages = LanguageTypography.Languages.Values.OrderBy(c => c.DisplayName)
            .Select(c => new { c.Name, c.DisplayName }).Prepend(new { Name = "", DisplayName = "Preset default (all languages)" }).ToArray();
        language.ItemsSource = languages;
        language.SelectedValuePath = "Name";
        language.SelectedIndex = 0;
        family.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().Order().Prepend("Inherit font").ToArray();
        language.SelectionChanged += (_, _) => { SyncFamily(); RestartPreview(); };
        family.SelectionChanged += (_, _) => ChangeFamily();
        outlinePicker = Button("Outline color…", PickOutlineColor);
        editor.Children.Add(InspectorSection("Motion", true, Field("_Entrance", entrance),
            durationField, scaleField, stackSpeedField, Field("Maximum tilt (degrees)", tilt), scatter));
        editor.Children.Add(InspectorSection("Typography", false, Field("_Font size", font),
            Field("Font for language", language), Field("Font family", family),
            Field("Highlight size (% of base font)", highlightSize), Field("Outline width", outline)));
        editor.Children.Add(InspectorSection("Color and emphasis", false, Field("Color _palette", palette), swatches, addColor,
            Field("Maximum text colors per line (includes main color)", maxLineColors),
            Field("Maximum highlighted words per line", maxHighlightWords), outlinePicker,
            Field("_Background opacity", background), blur));
        editor.Children.Add(InspectorSection("Manage preset", false, Button("Reset to default", ResetPreset), Field("Custom preset _name", name),
            Row(Button("Duplicate as custom", DuplicatePreset), Button("Rename", RenamePreset), Button("Delete", DeletePreset))));
        durationField.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(IsEnabled))
            { Source = duration, Converter = new BooleanToVisibilityConverter() });
        scaleField.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(IsEnabled))
            { Source = scale, Converter = new BooleanToVisibilityConverter() });
        stackSpeedField.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(IsEnabled))
            { Source = stageStackSpeed, Converter = new BooleanToVisibilityConverter() });
        presets.SelectionChanged += (_, _) =>
        {
            if (loadingPreset || presets.SelectedItem is not string selected) return;
            LoadDraft(preferences.Presets.First(p => p.Name == selected));
            preferences.ActivePreset = selected;
            save();
        };
        foreach (var control in new[] { maxLineColors, maxHighlightWords, highlightSize })
            control.SelectionChanged += (_, _) => DraftChanged();
        entrance.SelectionChanged += (_, _) => DraftChanged();
        palette.SelectionChanged += (_, _) =>
        {
                        if (loadingPreset || palette.SelectedIndex < 0) return;
            if (palette.SelectedIndex == Preset.Palettes.Length + 1)
                CommitPreset(draft with { RandomPalettePerSong = true });
            else if (palette.SelectedIndex == 0)
                CommitPreset(draft with { RandomPalettePerSong = false });
            else
            {
                var colors = Preset.Palettes[palette.SelectedIndex - 1].Colors;
                CommitPreset(draft with { Color = colors[0], AccentColors = colors[1..], RandomPalettePerSong = false });
            }
UpdatePalette();
        };
        foreach (var control in new[] { font, background, duration, scale, outline, tilt, stageStackSpeed })
        {
            control.ValueChanged += (_, _) => DraftChanged();
            control.AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft;
        }

        scatter.Click += (_, _) => DraftChanged();
        blur.Click += (_, _) => DraftChanged();
        previewTimer.Tick += (_, _) => PlaySample(advance: true);
        var presetPage = new DockPanel();
        presetPage.Children.Add(stage);
        presetPage.Children.Add(Scroll(editor));
        presetPage.SizeChanged += (_, args) =>
        {
            var wide = args.NewSize.Width >= 730;
            DockPanel.SetDock(stage, wide ? Dock.Left : Dock.Top);
            stage.Width = wide ? args.NewSize.Width - 344 : double.NaN;
            stage.Height = wide ? double.NaN : Math.Clamp(args.NewSize.Height * .37, 130, 230);
        };
        presetTab.Content = presetPage;
        tabs.Items.Add(presetTab);
        tabs.SelectedItem = presetTab;
        tabs.Items.Add(new TabItem { Header = "Now playing", Content = Scroll(playback) });
        tabs.Items.Add(new TabItem { Header = "Settings", Content = Scroll(advanced) });
        tabs.SelectionChanged += (sender, args) =>
        {
            if (ReferenceEquals(args.Source, tabs)) UpdatePreview();
        };
        tabs.Margin = new Thickness(12);
        Content = tabs;
        IsVisibleChanged += (_, _) => UpdatePreview();
        StateChanged += (_, _) => UpdatePreview();
        player.Changed += RefreshPlayback;
        player.Sessions.SessionsChanged += RefreshSessions;
        RefreshSessions();
        RefreshPresets(preferences.ActivePreset);
        RefreshPlayback();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!closing && !Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void CloseForExit()
    {
        closing = true;
        previewTimer.Stop();
        preview.Finish();
        player.Changed -= RefreshPlayback;
        player.Sessions.SessionsChanged -= RefreshSessions;
        Close();
    }

    public void UpdateNotice(string? message) => presetNotice.Text = notice.Text = message ?? "";
    public void RefreshMotion() => preview.RefreshMotion();

    private void RefreshSessions()
    {
        updatingSessions = true;
        var choices = new[] { new SessionChoice(null, "Automatic") }.Concat(player.Sessions.Available.Select((session, i) =>
            new SessionChoice(session, $"{player.Sessions.Label(session)} · Session {i + 1}"))).ToArray();
        sessions.ItemsSource = choices;
        sessions.SelectedItem = choices.FirstOrDefault(choice => Equals(choice.Session, player.Sessions.Manual)) ?? choices[0];
        updatingSessions = false;
    }

    private void RefreshPlayback()
    {
        trackHeading.Text = player.Track?.Title ?? "No track playing";
        trackArtist.Text = player.Track?.Artist ?? "";
        status.Text = player.Message;

        overlayToggle.Content = player.Visible ? "Hide overlay" : "Show overlay";
        if (viewedRevision != player.Revision)
        {
            viewedRevision = player.Revision;
            title.Text = player.SearchTrack?.Title ?? "";
            artist.Text = player.SearchTrack?.Artist ?? "";
            searchResults.ItemsSource = null;
            matchStatus.Text = "";
        }
        search.IsEnabled = player.CanMatch && player.CanUseOnline && !searching;
        attach.IsEnabled = player.CanMatch && searchResults.SelectedItem is MatchChoice;
        retry.IsEnabled = player.CanMatch && player.CanUseOnline && !player.HasManualLyrics;
        import.IsEnabled = player.CanMatch;
        earlier.IsEnabled = player.CanMatch && player.Offset > -10000;
        later.IsEnabled = player.CanMatch && player.Offset < 10000;
        offset.Text = player.Offset == 0 ? "No timing adjustment" :
            $"{Math.Abs(player.Offset)} ms {(player.Offset < 0 ? "earlier" : "later")}";
    }

    private async Task Search()
    {
        var queryTitle = title.Text.Trim();
        var queryArtist = artist.Text.Trim();
        if (queryTitle.Length == 0 || queryArtist.Length == 0)
        {
            matchStatus.Text = "Enter both a track title and an artist.";
            return;
        }
        var revision = player.Revision;
        searching = true;
        searchResults.ItemsSource = null;
        matchStatus.Text = "Searching…";
        RefreshPlayback();
        try
        {
            var results = await player.SearchAsync(queryTitle, queryArtist);
            if (revision != player.Revision || closing) return;
            var matches = results.Where(result => result.TimedLyrics is { } text && Lyrics.Parse(text).Count > 0)
                .Select(result => new MatchChoice(revision, result,
                    $"{result.Title} — {result.Artist}   {result.Album}   {result.Duration:0.#} s")).ToArray();
            searchResults.ItemsSource = matches;
            matchStatus.Text = matches.Length == 0 ? "No timed results. Try different terms or import an LRC." :
                "Select the result for this recording.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or
            OperationCanceledException or InvalidOperationException)
        {
            if (revision == player.Revision && !closing)
                matchStatus.Text = ex is InvalidOperationException ? ex.Message : "Search failed. Try again or import an LRC.";
        }
        finally
        {
            searching = false;
            if (!closing) RefreshPlayback();
        }
    }

    private void AttachSelected()
    {
        if (searchResults.SelectedItem is MatchChoice choice && choice.Result.TimedLyrics is { } text &&
            player.Attach(choice.Revision, Lyrics.Parse(text), manual: true))
            matchStatus.Text = "Selected lyrics attached.";
    }

    private async Task Import()
    {
        var revision = player.Revision;
        var dialog = new OpenFileDialog { Filter = "Timed lyrics (*.lrc)|*.lrc", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (await player.ImportAsync(dialog.FileName, revision)) matchStatus.Text = "Local lyrics attached.";
            else if (!closing) matchStatus.Text = "Playback changed before the file could be attached.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        {
            matchStatus.Text = ex is InvalidDataException ? ex.Message : "Could not read the LRC file.";
        }
    }

    private void CaptureShortcut(object sender, KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key is Key.Tab or Key.Escape) return;
        args.Handled = true;
        var modifiers = (uint)Keyboard.Modifiers;
        var candidate = new Hotkey(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!candidate.IsValid) return;
        draftShortcut = candidate;
        shortcut.Text = candidate.ToString();
    }

    private void RefreshPresets(string selected)
    {
        var choices = preferences.Presets.ToArray();
        var preset = choices.FirstOrDefault(p => p.Name == selected) ?? choices[0];
        loadingPreset = true;
        presets.ItemsSource = choices.Select(p => p.Name).ToArray();
        presets.SelectedItem = preset.Name;
        LoadDraft(preset);
    }

    private void LoadDraft(Preset preset)
    {
        loadingPreset = true;
        draft = preset;
        entrance.SelectedValue = preset.Effect;
        SyncFamily();
        outline.Value = preset.Outline;
        tilt.Value = preset.Tilt;
        scatter.IsChecked = preset.Scatter;
        blur.IsChecked = preset.Blur;
        font.Value = preset.FontSize;
        maxLineColors.SelectedItem = preset.MaxLineColors;
        maxHighlightWords.SelectedItem = preset.MaxHighlightWords;
        var percentages = Enumerable.Range(10, 31).Select(step => step * 5);
        if (preset.HighlightPercent is int percent) percentages = percentages.Append(percent).Distinct().Order();
        highlightSize.ItemsSource = percentages.Cast<object>().Prepend("Preset default").ToArray();
        highlightSize.SelectedItem = (object?)preset.HighlightPercent ?? "Preset default";
        UpdatePalette();
        UpdateOutlineSwatch();
        background.Value = preset.Background;
        duration.Value = preset.Duration;
        scale.Value = preset.Scale;
        stageStackSpeed.Value = preset.StageStackSpeed;
        UpdateEffectFields();
        name.Text = preferences.Custom.Contains(preset) ? preset.Name : "";
        presetStatus.Text = "";
        loadingPreset = false;
        sampleIndex = 0;
        RestartPreview();
    }

    private void UpdateEffectFields()
    {
        duration.IsEnabled = draft.Effect is Entrance.Pop or Entrance.Fade or Entrance.Ripple;
        scale.IsEnabled = draft.Effect == Entrance.Pop;
        stageStackSpeed.IsEnabled = draft.Effect == Entrance.Stack;
    }

    private void SyncFamily()
    {
        var loading = loadingPreset;
        loadingPreset = true;
        var tag = language.SelectedValue as string ?? "";
        family.SelectedItem = (tag.Length == 0 ? draft.Font : draft.LanguageFonts.GetValueOrDefault(tag)) ?? "Inherit font";
        loadingPreset = loading;
    }

    private void ChangeFamily()
    {
        if (loadingPreset || family.SelectedItem is not string value) return;
        var tag = language.SelectedValue as string ?? "";
        if (tag.Length == 0) CommitPreset(draft with { Font = family.SelectedIndex == 0 ? null : value });
        else
        {
            var fonts = new Dictionary<string, string>(draft.LanguageFonts);
            if (family.SelectedIndex == 0) fonts.Remove(tag);
            else fonts[tag] = value;
            CommitPreset(draft with { LanguageFonts = fonts });
        }
    }

    private string? PickColor(string initial)
    {
        using var dialog = new Forms.ColorDialog
        {
            Color = System.Drawing.ColorTranslator.FromHtml(initial),
            FullOpen = true
        };
        var owner = new DialogOwner(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) != Forms.DialogResult.OK) return null;

        var selected = dialog.Color;
        return $"#{selected.R:X2}{selected.G:X2}{selected.B:X2}";
    }

    private void PickOutlineColor()
    {
        if (PickColor(draft.OutlineColor) is not { } color) return;
        CommitPreset(draft with { OutlineColor = color });
        UpdateOutlineSwatch();
    }

    private void UpdateOutlineSwatch() => SetSwatch(outlinePicker, "Outline color…", draft.OutlineColor);

    private void PickPaletteColor(int index)
    {
        var colors = draft.AccentColors.ToList();
        var initial = index > 0 && index <= colors.Count ? colors[index - 1] : draft.Color;
        if (PickColor(initial) is not { } color) return;
        if (index == 0) CommitPreset(draft with { Color = color });
        else
        {
            if (index > colors.Count) colors.Add(color);
            else colors[index - 1] = color;
            CommitPreset(draft with { AccentColors = colors.ToArray() });
        }
        UpdatePalette();
        if (swatches.Children[index] is Panel row) row.Children[0].Focus();
    }

    private void RemovePaletteColor(int index)
    {
        var colors = draft.AccentColors.ToList();
        colors.RemoveAt(index - 1);
        CommitPreset(draft with { AccentColors = colors.ToArray() });
        UpdatePalette();
        if (swatches.Children[Math.Min(index, colors.Count)] is Panel row) row.Children[0].Focus();
    }

    private void UpdatePalette()
    {
        var colors = draft.AccentColors.Prepend(draft.Color).ToArray();
        var loading = loadingPreset;
        loadingPreset = true;
        palette.SelectedIndex = draft.RandomPalettePerSong ? Preset.Palettes.Length + 1 : Array.FindIndex(Preset.Palettes, p => p.Colors.SequenceEqual(colors, StringComparer.OrdinalIgnoreCase)) + 1;
        loadingPreset = loading;
        swatches.Visibility = addColor.Visibility = draft.RandomPalettePerSong ? Visibility.Collapsed : Visibility.Visible;
        swatches.Children.Clear();
        for (var i = 0; i < colors.Length; i++)
        {
            var index = i;
            var label = i == 0 ? "Main color" : $"Color {i + 1}";
            var pick = Button(label, () => PickPaletteColor(index));
            SetSwatch(pick, label, colors[i]);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { pick } };
            if (i > 0)
            {
                var remove = Button("×", () => RemovePaletteColor(index));
                remove.ToolTip = $"Remove {label.ToLowerInvariant()}";
                AutomationProperties.SetName(remove, (string)remove.ToolTip);
                row.Children.Add(remove);
            }
            swatches.Children.Add(row);
        }
        addColor.IsEnabled = draft.AccentColors.Length < Preset.MaxAccentColors;
    }

    private static void SetSwatch(Button button, string label, string color)
    {
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            new Border { Width = 22, Height = 16, CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) },
            new TextBlock { Text = label }
        } };
        AutomationProperties.SetName(button, $"Choose {label.TrimEnd('…').ToLowerInvariant()}");
    }

    private void RestartPreview()
    {
        previewTimer.Stop();
        UpdatePreview();
    }

    private void DraftChanged()
    {
        if (loadingPreset || entrance.SelectedValue is not Entrance effect) return;
        CommitPreset(draft with
        {
            MaxLineColors = (int)maxLineColors.SelectedItem, MaxHighlightWords = (int)maxHighlightWords.SelectedItem,
            HighlightPercent = highlightSize.SelectedItem as int?,
            Effect = effect, Outline = outline.Value, Tilt = tilt.Value, Scatter = scatter.IsChecked == true,
            Blur = blur.IsChecked == true, FontSize = font.Value, Background = background.Value,
            Duration = (int)duration.Value, Scale = scale.Value, StageStackSpeed = stageStackSpeed.Value
        });
        UpdateEffectFields();
    }

    private void CommitPreset(Preset updated)
    {
        if (draft == updated || !updated.IsValid) return;
        draft = updated;
        preferences.UpdatePreset(updated);
        presetStatus.Text = "";
        save();
        RestartPreview();
    }

    private void ResetPreset()
    {
        preferences.ResetPreset(draft);
        save();
        LoadDraft(preferences.Presets.First(p => p.Name == draft.Name));
    }

    private void DuplicatePreset()
    {
        var text = name.Text.Trim();
        if (!NameAvailable(text, null))
        {
            presetStatus.Text = "Enter a unique name for the new preset.";
            return;
        }
        preferences.Custom.Add(draft.Duplicate(text));
        preferences.ActivePreset = text;
        save();
        RefreshPresets(text);
    }

    private void RenamePreset()
    {
        var selected = draft;
        if (!preferences.Custom.Contains(selected))
        {
            presetStatus.Text = "Select a custom preset to rename.";
            return;
        }
        var text = name.Text.Trim();
        if (!NameAvailable(text, selected))
        {
            presetStatus.Text = "Enter a unique preset name.";
            return;
        }
        preferences.Custom[preferences.Custom.IndexOf(selected)] = selected with { Name = text };
        if (preferences.ActivePreset == selected.Name) preferences.ActivePreset = text;
        save();
        RefreshPresets(text);
    }

    private bool NameAvailable(string text, Preset? except) => text.Length > 0 &&
        !preferences.Presets.Any(preset => !ReferenceEquals(preset, except) &&
            string.Equals(preset.Name, text, StringComparison.OrdinalIgnoreCase));

    private void DeletePreset()
    {
        if (!preferences.Custom.Remove(draft))
        {
            presetStatus.Text = "Select a custom preset to delete.";
            return;
        }
        preferences.ActivePreset = Preset.BuiltIns[0].Name;
        save();
        RefreshPresets(preferences.ActivePreset);
    }

    private void UpdatePreview()
    {
        if (IsVisible && WindowState != WindowState.Minimized && ReferenceEquals(tabs.SelectedItem, presetTab))
        {
            if (previewTimer.IsEnabled) return;
            PlaySample(advance: false);
            previewTimer.Start();
        }
        else
        {
            previewTimer.Stop();
            preview.Finish();
        }
    }

    private void PlaySample(bool advance)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !ReferenceEquals(tabs.SelectedItem, presetTab)) return;
        if (advance) sampleIndex = (sampleIndex + 1) % Samples.Length;
        var tag = language.SelectedValue as string ?? "";
        var sample = tag.Length > 0 ? LanguageTypography.Languages[tag].NativeName + "  ·  " + Samples[sampleIndex] : Samples[sampleIndex];
        previewBackdrop.Effect = draft.Blur ? new System.Windows.Media.Effects.BlurEffect { Radius = 14 } : null;
        preview.SetLine(sample, draft.ForSong(player.Track), true, Stopwatch.GetTimestamp(), language: tag, cue: sampleIndex);
    }

    private static Slider Slider(double min, double max, double step) => new()
    {
        Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true
    };

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => action();
        return button;
    }

    private static FrameworkElement Field(string text, Control control)
    {
        var field = new StackPanel { Margin = new Thickness(0, 5, 0, 4) };
        var label = new Label { Content = text, Target = control, Padding = new Thickness(0, 0, 0, 3) };
        AutomationProperties.SetLabeledBy(control, label);
        field.Children.Add(label);
        field.Children.Add(control);
        return field;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6)
    };

    private static Expander InspectorSection(string title, bool open, params UIElement[] controls)
    {
        var fields = new StackPanel { Margin = new Thickness(4, 0, 4, 8) };
        foreach (var control in controls) fields.Children.Add(control);
        return new Expander { Header = title, IsExpanded = open, Content = fields, Margin = new Thickness(0, 5, 0, 0) };
    }

    private static Border Card(params UIElement[] children)
    {
        var content = new StackPanel();
        foreach (var child in children) content.Children.Add(child);
        var card = new Border
        {
            Child = content, Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10)
        };
        card.SetResourceReference(Border.BorderBrushProperty, SystemColors.GrayTextBrushKey);
        return card;
    }

    private static Panel Row(params UIElement[] children)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 5, 0, 5) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private static ScrollViewer Scroll(UIElement element) => new()
    {
        Content = element, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
}
