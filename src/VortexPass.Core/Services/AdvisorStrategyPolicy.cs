using System.Text.Json;
using VortexPass.Core.Models;

namespace VortexPass.Core.Services;

public static class AdvisorStrategyPolicy
{
    public static readonly string[] Levels = ["light", "moderate", "medium", "complex", "aggressive"];
    public static string[] Blocks(string level) => level switch
    {
        "light" => ["baseline", "direct", "minimal", "flowseal-alt9"],
        "moderate" => ["baseline", "direct", "minimal", "split", "flowseal-alt9", "flowseal-general", "flowseal-alt2"],
        _ => ["baseline", "direct", "minimal", "split", "disorder", "flowseal-alt9", "flowseal-general", "flowseal-alt2", "flowseal-fake-tls"]
    };
    public static int MaxTcpRepeats(string level) => level switch
    { "light" => 2, "moderate" => 4, "medium" => 6, "complex" => 8, "aggressive" => 10, _ => 0 };
    public static string[] YoutubeBlocks(string level) => level == "light" ? [..Blocks(level), "youtube-segment", "personalized"] : [..Blocks(level), "youtube-tls", "youtube-segment", "personalized"];
    public static int MaxOverlap(string level) => level switch
    { "light" => 256, "moderate" => 568, "medium" => 681, "complex" => 873, "aggressive" => 1024, _ => 0 };

    public static bool CanReuseStored(AdvisorStrategySpec? spec, string level, string storedNetwork, string network, int version)
    {
        if (spec is null || string.IsNullOrWhiteSpace(network) || storedNetwork != network || version != RouteProbePolicy.Version) return false;
        try { Validate(spec, level); return true; }
        catch (ArgumentException) { return false; }
    }

    public static void Validate(AdvisorStrategySpec spec, string selectedLevel, bool allowExperimental = true)
    {
        if (!Levels.Contains(selectedLevel, StringComparer.Ordinal) || spec.Level != selectedLevel)
            throw new ArgumentException("ИИ не может менять выбранный уровень.");
        if (spec.Tuning is < 0 or > 3) throw new ArgumentException("Недопустимая настройка стратегии.");
        if (spec.RouteTemplate is not ("inherit" or "standard" or "narrow") || spec.YouTubeTransport is not ("inherit" or "auto" or "quic" or "http2") ||
            spec.DiscordVoice is not ("inherit" or "direct" or "minimal" or "off"))
            throw new ArgumentException("Недопустимый транспорт или шаблон маршрута.");
        var blocks = Blocks(selectedLevel);
        if((spec.YouTube=="personalized")!=(spec.YouTubeParameters is not null))throw new ArgumentException("Индивидуальный блок YouTube требует собственных параметров");
        if(spec.YouTubeParameters is {} yp)YoutubeParametersPolicy.Validate(yp,selectedLevel);
        if(spec.Roblox is not ("baseline" or "minimal" or "split" or "disorder"))throw new ArgumentException("Недопустимый блок контента Roblox.");
        if (!YoutubeBlocks(selectedLevel).Contains(spec.YouTube, StringComparer.Ordinal) || new[] { spec.Discord, spec.Telegram, spec.Roblox }.Any(x => !blocks.Contains(x, StringComparer.Ordinal)))
            throw new ArgumentException("Модель предложила неизвестный блок стратегии.");
        if (!allowExperimental && spec.Experimental) throw new ArgumentException("Новые параметры разрешены только после проверки готовых блоков.");
        if (spec.TcpRepeats is { } tcp && (tcp < 1 || tcp > MaxTcpRepeats(selectedLevel)) ||
            spec.QuicRepeats is { } quic && (quic < 1 || quic > 4) ||
            spec.Overlap is { } overlap && (overlap < 0 || overlap > MaxOverlap(selectedLevel)))
            throw new ArgumentException("Параметры модели вышли за пределы выбранного уровня.");
    }

    public static string NormalizeProvider(string? provider)
    {
        var value = (provider ?? string.Empty).Trim();
        if (value.Length is < 1 or > 80 || value.Any(ch => char.IsControl(ch)))
            throw new ArgumentException("Укажите провайдера (до 80 символов). Например, Т2 или Ростелеком.");
        return value;
    }

    public static IReadOnlyList<AdvisorStrategySpec> ParseModelOutput(string json, string selectedLevel, bool experimental)
    {
        var specs = JsonSerializer.Deserialize<List<AdvisorStrategySpec>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new JsonException("Модель вернула пустой результат.");
        if (specs.Count is < 1 or > 3) throw new JsonException("Модель должна предложить от одного до трёх вариантов.");
        specs=specs.Select(s=>s.YouTubeParameters is {} p?s with{YouTubeParameters=YoutubeParametersPolicy.Normalize(p)}:s).ToList();
        foreach (var spec in specs) Validate(spec, selectedLevel, experimental);
        return specs.DistinctBy(x => x.Key).ToArray();
    }

    public static string ModelSchema(string level, bool experimental, bool youtubePersonalization=false,int youtubeCandidateLimit=2)
    {
        if(youtubePersonalization)return JsonSerializer.Serialize(new{type="array",minItems=1,maxItems=youtubeCandidateLimit,items=new{type="object",additionalProperties=false,
            properties=new{t=new{type="integer",@enum=level=="light"?new[]{0}:new[]{0,1,2,3}},s=new{type="integer",@enum=new[]{0,1,2}},r=new{type="integer",@enum=new[]{1,2,4,6}.Where(n=>n<=MaxTcpRepeats(level)).ToArray()},q=new{type="integer",@enum=new[]{1,2,3,4}}},required=new[]{"t","s","r","q"}}});
        object choice = new { type = "string", @enum = Blocks(level) };
        var properties = new Dictionary<string, object>
        {
            ["level"] = new { type = "string", @enum = new[] { level } },
            ["tuning"] = new { type = "integer", @enum = new[] { 0, 1, 2, 3 } },
            ["youTube"] = new { type = "string", @enum = YoutubeBlocks(level).Where(b=>b!="personalized").ToArray() }, ["discord"] = choice, ["telegram"] = choice,
            ["routeTemplate"] = new { type = "string", @enum = new[] { "standard", "narrow" } },
            ["youTubeTransport"] = new { type = "string", @enum = new[] { "auto", "quic", "http2" } },
            ["discordVoice"] = new { type = "string", @enum = new[] { "direct", "minimal", "off" } }
        };
        if (experimental)
        {
            properties["tcpRepeats"] = new { type = "integer", @enum = Enumerable.Range(1, MaxTcpRepeats(level)).ToArray() };
            properties["quicRepeats"] = new { type = "integer", @enum = new[] { 1, 2, 3, 4 } };
            properties["overlap"] = new { type = "integer", @enum = new[] { 0, Math.Min(256, MaxOverlap(level)), MaxOverlap(level) }.Distinct().ToArray() };
        }
        return JsonSerializer.Serialize(new { type = "array", minItems = 1, maxItems = 3,
            items = new { type = "object", additionalProperties = false, properties, required = properties.Keys.ToArray() } });
    }

    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    private sealed record YoutubeProposal([property:System.Text.Json.Serialization.JsonRequired]int T,[property:System.Text.Json.Serialization.JsonRequired]int S,[property:System.Text.Json.Serialization.JsonRequired]int R,[property:System.Text.Json.Serialization.JsonRequired]int Q);
    public static IReadOnlyList<AdvisorStrategySpec> ParseYoutubeOutput(string json,AdvisorRequest request)
    {
        var proposals=JsonSerializer.Deserialize<List<YoutubeProposal>>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new JsonException("Нет индивидуальных предложений");
        if(proposals.Count<1||proposals.Count>(request.SearchMode=="extended"?5:2))throw new JsonException("Слишком много индивидуальных вариантов для режима поиска");
        if(proposals.Any(p=>p.T is <0 or >3||p.S is <0 or >2||!new[]{1,2,4,6}.Contains(p.R)))throw new JsonException("Недопустимые коды индивидуальных параметров");
        return proposals.Select(p=>new AdvisorStrategySpec{Level=request.Level,Tuning=request.PreferredTuning,YouTube="personalized",YouTubeParameters=YoutubeParametersPolicy.Normalize(new(new[]{"split","disorder","fakeSplit","fakeDisorder"}[p.T],new[]{"firstByte","sni","firstAndSni"}[p.S],p.R,p.Q))})
            .Select(s=>{Validate(s,request.Level);return s;}).DistinctBy(s=>s.Key).ToArray();
    }
}
