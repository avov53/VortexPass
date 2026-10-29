using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using VortexPass.Core.Models;
using VortexPass.Core.Services;
var root=Path.GetFullPath(Path.Combine("artifacts","update-tests",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
var staging=Path.Combine(root,"staging");var target=Path.Combine(root,"target");Directory.CreateDirectory(staging);Directory.CreateDirectory(target);
File.WriteAllText(Path.Combine(staging,"VortexPass.exe"),"fixture-0.0.9");File.WriteAllText(Path.Combine(staging,"new.txt"),"new");
File.WriteAllText(Path.Combine(target,"VortexPass.exe"),"fixture-0.0.8");File.WriteAllText(Path.Combine(target,"settings.json"),"private-fixture");
var zip=Path.Combine(root,"future.zip");ZipFile.CreateFromDirectory(staging,zip);var bytes=File.ReadAllBytes(zip);var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var handler=new FixtureHttp(bytes,hash);var service=new GitHubUpdateService(new HttpClient(handler));
var available=await service.CheckAsync(new Version(0,0,8));Require(available.State==UpdateState.Available&&available.Release?.Version==new Version(0,0,9),"0.0.8 did not discover next version");
Require((await service.CheckAsync(new Version(0,0,9))).State==UpdateState.Current,"Same version prompted update");
var download=await service.DownloadAsync(available.Release!);Require(await HashVerifier.MatchesAsync(download.ZipPath,hash),"Download hash failed");
handler.Hash=new string('0',64);var rejected=false;try{await service.DownloadAsync(available.Release!);}catch(InvalidDataException){rejected=true;}Require(rejected,"Corrupt ZIP accepted");
var corrupted=false;try{UpdateFileInstaller.Install(staging,target,_=>{},(from,to)=>{if(!corrupted&&from.StartsWith(staging)&&to.EndsWith("VortexPass.exe")){corrupted=true;File.WriteAllText(to,"partial");throw new IOException("simulated disk failure");}File.Copy(from,to,true);});}catch(IOException){}
Require(corrupted&&File.ReadAllText(Path.Combine(target,"VortexPass.exe"))=="fixture-0.0.8","Partially overwritten binary was not rolled back");
UpdateFileInstaller.Install(staging,target,_=>{});Require(File.ReadAllText(Path.Combine(target,"VortexPass.exe"))=="fixture-0.0.9","Next version was not installed");
Require(File.ReadAllText(Path.Combine(target,"settings.json"))=="private-fixture","Private settings modified");
Console.WriteLine("PASS 0.0.8 -> simulated 0.0.9: discovery, same-version gate, download SHA-256, rejection, partial-write rollback, installation and settings preservation. No live application/network settings touched.");
static void Require(bool b,string m){if(!b)throw new Exception(m);}
sealed class FixtureHttp(byte[] zip,string hash):HttpMessageHandler
{
 public string Hash=hash;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
 {
   var url=request.RequestUri!.AbsoluteUri;HttpContent body;
   if(url.EndsWith("/releases/latest"))body=new StringContent("""{"tag_name":"v0.0.9","published_at":"2026-10-29T00:00:00Z","assets":[{"name":"VortexPass-win-x64.zip","browser_download_url":"https://fixture.invalid/future.zip"},{"name":"VortexPass-win-x64.zip.sha256","browser_download_url":"https://fixture.invalid/future.sha256"}]}""",Encoding.UTF8,"application/json");
   else if(url.EndsWith(".sha256"))body=new StringContent(Hash+"  VortexPass-win-x64.zip");else if(url.EndsWith(".zip"))body=new ByteArrayContent(zip);else throw new Exception("Unexpected HTTP destination");
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=body});
 }
}
