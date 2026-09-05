using System.Net;

namespace VortexPass.Core.Services;

public sealed record RobloxThumbnailResolution(IPAddress? Address, string Detail)
{
    public bool Succeeded => Address is not null;
}

public sealed class RobloxThumbnailResolver
{
    public const string ThumbnailHost = "tr.rbxcdn.com";
    private static readonly string[] RbxCdnHosts = ["t1.rbxcdn.com", "t2.rbxcdn.com", "images.rbxcdn.com"];
    public static readonly string[] AllRobloxCdnHosts =
    [
        "tr.rbxcdn.com",
        "images.rbxcdn.com",
        "t0.rbxcdn.com",
        "t1.rbxcdn.com",
        "t2.rbxcdn.com",
        "t3.rbxcdn.com",
        "t4.rbxcdn.com",
        "t5.rbxcdn.com",
        "t6.rbxcdn.com",
        "t7.rbxcdn.com",
        "c0.rbxcdn.com",
        "c1.rbxcdn.com",
        "c2.rbxcdn.com",
        "c3.rbxcdn.com",
        "c4.rbxcdn.com",
        "c5.rbxcdn.com",
        "c6.rbxcdn.com",
        "c7.rbxcdn.com",
        "static.rbxcdn.com",
        "js.rbxcdn.com",
        "css.rbxcdn.com",
        "setup.rbxcdn.com"
    ];
    private readonly DnsOverrideService _dnsOverrides;

    public RobloxThumbnailResolver(DnsOverrideService? dnsOverrides = null)
    {
        _dnsOverrides = dnsOverrides ?? new DnsOverrideService();
    }

    public Task<RobloxThumbnailResolution> ResolveAsync(CancellationToken cancellationToken) =>
        ResolveAsync(null, cancellationToken);

    public async Task<RobloxThumbnailResolution> ResolveAsync(string? preferredProvider = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _dnsOverrides.ResolveBestAsync(
                ThumbnailHost,
                verifyTls: false,
                timeout: TimeSpan.FromSeconds(4),
                preferredProvider: preferredProvider,
                cancellationToken: cancellationToken);

            if (result.Succeeded && result.Address is not null)
            {
                return new(result.Address, $"{result.Source} OK: {result.Address}");
            }

            // tr.rbxcdn.com is served by Roblox image CDN nodes (*.rbxcdn.com).
            // Resolving active image CDN endpoints (t1.rbxcdn.com, images.rbxcdn.com)
            // gives the exact edge IP that carries the valid *.rbxcdn.com SSL certificate.
            foreach (var cdnHost in RbxCdnHosts)
            {
                var fallback = await _dnsOverrides.ResolveBestAsync(
                    cdnHost,
                    verifyTls: false,
                    timeout: TimeSpan.FromSeconds(4),
                    preferredProvider: preferredProvider,
                    cancellationToken: cancellationToken);

                if (fallback.Succeeded && fallback.Address is not null)
                {
                    return new(fallback.Address, $"{fallback.Source} ({cdnHost}) OK: {fallback.Address}");
                }
            }

            return new(null, result.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(null, ex.Message);
        }
    }
}
