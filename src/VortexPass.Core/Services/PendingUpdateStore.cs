using System.Text.Json;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public sealed class PendingUpdateStore
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public PendingUpdateStore(string appDataDirectory)
    {
        Directory = appDataDirectory;
        StatePath = Path.Combine(appDataDirectory, "update-state.json");
    }

    public string Directory { get; }
    public string StatePath { get; }

    public async Task<PendingUpdateState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath)) return null;
        await using var stream = File.OpenRead(StatePath);
        return await JsonSerializer.DeserializeAsync<PendingUpdateState>(stream, _json, cancellationToken);
    }

    public async Task SaveAsync(PendingUpdateState state, CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        if (state.CreatedUtc == default) state.CreatedUtc = state.UpdatedUtc;
        await using var stream = File.Create(StatePath);
        await JsonSerializer.SerializeAsync(stream, state, _json, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(StatePath)) File.Delete(StatePath);
        return Task.CompletedTask;
    }
}
