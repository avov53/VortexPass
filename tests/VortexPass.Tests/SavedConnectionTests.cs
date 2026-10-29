using VortexPass.Core.Models;
using VortexPass.Core.Services;
internal static class SavedConnectionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(SavedConnectionProfile p)
    { try { SavedConnectionProfilePolicy.Validate(p); } catch (ArgumentException) { return; } catch (FormatException) { return; } throw new Exception("Invalid profile accepted"); }
    public static Task Validation()
    {
        foreach (var timestamp in new[] { "", "not-a-date", "2000-01-01T00:00:00Z" })
        {
            using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
            { name = "Saved", services = new[] { "telegramweb" }, updatedAt = timestamp, favorite = true, mode = "mobile", strategy = new { command = "unsafe" } }));
            var request = SavedConnectionProfilePolicy.Validate(SaveConnectionRequest.Read(json.RootElement));
            Check(request.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1), "Client timestamp became authoritative");
            Check(!request.Favorite && request.Mode == "standard" && request.Strategy is null, "Client injected metadata");
        }
        using (var json = System.Text.Json.JsonDocument.Parse("{\"name\":\"New\",\"services\":[\"discord\"]}"))
            Check(Guid.TryParse(SaveConnectionRequest.Read(json.RootElement).Id, out _), "Missing server ID");
        var p = new SavedConnectionProfile { Id = Guid.NewGuid().ToString(), Name = "Telegram", Services = ["telegram"], Domains = ["*.Example.org"] };
        var valid = SavedConnectionProfilePolicy.Validate(p);
        Check(valid.Domains.SequenceEqual(new[] { "example.org" }), "Domain normalization differs from winws policy");
        var flags = SavedConnectionProfilePolicy.ServiceFlags(valid);
        Check(flags.Telegram && flags.TelegramWeb && !flags.YouTube, "Telegram selection polluted other services");
        var domainOnly = SavedConnectionProfilePolicy.Validate(new() { Id=Guid.NewGuid().ToString(),Name="Site",Domains=["example.org"] });
        var custom = SavedConnectionProfilePolicy.ServiceFlags(domainOnly);
        Check(custom.AnyEnabled && custom.Custom && !custom.Web, "Domain-only profile broadens capture to general Web");
        var args = WinwsProfileComposer.Compose(custom, "light", new(), "files\\custom.txt", null);
        Check(args.Contains("custom.txt") && !args.Contains("list-youtube.txt"), "Domain-only profile is not composable");
        Reject(new() { Id=p.Id,Name="Bad",Services=["unsupported"] });
        Reject(new() { Id=p.Id,Name="Empty" });
        Reject(new() { Id=p.Id,Name="Bad",Domains=["https://example.org/"] });
        Reject(new() { Id=p.Id,Name="Bad",Services=["gamefilter"] });
        p.Provider = "Test ISP";
        var now = DateTimeOffset.UtcNow;
        var recommendation = new AdvisorRecommendation("checked", new() { Level = p.Level }, p.Provider, 1, 1, true, 30, 1, "", [], RouteProbePolicy.Version);
        var proof = new CheckedDraftRecommendation(DraftRecommendationPolicy.Composition(p), p.Provider, "network-A", now.AddMinutes(30), recommendation);
        DraftRecommendationPolicy.Validate(p, proof, "network-A", now);
        void RejectProof(SavedConnectionProfile draft, CheckedDraftRecommendation evidence, string network)
        {
            try { DraftRecommendationPolicy.Validate(draft, evidence, network, now); }
            catch (ArgumentException) { return; }
            throw new Exception("Foreign or stale draft recommendation accepted");
        }
        RejectProof(p, proof, "network-B");
        RejectProof(p, proof with { Expires = now.AddSeconds(-1) }, "network-A");
        RejectProof(p, proof with { Recommendation = recommendation with { ProbeVersion = 0 } }, "network-A");
        var changed = SavedConnectionProfilePolicy.Clone(p); changed.Id = Guid.NewGuid().ToString(); RejectProof(changed, proof, "network-A");
        changed = SavedConnectionProfilePolicy.Clone(p); changed.Domains.Add("other.example"); RejectProof(changed, proof, "network-A");
        changed = SavedConnectionProfilePolicy.Clone(p); changed.Games.Add("roblox"); RejectProof(changed, proof, "network-A");
        changed = SavedConnectionProfilePolicy.Clone(p); changed.Level = "aggressive"; RejectProof(changed, proof, "network-A");
        changed = SavedConnectionProfilePolicy.Clone(p); changed.Provider = "Other ISP"; RejectProof(changed, proof, "network-A");
        return Task.CompletedTask;
    }
    public static Task Snapshot()
    {
        var legacy = new AppSettings { SavedConnections = [new() { Id = Guid.NewGuid().ToString(), Name = "My route", Services = ["telegram"], Mode = "mobile", Favorite = true,
            Strategy = new() { Level = "medium" }, ProbeVersion = 2 }] };
        UnifiedProfileMigration.Apply(legacy); UnifiedProfileMigration.Apply(legacy);
        Check(legacy.SavedConnections.Count == 1 && legacy.SavedConnections[0].Strategy?.RouteTemplate == "narrow" &&
            legacy.SavedConnections[0].Mode == "standard" && legacy.SavedConnections[0].ProbeVersion == 2, "Migration lost or falsely revalidated a route");
        var p = SavedConnectionProfilePolicy.Validate(new() { Id=Guid.NewGuid().ToString(),Name="Calls",Services=["discord"],Games=["phasmophobia"] });
        var active = SavedConnectionProfilePolicy.Clone(p);
        p.Services.Clear(); p.Games.Add("roblox"); p.Name="Changed";
        Check(active.Services.Contains("discord") && active.Games.Count == 1 && active.Name == "Calls", "Editing mutates an active snapshot");
        Check(SavedConnectionProfilePolicy.ServiceFlags(active).GameFilter, "Selected game not enabled");
        var blank = new AppSettings(); Check(blank.SavedConnections.Count == 0, "Fresh install seeds unsolicited profiles");
        return Task.CompletedTask;
    }
}
