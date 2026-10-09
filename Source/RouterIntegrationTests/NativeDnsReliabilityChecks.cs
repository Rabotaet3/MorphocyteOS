using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunNativeDnsReliabilityChecks(string home, string executable)
    {
        var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var failed = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var healthy = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var healthyFails = false;
        var failedHits = 0; var healthyHits = 0;
        async Task Serve(UdpClient socket, bool first)
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var request = await socket.ReceiveAsync(lifetime.Token);
                    if (first) Interlocked.Increment(ref failedHits); else Interlocked.Increment(ref healthyHits);
                    if (!first) await Task.Delay(150, lifetime.Token);
                    var success = !first && !healthyFails;
                    var response = request.Buffer.ToList();
                    response[2] = 0x81; response[3] = success ? (byte)0x80 : (byte)0x82;
                    response[6] = 0; response[7] = success ? (byte)1 : (byte)0;
                    response[8] = response[9] = response[10] = response[11] = 0;
                    if (success) response.AddRange(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 0, 2, 7 });
                    await socket.SendAsync(response.ToArray(), request.RemoteEndPoint, lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        var failedTask = Serve(failed, true); var healthyTask = Serve(healthy, false);
        using var api = new CoreDiagnostics();
        var yaml = "proxies:\n - {name: Server, type: socks5, server: 127.0.0.1, port: 9999}\nproxy-groups:\n - {name: VPN, type: select, proxies: [Server]}\nrules:\n - MATCH,DIRECT\ndns:\n ipv6: false\n nameserver: [udp://127.0.0.1:" + ((IPEndPoint)failed.Client.LocalEndPoint!).Port + ", udp://127.0.0.1:" + ((IPEndPoint)healthy.Client.LocalEndPoint!).Port + "]\n";
        var doc = ClashConfigDocument.Parse(Path.Combine(home, "dns-race.yaml"), yaml);
        var json = JsonNode.Parse(SingBoxConfig.Build(doc, api.Port, api.Secret, false, includeTun: false))!;
        // Test only: connect DNS upstreams directly to loopback stubs. Production
        // configuration retains the VPN detour, which is checked separately.
        foreach (var server in json["dns"]!["servers"]!.AsArray()) server!.AsObject().Remove("detour");
        var path = Path.Combine(home, "dns-race.json"); File.WriteAllText(path, json.ToJsonString());
        var core = new CoreProcessManager();
        try
        {
            await core.StartAsync(CoreProcessManager.CreateStartInfo(executable, path, home), lifetime.Token);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:" + api.Port + "/") };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Secret);
            using (var result = JsonDocument.Parse(await client.GetStringAsync("dns/query?name=race.example&type=A", lifetime.Token)))
                Check(result.RootElement.GetProperty("Status").GetInt32() == 0
                    && result.RootElement.GetProperty("Answer")[0].GetProperty("data").GetString() == "192.0.2.7"
                    && failedHits > 0 && healthyHits > 0,
                    "real sing-box ignores fast SERVFAIL and accepts the healthy parallel DNS response without TUN or external requests");
            healthyFails = true;
            using (var result = JsonDocument.Parse(await client.GetStringAsync("dns/query?name=both-failed.example&type=A", lifetime.Token)))
                Check(result.RootElement.GetProperty("Status").GetInt32() == 2,
                    "all failed DNS upstreams return SERVFAIL rather than silently bypassing VPN");
        }
        finally
        {
            await core.StopAsync(); lifetime.Cancel(); await Task.WhenAll(failedTask, healthyTask);
        }
        return passed;
    }
}
