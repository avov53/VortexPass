using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public static class UnifiedProfileMigration
{
    public static void Apply(AppSettings settings)
    {
        foreach (var profile in settings.SavedConnections)
        {
            if (profile.Mode == "mobile" && profile.Strategy is { } strategy)
                profile.Strategy = strategy with { RouteTemplate = "narrow" };
            profile.Mode = "standard";
            profile.Favorite = false;
            profile.FollowNetworkMode = false;
        }
        if (settings.LocalAdvisor.AppliedMode == "mobile" && settings.LocalAdvisor.AppliedStrategy is { } applied)
            settings.LocalAdvisor.AppliedStrategy = applied with { RouteTemplate = "narrow" };
        settings.LocalAdvisor.AppliedMode = "standard";
        settings.CompatibilityMode = "standard";
        foreach (var network in settings.NetworkCompatibilityProfiles ?? []) network.Mode = "standard";
        // History keeps evidence/probe versions: old recommendations require a new test.
        settings.FavoriteProfiles.Clear();
    }
}
