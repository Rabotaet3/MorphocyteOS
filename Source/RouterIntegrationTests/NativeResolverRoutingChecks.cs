using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunNativeResolverRoutingChecks(string home, string executable)
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        using var life = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var dead = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var bootstrap = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var direct = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var deadHits = 0; var bootstrapHits = 0; var directHits = 0;
        async Task Serve(UdpClient socket, int kind)
        {
            try
            {
                while (true)
                {
                    var query = await socket.ReceiveAsync(life.Token);
                    if (kind == 0) { Interlocked.Increment(ref deadHits); continue; }
                    if (kind == 1) Interlocked.Increment(ref bootstrapHits); else Interlocked.Increment(ref directHits);
                    var answer = query.Buffer.ToList();
                    answer[2] = 0x81; answer[3] = 0x80; answer[6] = 0; answer[7] = 1;
                    answer[8] = answer[9] = answer[10] = answer[11] = 0;
                    answer.AddRange(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 127, 0, 0, 1 });
                    await socket.SendAsync(answer.ToArray(), query.RemoteEndPoint, life.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        var servers = new[] { Serve(dead, 0), Serve(bootstrap, 1), Serve(direct, 2) };
        int Port(UdpClient socket) => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        using var destination = new TcpListener(IPAddress.Loopback, 0); destination.Start();
        var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        using var mixed = new TcpListener(IPAddress.Loopback, 0); mixed.Start();
        var mixedPort = ((IPEndPoint)mixed.LocalEndpoint).Port; mixed.Stop();
        using var api = new CoreDiagnostics();
        var prefix = "mixed-port: " + mixedPort + "\nproxies:\n - {name: VPN, type: socks5, server: vpn.fixture, port: 1}\ndns:\n ipv6: false\n proxy-server-nameserver: [udp://127.0.0.1:" + Port(dead) + ", udp://127.0.0.1:" + Port(bootstrap) + "]\n direct-nameserver: [udp://127.0.0.1:" + Port(direct) + "]\n nameserver: [udp://127.0.0.1:" + Port(bootstrap) + "]\nrules:\n";
        try
        {
        foreach (var ipRule in new[] { false, true })
        {
            var yaml = prefix + " - DOMAIN,vpn.fixture,VPN\n" + (ipRule ? " - IP-CIDR,127.0.0.1/32,DIRECT\n - DOMAIN,ip.fixture,REJECT\n" : " - DOMAIN-SUFFIX,direct.fixture,DIRECT\n") + " - MATCH,REJECT\n";
            var doc = ClashConfigDocument.Parse(Path.Combine(home, "resolver-fixture.yaml"), yaml);
            var runtime = SingBoxConfig.Build(doc, api.Port, api.Secret, false, includeTun: false);
            // Exercise the generated TUN DNS branch through a loopback-only DNS
            // inbound: no virtual adapter, user VPN or Internet traffic is involved.
            var fixture = System.Text.Json.Nodes.JsonNode.Parse(runtime)!;
            var fakeRule = fixture["dns"]!["rules"]!.AsArray().Single(rule => rule?["server"]?.ToString().EndsWith("-fakeip") == true);
            using var dnsListener = new TcpListener(IPAddress.Loopback, 0); dnsListener.Start();
            var dnsPort = ((IPEndPoint)dnsListener.LocalEndpoint).Port; dnsListener.Stop();
            fixture["inbounds"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject {
                ["type"] = "direct", ["tag"] = fakeRule!["inbound"]![0]!.ToString(),
                ["listen"] = "127.0.0.1", ["listen_port"] = dnsPort });
            runtime = fixture.ToJsonString();
            using var json = JsonDocument.Parse(runtime);
            var dnsServers = json.RootElement.GetProperty("dns").GetProperty("servers").EnumerateArray().ToArray();
            var bootstrapServer = dnsServers.Single(server => server.GetProperty("tag").GetString()!.EndsWith("-bootstrap"));
            var directServer = dnsServers.Single(server => server.GetProperty("tag").GetString()!.EndsWith("-direct-resolver"));
            var path = Path.Combine(home, "resolver-fixture.json"); File.WriteAllText(path, runtime);
            var core = new CoreProcessManager();
            try
            {
                await core.StartAsync(CoreProcessManager.CreateStartInfo(executable, path, home), life.Token);
                async Task<byte[]> Query(int port, string name, ushort type = 1)
                {
                    using var socket = new UdpClient(); socket.Connect(IPAddress.Loopback, port);
                    var query = new List<byte> { 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
                    foreach (var label in name.Split('.')) { query.Add((byte)label.Length); query.AddRange(Encoding.ASCII.GetBytes(label)); }
                    query.AddRange(new byte[] { 0, (byte)(type >> 8), (byte)type, 0, 1 });
                    await socket.SendAsync(query.ToArray(), life.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(life.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    return (await socket.ReceiveAsync(timeout.Token)).Buffer;
                }
                if (!ipRule)
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var answer = await Query(bootstrapServer.GetProperty("server_port").GetInt32(), "bootstrap.fixture");
                    Check(answer[7] == 1 && deadHits > 0 && bootstrapHits > 0 && timer.Elapsed < TimeSpan.FromSeconds(3), "native bootstrap race answers with the first DNS silent, without waiting for its timeout");
                    var before = directHits;
                    answer = await Query(directServer.GetProperty("server_port").GetInt32(), "direct.fixture");
                    Check(answer[7] == 1 && directHits > before, "native direct resolver uses direct-nameserver rather than the bootstrap upstream");
                    answer = await Query(directServer.GetProperty("server_port").GetInt32(), "direct.fixture", 28);
                    Check(answer[7] == 0 && (answer[3] & 15) == 0, "explicit direct resolver preserves disabled IPv6 DNS");
                    // The actual domain_resolver transport must recurse only into its
                    // numeric bootstrap race, not the VPN or itself.
                    var bootstrapBefore = bootstrapHits;
                    using var proxy = new TcpClient(); await proxy.ConnectAsync(IPAddress.Loopback, mixedPort, life.Token);
                    await proxy.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT vpn.fixture:1 HTTP/1.1\r\nHost: vpn.fixture:1\r\n\r\n"), life.Token);
                    for (var attempt = 0; attempt < 40 && bootstrapHits == bootstrapBefore; attempt++) await Task.Delay(50, life.Token);
                    Check(bootstrapHits > bootstrapBefore, "VPN server hostname resolves through the native bootstrap transport with a silent primary DNS and no recursion");
                }
                if (ipRule)
                {
                    // Replace remote DNS detour only in this isolated local fixture:
                    // IP matching needs a real DNS answer, never an Internet request.
                    await core.StopAsync();
                    var local = System.Text.Json.Nodes.JsonNode.Parse(runtime)!;
                    foreach (var server in local["dns"]!["servers"]!.AsArray()) server!.AsObject().Remove("detour");
                    File.WriteAllText(path, local.ToJsonString());
                    await core.StartAsync(CoreProcessManager.CreateStartInfo(executable, path, home), life.Token);
                }
                var backend = destination.AcceptTcpClientAsync(life.Token).AsTask();
                using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, mixedPort, life.Token);
                var host = ipRule ? "ip.fixture" : "media.direct.fixture";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET http://{host}:{destinationPort}/ HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"), life.Token);
                using var arrived = await backend.WaitAsync(TimeSpan.FromSeconds(5), life.Token);
                await ReadHeader(arrived.GetStream(), life.Token);
                await arrived.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), life.Token);
                Check((await ReadHeader(client.GetStream(), life.Token)).StartsWith("HTTP/1.1 200"), ipRule ? "native generated IP-CIDR resolves before matching and preserves priority over later domain blocking" : "native DIRECT domain connection traverses the direct resolver race and reaches the loopback backend");
                var fakeAnswer = await Query(dnsPort, host);
                var fakeAddress = new IPAddress(fakeAnswer[^4..]).ToString();
                Check(fakeAddress.StartsWith("198.18."), "generated TUN DNS branch returns FakeIP without installing an adapter");
                var fakeBackend = destination.AcceptTcpClientAsync(life.Token).AsTask();
                using var fakeClient = new TcpClient(); await fakeClient.ConnectAsync(IPAddress.Loopback, mixedPort, life.Token);
                await fakeClient.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET http://{fakeAddress}:{destinationPort}/ HTTP/1.1\r\nHost: {fakeAddress}\r\nConnection: close\r\n\r\n"), life.Token);
                using var fakeArrived = await fakeBackend.WaitAsync(TimeSpan.FromSeconds(5), life.Token);
                await ReadHeader(fakeArrived.GetStream(), life.Token);
                await fakeArrived.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), life.Token);
                Check((await ReadHeader(fakeClient.GetStream(), life.Token)).StartsWith("HTTP/1.1 200"), ipRule ? "generated IP-CIDR matches the real resolved address behind FakeIP" : "generated DIRECT domain rule restores FakeIP and reaches the direct backend");
            }
            finally { await core.StopAsync(); }
        }
        }
        finally { life.Cancel(); await Task.WhenAll(servers); }
        return count;
    }
}
