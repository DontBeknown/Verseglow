using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MusicLyrics;

internal sealed class LyricVisual : FrameworkElement
{
    private sealed class CinemaDrawing : DrawingVisual
    {
        private readonly BlurEffect blur = new();

        public void Focus(double radius)
        {
            blur.Radius = radius;
            VisualEffect = radius > 0 ? blur : null;
        }
    }

    private static readonly Regex Words = new(@"\S+", RegexOptions.Compiled);
    private static readonly Regex SimpleReadingOrder = new(@"^[\u0020-\u024F\u0300-\u036F\u2000-\u206F\r\n]+$",
        RegexOptions.Compiled);
    private string text = "";
    private Preset preset = Preset.BuiltIns[0];
    private Entrance entrance;
    private long started;
    private bool animating;
    private double progress = 1;
    private FormattedText? layout;
    private Geometry?[] ripple = [];
    private Point origin;
    private Transform? placement;
    private Geometry? glyphs;
    private Pen? outline;
    private string? language;
    private double animationMs;
    private long cue;
    private CinemaDrawing? cinema;
    private static bool WordEffect(Entrance effect) =>
        effect is Entrance.Ripple or Entrance.Stack or Entrance.Tilt or Entrance.Fill or Entrance.WordByWord;
    internal static bool WordVisible(double progress, int index, int count) => progress * count >= index;

    protected override int VisualChildrenCount => cinema is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => index == 0 && cinema is not null
        ? cinema : throw new ArgumentOutOfRangeException(nameof(index));

    public LyricVisual()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => ClearLayout();
        Unloaded += (_, _) => Finish();
    }

    public void SetLine(string line, Preset style, bool animate, long startTick, double lineMs = 2400,
        string? language = null, long cue = 0)
    {
        StopRendering();
        text = line;
        this.language = language;
        this.cue = cue;
        preset = style;
        entrance = animate && Native.AnimationsEnabled() ? style.Effect : Entrance.Instant;
        if (WordEffect(entrance) && (!SimpleReadingOrder.IsMatch(text) ||
            text.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format || c is '\u2028' or '\u2029')))
        {
            entrance = Entrance.Fade;
            preset = style with { Duration = Preset.BuiltIns[0].Duration };
        }
        animationMs = entrance switch
        {
            Entrance.Stack or Entrance.Tilt or Entrance.Fill or Entrance.WordByWord => Math.Max(1, lineMs),
            Entrance.Cinema => 240,
            _ => preset.Duration
        };
        started = startTick;
        progress = entrance == Entrance.Instant ? 1 :
            Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds / animationMs, 0, 1);
        if (line.Length > 0 && progress < 1)
        {
            animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
        ClearLayout();
    }

    public void RefreshMotion()
    {
        if (!Native.AnimationsEnabled()) Finish();
    }

    public void Finish()
    {
        StopRendering();
        if (progress == 1) return;
        progress = 1;
        InvalidateVisual();
    }

    private void StopRendering()
    {
        if (!animating) return;
        CompositionTarget.Rendering -= OnFrame;
        animating = false;
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        var next = Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds / animationMs, 0, 1);
        if (entrance == Entrance.WordByWord && ripple.Length > 0)
        {
            if (WordVisible(next, ripple.Length - 1, ripple.Length))
            {
                Finish();
                return;
            }
            var unchanged = (int)(next * ripple.Length) == (int)(progress * ripple.Length);
            progress = next;
            if (unchanged) return;
        }
        progress = next;
        if (progress == 1) StopRendering();
        InvalidateVisual();
    }

    private void ClearLayout()
    {
        layout = null;
        ripple = [];
        InvalidateVisual();
    }

    private void DrawLyrics(DrawingContext drawing)
    {
        if (outline != null) drawing.DrawGeometry(null, outline, glyphs);
        drawing.DrawText(layout!, origin);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ClearLayout();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var showCinema = entrance == Entrance.Cinema && progress < 1 &&
            text.Length > 0 && ActualWidth > 24 && ActualHeight > 0;
        if (!showCinema && cinema is not null)
        {
            var finished = cinema;
            cinema = null;
            RemoveVisualChild(finished);
        }
        if (string.IsNullOrEmpty(text) || ActualWidth <= 24 || ActualHeight <= 0) return;
        if (layout is null)
        {
            int hash = 17;
            foreach (var c in text) hash = unchecked(hash * 31 + c);
            var random = new Random(hash);
            var color = (Color)ColorConverter.ConvertFromString(preset.Color);
            var direction = text.FirstOrDefault(char.IsLetter);
            var rtl = direction is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFEFF';
            layout = new FormattedText(text, CultureInfo.CurrentUICulture,
                rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                new Typeface(preset.Font ?? (preset.Effect switch
                {
                    Entrance.Stack => "Impact",
                    Entrance.Tilt => "Georgia",
                    Entrance.Fill => "Segoe UI Black",
                    _ => "Segoe UI"
                })), preset.FontSize, new SolidColorBrush(color),
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = ActualWidth - 24,
                TextAlignment = TextAlignment.Center,
                Trimming = TextTrimming.None
            };
            LanguageTypography.Apply(layout, text, preset.LanguageFonts, language);
            MatchCollection? words = null;
            if (preset.AccentColors.Length > 0)
            {
                words = Words.Matches(text);
                ApplyWordColors(layout, words);
            }
            if (preset.Effect == Entrance.Stack && SimpleReadingOrder.IsMatch(text))
            {
                var index = 0;
                foreach (Match word in words ??= Words.Matches(text))
                    layout.SetFontSize(preset.FontSize * (index++ % 3 == 0 ? 1.20 : .85), word.Index, word.Length);
            }
            origin = new Point(12, Math.Max(0, (ActualHeight - layout.Height) / 2));
            outline = preset.Outline > 0 ? new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.OutlineColor)), preset.Outline * 2) { LineJoin = PenLineJoin.Round } : null;
            glyphs = outline != null || preset.Tilt > 0 || preset.Scatter ? layout.BuildGeometry(origin) : null;
            placement = null;
            if ((preset.Tilt > 0 || preset.Scatter) && glyphs is { Bounds.IsEmpty: false })
            {
                var bounds = glyphs.GetRenderBounds(outline);
                var angleEligible = layout.Height < 1.8 * preset.FontSize && bounds.Width + 12 <= ActualWidth && bounds.Height + 8 <= ActualHeight;
                var angle = angleEligible ? (2 * random.NextDouble() - 1) * preset.Tilt : 0;
                var matrix = Matrix.Identity;
                matrix.RotateAt(angle, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                var rotated = new MatrixTransform(matrix).TransformBounds(bounds);
                if (rotated.Width + 12 > ActualWidth || rotated.Height + 8 > ActualHeight)
                {
                    matrix = Matrix.Identity;
                    rotated = bounds;
                }
                var x = preset.Scatter && angleEligible ? random.NextDouble() : .5;
                var y = preset.Scatter && angleEligible ? random.NextDouble() : .5;
                matrix.Translate((ActualWidth - rotated.Width - 12) * x + 6 - rotated.X,
                    (ActualHeight - rotated.Height - 8) * y + 4 - rotated.Y);
                placement = new MatrixTransform(matrix);
            }
            if (WordEffect(entrance) && progress < 1)
                ripple = (words ?? Words.Matches(text)).Select(word =>
                    layout.BuildHighlightGeometry(origin, word.Index, word.Length)).ToArray();
        }

        var background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(preset.Background * 255), 0, 0, 0));
        drawing.DrawRoundedRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        if (showCinema)
        {
            if (cinema is null)
            {
                cinema = new CinemaDrawing();
                AddVisualChild(cinema);
            }
            var remaining = Math.Pow(1 - progress, 2);
            cinema.Focus(10 * remaining);
            using var frame = cinema.RenderOpen();
            frame.PushOpacity(1 - .65 * remaining);
            frame.PushTransform(new TranslateTransform(0, 8 * remaining));
            if (placement != null) frame.PushTransform(placement);
            DrawLyrics(frame);
            if (placement != null) frame.Pop();
            frame.Pop();
            frame.Pop();
            return;
        }
        if (placement != null) drawing.PushTransform(placement);
        if (WordEffect(entrance) && progress < 1)
        {
            if (entrance == Entrance.Fill)
            {
                drawing.PushOpacity(.42);
                DrawLyrics(drawing);
                drawing.Pop();
            }
            for (var i = 0; i < ripple.Length; i++)
            {
                var start = ripple.Length > 1 ? .72 * i / (ripple.Length - 1) : 0;
                var opacity = Math.Clamp((progress - start) / .28, 0, 1);
                if (entrance == Entrance.WordByWord)
                    opacity = WordVisible(progress, i, ripple.Length) ? 1 : 0;
                else if (entrance != Entrance.Ripple)
                    opacity = Math.Clamp(progress * ripple.Length - i, 0, 1);
                if (opacity == 0 || ripple[i] is not { } clip) continue;
                var bounds = clip.Bounds;
                if (bounds.IsEmpty) continue;
                if (entrance == Entrance.Fill)
                    drawing.PushClip(new RectangleGeometry(new Rect(bounds.X, bounds.Y, bounds.Width * opacity, bounds.Height)));
                if (entrance == Entrance.Stack)
                    drawing.PushTransform(new TranslateTransform(0, (1 - opacity) * preset.FontSize * .32));
                if (entrance == Entrance.Tilt)
                    drawing.PushTransform(new RotateTransform((1 - opacity) * (i % 2 == 0 ? -13 : 13), bounds.X, bounds.Bottom));
                drawing.PushClip(clip);
                drawing.PushOpacity(entrance == Entrance.Fill ? 1 : opacity);
                DrawLyrics(drawing);
                drawing.Pop();
                drawing.Pop();
                if (entrance is Entrance.Fill or Entrance.Stack or Entrance.Tilt) drawing.Pop();
            }
            if (placement != null) drawing.Pop();
            return;
        }
        drawing.PushOpacity(progress);
        if (entrance == Entrance.Pop && progress < 1)
        {
            var scale = 1 - (1 - preset.Scale) * Math.Pow(1 - progress, 3);
            drawing.PushTransform(new ScaleTransform(scale, scale, ActualWidth / 2, ActualHeight / 2));
            DrawLyrics(drawing);
            drawing.Pop();
        }
        else DrawLyrics(drawing);
        drawing.Pop();
        if (placement != null) drawing.Pop();
    }

    private void ApplyWordColors(FormattedText line, MatchCollection words)
    {
        if (words.Count < 2) return;
        var random = new Random(HashCode.Combine(text, cue));
        var brushes = preset.AccentColors.Select(color =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))).ToArray();
        var remaining = (words.Count + 1) / 3;
        for (var i = 0; i < words.Count && remaining > 0; i++)
        {
            if (random.Next(words.Count - i) >= remaining) continue;
            var word = words[i];
            line.SetForegroundBrush(brushes[random.Next(brushes.Length)], word.Index, word.Length);
            remaining--;
        }
    }
}
