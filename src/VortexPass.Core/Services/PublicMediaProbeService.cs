using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VortexPass.Core.Services;

public sealed record PublicMediaResult(bool Confirmed, string Detail, long? LatencyMs = null);
public static class PublicMediaProbeService
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.FromMinutes(2) }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly SemaphoreSlim DiscoveryGate = new(3, 3);
    private static readonly ConcurrentDictionary<string, (Uri Uri, DateTimeOffset Expiry)> Cache = new();

    public static async Task<PublicMediaResult> CheckAsync(string kind, CancellationToken token,bool directFresh=false)
    {
        using var fresh=directFresh?new HttpClient(new SocketsHttpHandler{UseProxy=false,UseCookies=false,AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(3)}){Timeout=Timeout.InfiniteTimeSpan}:null;
        var client=fresh??Client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var watch = Stopwatch.StartNew();
        try
        {
            var uri = await DiscoverAsync(kind, timeout.Token,client,!directFresh);
            if (uri is null) return new(false, "Адрес реального контрольного ресурса не получен; проверка не завершилась");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 1023);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[512]; var read = await stream.ReadAtLeastAsync(buffer, 16, false, timeout.Token);
            var valid = kind == "video"
                ? RouteProbePolicy.IsVideo((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, buffer.AsSpan(0, read))
                : RouteProbePolicy.IsImage((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, buffer.AsSpan(0, read));
            // Never expose signed URL parameters or response bodies in diagnostics.
            return new(valid, $"{kind}: {uri.Host}, HTTP {(int)response.StatusCode}, {read} B; " + (valid ? "контент подтверждён" : "ответ не является требуемым медиаконтентом"), watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { token.ThrowIfCancellationRequested(); return new(false, "Проверка реального медиаресурса не завершилась"); }
    }

    private static async Task<Uri?> DiscoverAsync(string kind, CancellationToken token,HttpClient client,bool useCache)
    {
        await DiscoveryGate.WaitAsync(token);
        try
        {
            if (useCache&&Cache.TryGetValue(kind, out var cached) && cached.Expiry > DateTimeOffset.UtcNow) return cached.Uri;
            var page = kind == "telegram-media" ? "https://t.me/s/telegram" : "https://www.youtube.com/watch?v=jNQXAC9IVRw";
            using var request = new HttpRequestMessage(HttpMethod.Get, page);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VortexPass/0.0.7");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[768 * 1024]; var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), token);
                if (read == 0) break; count += read;
            }
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, count);
            IEnumerable<string> candidates;
            if (kind == "telegram-media") candidates = Regex.Matches(text, @"https://[^\s""'<>]+", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => System.Net.WebUtility.HtmlDecode(m.Value));
            else candidates = Regex.Matches(text, "\"url\"\\s*:\\s*(\"(?:\\\\.|[^\"])*\")", RegexOptions.None, TimeSpan.FromSeconds(1))
                .Select(m => JsonSerializer.Deserialize<string>(m.Groups[1].Value) ?? "");
            var uri = candidates.Select(s => Uri.TryCreate(s, UriKind.Absolute, out var parsed) ? parsed : null)
                .FirstOrDefault(u => u is not null && IsAllowedControlUri(kind, u));
            if (uri is not null&&useCache) Cache[kind] = (uri, DateTimeOffset.UtcNow.AddMinutes(5));
            return uri;
        }
        finally { DiscoveryGate.Release(); }
    }
    public static bool IsAllowedControlUri(string kind, Uri uri) => uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort &&
        (kind == "video" ? uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("videoplayback") :
         kind == "avatar" ? uri.Host.Equals("yt3.ggpht.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("yt3.googleusercontent.com", StringComparison.OrdinalIgnoreCase) :
         kind == "telegram-media" && (uri.Host.EndsWith(".cdn-telegram.org", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".telegramusercontent.com", StringComparison.OrdinalIgnoreCase)));
}
