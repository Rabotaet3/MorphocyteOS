using System.IO;
using System.Text.Json;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunNetworkCompatibilityChecks(string home, CoreDiagnostics api, Func<string, string, Task> nativeCheck)
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        var proxy = "proxies:\n - {name: VPN, type: socks5, server: 127.0.0.1, port: 1}\n";
        ClashConfigDocument Doc(string suffix) => ClashConfigDocument.Parse(Path.Combine(home, "network-compat.yaml"), proxy + suffix);
        foreach (var full in new[] { false, true })
        {
            var ordered = Doc("rules:\n - DOMAIN-SUFFIX,googlevideo.com,VPN\n - DOMAIN-KEYWORD,dns,DIRECT\n - MATCH,DIRECT\n");
            var text = SingBoxConfig.Build(ordered, api.Port, api.Secret, full);
            using var json = JsonDocument.Parse(text);
            var dns = json.RootElement.GetProperty("dns").GetProperty("rules").EnumerateArray().ToArray();
            var fake = Array.FindIndex(dns, rule => rule.TryGetProperty("inbound", out var inbound) && inbound[0].GetString()!.EndsWith("-tun"));
            var direct = Array.FindIndex(dns, rule => rule.TryGetProperty("domain_keyword", out var keyword) && keyword[0].GetString() == "dns");
            Check(fake >= 0 && fake < direct, "TUN DNS retains FakeIP until the actual process/domain route is selected, full TUN=" + full);
            if (!full)
            {
                var vpn = Array.FindIndex(dns, rule => rule.TryGetProperty("domain_suffix", out var names) && names[0].GetString() == "googlevideo.com");
                Check(vpn >= 0 && vpn < direct, "non-TUN DNS mirrors earlier VPN domain rules ahead of overlapping DIRECT keywords");
            }
            Check(json.RootElement.GetProperty("outbounds")[0].GetProperty("domain_resolver").GetString()!.EndsWith("-direct-resolver"), "DIRECT destinations have an independent race instead of the VPN-server bootstrap");
            await nativeCheck(text, "real sing-box accepts ordered DNS routing, full TUN=" + full);
        }
        foreach (var modifier in new[] { "", ",no-resolve" })
        {
            using var json = JsonDocument.Parse(SingBoxConfig.Build(Doc("rules:\n - IP-CIDR,127.0.0.1/32,DIRECT" + modifier + "\n - MATCH,REJECT\n"), api.Port, api.Secret, false));
            var resolves = json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().Count(rule => rule.GetProperty("action").GetString() == "resolve");
            Check(resolves == (modifier.Length == 0 ? 1 : 0), "ordinary IP-CIDR resolves domains; no-resolve keeps its distinct semantics: " + modifier);
        }
        foreach (var stack in new[] { "system", "gvisor" })
        {
            var text = SingBoxConfig.Build(Doc("tun: {stack: " + stack + ", mtu: 1500}\nrules:\n - MATCH,DIRECT\n"), api.Port, api.Secret, true);
            using var json = JsonDocument.Parse(text);
            var tun = json.RootElement.GetProperty("inbounds").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "tun");
            Check(tun.GetProperty("mtu").GetInt32() == 1500 && tun.GetProperty("stack").GetString() == stack, "explicit TUN MTU and stack survive translation: " + stack);
            await nativeCheck(text, "real sing-box validates explicit TUN settings: " + stack);
        }
        foreach (var option in new[] { "mtu: 0", "mtu: 1279", "mtu: 65536", "mtu: abc", "stack: unknown" })
        {
            try { SingBoxConfig.Build(Doc("tun: {" + option + "}\nrules:\n - MATCH,DIRECT\n"), api.Port, api.Secret, false); throw new Exception("Invalid TUN accepted"); }
            catch (InvalidDataException) { Check(true, "invalid TUN setting fails closed: " + option); }
        }
        var ws = "proxies:\n - {name: VPN, type: vless, server: 192.0.2.1, port: 443, uuid: 00000000-0000-0000-0000-000000000001, tls: true, network: ws, ws-opts: {path: '/ws?ed=2048&eh=X-Early&token=fixture', headers: {Host: fixture.example}}}\nrules:\n - MATCH,VPN\n";
        var wsText = SingBoxConfig.Build(ClashConfigDocument.Parse(Path.Combine(home, "ws-compat.yaml"), ws), api.Port, api.Secret, false);
        using (var json = JsonDocument.Parse(wsText))
        {
            var outbound = json.RootElement.GetProperty("outbounds")[1]; var transport = outbound.GetProperty("transport");
            Check(outbound.GetProperty("tls").GetProperty("server_name").GetString() == "fixture.example", "WebSocket Host provides TLS SNI when the subscription omits SNI");
            Check(transport.GetProperty("path").GetString() == "/ws?token=fixture" && transport.GetProperty("max_early_data").GetInt32() == 2048 && transport.GetProperty("early_data_header_name").GetString() == "X-Early", "embedded WebSocket early data is normalized without losing unrelated path parameters");
        }
        await nativeCheck(wsText, "real sing-box accepts WebSocket SNI and embedded early data compatibility");
        using (var json = JsonDocument.Parse(SingBoxConfig.Build(ClashConfigDocument.Parse(Path.Combine(home, "ws-sni.yaml"), ws.Replace("tls: true", "tls: true, servername: explicit.example")), api.Port, api.Secret, false)))
            Check(json.RootElement.GetProperty("outbounds")[1].GetProperty("tls").GetProperty("server_name").GetString() == "explicit.example", "explicit TLS SNI is never replaced by WebSocket Host");
        using (var json = JsonDocument.Parse("{\"connections\":[{\"id\":\"fixture\",\"upload\":500,\"download\":0,\"metadata\":{\"host\":\"rr3---sn-5hnednss.googlevideo.com\",\"destinationPort\":\"443\",\"network\":\"udp\"},\"chains\":[\"DIRECT\"]}]}"))
        {
            var before = CoreDiagnostics.ParseConnections(json.RootElement);
            var after = new[] { before[0] with { Upload = 1500 } };
            var step = ConnectionDiagnostic.VideoTransferStep(before, after);
            Check(before[0].Upload == 500 && before[0].Download == 0 && step.State == DiagnosticState.Warning && step.Result.Contains("напрямую: 1"), "manual diagnostics detect outgoing video traffic without replies and report its DIRECT route");
            Check(!step.Result.Contains("googlevideo") && !step.Result.Contains("fixture"), "video diagnostic report excludes raw hosts and API connection IDs");
            Check(ConnectionDiagnostic.VideoTransferStep(before, new[] { before[0] with { Download = 8000 } }).Result.Contains("получают данные: 1"), "manual diagnostics distinguish increasing download counters from mere open connections");
            Check(ConnectionDiagnostic.VideoTransferStep([], []).State == DiagnosticState.Information, "absence of active video is not reported as successful video playback");
        }
        return count;
    }
}
