using System.Text.Json;
using VortexPass.Core.Models;
using VortexPass.Core.Services;

internal static class WarpTransportTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run()
    {
        var folder=Path.GetFullPath(Path.Combine("artifacts","warp-transport-tests",Guid.NewGuid().ToString("N")));
        var store=new AppSettingsStore(folder);
        const string legacy="{\"ProtocolPreference\":\"auto\",\"AutomaticTransport\":false,\"MasquePreference\":\"h3-with-h2-fallback\"}";
        await File.WriteAllTextAsync(Path.Combine(folder,"settings.json"),"{\"DnsMode\":\"doh\",\"Warp\":"+legacy+",\"PendingConnectionWarpRestore\":{\"Settings\":"+legacy+",\"WasConnected\":false}}");
        var loaded=await store.LoadAsync();
        Check(loaded.Warp.TransportSelection=="auto"&&loaded.Warp.LegacyTransportBackup?.AutomaticTransport==false,"Actual legacy conflict did not migrate to Auto with backup");
        Check(loaded.PendingConnectionWarpRestore!.Settings.TransportSelection=="auto","Pending restore reintroduces hidden manual mode");
        var candidates=WarpPreflight.Candidates(loaded,"fixture-client");
        Check(candidates.Count==3&&candidates[0]==new WarpTransportVariant("masque","h2-only","none"),"Loaded settings do not reach production HTTP/2 candidate factory");
        await store.SaveAsync(loaded);var restarted=await store.LoadAsync();
        Check(restarted.Warp.TransportSelection=="auto"&&restarted.Warp.LegacyTransportBackup==loaded.Warp.LegacyTransportBackup&&restarted.DnsMode=="doh","Migration was repeated or changed DNS on restart");
        foreach(var item in new[]{("masque","h2-only","masque-h2"),("masque","h3-only","masque-h3"),("masque","h3-with-h2-fallback","masque-h3-fallback"),("wireguard","h3-only","wireguard")})
        {var settings=new WarpSettings{ProtocolPreference=item.Item1,AutomaticTransport=false,MasquePreference=item.Item2};WarpTransportPolicy.Migrate(settings);Check(settings.TransportSelection==item.Item3&&WarpPreflight.Candidates(settings).Count==1,"Manual transport lost in migration");}
        var invalid=new WarpSettings{TransportSelection="broken"};Check(WarpTransportPolicy.Migrate(invalid) is not null&&invalid.TransportSelection=="auto","Invalid selection was not diagnosed and normalized");
        var updated=new WarpSettings{TransportSelection="masque-h3"};
        using(var message=JsonDocument.Parse("{\"protocol\":\"auto\",\"masque\":\"h3-only\"}"))WarpTransportPolicy.ApplyMessage(updated,message.RootElement);
        Check(updated.TransportSelection=="auto","Legacy protocol=auto still permits hidden manual selection");
        using(var message=JsonDocument.Parse("{\"masque\":\"h2-only\"}"))WarpTransportPolicy.ApplyMessage(updated,message.RootElement);
        Check(updated.TransportSelection=="masque-h2","Legacy MASQUE-only edit did not select an explicit manual mode");
        using(var message=JsonDocument.Parse("{\"dnsMode\":\"doh\"}"))WarpTransportPolicy.ApplyMessage(updated,message.RootElement);
        Check(updated.TransportSelection=="masque-h2","DNS edit changed transport");
        foreach(var failSave in new[]{false,true})
        {
            var current="old";var disk="old";var failed=false;
            try{await WarpSettingsTransaction.ExecuteAsync(_=>{current="new";if(!failSave)throw new IOException("CLI failed");return Task.CompletedTask;},_=>throw new IOException("disk failed"),_=>{current="old";disk="old";return Task.CompletedTask;},default);}
            catch(IOException){failed=true;}
            Check(failed&&current=="old"&&disk=="old","Failed transaction leaked partial state");
        }
        var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        var retry=WarpSettingsTransaction.SaveAndRetryAsync(()=>pending.Task,()=>{calls++;return Task.CompletedTask;});
        Check(calls==0&&!retry.IsCompleted,"Retry raced saving settings");pending.SetResult(false);
        Check(!await retry&&calls==0,"Failed save still started advisor");
        Check(await WarpSettingsTransaction.SaveAndRetryAsync(()=>Task.FromResult(true),()=>{calls++;return Task.CompletedTask;})&&calls==1,"Successful save failed to start exactly one retry");
    }
}
