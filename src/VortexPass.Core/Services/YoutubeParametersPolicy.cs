using VortexPass.Core.Models;
namespace VortexPass.Core.Services;
public static class YoutubeParametersPolicy
{
    public const int SearchVersion=1;
    public static YoutubeParameters Normalize(YoutubeParameters p)=>p.TcpTechnique is "split" or "disorder"?p with{FakeRepeats=0}:p;
    public static void Validate(YoutubeParameters p,string level)
    {
        if(p!=Normalize(p)||p.TcpTechnique is not ("split" or "disorder" or "fakeSplit" or "fakeDisorder")||
            level=="light"&&p.TcpTechnique!="split"||p.SplitPattern is not ("firstByte" or "firstAndSni" or "sni")||p.QuicRepeats is <1 or >4||
            (p.TcpTechnique.StartsWith("fake",StringComparison.Ordinal)?!new[]{1,2,4,6}.Contains(p.FakeRepeats)||p.FakeRepeats>AdvisorStrategyPolicy.MaxTcpRepeats(level):p.FakeRepeats!=0))
            throw new ArgumentException("Недопустимые индивидуальные параметры YouTube");
    }
    public static string Tcp(YoutubeParameters p,bool https)
    {
        if(!https)return "--dpi-desync=multisplit --dpi-desync-split-pos=method+2 --dpi-desync-cutoff=n3";
        var technique=p.TcpTechnique switch{"split"=>"multisplit","disorder"=>"multidisorder","fakeSplit"=>"fake,multisplit",_=>"fake,multidisorder"};
        var split=p.SplitPattern switch{"firstByte"=>"1","sni"=>"midsld",_=>"1,midsld"};
        return $"--dpi-desync={technique} --dpi-desync-split-pos={split}"+(p.FakeRepeats>0?$" --dpi-desync-repeats={p.FakeRepeats} --dpi-desync-fooling=badseq --dpi-desync-fake-tls=files\\tls_clienthello_www_google_com.bin --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com":"")+" --dpi-desync-cutoff=n3";
    }
    public static object Schema(string level)=>new{type="object",additionalProperties=false,properties=new{
        tcpTechnique=new{type="string",@enum=level=="light"?new[]{"split"}:new[]{"split","disorder","fakeSplit","fakeDisorder"}},
        splitPattern=new{type="string",@enum=new[]{"firstByte","firstAndSni","sni"}},
        fakeRepeats=new{type="integer",@enum=new[]{1,2,4,6}.Where(n=>n<=AdvisorStrategyPolicy.MaxTcpRepeats(level)).ToArray()},
        quicRepeats=new{type="integer",@enum=new[]{1,2,3,4}}},required=new[]{"tcpTechnique","splitPattern","fakeRepeats","quicRepeats"}};
}
