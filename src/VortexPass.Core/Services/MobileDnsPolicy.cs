namespace VortexPass.Core.Services;

public static class MobileDnsPolicy
{
    // DNS blocking is not specific to mobile operators. Keep the guard active
    // for every running winws profile so browsers can use the Windows resolver
    // without requiring their own DoH setting. The official WARP service may
    // own system DNS in full-tunnel/DNS-only mode. Local proxy mode serves only
    // configured applications, so its presence alone does not replace Windows DNS.
    public static bool ShouldRun(bool protectionActive, string? compatibilityMode, bool warpOwnsSystemDns) =>
        protectionActive && !warpOwnsSystemDns;

    public static bool WarpOwnsSystemDns(bool connected, string? effectiveRoutingMode) => connected && effectiveRoutingMode is "full" or "dnsOnly";

    public static bool CanPinEmergencyHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && HostsFileManager.IsTelegramWebFallbackHost(host);

    public static bool MustDiscardLegacyRoutes(int profileSettingsVersion) => profileSettingsVersion < 7;
}
