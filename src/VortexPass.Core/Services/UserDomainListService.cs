using System.Globalization;
using System.Net;

namespace VortexPass.Core.Services;

public sealed record RuntimeDomainLists(string? BypassPath, string? ExclusionPath, int BypassCount, int ExclusionCount);

public sealed class UserDomainListService
{
    private readonly string _directory;

    public UserDomainListService(string appDataDirectory) => _directory = Path.Combine(appDataDirectory, "lists");

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? values)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values ?? [])
        {
            var candidate = raw.Trim().TrimEnd('.');
            if (candidate.Length == 0) continue;
            if (candidate.Contains("://", StringComparison.Ordinal) || candidate.Contains('/') || candidate.Contains('\\'))
                throw new FormatException($"Укажите только домен без протокола и пути: {raw}");
            if (IPAddress.TryParse(candidate, out _))
                throw new FormatException($"IP-адреса в этом списке не поддерживаются: {raw}");

            string ascii;
            try { ascii = new IdnMapping().GetAscii(candidate).ToLowerInvariant(); }
            catch (ArgumentException) { throw new FormatException($"Некорректный домен: {raw}"); }

            if (ascii.Length > 253 || ascii.Split('.').Length < 2 || ascii.Split('.').Any(label =>
                    label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-') ||
                    label.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-'))))
                throw new FormatException($"Некорректный домен: {raw}");
            result.Add(ascii);
        }
        return result.ToList();
    }

    public async Task<RuntimeDomainLists> WriteAsync(IEnumerable<string>? bypass, IEnumerable<string>? exclusions, CancellationToken cancellationToken = default)
    {
        var normalizedBypass = Normalize(bypass);
        var normalizedExclusions = Normalize(exclusions);
        Directory.CreateDirectory(_directory);
        var bypassPath = Path.Combine(_directory, "list-general-user.txt");
        var exclusionPath = Path.Combine(_directory, "list-exclude-user.txt");
        await File.WriteAllLinesAsync(bypassPath, normalizedBypass, cancellationToken);
        await File.WriteAllLinesAsync(exclusionPath, normalizedExclusions, cancellationToken);
        return new(normalizedBypass.Count == 0 ? null : bypassPath, normalizedExclusions.Count == 0 ? null : exclusionPath,
            normalizedBypass.Count, normalizedExclusions.Count);
    }
}
