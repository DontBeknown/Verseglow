using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicLyrics;

internal sealed class Lrclib : IDisposable
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly SemaphoreSlim requests = new(1);
    private DateTimeOffset retryAfter;
    private ProductInfoHeaderValue? contactHeader;
    public string Contact { get; private set; } = "";
    public bool Enabled => contactHeader is not null;

    public Lrclib(string? evaluationContact, HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.MaxResponseContentBufferSize = MaxResponseBytes;
        http.Timeout = TimeSpan.FromSeconds(10);
        var cleared = string.Equals(typeof(Lrclib).Assembly.GetCustomAttributeValue("OnlineLyricsCleared"),
            "true", StringComparison.OrdinalIgnoreCase);
        SetContact(evaluationContact ??
            (cleared ? typeof(Lrclib).Assembly.GetCustomAttributeValue("OnlineLyricsContact") : null) ?? "");
    }

    public bool SetContact(string value)
    {
        var contact = value.Trim();
        ProductInfoHeaderValue? header = null;
        if (contact.Length > 0 && (contact.Length > 250 || contact.Any(char.IsControl) ||
            !(Uri.TryCreate(contact, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                uri.Host.Length > 0 && uri.UserInfo.Length == 0 ||
              System.Net.Mail.MailAddress.TryCreate(contact, out var address) && address.Address == contact) ||
            !ProductInfoHeaderValue.TryParse($"({contact})", out header))) return false;
        contactHeader = header;
        Contact = contact;
        return true;
    }

    public async Task<IReadOnlyList<LyricResult>> SearchAsync(string title, string artist, string album,
        CancellationToken cancellation)
    {
        if (contactHeader is not { } header) return [];
        await requests.WaitAsync(cancellation);
        try
        {
            if (retryAfter > DateTimeOffset.UtcNow) throw new InvalidOperationException("Lyrics service is rate limited. Try again later.");
            var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            if (!string.IsNullOrWhiteSpace(album)) query += $"&album_name={Uri.EscapeDataString(album)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://lrclib.net/api/search?" + query);
            request.Headers.UserAgent.ParseAdd("Verseglow/1.0");
            request.Headers.UserAgent.Add(header);
            using var response = await http.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                retryAfter = retry?.Date ?? DateTimeOffset.UtcNow.Add(retry?.Delta ?? TimeSpan.FromMinutes(1));
                throw new InvalidOperationException("Lyrics service is rate limited. Try again later.");
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellation));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The lyrics service returned an invalid result.");
            var results = new List<LyricResult>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var resultId)) continue;
                double? duration = item.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds > 0 ? seconds : null;
                results.Add(new LyricResult(resultId, Read(item, "trackName") ?? "", Read(item, "artistName") ?? "",
                    Read(item, "albumName") ?? "", duration, Read(item, "syncedLyrics")));
            }
            cancellation.ThrowIfCancellationRequested();
            return results;
        }
        finally { requests.Release(); }
    }

    private static string? Read(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose() => http.Dispose();
}

internal static class AssemblyMetadata
{
    public static string? GetCustomAttributeValue(this Assembly assembly, string key)
    {
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (attribute.Key == key) return attribute.Value;
        return null;
    }
}
