using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public static class WinwsProfileComposer
{
    public const string FlowsealCommit = "61d7596c7ce82b6f320e5c79ff4598856f4ae7d3";

    public static string Compose(BypassProfileSettings profiles, string level) =>
        Compose(profiles, level, new GameFilterSettings(), null, null);

    public static string Compose(
        BypassProfileSettings profiles,
        string level,
        GameFilterSettings gameFilter,
        string? customBypassListPath,
        string? exclusionListPath)
    {
        level = NormalizeLevel(level);
        var includeYouTubeWeb = profiles.YouTube || profiles.Web;
        var includeDiscordWeb = profiles.Discord || profiles.Web;
        var includeTelegramWeb = profiles.Web;
        var includeTelegramDesktop = profiles.Telegram;
        var includeDiscordVoice = profiles.Discord || profiles.Web;
        var includePhasmophobia = profiles.GameFilter && gameFilter.SelectedGames.Contains("phasmophobia", StringComparer.OrdinalIgnoreCase);
        var includeRoblox = profiles.GameFilter && gameFilter.SelectedGames.Contains("roblox", StringComparer.OrdinalIgnoreCase);
        var gameProtocol = NormalizeGameProtocol(gameFilter.Protocol);
        var includeGameTcp = includePhasmophobia && gameProtocol is "tcp" or "all";
        var includeGameUdp = includePhasmophobia && gameProtocol is "udp" or "all";

        if (!includeYouTubeWeb && !includeDiscordWeb && !includeTelegramWeb && !includeTelegramDesktop && !includePhasmophobia && !includeRoblox && string.IsNullOrWhiteSpace(customBypassListPath))
            return string.Empty;

        var tcpPorts = new SortedSet<int> { 80, 443 };
        var udpPorts = new List<string> { "443" };
        if (includeDiscordVoice)
        {
            foreach (var port in new[] { 2053, 2083, 2087, 2096, 8443 }) tcpPorts.Add(port);
            udpPorts.Add("19294-19344");
            udpPorts.Add("50000-50100");
        }
        if (includeGameTcp) foreach (var port in new[] { 5055, 5056, 5058 }) tcpPorts.Add(port);
        if (includeGameUdp) udpPorts.Add("5055-5058");

        var capture = new List<string>
        {
            $"--wf-tcp={string.Join(',', tcpPorts)}",
            $"--wf-udp={string.Join(',', udpPorts)}"
        };
        var rules = new List<string>();

        if (includeYouTubeWeb)
            AddDomainRules(rules, "files\\list-youtube.txt", level, includeHttp: true, exclusionListPath);

        if (includeDiscordWeb)
            AddDomainRules(rules, "files\\list-discord.txt", level, includeHttp: true, exclusionListPath);

        if (includeTelegramWeb || includeTelegramDesktop)
            AddDomainRules(rules, "files\\list-telegram.txt", level, includeHttp: false, exclusionListPath);

        if (includeRoblox)
            AddDomainRules(rules, "files\\list-roblox.txt", level, includeHttp: true, exclusionListPath);

        if (!string.IsNullOrWhiteSpace(customBypassListPath))
            AddDomainRules(rules, customBypassListPath, level, includeHttp: true, exclusionListPath);

        if (includeDiscordVoice)
        {
            rules.Add($"--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun " +
                      "--dpi-desync=fake --dpi-desync-fake-discord=files\\ACTIVE_DISCORD_UDP.bin " +
                      "--dpi-desync-fake-stun=files\\ACTIVE_DISCORD_UDP.bin " +
                      $"--dpi-desync-repeats={Repeats(level)} --new");
            rules.Add("--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media " +
                      TcpStrategy(level, "681", "files\\tls_clienthello_www_google_com.bin") + ExclusionOption(exclusionListPath) + " --new");
        }

        if (includeGameUdp)
        {
            rules.Add($"--filter-udp=5055-5058 --dpi-desync=fake --dpi-desync-any-protocol " +
                      $"--dpi-desync-repeats={Repeats(level)} --new");
        }
        if (includeGameTcp)
        {
            rules.Add("--filter-tcp=5055,5056,5058 --dpi-desync-any-protocol " +
                      TcpStrategy(level, "681", "files\\tls_clienthello_www_google_com.bin") + " --new");
        }

        return $"{string.Join(' ', capture)} {string.Join(' ', rules)}";
    }

    private static void AddDomainRules(List<string> rules, string hostlist, string level, bool includeHttp, string? exclusionListPath)
    {
        var hostlistOption = $"--hostlist={QuotePath(hostlist)}";
        var exclusionOption = ExclusionOption(exclusionListPath);
        if (includeHttp)
        {
            rules.Add($"--filter-tcp=80 {hostlistOption} " +
                      TcpStrategy(level, "568", "files\\tls_clienthello_4pda_to.bin") + exclusionOption + " --new");
        }

        rules.Add($"--filter-tcp=443 {hostlistOption} " +
                  TcpStrategy(level, "681", "files\\tls_clienthello_www_google_com.bin") + exclusionOption + " --new");
        rules.Add($"--filter-udp=443 {hostlistOption} --filter-l7=quic --dpi-desync=fake " +
                  $"--dpi-desync-repeats={Repeats(level)} --dpi-desync-fake-quic=files\\quic_initial_www_google_com.bin{exclusionOption} --new");
    }

    private static string QuotePath(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
    private static string ExclusionOption(string? path) => string.IsNullOrWhiteSpace(path) ? string.Empty : $" --hostlist-exclude={QuotePath(path)}";

    private static string TcpStrategy(string level, string overlap, string pattern) => level switch
    {
        "light" => "--dpi-desync=fake --dpi-desync-repeats=2 --dpi-desync-autottl=2",
        "moderate" => $"--dpi-desync=fake,fakedsplit --dpi-desync-repeats=4 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls={pattern}",
        "medium" => $"--dpi-desync=multisplit --dpi-desync-split-seqovl={overlap} --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern={pattern}",
        "complex" => $"--dpi-desync=fake,multisplit --dpi-desync-repeats=6 --dpi-desync-split-seqovl={overlap} --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern={pattern}",
        "aggressive" => $"--dpi-desync=fake,fakedsplit --dpi-desync-repeats=12 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls=files\\stun.bin --dpi-desync-fake-tls={pattern}",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    private static int Repeats(string level) => level switch
    {
        "light" => 2,
        "moderate" => 4,
        "medium" => 6,
        "complex" => 8,
        "aggressive" => 12,
        _ => 6
    };

    public static string NormalizeLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "light" or "moderate" or "medium" or "complex" or "aggressive" => level.ToLowerInvariant(),
        _ => "medium"
    };

    public static string NormalizeGameProtocol(string? protocol) => protocol?.ToLowerInvariant() switch
    {
        "udp" or "tcp" or "all" => protocol.ToLowerInvariant(),
        _ => "all"
    };
}
