using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using VortexPass.Core.Services;
using Guard=VortexPass.DnsGuard.Program;

// Inspect the real snapshot writer without ever applying DNS settings.
var capture=(Task<VortexPass.Core.Models.DnsAdapterSnapshot?>)typeof(Guard).GetMethod("CaptureActiveAdapterAsync",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[])!;
var snapshot=await capture;
if(snapshot is not null)
{
 if(!Guid.TryParse(snapshot.InterfaceGuid,out var guid)||guid==Guid.Empty)throw new Exception("Snapshot lost adapter GUID");
 using var registry=Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{{{guid:D}}}");
 if(registry is null||snapshot.AutomaticV4!=string.IsNullOrWhiteSpace(registry.GetValue("NameServer") as string))throw new Exception("Snapshot DHCP/manual mode differs from Windows");
 Console.WriteLine("PASS adapter snapshot read only: GUID and DHCP/manual mode match Windows; DNS settings untouched");
}

using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(30));
var good=new FakeDoh();var slow=new FakeDoh{Slow=true};
using var goodClient=new HttpClient(good);using var slowClient=new HttpClient(slow);
await using var server=new Guard.DnsServer([new("fixture-good",new("https://fixture.invalid/dns-query"),[],goodClient),new("fixture-slow",new("https://fixture.invalid/dns-query"),[],slowClient)]);
await server.BindAsync(lifetime.Token,0);var running=server.RunAsync(lifetime.Token);
async Task<byte[]> Udp(byte[] query)
{
 using var client=new UdpClient(AddressFamily.InterNetwork);await client.SendAsync(query,new IPEndPoint(IPAddress.Loopback,server.Port),lifetime.Token);
 return(await client.ReceiveAsync(lifetime.Token)).Buffer;
}
var queries=Enumerable.Range(0,20).Select(i=>{var q=AdvancedRouteTests.Question();q[0]=(byte)i;return q;}).ToArray();
var answers=await Task.WhenAll(queries.Select(Udp));
if(answers.Where((r,i)=>r[0]!=queries[i][0]||!DnsWirePolicy.Inspect(queries[i],r).HasAddress).Any())throw new Exception("UDP response ID/answer corrupt");
if(good.Calls!=1)throw new Exception($"Same queries not coalesced: {good.Calls}");
if(slow.Calls!=0)throw new Exception("Healthy primary unnecessarily raced the reserve");
var ipv6=AdvancedRouteTests.Question(type:28);var watch=Stopwatch.StartNew();var nodata=await Udp(ipv6);
if(DnsWirePolicy.Inspect(ipv6,nodata).ResponseCode!=0||watch.ElapsedMilliseconds>2000)throw new Exception("Missing AAAA stalls usable IPv4");
using(var tcp=new TcpClient(AddressFamily.InterNetwork))
{
 await tcp.ConnectAsync(IPAddress.Loopback,server.Port,lifetime.Token);var stream=tcp.GetStream();
 for(var i=0;i<2;i++)
 {
  var q=AdvancedRouteTests.Question();q[0]=(byte)(50+i);await stream.WriteAsync(new byte[]{(byte)(q.Length>>8),(byte)q.Length},lifetime.Token);await stream.WriteAsync(q,lifetime.Token);
  var size=new byte[2];await stream.ReadExactlyAsync(size,lifetime.Token);var data=new byte[BinaryPrimitives.ReadUInt16BigEndian(size)];await stream.ReadExactlyAsync(data,lifetime.Token);
  if(!DnsWirePolicy.Inspect(q,data).HasAddress)throw new Exception("Reusable TCP DNS failed");
 }
}
// Replies to abandoned clients can produce UDP connection-reset notifications
// on Windows. Neither these clients nor malformed packets may kill a listener.
for(var i=0;i<20;i++)
{
 using var abandoned=new UdpClient(AddressFamily.InterNetwork);
 await abandoned.SendAsync(AdvancedRouteTests.Question("abandoned"+i+".example"),new IPEndPoint(IPAddress.Loopback,server.Port),lifetime.Token);
}
using(var malformed=new UdpClient(AddressFamily.InterNetwork))
 await malformed.SendAsync(new byte[]{1,2,3},new IPEndPoint(IPAddress.Loopback,server.Port),lifetime.Token);
await Task.Delay(300,lifetime.Token);
var survived=await Udp(AdvancedRouteTests.Question("still-serving.example"));
if(!DnsWirePolicy.Inspect(AdvancedRouteTests.Question("still-serving.example"),survived).HasAddress||running.IsCompleted)
 throw new Exception("Abandoned UDP clients stopped DNS listener");
server.ClearCache();good.Fail=true;slow.Slow=false;
var recovered=await Udp(AdvancedRouteTests.Question("fallback.example.org"));
if(!DnsWirePolicy.Inspect(AdvancedRouteTests.Question("fallback.example.org"),recovered).HasAddress)throw new Exception("Resolver failover failed");
// Real UDP upstream returns TC; the same query must be retried over TCP.
var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
try
{
 var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
 using var udpUpstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, upstreamPort));
 var fixture = Task.Run(async () =>
 {
  var packet = await udpUpstream.ReceiveAsync(lifetime.Token);
  var truncated = packet.Buffer.ToArray(); truncated[2] = 0x83; truncated[3] = 0x80;
  await udpUpstream.SendAsync(truncated, packet.RemoteEndPoint, lifetime.Token);
  using var accepted = await upstream.AcceptTcpClientAsync(lifetime.Token);
  var stream = accepted.GetStream(); var prefix = new byte[2]; await stream.ReadExactlyAsync(prefix, lifetime.Token);
  var q = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)]; await stream.ReadExactlyAsync(q, lifetime.Token);
  var answer = AdvancedRouteTests.Answer(q); BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)answer.Length);
  await stream.WriteAsync(prefix, lifetime.Token); await stream.WriteAsync(answer, lifetime.Token);
 });
 var question = AdvancedRouteTests.Question("tcp-fallback.example.org");
 var reply = await Guard.DnsServer.QueryPlainDnsAsync(IPAddress.Loopback, question, lifetime.Token, upstreamPort);
 if(!DnsWirePolicy.Inspect(question, reply).HasAddress)throw new Exception("Google UDP mode lost TCP fallback");
 await fixture;
}
finally { upstream.Stop(); }
// Resolver health is independent of a selected domain returning NXDOMAIN.
using(var healthyClient=new HttpClient(new FakeDoh {MissingHost="blocked.example"}))
await using(var healthyServer=new Guard.DnsServer([new("fixture-health",new("https://fixture.invalid/dns-query"),[],healthyClient)],["blocked.example"]))
{
 if(await healthyServer.SelectProviderAsync(lifetime.Token) is null)throw new Exception("One unavailable service rejected a healthy resolver");
}
using(var failedClient=new HttpClient(new FakeDoh {Fail=true}))
await using(var failedServer=new Guard.DnsServer([new("fixture-failed",new("https://fixture.invalid/dns-query"),[],failedClient)]))
{
 if(await failedServer.SelectProviderAsync(lifetime.Token) is not null)throw new Exception("Broken resolver marked healthy");
}
// A failed listener must be reported immediately instead of hidden in WhenAll.
using(var failedLifetime=new CancellationTokenSource(TimeSpan.FromSeconds(4)))
await using(var failedListener=new Guard.DnsServer([new("fixture",new("https://fixture.invalid/dns-query"),[],goodClient)]))
{
 await failedListener.BindAsync(failedLifetime.Token,0);var task=failedListener.RunAsync(failedLifetime.Token);
 var sockets=(List<UdpClient>)typeof(Guard.DnsServer).GetField("_udp",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(failedListener)!;
 sockets[0].Dispose();var observed=false;
 try{await task.WaitAsync(TimeSpan.FromSeconds(2));}
 catch(Exception ex)when(ex is ObjectDisposedException or SocketException or IOException or OperationCanceledException){observed=true;}
 finally{failedLifetime.Cancel();}
 if(!observed)throw new Exception("Listener termination hidden from DNS lifecycle");
}
// Exercise the production server: valid negatives never race or promote a reserve.
var firstFixture=new FakeDoh();var reserveFixture=new FakeDoh();
using(var firstClient=new HttpClient(firstFixture))
using(var reserveClient=new HttpClient(reserveFixture))
await using(var stable=new Guard.DnsServer([new("primary",new("https://fixture.invalid/dns-query"),[],firstClient),new("reserve",new("https://fixture.invalid/dns-query"),[],reserveClient)],preferredProvider:"primary"))
{
 await stable.BindAsync(lifetime.Token,0);if((await stable.SelectProviderAsync(lifetime.Token))?.Name!="primary")throw new Exception("Preferred healthy DNS not selected");
 var changed=0;stable.ResolverChanged=_=>Interlocked.Increment(ref changed);var listener=stable.RunAsync(lifetime.Token);
 var reserveBefore=reserveFixture.Calls;firstFixture.MissingHost="missing.example";
 var writer=new StringWriter();var previous=Console.Out;Console.SetOut(writer);
 try
 {
  var names=Enumerable.Range(0,16).Select(i=>i%2==0?"missing.example":"negative-aaaa"+i+".example").ToArray();
  await Task.WhenAll(names.Select(async(name)=>
  {
   var q=AdvancedRouteTests.Question(name,type:name=="missing.example"?(ushort)1:(ushort)28);
   using var udp=new UdpClient();await udp.SendAsync(q,new IPEndPoint(IPAddress.Loopback,stable.Port),lifetime.Token);
   var payload=(await udp.ReceiveAsync(lifetime.Token)).Buffer;var result=DnsWirePolicy.Inspect(q,payload);
   if(result.ResponseCode!=(name=="missing.example"?3:0))throw new Exception("Valid negative answer replaced");
  }));
 }
 finally{Console.SetOut(previous);}
 if(writer.ToString().Contains("missing.example")||reserveFixture.Calls!=reserveBefore||changed!=0||stable.CurrentResolver!="primary")throw new Exception("Per-domain answers changed resolver or leaked detail while disabled");
 firstFixture.Fail=true;
 var query=AdvancedRouteTests.Question("transport-fallback.example");using var client=new UdpClient();await client.SendAsync(query,new IPEndPoint(IPAddress.Loopback,stable.Port),lifetime.Token);
 var fallback=(await client.ReceiveAsync(lifetime.Token)).Buffer;
 if(!DnsWirePolicy.Inspect(query,fallback).HasAddress||reserveFixture.Calls<=reserveBefore||changed!=0)throw new Exception("Ordered transport fallback failed or changed primary selection");
 Console.WriteLine("PASS production DNS: parallel NXDOMAIN/NODATA preserve primary, transport uses reserve, default logs omit names");
}
var robloxPrimary=new FakeDoh();var robloxSecure=new FakeDoh();
await using(var scoped=new Guard.DnsServer([new("primary",new("https://fixture.invalid/dns-query"),[],new HttpClient(robloxPrimary)),new("secure",new("https://fixture.invalid/dns-query"),[IPAddress.Loopback],new HttpClient(robloxSecure),true)]))
{
 await scoped.BindAsync(lifetime.Token,0);await scoped.SelectProviderAsync(lifetime.Token);var loop=scoped.RunAsync(lifetime.Token);
 var query=AdvancedRouteTests.Question("tr.rbxcdn.com");using var browser=new UdpClient();await browser.SendAsync(query,new IPEndPoint(IPAddress.Loopback,scoped.Port),lifetime.Token);
 var response=(await browser.ReceiveAsync(lifetime.Token)).Buffer;
 if(!DnsWirePolicy.Inspect(query,response).HasAddress||robloxSecure.Calls!=0||scoped.CurrentResolver!="primary")throw new Exception("Production Roblox recovery failed or switched the main resolver");
 var other=AdvancedRouteTests.Question("youtube.com");await browser.SendAsync(other,new IPEndPoint(IPAddress.Loopback,scoped.Port),lifetime.Token);await browser.ReceiveAsync(lifetime.Token);
 if(robloxSecure.Calls!=0)throw new Exception("Roblox DNS proxy received another service's query");
 Console.WriteLine("PASS real DNS server serves compact Roblox addresses without proxy or HTTPS, preserving primary resolver");
}
lifetime.Cancel();try{await running;}catch(OperationCanceledException){}
Console.WriteLine("PASS real DNS UDP/TCP server on temporary loopback port: coalescing, independent IDs, AAAA NODATA with A, TCP reuse and resolver failure; no adapter changes");

sealed class FakeDoh:HttpMessageHandler
{
 public int Calls;public bool Slow,Fail; public string? MissingHost;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
 {
  Interlocked.Increment(ref Calls);await Task.Delay(Slow?6000:50,token);if(Fail)throw new HttpRequestException("fixture failure");
  var query=await request.Content!.ReadAsByteArrayAsync(token);byte[] payload;
  if(DnsWirePolicy.QuestionName(query)==MissingHost){payload=query.ToArray();payload[2]=0x81;payload[3]=0x83;}
  else if(query[^3]==28){payload=query.ToArray();payload[2]=0x81;payload[3]=0x80;}
  else payload=AdvancedRouteTests.Answer(query);
  return new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)};
 }
}
