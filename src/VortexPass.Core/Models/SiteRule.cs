using System.Text.Json.Serialization;
namespace VortexPass.Core.Models;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SiteRule
{
    [JsonPropertyName("id")]public string Id {get;init;}=Guid.NewGuid().ToString();
    [JsonPropertyName("name")]public string Name {get;init;}="";
    [JsonPropertyName("domain")]public string Domain {get;init;}="";
    [JsonPropertyName("components")]public List<string> Components {get;init;}=["page"];
    [JsonPropertyName("resourceDomains")]public List<string> ResourceDomains {get;init;}=[];
    [JsonPropertyName("protocol")]public string Protocol {get;init;}="auto";
    [JsonPropertyName("ports")]public string Ports {get;init;}="";
    [JsonPropertyName("connection")]public string Connection {get;init;}="auto";
}
