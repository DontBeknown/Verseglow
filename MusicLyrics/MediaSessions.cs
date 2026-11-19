using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace MusicLyrics;

internal sealed class MediaSessions(Dispatcher dispatcher) : IDisposable
{
    private SessionManager? manager;
    private Session? manual;
    private bool disposed;
    private readonly Dictionary<string, string> labels = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Session> Available { get; private set; } = [];
    public Session? Selected { get; private set; }
    public Session? Manual => manual;
    public event Action? SessionsChanged;
    public event Action? SelectionChanged;
    public event Action? MediaChanged;
    public event Action? TimelineChanged;
    public event Action? StateChanged;

    public async Task StartAsync()
    {
        var requested = await SessionManager.RequestAsync();
        if (disposed) return;
        manager = requested;
        manager.SessionsChanged += OnSessionsChanged;
        manager.CurrentSessionChanged += OnCurrentSessionChanged;
        Refresh();
    }

    public void Select(Session? session)
    {
        manual = session;
        Refresh();
    }

    private void Refresh()
    {
        if (disposed || manager is null) return;
        Available = manager.GetSessions().Where(IsSupported).ToArray();
        manual = Find(manual);
        var current = Find(manager.GetCurrentSession());
        var next = manual ?? current ?? Available.FirstOrDefault(session =>
            session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            ?? Available.FirstOrDefault();
        if (!Equals(next, Selected))
        {
            Unsubscribe();
            Selected = next;
            if (next is not null)
            {
                next.MediaPropertiesChanged += OnMediaChanged;
                next.PlaybackInfoChanged += OnStateChanged;
                next.TimelinePropertiesChanged += OnTimelineChanged;
            }
            SelectionChanged?.Invoke();
        }
        SessionsChanged?.Invoke();
    }

    private Session? Find(Session? session)
    {
        if (session is null) return null;
        return Available.FirstOrDefault(candidate => Equals(candidate, session));
    }

    public string Label(Session session)
    {
        var id = session.SourceAppUserModelId;
        if (labels.TryGetValue(id, out var label)) return label;
        label = PlaybackSource.Label(id) ?? PlaybackSource.Label(id, RegisteredName(id)) ?? id;
        labels[id] = label;
        return label;
    }

    private bool IsSupported(Session session)
    {
        var label = Label(session);
        return label is "Spotify" || PlaybackSource.IsBrowser(label);
    }

    private static string? RegisteredName(string id)
    {
        // Firefox-based browsers publish an installation-specific app ID.
        if (id.Split(';', 2)[0] is not { Length: 16 } hash || !hash.All(Uri.IsHexDigit)) return null;
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } shellType) return null;
            dynamic? shell = Activator.CreateInstance(shellType);
            return (string?)shell?.NameSpace("shell:AppsFolder")?.ParseName(id)?.Name;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    private void OnSessionsChanged(SessionManager _, SessionsChangedEventArgs args) => Post(Refresh);
    private void OnCurrentSessionChanged(SessionManager _, CurrentSessionChangedEventArgs args) => Post(Refresh);
    private void OnMediaChanged(Session session, MediaPropertiesChangedEventArgs args) =>
        Post(() => { if (Equals(session, Selected)) MediaChanged?.Invoke(); });
    private void OnStateChanged(Session session, PlaybackInfoChangedEventArgs args) =>
        Post(() => { if (Equals(session, Selected)) StateChanged?.Invoke(); });
    private void OnTimelineChanged(Session session, TimelinePropertiesChangedEventArgs args) =>
        Post(() => { if (Equals(session, Selected)) TimelineChanged?.Invoke(); });

    private void Post(Action action) => dispatcher.BeginInvoke(new Action(() =>
    {
        if (!disposed) action();
    }));

    private void Unsubscribe()
    {
        if (Selected is not { } session) return;
        session.MediaPropertiesChanged -= OnMediaChanged;
        session.PlaybackInfoChanged -= OnStateChanged;
        session.TimelinePropertiesChanged -= OnTimelineChanged;
    }

    public void Dispose()
    {
        disposed = true;
        Unsubscribe();
        if (manager is null) return;
        manager.SessionsChanged -= OnSessionsChanged;
        manager.CurrentSessionChanged -= OnCurrentSessionChanged;
    }
}
