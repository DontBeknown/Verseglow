using System;

namespace MusicLyrics;

internal sealed class PlaybackClock
{
    private double? anchor;
    private double at;
    public double Rate { get; private set; }

    public void Anchor(double positionMs, double rate, double nowMs)
    {
        anchor = positionMs;
        at = nowMs;
        Rate = rate;
    }

    public void AnchorSample(double positionMs, double rate, double sampleAgeMs, bool sameSample, double nowMs)
    {
        var predicted = sameSample ? Position(nowMs) : null;
        Anchor(predicted ?? positionMs + Math.Max(0, sampleAgeMs) * rate, rate, nowMs);
    }

    public void Clear()
    {
        anchor = null;
        Rate = 0;
    }

    public double? Position(double nowMs) => anchor + Math.Max(0, nowMs - at) * Rate;
}

internal readonly record struct OverlayRect(double X, double Y, double Width, double Height)
{
    public OverlayRect Fit(double availableWidth, double availableHeight)
    {
        var width = Math.Clamp(Width, Math.Min(100, availableWidth), availableWidth);
        var height = Math.Clamp(Height, Math.Min(50, availableHeight), availableHeight);
        return new OverlayRect(Math.Clamp(X, 0, availableWidth - width),
            Math.Clamp(Y, 0, availableHeight - height), width, height);
    }
}
