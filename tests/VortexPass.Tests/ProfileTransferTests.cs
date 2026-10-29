using VortexPass.Core.Models;
using VortexPass.Core.Services;
internal static class ProfileTransferTests
{
    public static Task Run()
    {
        var source = new SavedConnectionProfile { Id=Guid.NewGuid().ToString(),Name="Видео и игры",Services=["telegramweb","gamefilter"],Games=["roblox"],
            Domains=["example.org"],Provider="Экотелеком",Level="aggressive",Tuning=3,Strategy=new(){Level="aggressive"},StrategyNetworkKey="private-network" };
        var json=ProfileTransferService.Export(source);var link=ProfileTransferService.Link(json);
        foreach(var text in new[]{json,link})
        {
            var copy=ProfileTransferService.Parse(text);
            if(copy.Id==source.Id||copy.Level!=source.Level||copy.Tuning!=3||!copy.Games.SequenceEqual(source.Games)||copy.Provider!=source.Provider||copy.Strategy is not null||!copy.RouteNeedsCheck)
                throw new Exception("Portable profile lost settings or imported foreign route");
        }
        if(json.Contains("private-network")||json.Contains("AppliedStrategy"))throw new Exception("Private evidence exported");
        void Reject(string text){try{ProfileTransferService.Parse(text);}catch(ArgumentException){return;}throw new Exception("Unsafe profile accepted");}
        Reject(json.Replace("\"siteRules\": []","\"siteRules\": [{\"command\":\"unsafe\"}]"));
        Reject(json.Replace("\"version\": 5","\"version\": 99"));
        Reject(json.Replace("\"domains\": [","\"domains\": [null,"));
        Reject(json.Replace("\"name\":", "\"command\":\"winws --unsafe\",\"name\":"));
        Reject(new string('x',65537));Reject("vortexpass-profile:%%invalid");
        var hosts=RouteProbePolicy.DnsHostsForServices(["telegram","roblox"]);
        if(hosts.Contains("api.devices.cloudflare.com")||hosts.Contains("www.youtube.com"))throw new Exception("Unselected DNS blocks profile tests");
        var command=WinwsProfileComposer.Compose(new(){Discord=true},"aggressive",new(),null,null,3);
        var dns=command.Split(" --new").Where(rule=>rule.Contains("list-doh"));
        if(dns.Any(rule=>rule.Contains("repeats=18")||!rule.Contains("cutoff=n3")))throw new Exception("Global tuning leaks into resolver route");
        return Task.CompletedTask;
    }
}
