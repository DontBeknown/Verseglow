using System;

namespace MusicLyrics;

internal static class PlaybackSource
{
    public static string? Label(string id, string? registeredName = null)
    {
        if (id.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (id.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (id.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (id.Contains("ZenBrowser", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("Zen Browser", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("zen.exe", StringComparison.OrdinalIgnoreCase) ||
            registeredName is "Zen" or "Zen Browser" or "Zen Private Browsing") return "Zen";
        return null;
    }

    public static bool IsBrowser(string label) => label is "Chrome" or "Edge" or "Zen";
}
