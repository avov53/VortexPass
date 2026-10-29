using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public static class MobileRoutePolicy
{
    public static readonly string[] SystemWarpDirectDomains =
    [
        "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com",
        "googleusercontent.com", "youtubei.googleapis.com", "googleapis.com",
        "roblox.com", "rbxcdn.com"
    ];

    private static readonly Dictionary<string, string[]> ProxyDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["discord"] =
        [
            "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media",
            "cdn.discordapp.com", "media.discordapp.net", "gateway.discord.gg", "updates.discord.com"
        ],
        ["telegram"] =
        [
            "telegram.org", "web.telegram.org", "webk.telegram.org", "webz.telegram.org",
            "telesco.pe", "telegram-cdn.org", "cdn-telegram.org", "telegramusercontent.com"
        ]
    };

    public static bool AcceptsDirectRoute(ServiceHealthResult result) => result.Id.ToLowerInvariant() switch
    {
        // A TLS handshake or the HTML shell is not enough: Healthy is emitted
        // only after the API, preview/avatar and video probes returned data.
        "youtube" => result.State == ServiceHealthState.Healthy,
        // Partial Discord is reserved for an unconfirmed voice session. Web,
        // gateway and CDN have already succeeded in that state.
        "discord" => result.State is ServiceHealthState.Healthy or ServiceHealthState.Partial,
        "telegram" => result.State == ServiceHealthState.Healthy,
        _ => result.IsHealthy
    };

    public static IReadOnlyList<string> DomainsForFallback(IEnumerable<string> serviceIds) => serviceIds
        .Where(id => !id.Equals("youtube", StringComparison.OrdinalIgnoreCase))
        .SelectMany(id => ProxyDomains.GetValueOrDefault(id) ?? [])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static Uri? ProbeUri(string serviceId) => serviceId.ToLowerInvariant() switch
    {
        "discord" => new Uri("https://discord.com/api/v10/gateway"),
        "telegram" => new Uri("https://web.telegram.org/"),
        _ => null
    };

    public static string AlternateStrategy(string? current) =>
        WinwsProfileComposer.NormalizeMobileServiceStrategy(current) == "fallback" ? "primary" : "fallback";

    public static string PreferredStrategy(MobileServiceRouteState route)
    {
        var saved = route.LastSuccessfulStrategy;
        return string.IsNullOrWhiteSpace(saved)
            ? WinwsProfileComposer.NormalizeMobileServiceStrategy(route.Strategy)
            : WinwsProfileComposer.NormalizeMobileServiceStrategy(saved);
    }

    public static bool HasFreshSuccessfulStrategy(
        MobileServiceRouteState route,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        return !string.IsNullOrWhiteSpace(route.LastSuccessfulStrategy) &&
               route.LastConfirmedUtc.HasValue &&
               route.LastConfirmedUtc.Value >= now.Subtract(maximumAge) &&
               route.State == "confirmed" &&
               !route.AppliedRoute.StartsWith("warp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldTryAlternate(int consecutiveFailures) => consecutiveFailures >= 2;

    public static bool IsT2Provider(string? providerKey, string? providerName)
    {
        var value = $"{providerKey} {providerName}";
        return value.Contains("tele2", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("t2 mobile", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("t2 ru", StringComparison.OrdinalIgnoreCase);
    }
}
