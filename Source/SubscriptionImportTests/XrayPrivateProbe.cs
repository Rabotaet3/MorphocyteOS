using System.Net;
using System.Net.Http;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

// Explicit command-line diagnostic only. Never logs links, names, hosts, IDs,
// response bodies or exception messages; never changes routes or user settings.
internal static class XrayPrivateProbe
{
    internal static async Task Run(ImportedProfile profile, CancellationToken token)
    {
        var document = ClashConfigDocument.Parse(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "probe.yaml"), profile.Yaml);
        using var plan = XrayBridgePlan.Create(document);
        var validation = await plan.ValidateAsync(token);
        if (!validation.Success) { Console.WriteLine("Private HTTPS probe: invalid Xray configuration."); return; }
        var chosen = document.ProxyNodes.Select((node, index) => (Node:node,Index:index+1))
            .Where(item => item.Node.Children[new YamlScalarNode("type")].ToString() == XrayProfile.NodeType)
            .ToArray();
        var core = new CoreProcessManager();
        var runtime=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"morphocyte-probe-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            using var api=new CoreDiagnostics();
            var json=System.Text.Json.Nodes.JsonNode.Parse(SingBoxConfig.Build(document,api.Port,api.Secret,false,includeTun:false,xrayEndpoints:plan.Endpoints))!;
            // Diagnostic never occupies the application's ordinary proxy port.
            var inbounds=json["inbounds"]!.AsArray();
            foreach(var item in inbounds.Where(i=>i?["type"]?.ToString()=="mixed").ToArray()) inbounds.Remove(item);
            await System.IO.File.WriteAllTextAsync(runtime,json.ToJsonString(),token);
            var singBox=System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,"../../../../Packaging/Engine/sing-box.exe"));
            await core.StartWithBridgeAsync(CoreProcessManager.CreateStartInfo(singBox,runtime),plan,token);
            using var concurrency=new SemaphoreSlim(3,3);
            await Task.WhenAll(chosen.Select(async item => {
                await concurrency.WaitAsync(token);
                try
                {
                var endpoint=plan.Endpoints[item.Node.Children[new YamlScalarNode("name")].ToString()];
                using var handler=new SocketsHttpHandler { UseProxy=true, Proxy=new WebProxy($"socks5://127.0.0.1:{endpoint.Port}")
                    { Credentials=new NetworkCredential(endpoint.Username,endpoint.Password) }, AllowAutoRedirect=false,UseCookies=false };
                using var http=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(18) };
                try
                {
                    using var response=await http.GetAsync("https://www.gstatic.com/generate_204",HttpCompletionOption.ResponseHeadersRead,token);
                    Console.WriteLine($"Record {item.Index}: HTTPS through Xray HTTP={(int)response.StatusCode}");
                }
                catch(Exception error) when(error is HttpRequestException or OperationCanceledException)
                { Console.WriteLine($"Record {item.Index}: HTTPS through Xray unsuccessful (details omitted)"); }
                }
                finally { concurrency.Release(); }
            }));
        }
        finally
        {
            var stopped=await core.StopAsync();
            System.IO.File.Delete(runtime);
            Console.WriteLine("Private probe owned Xray stopped="+stopped+"; no TUN or system proxy changed.");
        }
    }
}
