using System.Text.Json;

namespace VortexPass.Core.Models;

// Client-editable fields only. Legacy metadata is deliberately ignored.
public sealed class SaveConnectionRequest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RobloxAssetsMode { get; set; } = "auto";
    public string TelegramWebRoute { get; set; } = "warpProxy";
    public string Provider { get; set; } = "";
    public List<string> Services { get; set; } = [];
    public List<string> Games { get; set; } = [];
    public List<string> Domains { get; set; } = [];
    public List<SiteRule> SiteRules {get;set;}=[];
    public string Level { get; set; } = "medium";
    public int Tuning { get; set; }

    public static SavedConnectionProfile Read(JsonElement input)
    {
        if (input.GetRawText().Length > 64 * 1024) throw new ArgumentException("Профиль слишком большой.");
        SaveConnectionRequest request;
        try { request = input.Deserialize<SaveConnectionRequest>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Не удалось прочитать профиль."); }
        catch (JsonException) { throw new ArgumentException("Не удалось прочитать настройки профиля. Проверьте название, сервисы и уровень."); }
        return new SavedConnectionProfile
        {
            RobloxAssetsMode = request.RobloxAssetsMode,
            TelegramWebRoute = request.TelegramWebRoute,
            Id = string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString("D") : request.Id,
            Name = request.Name, Provider = request.Provider, Services = request.Services, Games = request.Games, Domains = request.Domains,
            Level = request.Level, Tuning = request.Tuning, SiteRules=request.SiteRules
        };
    }
}
