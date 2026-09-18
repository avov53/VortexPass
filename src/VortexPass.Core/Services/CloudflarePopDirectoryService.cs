using System.Text.Json;
using System.Text.RegularExpressions;

namespace VortexPass.Core.Services;

public sealed record CloudflarePopLocation(string Code, string City, string Country, string? Status = null)
{
    public string DisplayName => $"{Code} — {City}, {Country}";
}

public sealed class CloudflarePopDirectoryService
{
    public const string StatusApiUrl = "https://www.cloudflarestatus.com/api/v2/components.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);
    private static readonly Regex LocationName = new(
        @"^(?<place>.+)\s+-\s+\((?<code>[A-Z0-9]{3})\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _cachePath;
    private readonly HttpClient _http;

    public CloudflarePopDirectoryService(string cacheDirectory, HttpClient http)
    {
        _cachePath = Path.Combine(cacheDirectory, "cloudflare-pop-directory.json");
        _http = http;
    }

    public async Task<CloudflarePopLocation?> ResolveAsync(string? code, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null) return null;

        var cachedJson = await ReadCacheAsync(cancellationToken);
        var cached = ParseLocations(cachedJson).GetValueOrDefault(normalized);
        var cacheFresh = File.Exists(_cachePath) &&
                         !ShouldRefresh(File.GetLastWriteTimeUtc(_cachePath), DateTimeOffset.UtcNow);
        if (cached is not null && cacheFresh) return cached;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, StatusApiUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            var locations = ParseLocations(json);
            if (locations.Count > 0)
            {
                await WriteCacheAsync(json, cancellationToken);
                return locations.GetValueOrDefault(normalized) ?? cached;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return cached;
    }

    public static IReadOnlyDictionary<string, CloudflarePopLocation> ParseLocations(string? json)
    {
        var result = new Dictionary<string, CloudflarePopLocation>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var component in components.EnumerateArray())
            {
                if (!component.TryGetProperty("name", out var rawName)) continue;
                var name = rawName.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var match = LocationName.Match(name);
                if (!match.Success) continue;
                var place = match.Groups["place"].Value.Trim();
                var separator = place.LastIndexOf(", ", StringComparison.Ordinal);
                if (separator <= 0 || separator >= place.Length - 2) continue;
                var code = match.Groups["code"].Value.ToUpperInvariant();
                var city = place[..separator].Trim();
                var country = place[(separator + 2)..].Trim();
                var status = component.TryGetProperty("status", out var rawStatus) ? rawStatus.GetString() : null;
                result[code] = new(code, city, country, status);
            }
        }
        catch (JsonException) { }
        return result;
    }

    public static bool ShouldRefresh(DateTimeOffset cachedAt, DateTimeOffset now) => now - cachedAt >= CacheTtl;

    private async Task<string?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        try { return File.Exists(_cachePath) ? await File.ReadAllTextAsync(_cachePath, cancellationToken) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task WriteCacheAsync(string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"pop-directory-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            File.Move(temporary, _cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string? NormalizeCode(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        return normalized is { Length: 3 } && normalized.All(char.IsLetterOrDigit) ? normalized : null;
    }
}
