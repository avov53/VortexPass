using System.Net;
using System.Net.Sockets;

namespace VortexPass.Core.Services;

public static class RouteProbePolicy
{
    public const int Version = 15;
    public static readonly string[] RequiredDnsHosts = ["www.youtube.com", "i.ytimg.com", "yt3.ggpht.com", "discord.com",
        "updates.discord.com", "stable.dl2.discordapp.net", "gateway.discord.gg", "web.telegram.org",
        "kws1.web.telegram.org", "kws2.web.telegram.org", "kws3.web.telegram.org", "kws4.web.telegram.org", "kws5.web.telegram.org",
        "api.cloudflareclient.com", "api.devices.cloudflare.com"];
    public static string[] DnsHostsForServices(IEnumerable<string> services) => services.SelectMany(service => service switch
    {
        "youtube" => new[] { "www.youtube.com", "i.ytimg.com", "yt3.ggpht.com" },
        "discord" or "discord-voice" => new[] { "discord.com", "updates.discord.com", "stable.dl2.discordapp.net", "gateway.discord.gg" },
        "telegram" => new[] { "web.telegram.org" }.Concat(TelegramEndpointRegistry.NamedDataCenters.SelectMany(name=>new[]{name+".web.telegram.org",name+"-1.web.telegram.org"})).ToArray(),
        "roblox" => new[] { "www.roblox.com", "thumbnails.roblox.com", "assetdelivery.roblox.com" },
        _ => service.StartsWith("domain:") ? new[] { service[7..] } : Array.Empty<string>()
    }).Distinct().ToArray();
    public static async Task<IReadOnlyList<string>> MissingWindowsDnsAsync(CancellationToken token, IEnumerable<string>? hosts = null)
    {
        var results = await Task.WhenAll((hosts ?? RequiredDnsHosts).Select(async host =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try { return (Host: host, Ok: (await Dns.GetHostAddressesAsync(host, timeout.Token)).Length > 0); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); return (Host: host, Ok: false); }
        }));
        return results.Where(x => !x.Ok).Select(x => x.Host).ToArray();
    }
    public static bool IsImage(int status, string? mediaType, ReadOnlySpan<byte> bytes) => status is 200 or 206 &&
        mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true &&
        (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) || bytes.StartsWith(new byte[] { 137, 80, 78, 71 }) ||
         bytes.StartsWith("GIF8"u8) || bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ||
         bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8) && (mediaType.Contains("avif") || mediaType.Contains("heif")));
    public static bool IsVideo(int status, string? mediaType, ReadOnlySpan<byte> bytes) => status is 200 or 206 &&
        mediaType is not null && (mediaType.StartsWith("video/") || mediaType.StartsWith("audio/") || mediaType == "application/octet-stream") &&
        (bytes.Length >= 8 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8) || bytes.StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }));
    public static bool IsUsablePage(int status, string? mediaType, int received) => status is 200 or 206 && received > 0 &&
        mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
}
