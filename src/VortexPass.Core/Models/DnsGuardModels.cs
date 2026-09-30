namespace VortexPass.Core.Models;

public sealed class DnsAdapterSnapshot
{
    public int InterfaceIndex { get; set; }
    public string InterfaceAlias { get; set; } = string.Empty;
    public string InterfaceGuid { get; set; } = string.Empty;
    public string[] DnsServersV4 { get; set; } = [];
    public string[] DnsServersV6 { get; set; } = [];
    public bool AutomaticV4 { get; set; }
    public bool AutomaticV6 { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
}

public sealed record DnsGuardStatus(
    string State,
    string Detail,
    string? Resolver = null,
    string? Adapter = null,
    int? InterfaceIndex = null,
    DateTimeOffset? CheckedAt = null)
{
    public bool Ready => State.Equals("ready", StringComparison.OrdinalIgnoreCase);
}
