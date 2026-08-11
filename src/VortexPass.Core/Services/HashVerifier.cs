using System.Security.Cryptography;

namespace VortexPass.Core.Services;

public static class HashVerifier
{
    public static async Task<bool> MatchesAsync(string filePath, string expectedHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return false;
        await using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedHash.Trim().ToUpperInvariant(), StringComparison.OrdinalIgnoreCase);
    }
}
