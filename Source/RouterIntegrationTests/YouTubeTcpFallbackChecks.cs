using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunYouTubeTcpFallbackChecks(string home, CoreDiagnostics api, Func<string, string, Task> nativeCheck)
    {
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        const string prefix = "mixed-port: 0\nproxies:\n - {name: VPN, type: socks5, server: 127.0.0.1, port: 1}\n - {name: Bypass, type: direct}\n";
        var path = Path.Combine(home, "youtube-fallback.yaml");

        // Evaluate only the generated matcher shapes. The real engine also
        // validates every fixture and exercises rejection over loopback below.
        bool Matches(JsonElement rule, string host, string network, int port, string process, string ip)
        {
            if (rule.TryGetProperty("type", out var type) && type.GetString() == "logical")
                return rule.GetProperty("rules").EnumerateArray().All(child => Matches(child, host, network, port, process, ip));
            foreach (var field in rule.EnumerateObject())
            {
                var values = field.Value;
                bool AnyText(Func<string, bool> test) => values.EnumerateArray().Any(value => test(value.GetString()!));
                var ok = field.Name switch
                {
                    "domain" => AnyText(value => host.Equals(value, StringComparison.OrdinalIgnoreCase)),
                    "domain_suffix" => AnyText(value => host.Equals(value, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + value, StringComparison.OrdinalIgnoreCase)),
                    "domain_keyword" => AnyText(value => host.Contains(value, StringComparison.OrdinalIgnoreCase)),
                    "network" => AnyText(value => value == network),
                    "port" => values.EnumerateArray().Any(value => value.GetInt32() == port),
                    "source_port" => values.EnumerateArray().Any(value => value.GetInt32() == 50000),
                    "process_path_regex" => AnyText(value => Regex.IsMatch(process, value)),
                    "ip_cidr" => AnyText(value => System.Net.IPNetwork.Parse(value).Contains(IPAddress.Parse(ip))),
                    "source_ip_cidr" => AnyText(value => System.Net.IPNetwork.Parse(value).Contains(IPAddress.Parse("192.0.2.10"))),
                    "inbound" or "protocol" => false, // No internal/speed/DNS inbound in this model.
                    _ => true
                };
                if (!ok) return false;
            }
            return true;
        }
        string Route(JsonElement root, string host, string network = "udp", int port = 443, string process = "Browser.exe", string ip = "192.0.2.10")
        {
            foreach (var rule in root.GetProperty("route").GetProperty("rules").EnumerateArray())
            {
                var action = rule.GetProperty("action").GetString();
                if (action is not ("route" or "reject") || !Matches(rule, host, network, port, process, ip)) continue;
                return action == "reject" ? "reject:" + rule.GetProperty("method").GetString() : rule.GetProperty("outbound").GetString()!;
            }
            return root.GetProperty("route").GetProperty("final").GetString()!;
        }
        foreach (var full in new[] { false, true })
        foreach (var includeTun in new[] { false, true })
        foreach (var respectDns in new[] { false, true })
        {
            var yaml = prefix + "tun: {stack: system, mtu: 1500}\ndns:\n respect-rules: " + respectDns.ToString().ToLowerInvariant() +
                "\nrules:\n - DOMAIN,blocked.googlevideo.com,REJECT-DROP\n - DOMAIN-SUFFIX,direct.googlevideo.com,Bypass\n" +
                " - PROCESS-NAME,DirectBrowser.exe,DIRECT\n - IP-CIDR,192.0.2.9/32,DIRECT,no-resolve\n" +
                " - DOMAIN-SUFFIX,googlevideo.com,VPN\n - MATCH,DIRECT\n";
            var doc = ClashConfigDocument.Parse(path, yaml);
            var before = doc.BuildText(doc.Rules);
            var text = SingBoxConfig.Build(doc, api.Port, api.Secret, full, includeTun: includeTun);
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            var label = $"full={full}, adapter={includeTun}, respect-DNS={respectDns}";
            Check(Route(root, "rr.fixture.googlevideo.com") == "reject:default", "YouTube UDP/443 gets an explicit rejection: " + label);
            Check(Route(root, "GOOGLEVIDEO.COM") == "reject:default", "YouTube apex/case matching is supported: " + label);
            Check(Route(root, "rr.fixture.googlevideo.com", "tcp") == "VPN", "YouTube TCP remains on its chosen VPN route: " + label);
            Check(Route(root, "rr.fixture.googlevideo.com", port: 8443) == "VPN", "other YouTube UDP ports remain untouched: " + label);
            foreach (var host in new[] { "other.example", "notgooglevideo.com", "googlevideo.com.other.example" })
                Check(Route(root, host) == (full ? "VPN" : "DIRECT"), "unrelated UDP/443 is not rejected (" + host + "): " + label);
            Check(Route(root, "rr.direct.googlevideo.com") == "DIRECT", "named domain DIRECT exception precedes fallback: " + label);
            Check(Route(root, "rr.fixture.googlevideo.com", process: @"C:\Apps\DIRECTBROWSER.EXE") == "DIRECT", "process DIRECT exception precedes fallback: " + label);
            Check(Route(root, "rr.fixture.googlevideo.com", ip: "192.0.2.9") == "DIRECT", "IP DIRECT exception precedes fallback: " + label);
            Check(Route(root, "blocked.googlevideo.com") == "reject:drop", "explicit REJECT-DROP is not changed: " + label);
            var guards = root.GetProperty("route").GetProperty("rules").EnumerateArray().Where(rule => rule.TryGetProperty("no_drop", out _)).ToArray();
            Check(guards.Length > 0 && guards.All(rule => rule.GetProperty("no_drop").GetBoolean() && !rule.TryGetProperty("inbound", out _)),
                "fallback is immediate even under retries and is not copied into DNS re-entry: " + label);
            if (includeTun)
            {
                var tun = root.GetProperty("inbounds").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "tun");
                Check(tun.GetProperty("stack").GetString() == "system" && tun.GetProperty("mtu").GetInt32() == 1500, "test changes neither stack nor MTU: " + label);
            }
            Check(doc.BuildText(doc.Rules) == before, "fallback never edits the saved YAML: " + label);
            await nativeCheck(text, "real engine validates scoped fallback: " + label);
        }
        foreach (var rule in new[] {
            "PROCESS-NAME,Browser.exe,VPN", "PROCESS-PATH,C:/Apps/Browser.exe,VPN",
            "DOMAIN,rr.fixture.googlevideo.com,VPN", "DOMAIN-KEYWORD,googlevideo,VPN",
            "IP-CIDR,192.0.2.0/24,VPN,no-resolve", "SRC-IP-CIDR,192.0.2.0/24,VPN",
            "DST-PORT,443,VPN", "SRC-PORT,50000,VPN", "NETWORK,udp,VPN", "MATCH,VPN" })
        {
            var doc = ClashConfigDocument.Parse(path, prefix + "rules:\n - " + rule + (rule.StartsWith("MATCH,") ? "\n" : "\n - MATCH,DIRECT\n"));
            var text = SingBoxConfig.Build(doc, api.Port, api.Secret, false, includeTun: false);
            using var json = JsonDocument.Parse(text);
            Check(Route(json.RootElement, "rr.fixture.googlevideo.com", process: @"C:\Apps\Browser.exe") == "reject:default", "fallback respects this VPN decision: " + rule);
            await nativeCheck(text, "real engine accepts composed routing matcher: " + rule);
        }
        var directOnly = ClashConfigDocument.Parse(path, prefix + "rules:\n - MATCH,DIRECT\n");
        using (var json = JsonDocument.Parse(SingBoxConfig.Build(directOnly, api.Port, api.Secret, false)))
            Check(!json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().Any(rule => rule.TryGetProperty("no_drop", out _)),
                "DIRECT-only profile adds no fallback guards at all");
        foreach (var full in new[] { false, true })
        {
            var selectable = ClashConfigDocument.Parse(path, "proxies:\n - {name: Server, type: socks5, server: 127.0.0.1, port: 1}\n" +
                "proxy-groups:\n - {name: VPN, type: select, proxies: [Nested]}\n - {name: Nested, type: select, proxies: [Server, DIRECT]}\n" +
                "rules:\n - DOMAIN-SUFFIX,googlevideo.com,VPN\n - MATCH,VPN\n");
            var text = SingBoxConfig.Build(selectable, api.Port, api.Secret, full);
            using var json = JsonDocument.Parse(text);
            Check(json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().Any(rule => rule.TryGetProperty("no_drop", out _)) == full,
                "nested selectable DIRECT remains untouched; full-TUN prunes it before applying fallback, full=" + full);
            await nativeCheck(text, "native engine accepts nested selection safety, full=" + full);
        }

        // Send a synthetic SOCKS5 UDP packet to the real core, with a numeric
        // loopback proxy as the only possible upstream. No TUN or Internet used.
        foreach (var full in new[] { false, true })
        {
            using var life = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
            using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var mixedPort = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
            var yaml = prefix.Replace("mixed-port: 0", "mixed-port: " + mixedPort).Replace("port: 1}", "port: " + upstreamPort + "}") +
                "rules:\n - DOMAIN-SUFFIX,googlevideo.com,VPN\n - MATCH,DIRECT\n";
            var runtime = Path.Combine(home, "youtube-native-" + full + ".json");
            var fixture = System.Text.Json.Nodes.JsonNode.Parse(SingBoxConfig.Build(ClashConfigDocument.Parse(path, yaml), api.Port, api.Secret, full, includeTun: false))!;
            fixture["log"]!["level"] = "debug"; // Rejections are not emitted at the production INFO level.
            File.WriteAllText(runtime, fixture.ToJsonString());
            var messages = new ConcurrentQueue<string>();
            var core = new CoreProcessManager(); core.Output += messages.Enqueue;
            try
            {
                await core.StartAsync(CoreProcessManager.CreateStartInfo(Path.Combine(AppContext.BaseDirectory, "sing-box.exe"), runtime, home), life.Token);
                using var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, mixedPort, life.Token);
                var stream = control.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, life.Token);
                var auth = new byte[2]; await stream.ReadExactlyAsync(auth, life.Token);
                Check(auth.SequenceEqual(new byte[] { 5, 0 }), "native UDP fixture authenticates without user state, full=" + full);
                await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, life.Token);
                var reply = new byte[4]; await stream.ReadExactlyAsync(reply, life.Token);
                Check(reply[1] == 0 && reply[3] is 1 or 4, "native core opens the loopback UDP association, full=" + full);
                var address = new byte[reply[3] == 1 ? 4 : 16]; await stream.ReadExactlyAsync(address, life.Token);
                var port = new byte[2]; await stream.ReadExactlyAsync(port, life.Token);
                var relay = new IPEndPoint(new IPAddress(address), (port[0] << 8) | port[1]);
                if (relay.Address.Equals(IPAddress.Any)) relay.Address = IPAddress.Loopback;
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var host = Encoding.ASCII.GetBytes("rr.fixture.googlevideo.com");
                var packet = new byte[] { 0, 0, 0, 3, (byte)host.Length }.Concat(host).Concat(new byte[] { 1, 187, 1, 2, 3, 4 }).ToArray();
                await udp.SendAsync(packet, relay, life.Token);
                for (var attempt = 0; attempt < 40 && !messages.Any(line => line.Contains("reject", StringComparison.OrdinalIgnoreCase)); attempt++)
                    await Task.Delay(50, life.Token);
                Check(messages.Any(line => line.Contains("reject", StringComparison.OrdinalIgnoreCase)) && !upstream.Pending(),
                    "native engine rejects YouTube UDP before contacting upstream, full=" + full + "; " + string.Join(" | ", messages.TakeLast(3)));
                using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, mixedPort, life.Token);
                await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT rr.fixture.googlevideo.com:443 HTTP/1.1\r\nHost: rr.fixture.googlevideo.com\r\n\r\n"), life.Token);
                await ReadHeader(tcp.GetStream(), life.Token);
                await tcp.GetStream().WriteAsync(new byte[] { 1, 2, 3, 4 }, life.Token);
                using var accepted = await upstream.AcceptTcpClientAsync(life.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3), life.Token);
                Check(accepted.Connected, "native engine still opens the selected upstream for YouTube TCP/443, full=" + full);
            }
            finally { await core.StopAsync(); }
        }
        return count;
    }
}
