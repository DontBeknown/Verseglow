using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media.Control;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace MusicLyrics;

internal sealed class Player : IDisposable
{
    private readonly DispatcherTimer boundary = new(DispatcherPriority.Normal);
    private readonly PlaybackClock clock = new();
    private readonly LyricSelection selection = new();
    private readonly Lrclib provider;
    private readonly StreamWriter? probe;
    private CancellationTokenSource lookup = new();
    private Status? state;
    private DateTimeOffset sourceUpdated;
    private double sourcePosition;
    private double? timelineEnd;
    private int metadataRead;
    private bool metadataPending;
    private bool disposed;
    private int index = -1;
    private string lookupStatus = "";
    private long? requestedRevision;
    private long? pendingLookup;
    private LyricResult? automaticMatch;
    private bool lastVisible;
    private string? sessionError;
    public MediaSessions Sessions { get; }
    public Track? Track { get; private set; }
    private bool IsBrowser => Sessions.Selected is { } session && PlaybackSource.IsBrowser(Sessions.Label(session));
    public Track? SearchTrack => Track is { } track && IsBrowser ? Lyrics.BrowserTrack(track) : Track;
    public int Offset { get; private set; }
    public bool Visible { get; set; } = true;
    public bool HasTimeline => clock.Position(Now()) is not null;
    public bool CanUseOnline => provider.Enabled;
    public string OnlineContact => provider.Contact;
    public bool HasManualLyrics => selection.IsManual;
    public bool CanMatch => Track is not null &&
        state is not (Status.Stopped or Status.Closed);
    public long Revision => selection.Revision;
    public string Message => sessionError ??
        (Sessions.Selected is null ? "No supported playback session. Play a track in Spotify or a YouTube video in Zen, Chrome, or Edge." :
        !CanMatch ? "Waiting for a track." :
        !HasTimeline ? "No usable playback timeline. Try another session." :
        probe is not null ? "Playback probe is recording." :
        selection.Lines.Count > 0 ? Visible ? "Lyrics ready." : "Overlay hidden." :
        !CanUseOnline ? "Automatic lyrics lookup is off. Turn it on in Settings or import an LRC." :
        string.IsNullOrEmpty(lookupStatus) ? "No timed lyrics. Search or import a local LRC." : lookupStatus);
    public string? CurrentLanguage => index >= 0 && index < selection.Lines.Count ? selection.Lines[index].Language : null;
    public long CurrentCue => index >= 0 && index < selection.Lines.Count ? selection.Lines[index].AtMs : 0;
    public event Action<string, bool, long, double>? LineChanged;
    public event Action? Changed;

    public Player(Dispatcher dispatcher, Lrclib provider, StreamWriter? probe)
    {
        this.provider = provider;
        this.probe = probe;
        Sessions = new MediaSessions(dispatcher);
        Sessions.SelectionChanged += SelectionChanged;
        Sessions.MediaChanged += () => _ = ReadMetadata();
        Sessions.StateChanged += () => ReadTimeline("state");
        Sessions.TimelineChanged += () => ReadTimeline("timeline");
        boundary.Tick += (_, _) => Render(allowAnimation: true);
    }

    public async Task StartAsync()
    {
        try { await Sessions.StartAsync(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            sessionError = "Windows media sessions are unavailable. Restart Verseglow to try again.";
            Log("session-error", detail: ex.Message);
        }
        if (probe is not null) Render(allowAnimation: false);
        Changed?.Invoke();
    }

    private void SelectionChanged()
    {
        metadataRead++;
        metadataPending = false;
        state = null;
        sourceUpdated = default;
        sourcePosition = 0;
        timelineEnd = null;
        clock.Clear();
        ResetTrack(null);
        ReadTimeline("session");
        _ = ReadMetadata();
    }

    private async Task ReadMetadata()
    {
        if (Sessions.Selected is not { } session) return;
        var read = ++metadataRead;
        metadataPending = true;
        Render(allowAnimation: false);
        GlobalSystemMediaTransportControlsSessionMediaProperties? media = null;
        try { media = await session.TryGetMediaPropertiesAsync(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Log("media-error", detail: ex.Message);
        }
        if (disposed || read != metadataRead) return;
        metadataPending = false;
        var title = media?.Title?.Trim() ?? "";
        var artist = media?.Artist?.Trim() ?? "";
        var album = media?.AlbumTitle?.Trim() ?? "";
        if (Track is null || Track.Title != title || Track.Artist != artist || Track.Album != album)
            ResetTrack(title.Length > 0 ? new Track(title, artist, album, null) : null);
        ReadTimeline("media");
    }

    private void ResetTrack(Track? track)
    {
        lookup.Cancel();
        lookup.Dispose();
        lookup = new();
        Track = track;
        selection.Begin();
        pendingLookup = null;
        requestedRevision = null;
        automaticMatch = null;
        lookupStatus = "";
        Offset = 0;
        index = -1;
        lastVisible = false;
        LineChanged?.Invoke("", false, Stopwatch.GetTimestamp(), 0);
        Changed?.Invoke();
    }

    private void ReadTimeline(string reason)
    {
        if (Sessions.Selected is not { } session)
        {
            Log(reason);
            Changed?.Invoke();
            return;
        }
        var previousState = state;
        var now = Now();
        var predicted = clock.Position(now);
        try
        {
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            state = info?.PlaybackStatus;
            if (state is Status.Stopped or Status.Closed)
            {
                clock.Clear();
                if (previousState != state && Track is not null) ResetTrack(Track);
            }
            else if (timeline is null || timeline.LastUpdatedTime <= DateTimeOffset.UnixEpoch ||
                timeline.EndTime <= timeline.StartTime ||
                timeline.Position < timeline.StartTime || timeline.Position > timeline.EndTime ||
                (state is not (Status.Playing or Status.Paused)))
                clock.Clear();
            else
            {
                var rate = state == Status.Playing ? info!.PlaybackRate ?? 1 : 0;
                if (!double.IsFinite(rate) || rate <= 0) rate = state == Status.Playing ? 1 : 0;
                var position = (timeline.Position - timeline.StartTime).TotalMilliseconds;
                timelineEnd = (timeline.EndTime - timeline.StartTime).TotalMilliseconds;
                var age = Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalMilliseconds);
                var sameSample = sourceUpdated == timeline.LastUpdatedTime && sourcePosition == position;
                sourceUpdated = timeline.LastUpdatedTime;
                sourcePosition = position;
                clock.AnchorSample(position, rate, age, sameSample, now);
                if (clock.Position(now) > timelineEnd) clock.Anchor(timelineEnd.Value, rate, now);
                if (Track is { } track)
                {
                    Track = track with { Duration = timelineEnd.Value / 1000 };
                    if (automaticMatch is { } match && !Lyrics.DurationMatches(Track, match))
                    {
                        automaticMatch = null;
                        selection.ClearAutomatic();
                        requestedRevision = null;
                    }
                    else if (track.Duration is { } oldDuration && selection.Lines.Count == 0 &&
                        Math.Abs(oldDuration - Track.Duration!.Value) > 2)
                        requestedRevision = null;
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            clock.Clear();
            Log("timeline-error", detail: ex.Message);
        }
        var actual = clock.Position(now);
        var continuous = previousState == Status.Playing && state == Status.Playing && predicted.HasValue &&
            actual.HasValue && Math.Abs(predicted.Value - actual.Value) < 250 && reason != "media";
        Log(reason, actual);
        Render(continuous);
        Changed?.Invoke();
        if (probe is null && provider.Enabled && !metadataPending && HasTimeline && CanMatch &&
            selection.Lines.Count == 0 && requestedRevision != Revision)
            _ = RetryAsync();
    }

    public async Task RetryAsync()
    {
        if (!CanMatch || !provider.Enabled || SearchTrack is not { } track || selection.IsManual) return;
        var revision = Revision;
        if (pendingLookup == revision) return;
        requestedRevision = revision;
        var browserTrack = IsBrowser ? Track : null;
        lookupStatus = "Looking up timed lyrics…";
        pendingLookup = revision;
        var cancellation = lookup.Token;
        Changed?.Invoke();
        try
        {
            IReadOnlyList<LyricResult> results = [];
            if (!string.IsNullOrWhiteSpace(track.Artist))
                results = await provider.SearchAsync(track.Title, track.Artist, track.Album, cancellation);
            if (disposed || cancellation.IsCancellationRequested || revision != Revision || selection.IsManual) return;
            var exact = Lyrics.Match(SearchTrack ?? track, results, browserTrack is not null);
            var match = exact;
            if (match is null)
            {
                var fallback = browserTrack is null ? track : Lyrics.BrowserFallback(browserTrack);
                if (!string.IsNullOrWhiteSpace(fallback.Artist) &&
                    (track.Album.Length > 0 || fallback.Title != track.Title || fallback.Artist != track.Artist))
                {
                    var extracted = await provider.SearchAsync(fallback.Title, fallback.Artist, "", cancellation);
                    if (disposed || cancellation.IsCancellationRequested || revision != Revision || selection.IsManual) return;
                    results = results.Concat(extracted).ToArray();
                }
                match = Lyrics.BestMatch(fallback with { Duration = Track?.Duration }, results);
                if (match is null)
                {
                    var broader = await provider.SearchByTitleAsync(fallback.Title, cancellation);
                    if (disposed || cancellation.IsCancellationRequested || revision != Revision || selection.IsManual) return;
                    match = Lyrics.BestMatch(fallback with { Duration = Track?.Duration }, broader);
                }
            }
            if (match?.TimedLyrics is { } lrc)
            {
                if (Attach(revision, Lyrics.Parse(lrc), manual: false)) automaticMatch = exact;
            }
            else
            {
                lookupStatus = "Online search found no usable timed lyrics. Search manually or import a local LRC.";
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (disposed || cancellation.IsCancellationRequested || revision != Revision) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidDataException or
            InvalidOperationException or OperationCanceledException)
        {
            if (disposed || cancellation.IsCancellationRequested || revision != Revision || selection.IsManual) return;
            lookupStatus = ex is InvalidOperationException ? ex.Message :
                "Online lookup failed. Retry, search manually, or import a local LRC.";
            Changed?.Invoke();
        }
        finally
        {
            if (!cancellation.IsCancellationRequested && pendingLookup == revision) pendingLookup = null;
        }
    }

    public bool ConfigureOnline(string contact)
    {
        var previous = provider.Contact;
        if (!provider.SetContact(contact)) return false;
        if (provider.Contact == previous) return true;
        lookup.Cancel();
        lookup.Dispose();
        lookup = new();
        pendingLookup = null;
        requestedRevision = null;
        lookupStatus = "";
        automaticMatch = null;
        selection.ClearAutomatic();
        Refresh();
        if (probe is null && HasTimeline && !metadataPending) _ = RetryAsync();
        return true;
    }

    public Task<IReadOnlyList<LyricResult>> SearchAsync(string title, string artist) =>
        provider.SearchAsync(title, artist, "", lookup.Token);

    public bool Attach(long revision, IReadOnlyList<LyricLine> lines, bool manual)
    {
        if (!CanMatch || !selection.TrySet(revision, lines, manual)) return false;
        if (manual) automaticMatch = null;
        index = -1;
        Render(allowAnimation: false);
        Changed?.Invoke();
        return true;
    }

    public async Task<bool> ImportAsync(string path, long revision)
    {
        if (!CanMatch || revision != Revision) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        if (file.Length > 1024 * 1024) throw new InvalidDataException("Choose an LRC file under 1 MB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        var lines = Lyrics.Parse(text);
        if (lines.Count == 0) throw new InvalidDataException("The file contains no usable timed lyrics.");
        return !disposed && Attach(revision, lines, manual: true);
    }

    public void Adjust(int delta)
    {
        if (!CanMatch) return;
        Offset = Lyrics.Offset(Offset, delta);
        Render(allowAnimation: false);
        Changed?.Invoke();
    }

    public void Refresh()
    {
        Render(allowAnimation: false);
        Changed?.Invoke();
    }

    private void Render(bool allowAnimation)
    {
        boundary.Stop();
        var tick = Stopwatch.GetTimestamp();
        if (probe is not null && Visible)
        {
            var sample = clock.Position(Now(tick));
            var positionMs = sample is null ? -1 : Math.Min(sample.Value, timelineEnd ?? sample.Value);
            var second = positionMs >= 0 ? (int)(positionMs / 1000) : -1;
            if (index != second || !lastVisible || !allowAnimation)
            {
                index = second;
                lastVisible = true;
                var text = second >= 0 ? $"Verseglow playback probe   {positionMs / 1000:0.000} s" :
                    "Verseglow overlay probe";
                LineChanged?.Invoke(text, false, tick, 0);
            }
            if (clock.Rate > 0 && sample is not null && positionMs < timelineEnd)
                Schedule(((second + 1) * 1000 - positionMs) / clock.Rate);
            return;
        }
        if (!Visible || !CanMatch || metadataPending || clock.Position(Now(tick)) is not { } position)
        {
            if (lastVisible || index != -1) LineChanged?.Invoke("", false, tick, 0);
            lastVisible = false;
            index = -1;
            return;
        }
        var lines = selection.Lines;
        var adjusted = Math.Min(position, timelineEnd ?? position) - Offset;
        var nextIndex = Lyrics.At(lines, adjusted);
        if (nextIndex != index || !lastVisible || !allowAnimation)
        {
            index = nextIndex;
            lastVisible = true;
            var age = index >= 0 ? Math.Max(0, adjusted - lines[index].AtMs) / Math.Max(clock.Rate, 1e-9) : 0;
            var span = index >= 0 && clock.Rate > 0
                ? Math.Clamp(((index + 1 < lines.Count ? lines[index + 1].AtMs :
                    timelineEnd - Offset ?? lines[index].AtMs + 2400) - lines[index].AtMs) / clock.Rate, 1, 30000)
                : 0;
            var start = tick - (long)(Math.Min(age, 30000) * Stopwatch.Frequency / 1000);
            LineChanged?.Invoke(index >= 0 ? lines[index].Text : "", allowAnimation && clock.Rate > 0, start, span);
        }
        if (clock.Rate > 0 && index + 1 < lines.Count &&
            (timelineEnd is null || lines[index + 1].AtMs + Offset <= timelineEnd.Value))
            Schedule((lines[index + 1].AtMs - adjusted) / clock.Rate);
    }

    private void Schedule(double delay)
    {
        boundary.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling(delay), 1, int.MaxValue));
        boundary.Start();
    }

    private void Log(string reason, double? position = null, string? detail = null)
    {
        probe?.WriteLine(JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow, reason, source = Sessions.Selected?.SourceAppUserModelId,
            state = state?.ToString(), title = Track?.Title, artist = Track?.Artist, position,
            sourcePosition, sourceUpdated, rate = clock.Rate, timelineEnd, detail
        }));
    }

    private static double Now(long? tick = null) => (tick ?? Stopwatch.GetTimestamp()) * (1000d / Stopwatch.Frequency);

    public void Dispose()
    {
        disposed = true;
        boundary.Stop();
        Sessions.Dispose();
        lookup.Cancel();
        lookup.Dispose();
        probe?.Dispose();
    }
}
