namespace VortexPass.Core.Services;

public static class MobileDnsPolicy
{
    // DNS blocking is not specific to mobile operators. Keep the guard active
    // for every running winws profile so browsers can use the Windows resolver
    // without requiring their own DoH setting. The official WARP service may
    // update its virtual-adapter DNS even in local proxy mode; a second local
    // resolver then races it and can make both PAC routes and normal lookups
    // fail. Therefore DNS Guard must stay out of every connected WARP mode.
    public static bool ShouldRun(bool protectionActive, string? compatibilityMode, bool warpOwnsSystemDns) =>
        protectionActive && !warpOwnsSystemDns;

    public static bool WarpOwnsSystemDns(bool connected, string? effectiveRoutingMode) => connected;

    public static bool CanPinEmergencyHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && HostsFileManager.IsTelegramWebFallbackHost(host);

    public static bool MustDiscardLegacyRoutes(int profileSettingsVersion) => profileSettingsVersion < 7;
}
