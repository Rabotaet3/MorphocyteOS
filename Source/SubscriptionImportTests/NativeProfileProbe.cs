using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

// Read-only user-profile diagnosis, explicitly invoked with the path on stdin.
// Print only counts and outcomes; no names, addresses, credentials or raw errors.
internal static class NativeProfileProbe
{
    internal static async Task<int> Run(string profilePath,string? physicalInterface=null,string? requestedServer=null)
    {
        var scratch=Path.Combine(Path.GetTempPath(),"MorphocyteOS-section-probe-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var core=new CoreProcessManager();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(65));
        try
        {
            var original=await File.ReadAllTextAsync(profilePath,deadline.Token);
            var sourceHash=ClashConfigDocument.Hash(original);
            var document=ClashConfigDocument.Parse(Path.Combine(scratch,"profile.yaml"),original);
            var real=VpnServerCatalog.Choices(document,subscriptionOrder:true);
            var heading=document.ProxyNodes.FirstOrDefault(SubscriptionInformation.IsSection)?.Children[new YamlScalarNode("name")].ToString();
            using var api=new CoreDiagnostics();
            using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
            var speedPort=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var bridge=XrayBridgePlan.Create(document);
            if(requestedServer is not null&&!real.Contains(requestedServer)) throw new Exception("Requested server is absent");
            var json=JsonNode.Parse(SingBoxConfig.Build(document,api.Port,api.Secret,false,speedPort,requestedServer??heading,includeTun:false,xrayEndpoints:bridge.Endpoints))!;
            if(physicalInterface is not null)
            {
                json["route"]!["auto_detect_interface"]=false;
                json["route"]!["default_interface"]=physicalInterface;
            }
            var inbounds=json["inbounds"]!.AsArray();
            foreach(var inbound in inbounds.Where(item=>item?["type"]?.ToString()=="mixed").ToArray()) inbounds.Remove(inbound);
            json["log"]!["level"]="error";
            if(inbounds.Any(item=>item?["type"]?.ToString()=="tun")) throw new Exception("Unexpected tunnel listener");
            var runtime=Path.Combine(scratch,"runtime.json"); await File.WriteAllTextAsync(runtime,json.ToJsonString(),deadline.Token);
            var engine=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../Packaging/Engine/sing-box.exe"));
            await core.StartWithBridgeAsync(CoreProcessManager.CreateStartInfo(engine,runtime),bridge,deadline.Token);
            for(var attempt=0;;attempt++)
                try { await api.ReadVersionAsync(deadline.Token); break; }
                catch(HttpRequestException) when(attempt<20) { await Task.Delay(100,deadline.Token); }
            var selected=await api.ReadSelectionAsync(document.VpnRoute!,deadline.Token);
            var correct=selected==(requestedServer??real.FirstOrDefault());
            Console.WriteLine("ActualServers="+real.Count+"; requested selection confirmed="+correct);
            var ok=correct;
            async Task<bool> Probe(string label)
            {
                try { Console.WriteLine(label+": controller HTTPS delay="+await api.CheckProxyAsync(document.VpnRoute!,deadline.Token)+" ms"); }
                catch(Exception error) { Console.WriteLine(label+": controller HTTPS failed; kind="+error.GetType().Name); }
                try
                {
                    using var handler=new SocketsHttpHandler { UseProxy=true,Proxy=new WebProxy($"socks5://127.0.0.1:{speedPort}") {
                        Credentials=new NetworkCredential("morphocyte",api.Secret) },AllowAutoRedirect=false,UseCookies=false };
                    using var http=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(15) };
                    using var response=await http.GetAsync("https://speed.cloudflare.com/__down?bytes=65536",deadline.Token);
                    var bytes=await response.Content.ReadAsByteArrayAsync(deadline.Token);
                    var success=response.StatusCode==HttpStatusCode.OK && bytes.Length==65536;
                    Console.WriteLine(label+": speed-channel HTTP="+(int)response.StatusCode+"; bytes="+bytes.Length);
                    return success;
                }
                catch(Exception error) { Console.WriteLine(label+": speed channel failed; kind="+error.GetType().Name+"; inner="+error.InnerException?.GetType().Name); return false; }
            }
            ok &= await Probe("Selected server");
            if(requestedServer is not null&&bridge.Endpoints.TryGetValue(requestedServer,out var localEndpoint))
            {
                try
                {
                    using var handler=new SocketsHttpHandler { UseProxy=true,Proxy=new WebProxy($"socks5://127.0.0.1:{localEndpoint.Port}") {
                        Credentials=new NetworkCredential(localEndpoint.Username,localEndpoint.Password) },AllowAutoRedirect=false,UseCookies=false };
                    using var http=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(12) };
                    using var response=await http.GetAsync("https://www.gstatic.com/generate_204",HttpCompletionOption.ResponseHeadersRead,deadline.Token);
                    Console.WriteLine("Direct Xray SOCKS HTTPS status="+(int)response.StatusCode);
                }
                catch(Exception error) {
                    var message=error.GetBaseException().Message.Replace(localEndpoint.Password,"<secret>");
                    message=System.Text.RegularExpressions.Regex.Replace(message,@"(?i)(?:https?|vless|vmess)://\S+|[0-9a-f]{8}-[0-9a-f-]{27,}|[0-9a-f]{48,}","<redacted>");
                    Console.WriteLine("Direct Xray SOCKS HTTPS failed; kind="+error.GetType().Name+"; reason="+message[..Math.Min(220,message.Length)]);
                }
            }
            var extended=document.ProxyNodes.FirstOrDefault(node=>node.Children[new YamlScalarNode("type")].ToString()=="xray");
            if(extended is not null&&requestedServer is null)
            {
                var name=extended.Children[new YamlScalarNode("name")].ToString();
                ok &= await api.RestoreSelectionAsync(document.VpnRoute!,name,deadline.Token);
                ok &= await Probe("Xray-backed server");
            }
            var unchanged=sourceHash==ClashConfigDocument.Hash(await File.ReadAllTextAsync(profilePath,deadline.Token));
            Console.WriteLine("Original profile unchanged="+unchanged);
            return ok && unchanged?0:1;
        }
        catch { Console.WriteLine("Isolated profile probe failed (details omitted)"); return 1; }
        finally
        {
            var stopped=await core.StopAsync();
            // This exact directory was created above for the probe. Never use the
            // user-supplied profile path as a cleanup target.
            if(stopped) Directory.Delete(scratch,true);
            Console.WriteLine("Owned probe processes stopped="+stopped+"; TUN and system proxy unchanged.");
        }
    }
}
