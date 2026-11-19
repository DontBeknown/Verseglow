using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Screen = System.Windows.Forms.Screen;

namespace MusicLyrics;

internal sealed class App : Application
{
    private readonly Preferences preferences;
    private readonly Window overlay;
    private bool backdropBlur;
    private readonly LyricVisual visual = new();
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon = new(typeof(App), "MusicLyrics.ico");
    private readonly Forms.ToolStripMenuItem toggle;
    private readonly HwndSource messages;
    private readonly Lrclib lrclib;
    private readonly Player player;
    private readonly bool probe;
    private SettingsWindow? settings;
    private bool editing;
    private int registeredId;
    private string? error;

    [STAThread]
    public static void Main(string[] args)
    {
        var probe = args.Contains("--probe");
        using var instance = new Mutex(true, probe ? "Local\\MusicLyrics.Probe" : "Local\\MusicLyrics", out var first);
        if (!first) return;
        var logOption = Array.IndexOf(args, "--probe-log");
        var logPath = logOption >= 0 && logOption + 1 < args.Length ? args[logOption + 1] : null;
        string? contact = null;
        var evaluation = Array.IndexOf(args, "--evaluate-online");
        if (evaluation >= 0 && evaluation + 1 < args.Length) contact = args[evaluation + 1];
        var app = new App(probe, contact, logPath);
        app.Run();
    }

    private App(bool probe, string? evaluationContact, string? logPath)
    {
        this.probe = probe;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        preferences = Preferences.Load();
        overlay = new Window
        {
            Title = "MusicLyrics overlay",
            AllowsTransparency = true, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            Background = System.Windows.Media.Brushes.Transparent, Content = visual
        };
        overlay.SourceInitialized += (_, _) => Native.MakeClickThrough(new WindowInteropHelper(overlay).Handle);
        messages = new HwndSource(new HwndSourceParameters("MusicLyrics messages") { WindowStyle = 0 });
        messages.AddHook(ReceiveMessage);

        StreamWriter? log = null;
        if (probe)
        {
            logPath = Path.GetFullPath(logPath ?? Path.Combine(Preferences.Folder, "playback-probe.jsonl"));
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            log = new StreamWriter(logPath, append: false) { AutoFlush = true };
            log.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { windows = Environment.OSVersion.VersionString }));
        }
        lrclib = new Lrclib(evaluationContact ?? preferences.OnlineLyricsContact);
        player = new Player(Dispatcher, lrclib, log);
        player.LineChanged += ShowLine;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        toggle = new Forms.ToolStripMenuItem("Hide overlay", null, (_, _) => Toggle());
        menu.Items.Add(toggle);
        menu.Items.Add("Edit placement", null, (_, _) => EditPlacement());
        if (probe) menu.Items.Add("Open probe log", null, (_, _) => Process.Start(new ProcessStartInfo(logPath!)
            { UseShellExecute = true }));
        menu.Items.Add("Exit", null, (_, _) =>
        {
            settings?.CloseForExit();
            Shutdown();
        });
        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon, ContextMenuStrip = menu,
            Text = probe ? "MusicLyrics playback probe" : "MusicLyrics", Visible = true
        };
        tray.DoubleClick += (_, _) => OpenSettings();
        SetShortcut(preferences.Shortcut, save: false);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Startup += async (_, _) => await player.StartAsync();
    }

    private Preset Active => preferences.Presets.FirstOrDefault(p => p.Name == preferences.ActivePreset) ??
        Preset.BuiltIns[0];

    private void OpenSettings()
    {
        if (settings is null)
        {
            settings = new SettingsWindow(player, preferences, () =>
            {
                Save();
                player.Refresh();
            }, key => SetShortcut(key, save: true), EditPlacement, Toggle);
            settings.UpdateNotice(error);
        }
        settings.Show();
        if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
        settings.Activate();
    }

    private void ShowLine(string line, bool animate, long stamp, double span)
    {
        if (editing || line.Length == 0)
        {
            visual.Finish();
            if (overlay.IsVisible) overlay.Hide();
            return;
        }
        if (!overlay.IsVisible)
        {
            ApplyPlacement();
            overlay.Show();
            ApplyPlacement();
        }
        var style = Active;
        if (backdropBlur != style.Blur)
        {
            Native.SetBackdropBlur(new WindowInteropHelper(overlay).Handle, style.Blur);
            backdropBlur = style.Blur;
        }
        visual.SetLine(line, style, animate, stamp, span, player.CurrentLanguage, player.CurrentCue);
    }

    private void Toggle()
    {
        player.Visible = !player.Visible;
        toggle.Text = player.Visible ? "Hide overlay" : "Show overlay";
        player.Refresh();
    }

    private nint ReceiveMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.HotkeyMessage && wParam == registeredId && registeredId != 0)
        {
            Toggle();
            handled = true;
        }
        else if (message == Native.SettingChange)
        {
            visual.RefreshMotion();
            settings?.RefreshMotion();
            if (overlay.IsVisible) ApplyPlacement();
        }
        return 0;
    }

    private bool SetShortcut(Hotkey key, bool save)
    {
        if (registeredId != 0 && key == preferences.Shortcut) return true;
        var nextId = registeredId == 1 ? 2 : 1;
        if (!Native.RegisterHotKey(messages.Handle, nextId, key.Modifiers | 0x4000, key.Key))
        {
            SetNotice($"Shortcut {key} is unavailable. Choose another in Settings.");
            return false;
        }
        if (registeredId != 0) Native.UnregisterHotKey(messages.Handle, registeredId);
        registeredId = nextId;
        preferences.Shortcut = key;
        SetNotice(null);
        if (save) Save();
        return true;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() =>
    {
        if (preferences.Layout is not null || overlay.IsVisible) ApplyPlacement();
    });

    private Placement CurrentPlacement() => preferences.Layout ??
        Placement.Default(Screen.PrimaryScreen ?? Screen.AllScreens.First());

    private void ApplyPlacement()
    {
        var (screen, effective) = CurrentPlacement().Resolve();
        if (overlay.IsVisible || overlay.IsLoaded || !editing) Native.Place(overlay, screen, effective.Rect);
        if (preferences.Layout is not null && effective != preferences.Layout)
        {
            preferences.Layout = effective;
            Save();
        }
    }

    private void EditPlacement()
    {
        if (editing) return;
        editing = true;
        ShowLine("", false, 0, 0);
        try
        {
            var (current, placement) = CurrentPlacement().Resolve();
            var editor = new PlacementWindow(current, placement, Active);
            if (settings?.IsVisible == true) editor.Owner = settings;
            if (editor.ShowDialog() == true && editor.Result is { } result)
            {
                preferences.Layout = result;
                Save();
            }
        }
        finally
        {
            editing = false;
            player.Refresh();
        }
    }

    private void Save()
    {
        if (probe) return;
        try { preferences.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetNotice("Settings could not be saved. Changes are available until you exit.");
        }
    }

    private void SetNotice(string? message)
    {
        error = message;
        settings?.UpdateNotice(error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        player.Dispose();
        lrclib.Dispose();
        if (registeredId != 0) Native.UnregisterHotKey(messages.Handle, registeredId);
        messages.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        trayIcon.Dispose();
        base.OnExit(e);
    }
}
