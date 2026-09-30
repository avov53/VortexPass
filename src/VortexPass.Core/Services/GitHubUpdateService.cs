using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public sealed class GitHubUpdateService
{
    public const string Repository = "avov53/VortexPass";
    public const string ZipAssetName = "VortexPass-win-x64.zip";
    public const string HashAssetName = "VortexPass-win-x64.zip.sha256";

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public GitHubUpdateService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VortexPass-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateState.Failed, Error: "В репозитории пока нет опубликованного релиза.");
            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(_json, cancellationToken);
            if (release is null || !Version.TryParse(release.TagName.TrimStart('v', 'V'), out var version))
                return new(UpdateState.Failed, Error: "У последнего релиза некорректный тег версии.");

            var zip = release.Assets.FirstOrDefault(x => x.Name == ZipAssetName);
            var hash = release.Assets.FirstOrDefault(x => x.Name == HashAssetName);
            if (zip is null || hash is null)
                return new(UpdateState.Failed, Error: $"Релиз v{version} должен содержать {ZipAssetName} и {HashAssetName}.");

            var releaseInfo = new ReleaseInfo(release.TagName, version, new Uri(zip.BrowserDownloadUrl), new Uri(hash.BrowserDownloadUrl), release.PublishedAt);
            return version > currentVersion
                ? new(UpdateState.Available, releaseInfo)
                : new(UpdateState.Current, releaseInfo);
        }
        catch (HttpRequestException ex)
        {
            var state = ClassifyTransportFailure(ex);
            var error = state == UpdateState.Unavailable
                ? "Проверка обновлений временно недоступна: DNS или соединение с GitHub ещё не готовы. VortexPass повторит проверку после запуска защищённого DNS."
                : $"GitHub вернул ошибку при проверке обновлений: {ex.Message}";
            return new(state, Error: error);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(UpdateState.Failed, Error: ex.Message);
        }
    }

    public static UpdateState ClassifyTransportFailure(HttpRequestException exception) =>
        exception.StatusCode.HasValue ? UpdateState.Failed : UpdateState.Unavailable;

    public async Task<DownloadedRelease> DownloadAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "VortexPass-update", release.Version.ToString());
        Directory.CreateDirectory(directory);
        var zipPath = Path.Combine(directory, ZipAssetName);
        var hashText = await _http.GetStringAsync(release.HashUrl, cancellationToken);
        var expected = hashText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidDataException("SHA-256 файл релиза пустой или повреждён.");

        if (File.Exists(zipPath) && await HashVerifier.MatchesAsync(zipPath, expected, cancellationToken))
            return new DownloadedRelease(zipPath, release.Version, expected);

        using (var response = await _http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long completed = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                completed += read;
                if (total is > 0) progress?.Report((double)completed / total.Value);
            }
        }

        if (string.IsNullOrWhiteSpace(expected) || !await HashVerifier.MatchesAsync(zipPath, expected, cancellationToken))
        {
            File.Delete(zipPath);
            throw new InvalidDataException("Контрольная сумма скачанного обновления не совпала.");
        }
        return new DownloadedRelease(zipPath, release.Version, expected);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; init; } = string.Empty;
        [JsonPropertyName("published_at")] public DateTimeOffset PublishedAt { get; init; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}

public sealed record DownloadedRelease(string ZipPath, Version Version, string Sha256);
