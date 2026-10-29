using VortexPass.Core.Models;
namespace VortexPass.Core.Services;

public static class YoutubeRoutePersistence
{
    public static void Update(AppSettings settings,SavedConnectionProfile profile,AdvisorStrategySpec baseline,AdvisorStrategySpec candidate,string network,string signature,bool mediaConfirmed)
    {
        YoutubeOnlyPolicy.Validate(baseline,candidate);
        var proof=new YoutubeRouteVerification(YoutubeOnlyPolicy.ProbeVersion,network,candidate.Key,signature,DateTimeOffset.UtcNow,true,mediaConfirmed,SearchVersion:candidate.YouTubeParameters is null?0:YoutubeParametersPolicy.SearchVersion);
        profile.Strategy=candidate;profile.YoutubeVerification=proof;profile.StrategyNetworkKey=network;profile.ProbeVersion=RouteProbePolicy.Version;
        profile.NetworkRoutes[network]=profile.NetworkRoutes.TryGetValue(network,out var old)?old with{Strategy=candidate,YoutubeVerification=proof}:new(candidate,profile.Provider,profile.ProbeVersion,DateTimeOffset.UtcNow,profile.WarpProofKey,proof);
        settings.LocalAdvisor.AppliedStrategy=candidate;settings.LocalAdvisor.AppliedNetworkKey=network;settings.LocalAdvisor.AppliedProbeVersion=RouteProbePolicy.Version;settings.LocalAdvisor.AppliedMode=profile.Mode;
    }
}
