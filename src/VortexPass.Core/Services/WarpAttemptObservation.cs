using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VortexPass.Core.Services;

public sealed record WarpAttemptObservation(string Transport,IReadOnlyList<string> Endpoints,IReadOnlyList<string> ServerNames)
{
    public static WarpAttemptObservation Parse(string text,DateTimeOffset since)
    {
        var transport="unknown";var endpoints=new HashSet<string>();var names=new HashSet<string>();
        foreach(var line in text.Split('\n'))
        {
            var space=line.IndexOf(' ');if(space<0||!DateTimeOffset.TryParse(line[..space],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var time)||time<since)continue;
            if(line.Contains("h3_tun:")&&(line.Contains("handshake initiation")||line.Contains("Established QUIC")))transport="QUIC/UDP";
            if(line.Contains("h2_tun:")&&(line.Contains("Connecting")||line.Contains("Established")))transport="HTTP/2/TCP";
            foreach(Match match in Regex.Matches(line,@"(?:dst=|endpoint=)(\[[0-9a-fA-F:]+\]:\d+|\d+\.\d+\.\d+\.\d+:\d+)"))endpoints.Add(match.Groups[1].Value);
            foreach(Match match in Regex.Matches(line,"sni=\"([a-zA-Z0-9.-]+)\""))
                if(match.Groups[1].Value.EndsWith(".cloudflareclient.com",StringComparison.OrdinalIgnoreCase))names.Add(match.Groups[1].Value);
        }
        return new(transport,endpoints.Order().Take(8).ToArray(),names.Order().Take(8).ToArray());
    }
    public static async Task<WarpAttemptObservation> ReadAsync(DateTimeOffset since,CancellationToken token=default)
    {
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Cloudflare","cfwarp_service_log.txt");
        try
        {
            await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            stream.Seek(Math.Max(0,stream.Length-524288),SeekOrigin.Begin);
            using var reader=new StreamReader(stream,Encoding.UTF8);
            return Parse(await reader.ReadToEndAsync(token),since);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return new("unknown",[],[]);}
    }
    public string Summary=>"наблюдаемый транспорт: "+Transport+"; узлы: "+string.Join(",",Endpoints)+"; SNI: "+string.Join(",",ServerNames);
}
