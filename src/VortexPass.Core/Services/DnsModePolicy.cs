namespace VortexPass.Core.Services;

public static class DnsModePolicy
{
    // Missing/invalid settings use Google UDP with TCP fallback. Explicit choices survive upgrades.
    public static string Normalize(string? mode) => mode switch
    {
        "system" => "system",
        "google-udp" => "google-udp",
        "doh" => "doh",
        _ => "google-udp"
    };
    public static bool UsesGuard(string? mode) => Normalize(mode) != "system";
}
