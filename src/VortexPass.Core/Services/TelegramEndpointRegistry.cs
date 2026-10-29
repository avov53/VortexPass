namespace VortexPass.Core.Services;

// Official Web K dcConfigurator and MTProto TLS transport URI format.
public static class TelegramEndpointRegistry
{
    public const string HttpsFallback = "https://web.telegram.org/k/?http=1";
    public static readonly Uri[] Pages = [new("https://web.telegram.org/k/"), new("https://web.telegram.org/a/")];
    public static readonly string[] NamedDataCenters = ["pluto", "venus", "aurora", "vesta", "flora"];
    public static readonly string[] MessageHosts = Enumerable.Range(1, 5)
        .SelectMany(dc => new[] { $"kws{dc}.web.telegram.org", $"zws{dc}.web.telegram.org", $"{NamedDataCenters[dc - 1]}.web.telegram.org" }).ToArray();
    public static readonly string[] MediaHosts = MessageHosts.Select(h => h.Replace(".web.telegram.org", "-1.web.telegram.org")).ToArray();
    public static readonly string[] TransportHosts = MessageHosts.Concat(MediaHosts).ToArray();
    public static bool IsTransport(string host) => TransportHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ||
        host.Equals("kws.web.telegram.org", StringComparison.OrdinalIgnoreCase) || host.Equals("zws.web.telegram.org", StringComparison.OrdinalIgnoreCase);
    public static bool IsMediaTransport(string host) => MediaHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
    public static Uri WebSocket(string host) => new($"wss://{host}/apiws");
    public static bool AllDataCentersReachable(IReadOnlyList<bool> results) => results.Count == 15 &&
        Enumerable.Range(0, 5).All(dc => results.Skip(dc * 3).Take(3).Any(ok => ok));
}
