using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Screen = System.Windows.Forms.Screen;

namespace MusicLyrics;

internal sealed class PlacementWindow : Window
{
    public Placement? Result { get; private set; }
    private Screen screen;

    public PlacementWindow(Screen screen, Placement current, Preset preset)
    {
        this.screen = screen;
        var initial = current.Monitor == screen.DeviceName ? current : Placement.Default(screen);
        Title = "Place lyric overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        MinWidth = 275;
        MinHeight = 70;
        Width = initial.Rect.Width;
        Height = initial.Rect.Height;
        var panel = new DockPanel { ClipToBounds = true, Background = Brushes.WhiteSmoke };
        var bar = new DockPanel { Background = Brushes.WhiteSmoke, LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Top);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(6, 3, 6, 3), IsCancel = true };
        DockPanel.SetDock(cancel, Dock.Right);
        bar.Children.Add(cancel);
        var save = new Button { Content = "Save", Padding = new Thickness(6, 3, 6, 3), IsDefault = true };
        DockPanel.SetDock(save, Dock.Right);
        bar.Children.Add(save);
        save.Click += (_, _) => Save();
        var monitors = new Button { Content = "Display ▾", Padding = new Thickness(6, 3, 6, 3) };
        AutomationProperties.SetName(monitors, "Choose overlay display");
        DockPanel.SetDock(monitors, Dock.Right);
        monitors.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = monitors };
            foreach (var (display, index) in Screen.AllScreens.Select((display, index) => (display, index)))
            {
                var item = new MenuItem
                {
                    Header = $"Display {index + 1} ({display.Bounds.Width} × {display.Bounds.Height})" +
                        (display.Primary ? " · Primary" : ""),
                    IsChecked = this.screen.DeviceName == display.DeviceName
                };
                item.Click += (_, _) =>
                {
                    if (this.screen.DeviceName == display.DeviceName) return;
                    this.screen = display;
                    var layout = initial.Monitor == display.DeviceName ? initial : Placement.Default(display);
                    Native.Place(this, display, layout.Rect);
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        bar.Children.Add(monitors);
        var handle = new TextBlock { Text = "Drag to move", Padding = new Thickness(8, 5, 0, 4) };
        handle.MouseLeftButtonDown += (_, _) => DragMove();
        bar.Children.Add(handle);
        panel.Children.Add(bar);
        var sample = new LyricVisual();
        sample.SetLine("Follow the words wherever you go", preset, false, Stopwatch.GetTimestamp());
        panel.Children.Add(sample);
        Content = panel;
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this);
            source.AddHook(RestrictBounds);
            Native.Place(this, screen, initial.Rect);
        };
        Loaded += (_, _) => Native.Place(this, screen, initial.Rect);
    }

    private void Save()
    {
        if (!Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var actual)) return;
        var bounds = screen.Bounds;
        var scale = Native.Scale(screen);
        Result = new Placement(screen.DeviceName, new OverlayRect(
            (actual.Left - bounds.Left) / scale, (actual.Top - bounds.Top) / scale,
            actual.Width / scale, actual.Height / scale).Fit(bounds.Width / scale, bounds.Height / scale));
        DialogResult = true;
    }

    private nint RestrictBounds(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is not (Native.Moving or Native.Sizing) || lParam == 0) return 0;
        var actual = Marshal.PtrToStructure<Native.Rect>(lParam);
        var bounds = screen.Bounds;
        if (message == Native.Moving)
        {
            var width = Math.Min(actual.Width, bounds.Width);
            var height = Math.Min(actual.Height, bounds.Height);
            actual.Left = Math.Clamp(actual.Left, bounds.Left, bounds.Right - width);
            actual.Top = Math.Clamp(actual.Top, bounds.Top, bounds.Bottom - height);
            actual.Right = actual.Left + width;
            actual.Bottom = actual.Top + height;
        }
        else
        {
            actual.Left = Math.Max(actual.Left, bounds.Left);
            actual.Top = Math.Max(actual.Top, bounds.Top);
            actual.Right = Math.Min(actual.Right, bounds.Right);
            actual.Bottom = Math.Min(actual.Bottom, bounds.Bottom);
        }
        Marshal.StructureToPtr(actual, lParam, false);
        handled = true;
        return 1;
    }
}
