using System.Text.Json;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public sealed record CheckedDraftRecommendation(string Composition, string Provider, string Network,
    DateTimeOffset Expires, AdvisorRecommendation Recommendation);

public static class DraftRecommendationPolicy
{
    public static string Composition(SavedConnectionProfile p) => JsonSerializer.Serialize(new
    { p.Id, p.Level, p.Tuning, p.RobloxAssetsMode, p.TelegramWebRoute, Services = p.Services.Order().ToArray(), Games = p.Games.Order().ToArray(), Domains = p.Domains.Order().ToArray(), p.SiteRules });
    public static void Validate(SavedConnectionProfile p, CheckedDraftRecommendation tested, string currentNetwork, DateTimeOffset now)
    {
        if (tested.Network != currentNetwork || tested.Expires < now || tested.Recommendation.ProbeVersion != RouteProbePolicy.Version ||
            tested.Provider != AdvisorStrategyPolicy.NormalizeProvider(p.Provider) || tested.Composition != Composition(p) ||
            tested.Recommendation.Strategy.Tuning != p.Tuning)
            throw new ArgumentException("Профиль или сеть изменились. Повторите подбор маршрута.");
        AdvisorStrategyPolicy.Validate(tested.Recommendation.Strategy, p.Level);
    }
}
