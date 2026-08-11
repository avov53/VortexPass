namespace VortexPass.Core.Models;

public enum UpdateState
{
    NotChecked,
    Checking,
    Current,
    Available,
    Downloading,
    Failed
}

public sealed record ReleaseInfo(string Tag, Version Version, Uri ZipUrl, Uri HashUrl, DateTimeOffset PublishedAt);

public sealed record UpdateCheckResult(UpdateState State, ReleaseInfo? Release = null, string? Error = null);
