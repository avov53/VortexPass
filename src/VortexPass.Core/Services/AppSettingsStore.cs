using System.Text.Json;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public string DirectoryPath=>Path.GetDirectoryName(_settingsPath)!;

    public AppSettingsStore(string appDataDirectory)
    {
        Directory.CreateDirectory(appDataDirectory);
        _settingsPath = Path.Combine(appDataDirectory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        if (!File.Exists(_settingsPath)){settings=new();settings.Warp.TransportSelection="auto";}
        else {await using var stream=File.OpenRead(_settingsPath);settings=await JsonSerializer.DeserializeAsync<AppSettings>(stream,cancellationToken:cancellationToken)??new();}
        if(settings.StartProtectionOnStartup && !settings.SavedConnections.Any(p=>p.Id==settings.StartupConnectionId)){settings.StartProtectionOnStartup=false;settings.StartupProfileChoiceRequired=true;} WarpTransportPolicy.Normalize(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            if(settings.StartProtectionOnStartup && !settings.SavedConnections.Any(p=>p.Id==settings.StartupConnectionId)){settings.StartProtectionOnStartup=false;settings.StartupProfileChoiceRequired=true;} WarpTransportPolicy.Normalize(settings);
            var temporaryPath = _settingsPath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}

