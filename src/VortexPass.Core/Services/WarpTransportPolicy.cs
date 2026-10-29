using System.Text.Json;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public sealed record WarpLegacyTransportBackup(string ProtocolPreference,bool AutomaticTransport,string MasquePreference);
public static class WarpTransportPolicy
{
    public static readonly string[] Selections=["auto","masque-h2","masque-h3-fallback","masque-h3","wireguard"];
    public static string? Migrate(WarpSettings settings)
    {
        if(settings.TransportSelection is not null&&Selections.Contains(settings.TransportSelection))return null;
        settings.LegacyTransportBackup??=new(settings.ProtocolPreference,settings.AutomaticTransport,settings.MasquePreference);
        var invalid=settings.TransportSelection is not null;
        settings.TransportSelection=invalid?"auto":settings.ProtocolPreference switch
        {"wireguard"=>"wireguard","masque"=>FromMasque(settings.MasquePreference),_=>"auto"};
        settings.TransportSelectionSource=invalid?"normalization":"migration";
        return invalid?"Неизвестный выбор транспорта WARP заменён на Авто":"Старые настройки WARP перенесены: "+Label(settings);
    }
    public static void Normalize(AppSettings settings)
    {
        settings.Warp??=new();Migrate(settings.Warp);
        if(settings.PendingConnectionWarpRestore is { } pending)Migrate(pending.Settings);
    }
    public static string Selection(WarpSettings settings){Migrate(settings);return settings.TransportSelection!;}
    public static bool IsAutomatic(WarpSettings settings)=>Selection(settings)=="auto";
    public static WarpTransportVariant Resolve(WarpSettings settings)=>Selection(settings) switch
    {
        "wireguard"=>new("wireguard","h3-with-h2-fallback","none"),
        "masque-h3"=>new("masque","h3-only","none"),
        "masque-h3-fallback"=>new("masque","h3-with-h2-fallback","none"),
        _=>new("masque","h2-only","none")
    };
    public static string Label(WarpSettings settings)=>Selection(settings) switch
    {"auto"=>"Авто; сначала запрос HTTP/2 без DPI, затем усиленный обход WARP","masque-h2"=>"Ручной: HTTP/2","masque-h3"=>"Ручной: HTTP/3","masque-h3-fallback"=>"Ручной: HTTP/3 + fallback",_=>"Ручной: WireGuard"};
    private static string FromMasque(string? value)=>value switch{"h2-only"=>"masque-h2","h3-only"=>"masque-h3","h3-with-h2-fallback"=>"masque-h3-fallback",_=>"auto"};
    public static void ApplyMessage(WarpSettings settings,JsonElement root)
    {
        Migrate(settings);string? selection=null;
        if(root.TryGetProperty("transportSelection",out var chosen))
        {selection=chosen.GetString();if(selection is null||!Selections.Contains(selection))throw new ArgumentException("Некорректный транспорт WARP");}
        else if(root.TryGetProperty("protocol",out var protocol))
        {selection=protocol.GetString() switch{"auto"=>"auto","wireguard"=>"wireguard","masque"=>root.TryGetProperty("masque",out var raw)?FromMasque(raw.GetString()):"masque-h2",_=>throw new ArgumentException("Некорректный протокол WARP")};}
        else if(root.TryGetProperty("masque",out var masque))selection=FromMasque(masque.GetString());
        if(selection is not null){settings.TransportSelection=selection;settings.TransportSelectionSource="user";}
    }
}
