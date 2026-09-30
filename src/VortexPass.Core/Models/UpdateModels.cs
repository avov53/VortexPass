namespace VortexPass.Core.Models;

public enum UpdateState
{
    NotChecked,
    Checking,
    Current,
    Available,
    AvailablePrompt,
    Preparing,
    Deferred,
    Installing,
    Downloading,
    Unavailable,
    Failed
}

public sealed record ReleaseInfo(string Tag, Version Version, Uri ZipUrl, Uri HashUrl, DateTimeOffset PublishedAt);

public sealed record UpdateCheckResult(UpdateState State, ReleaseInfo? Release = null, string? Error = null);

public sealed class PendingUpdateState
{
    public string Status { get; set; } = "none";
    public string Version { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string ZipPath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string RestartPath { get; set; } = string.Empty;
    public string? LastError { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
