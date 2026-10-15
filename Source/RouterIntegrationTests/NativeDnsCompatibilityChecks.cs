using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunNativeDnsCompatibilityChecks(string scratch)
    {
        var passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        var executable = await CoreBackend.EnsureSingBoxAsync();
        var home = Path.Combine(scratch, "native-dns-compatibility"); Directory.CreateDirectory(home);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = lifetime.Token;
        using var dns = new TcpListener(IPAddress.Loopback, 0); dns.Start();
        using var vpn = new TcpListener(IPAddress.Loopback, 0); vpn.Start();
        var dnsPort = ((IPEndPoint)dns.LocalEndpoint).Port;
        using var udpDns = new UdpClient(new IPEndPoint(IPAddress.Loopback, dnsPort));
        var vpnPort = ((IPEndPoint)vpn.LocalEndpoint).Port;
        var dnsHits = 0; var vpnHits = 0;
        var tasks = new List<Task>();
        byte[] Answer(byte[] query)
        {
            Interlocked.Increment(ref dnsHits);
            var end = 12;
            while (end < query.Length && query[end] != 0) end += query[end] + 1;
            end += 5;
            if (end > query.Length) throw new IOException("Invalid fixture DNS query");
            var type = (query[end - 4] << 8) | query[end - 3];
            var answer = query[..end].ToList();
            answer[2] = 0x81; answer[3] = 0x80; answer[6] = 0; answer[7] = type == 1 ? (byte)1 : (byte)0;
            answer[8] = answer[9] = answer[10] = answer[11] = 0;
            if (type == 1) answer.AddRange([0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,192,0,2,22]);
            return answer.ToArray();
        }
        async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    while (!token.IsCancellationRequested)
                    {
                        var length = new byte[2]; await stream.ReadExactlyAsync(length, token);
                        var query = new byte[(length[0] << 8) | length[1]]; await stream.ReadExactlyAsync(query, token);
                        var answer = Answer(query);
                        var wire = new List<byte> { (byte)(answer.Length >> 8), (byte)answer.Length }; wire.AddRange(answer);
                        await stream.WriteAsync(wire.ToArray(), token);
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            }
        }
        async Task AcceptDns()
        {
            try { while (!token.IsCancellationRequested) { var client = await dns.AcceptTcpClientAsync(token); lock (tasks) tasks.Add(Serve(client)); } }
            catch (OperationCanceledException) { }
        }
        async Task AcceptVpn()
        {
            try { while (!token.IsCancellationRequested) { using var client = await vpn.AcceptTcpClientAsync(token); Interlocked.Increment(ref vpnHits); } }
            catch (OperationCanceledException) { }
        }
        async Task AcceptUdpDns()
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var request = await udpDns.ReceiveAsync(token);
                    await udpDns.SendAsync(Answer(request.Buffer), request.RemoteEndPoint, token);
                }
            }
            catch (OperationCanceledException) { }
        }
        var dnsLoop = AcceptDns(); var vpnLoop = AcceptVpn(); var udpLoop = AcceptUdpDns();
        var prefix = $"mixed-port: 0\nproxies:\n - {{name: Server, type: socks5, server: 127.0.0.1, port: {vpnPort}}}\nproxy-groups:\n - {{name: VPN, type: select, proxies: [Server]}}\nrules:\n - IP-CIDR,127.0.0.0/8,DIRECT,no-resolve\n - MATCH,VPN\ndns:\n use-system-hosts: false\n ipv6: false\n respect-rules: true\n nameserver: [tcp://127.0.0.1:{dnsPort}]\n nameserver-policy: {{'+.policy.example': tcp://127.0.0.1:{dnsPort}}}\n";
        async Task<string> Query(string yaml, string domain, bool full, string label)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var secret = Guid.NewGuid().ToString("N").ToUpperInvariant() + Guid.NewGuid().ToString("N").ToUpperInvariant();
            var jsonPath = Path.Combine(home, label + ".json");
            File.WriteAllText(jsonPath, SingBoxConfig.Build(ClashConfigDocument.Parse(Path.Combine(home, label + ".yaml"), yaml), port, secret, full, includeTun: false));
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = home,
                RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("run"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(jsonPath);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            try
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.Authorization = new("Bearer", secret);
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    try { using var ready = await http.GetAsync($"http://127.0.0.1:{port}/version", token); if (ready.IsSuccessStatusCode) break; }
                    catch (HttpRequestException) { }
                    if (process.HasExited) throw new Exception("Native fixture failed: " + await errors);
                    await Task.Delay(50, token);
                }
                var body = await http.GetStringAsync($"http://127.0.0.1:{port}/dns/query?name={domain}&type=A", token);
                using var answer = JsonDocument.Parse(body);
                return answer.RootElement.GetProperty("Answer")[0].GetProperty("data").GetString()!;
            }
            finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); await output; await errors; }
        }
        try
        {
            foreach (var full in new[] { false, true })
            {
                var priorHits = dnsHits;
                Check(await Query(prefix, "host.policy.example", full, "policy-" + full) == "192.0.2.22" && dnsHits > priorHits && vpnHits == 0,
                    "native DNS policy honors DIRECT without recursive hijack or VPN bypass of policy, full TUN=" + full);
                priorHits = dnsHits;
                Check(await Query(prefix.Replace("tcp://", "udp://"), "host.policy.example", full, "udp-policy-" + full) == "192.0.2.22"
                    && dnsHits > priorHits && vpnHits == 0, "native UDP DNS also honors DIRECT through the authenticated rule-routing bridge, full TUN=" + full);
            }
            var hitsBeforeHosts = dnsHits;
            Check(await Query("hosts:\n static.example: [192.0.2.9, '2001:db8::9']\n" + prefix, "static.example", false, "hosts") == "192.0.2.9"
                && dnsHits == hitsBeforeHosts, "native profile hosts override answers without an upstream request");
            Check(await Query("hosts:\n static.example: 192.0.2.9\n" + prefix.Replace(" ipv6: false", " use-hosts: false\n ipv6: false"),
                "host.policy.example", false, "hosts-disabled") == "192.0.2.22", "disabling profile hosts preserves ordinary DNS policy routing");
        }
        finally
        {
            lifetime.Cancel(); await Task.WhenAll(dnsLoop, vpnLoop, udpLoop);
            Task[] pending; lock (tasks) pending = tasks.ToArray(); await Task.WhenAll(pending);
        }
        return passed;
    }
}
