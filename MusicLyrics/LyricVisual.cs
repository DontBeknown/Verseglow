using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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
    private static readonly Regex LyricWords = new(@"[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:['\u2019-][\p{L}\p{N}][\p{L}\p{M}\p{N}]*)*", RegexOptions.Compiled);
    private static readonly Regex SimpleReadingOrder = new(@"^[\u0020-\u024F\u0300-\u036F\u2000-\u206F\r\n]+$",
        RegexOptions.Compiled);
    private string text = "";
    private Preset preset = Preset.BuiltIns[0];
    private Entrance entrance;
    private long started;
    private readonly DispatcherTimer animationTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(34)
    };
    private readonly DrawingGroup lineText = new();
    private DrawingGroup? popText;
    private DrawingGroup? outgoing;
    private Point anchor = new(.5, .5), priorAnchor;
    private TranslateTransform? travel;
    private double transitionMs;
    private long transitionStart;
    private double progress = 1;
    private FormattedText? layout;
    private (Geometry? Clip, Geometry? Glyphs)[] ripple = [];
    private Point origin;
    private Transform? placement;
    private Geometry? glyphs;
    private Pen? outline;
    private string? language;
    private double animationMs;
    private long cue;
    private CinemaDrawing? cinema;
    internal static (int Index, int Length)[] EmphasisWords(string text)
    {
        if (!LanguageTypography.HasThai(text))
            return LyricWords.Matches(text).Select(m => (m.Index, m.Length)).ToArray();

        var tokens = new Windows.Data.Text.WordsSegmenter("th").GetTokens(text);
        return tokens.Where(t => LyricWords.IsMatch(t.Text))
            .Select(t => ((int)t.SourceTextSegment.StartPosition, (int)t.SourceTextSegment.Length)).ToArray();
    }

    private static bool WordEffect(Entrance effect) =>
        effect is Entrance.Ripple or Entrance.Stack or Entrance.Tilt or Entrance.Fill or Entrance.WordByWord;
    internal static bool WordVisible(double progress, int index, int count) => progress * count >= index;

    protected override int VisualChildrenCount => cinema is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => index == 0 && cinema is not null
        ? cinema : throw new ArgumentOutOfRangeException(nameof(index));

    public LyricVisual()
    {
        ClipToBounds = true;
        animationTimer.Tick += OnFrame;
        SizeChanged += (_, _) => ClearLayout();
        Unloaded += (_, _) => Finish();
    }

    public void SetLine(string line, Preset style, bool animate, long startTick, double lineMs = 2400,
        string? language = null, long cue = 0, long? previousCue = null)
    {
        animationTimer.Stop();
        var motion = animate && Native.AnimationsEnabled();
        transitionStart = Stopwatch.GetTimestamp();
        transitionMs = motion && layout != null && previousCue == this.cue && cue != this.cue
            ? Math.Max(0, Math.Min(320, lineMs * .45) - Stopwatch.GetElapsedTime(startTick).TotalMilliseconds) : 0;
        priorAnchor = anchor;
        outgoing = null;
        if (transitionMs > 0 && style.Exit != ExitTransition.None)
        {
            outgoing = new DrawingGroup { Opacity = 0 };
            using (var snapshot = outgoing.Open())
            {
                if (placement != null) snapshot.PushTransform(placement.CloneCurrentValue());
                if (outline != null) snapshot.DrawGeometry(null, outline, glyphs);
                snapshot.DrawDrawing(lineText.CloneCurrentValue());
                if (placement != null) snapshot.Pop();
            }
            if (style.Exit == ExitTransition.Lift)
            {
                var lift = new TranslateTransform();
                lift.BeginAnimation(TranslateTransform.YProperty, Ease(0, -Math.Min(24, preset.FontSize * .5)));
                outgoing.Transform = lift;
            }
            else if (style.Exit == ExitTransition.Recede && !outgoing.Bounds.IsEmpty)
            {
                var bounds = outgoing.Bounds;
                var shrink = new ScaleTransform(1, 1, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                shrink.BeginAnimation(ScaleTransform.ScaleXProperty, Ease(1, .78));
                shrink.BeginAnimation(ScaleTransform.ScaleYProperty, Ease(1, .78));
                outgoing.Transform = shrink;
            }
            outgoing.BeginAnimation(DrawingGroup.OpacityProperty, Ease(1));
        }
        text = line;
        this.language = language;
        this.cue = cue;
        preset = style;
        entrance = motion ? style.Effect : Entrance.Instant;
        if (WordEffect(entrance) && (!SimpleReadingOrder.IsMatch(text) ||
            text.Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format || c is '\u2028' or '\u2029')))
        {
            entrance = Entrance.Fade;
            preset = style with { Duration = Preset.BuiltIns[0].Duration };
        }
        animationMs = entrance switch
        {
            Entrance.Stack => Math.Max(1, lineMs / preset.StageStackSpeed),
            Entrance.Tilt or Entrance.Fill or Entrance.WordByWord => Math.Max(1, lineMs),
            Entrance.Cinema => 240,
            _ => preset.Duration
        };
        started = startTick;
        progress = entrance == Entrance.Instant ? 1 :
            Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds / animationMs, 0, 1);
        if (line.Length > 0 && progress < 1)
            animationTimer.Start();
        ClearLayout();
    }

    public void RefreshMotion()
    {
        if (!Native.AnimationsEnabled()) Finish();
    }

    public void Finish()
    {
        animationTimer.Stop();
        transitionMs = 0;
        if (outgoing != null || travel != null)
        {
            outgoing = null;
            travel?.BeginAnimation(TranslateTransform.XProperty, null);
            travel?.BeginAnimation(TranslateTransform.YProperty, null);
            InvalidateVisual();
        }
        if (progress == 1) return;
        progress = 1;
        UpdateAnimation();
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        var next = Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds / animationMs, 0, 1);
        if (entrance == Entrance.WordByWord && ripple.Length > 0)
        {
            if (WordVisible(next, ripple.Length - 1, ripple.Length))
            {
                animationTimer.Stop();
                progress = 1;
                UpdateAnimation();
                return;
            }
            var unchanged = (int)(next * ripple.Length) == (int)(progress * ripple.Length);
            progress = next;
            if (unchanged) return;
        }
        progress = next;
        if (progress == 1) animationTimer.Stop();
        UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (popText is null)
        {
            InvalidateVisual();
            return;
        }

        var transform = (ScaleTransform)popText.Transform;
        var scale = 1 - (1 - preset.Scale) * Math.Pow(1 - progress, 3);
        transform.ScaleX = scale;
        transform.ScaleY = scale;
        popText.Opacity = progress;
    }

    private DoubleAnimation Ease(double from, double to = 0) => new(from, to, TimeSpan.FromMilliseconds(transitionMs))
    {
        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
    };

    private void ClearLayout()
    {
        layout = null;
        ripple = [];
        popText = null;
        InvalidateVisual();
    }

    private void DrawLyrics(DrawingContext drawing)
    {
        if (outline != null) drawing.DrawGeometry(null, outline, glyphs);
        drawing.DrawDrawing(lineText);
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
            var random = new Random(preset.Scatter ? unchecked(hash * 31 + cue.GetHashCode()) : hash);
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
            var stackSize = preset.Effect == Entrance.Stack && SimpleReadingOrder.IsMatch(text);
            if (preset.MaxHighlightWords > 0 && (preset.HighlightPercent.HasValue || stackSize ||
                (preset.MaxLineColors > 1 && preset.AccentColors.Length > 0)))
            {
                var emphasis = new Random(HashCode.Combine(text, cue));
                var words = EmphasisWords(text).ToList();
                var count = emphasis.Next(1, Math.Min(words.Count, preset.MaxHighlightWords) + 1);
                var accents = preset.AccentColors.Where(c => !c.Equals(preset.Color, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                emphasis.Shuffle(accents);
                var accentCount = Math.Min(preset.MaxLineColors - 1, accents.Length);
                var endingAvailable = true;
                for (var i = 0; i < count && words.Count > 0; i++)
                {
                    // The original ending gets one extra ticket until selected.
                    var tickets = words.Count * 4 + (endingAvailable ? 1 : 0);
                    var index = Math.Min(emphasis.Next(tickets) / 4, words.Count - 1);
                    var word = words[index];
                    if (index == words.Count - 1) endingAvailable = false;
                    words.RemoveAt(index);
                    if (accentCount > 0)
                    {
                        var accent = accents[emphasis.Next(accentCount)];
                        layout.SetForegroundBrush(new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent)), word.Index, word.Length);
                    }
                    if (preset.HighlightPercent is int percent)
                        layout.SetFontSize(preset.FontSize * percent / 100, word.Index, word.Length);
                    else if (stackSize)
                        layout.SetFontSize(preset.FontSize * (1.10 + .10 * emphasis.NextDouble()), word.Index, word.Length);
                }
            }
            origin = new Point(12, Math.Max(0, (ActualHeight - layout.Height) / 2));
            outline = preset.Outline > 0 ? new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.OutlineColor)), preset.Outline * 2) { LineJoin = PenLineJoin.Round } : null;
            glyphs = outline != null || preset.Tilt > 0 || preset.Scatter ? layout.BuildGeometry(origin) : null;
            placement = null;
            travel = null;
            anchor = new Point(.5, .5);
            if ((preset.Tilt > 0 || preset.Scatter) && glyphs is { Bounds.IsEmpty: false })
            {
                var bounds = glyphs.GetRenderBounds(outline);
                var fullHeight = layout.Height;
                var maxLines = layout.MaxLineCount;
                layout.MaxLineCount = 1;
                var angleEligible = layout.Height == fullHeight && bounds.Width + 12 <= ActualWidth && bounds.Height + 8 <= ActualHeight;
                layout.MaxLineCount = maxLines;
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
                anchor = new Point(x, y);
                if (preset.Scatter && angleEligible && Stopwatch.GetElapsedTime(transitionStart).TotalMilliseconds < transitionMs)
                {
                    travel = new TranslateTransform();
                    travel.BeginAnimation(TranslateTransform.XProperty, Ease((priorAnchor.X - x) * (ActualWidth - rotated.Width - 12)));
                    travel.BeginAnimation(TranslateTransform.YProperty, Ease((priorAnchor.Y - y) * (ActualHeight - rotated.Height - 8)));
                    placement = new TransformGroup { Children = { placement, travel } };
                }
            }
            if (WordEffect(entrance) && progress < 1)
            {
                ripple = Words.Matches(text).Select(word =>
                {
                    var clip = layout.BuildHighlightGeometry(origin, word.Index, word.Length);
                    // Isolate the glyphs before stroking so the outline can extend past the word's layout box.
                    Geometry? wordGlyphs = clip != null && outline != null && glyphs != null
                        ? Geometry.Combine(glyphs, clip, GeometryCombineMode.Intersect, null) : null;
                    wordGlyphs?.Freeze();
                    return (clip, wordGlyphs);
                }).ToArray();
            }
            using (var cached = lineText.Open())
                cached.DrawText(layout, origin);
            if (entrance == Entrance.Pop && progress < 1)
            {
                popText = new DrawingGroup
                {
                    Transform = new ScaleTransform(1, 1, ActualWidth / 2, ActualHeight / 2)
                };
                UpdateAnimation();
                using var popDrawing = popText.Open();
                DrawLyrics(popDrawing);
            }
        }

        var background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(preset.Background * 255), 0, 0, 0));
        drawing.DrawRoundedRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        if (outgoing != null) drawing.DrawDrawing(outgoing);
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
                var word = ripple[i];
                if (opacity == 0 || word.Clip is not { } clip) continue;
                var bounds = clip.Bounds;
                if (bounds.IsEmpty) continue;
                var sweeping = entrance == Entrance.Fill && opacity < 1;
                if (sweeping)
                {
                    var padding = preset.Outline;
                    drawing.PushClip(new RectangleGeometry(new Rect(bounds.X - padding, bounds.Y - padding,
                        bounds.Width * opacity + padding, bounds.Height + 2 * padding)));
                }
                if (entrance == Entrance.Stack)
                    drawing.PushTransform(new TranslateTransform(0, (1 - opacity) * preset.FontSize * .32));
                if (entrance == Entrance.Tilt)
                    drawing.PushTransform(new RotateTransform((1 - opacity) * (i % 2 == 0 ? -13 : 13), bounds.X, bounds.Bottom));
                drawing.PushOpacity(entrance == Entrance.Fill ? 1 : opacity);
                if (word.Glyphs != null) drawing.DrawGeometry(null, outline, word.Glyphs);
                drawing.PushClip(clip);
                drawing.DrawDrawing(lineText);
                drawing.Pop();
                drawing.Pop();
                if (sweeping || entrance is Entrance.Stack or Entrance.Tilt) drawing.Pop();
            }
            if (placement != null) drawing.Pop();
            return;
        }
        if (popText is not null)
            drawing.DrawDrawing(popText);
        else
        {
            drawing.PushOpacity(progress);
            DrawLyrics(drawing);
            drawing.Pop();
        }
        if (placement != null) drawing.Pop();
    }
}
