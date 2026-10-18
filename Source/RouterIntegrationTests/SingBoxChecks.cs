using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunSingBoxChecks(string scratch)
    {
        var home = Path.Combine(scratch, "singbox"); Directory.CreateDirectory(home);
        var passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        var executable = await CoreBackend.EnsureSingBoxAsync();
        var defaultsPath = Path.Combine(home, "default-core-settings.json");
        var defaults = AppSettings.Load(defaultsPath);
        await BundledResources.PrepareDefaultsAsync(defaults, CancellationToken.None);
        Check(defaults.CorePath == executable && defaults.CoreDefaultsVersion == 1, "fresh installation defaults to sing-box without starting VPN");
        var defaultYaml = File.ReadAllText(defaults.ConfigPath);
        var legacy = AppSettings.Load(Path.Combine(home, "legacy-core-settings.json"));
        legacy.ConfigPath = defaults.ConfigPath;
        legacy.CorePath = Path.Combine(AppContext.BaseDirectory, "mihomo.exe");
        await BundledResources.PrepareDefaultsAsync(legacy, CancellationToken.None);
        Check(legacy.CorePath == executable && File.ReadAllText(legacy.ConfigPath) == defaultYaml, "old bundled Mihomo default migrates once without changing YAML");
        legacy.CorePath = Path.Combine(AppContext.BaseDirectory, "mihomo.exe"); legacy.Save();
        var manualChoice = AppSettings.Load(Path.Combine(home, "legacy-core-settings.json"));
        await BundledResources.PrepareDefaultsAsync(manualChoice, CancellationToken.None);
        Check(Path.GetFileName(manualChoice.CorePath) == "mihomo.exe", "explicit Mihomo choice survives restart after default migration");
        var customCore = Path.Combine(home, "custom-core.exe"); File.WriteAllText(customCore, "inert custom-core fixture");
        var custom = AppSettings.Load(Path.Combine(home, "custom-settings.json")); custom.CorePath = customCore;
        await BundledResources.PrepareDefaultsAsync(custom, CancellationToken.None);
        Check(custom.CorePath == customCore, "default migration preserves a user's custom executable");
        var packagedEngine = Path.Combine(AppContext.BaseDirectory, "ThirdPartySource", "Engine", CoreBackend.SingBoxFileName);
        if (File.Exists(packagedEngine)) throw new Exception("Nested engine fixture already exists");
        Directory.CreateDirectory(Path.GetDirectoryName(packagedEngine)!);
        File.Copy(executable, packagedEngine);
        try
        {
            Check(await CoreBackend.EnsureSingBoxAsync() == packagedEngine, "release-compatible nested engine is selected and its pinned hash verified");
            var nestedYaml = Path.Combine(home, "nested.yaml"); File.WriteAllText(nestedYaml, BundledResources.TemplateText);
            var nestedValidation = await CoreProcessManager.ValidateAsync(packagedEngine, nestedYaml, CancellationToken.None);
            Check(nestedValidation.Success, "bundled nested sing-box validates YAML without starting VPN");
            File.AppendAllText(packagedEngine, "tampered-fixture");
            try { await CoreBackend.EnsureSingBoxAsync(); throw new Exception("Modified bundled engine accepted"); }
            catch (IOException) { Check(true, "modified nested core cannot bypass bundled-engine integrity checks"); }
        }
        finally { File.Delete(packagedEngine); }
        passed += await RunNativeDnsReliabilityChecks(home, executable);
        passed += await RunNativeResolverRoutingChecks(home, executable);
        using var api = new CoreDiagnostics();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var prefix = "mixed-port: 0\nproxies:\n";
        var suffix = "proxy-groups:\n - {name: VPN, type: select, proxies: [Server]}\nrules:\n - DOMAIN-SUFFIX,direct.example,DIRECT\n - PROCESS-NAME,Sample.exe,DIRECT\n - DOMAIN,blocked.example,REJECT\n - DOMAIN-SUFFIX,vpn.example,VPN\n - MATCH,DIRECT\n";
        var fixtures = new[] {
            " - {name: Server, type: socks5, server: 127.0.0.1, port: 9999}\n",
            " - {name: Server, type: http, server: 127.0.0.1, port: 9999, tls: true, username: fixture, password: fixture}\n",
            " - {name: Server, type: vless, server: example.invalid, port: 443, uuid: 00000000-0000-0000-0000-000000000001, tls: true, servername: example.invalid, client-fingerprint: chrome, network: ws, ws-opts: {path: /ws, headers: {Host: example.invalid}}}\n",
            " - {name: Server, type: vless, server: example.invalid, port: 443, uuid: 00000000-0000-0000-0000-000000000001, tls: true, servername: example.invalid, flow: xtls-rprx-vision, client-fingerprint: chrome, reality-opts: {public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: '1234'}}\n",
            " - {name: Server, type: vless, server: example.invalid, port: 443, uuid: 00000000-0000-0000-0000-000000000001, tls: true, network: grpc, grpc-opts: {grpc-service-name: fixture}}\n",
            " - {name: Server, type: vless, server: example.invalid, port: 443, uuid: 00000000-0000-0000-0000-000000000001, tls: true, network: ws, ws-opts: {path: /upgrade, v2ray-http-upgrade: true, headers: {Host: example.invalid}}}\n",
            " - {name: Server, type: vmess, server: example.invalid, port: 443, uuid: 00000000-0000-0000-0000-000000000001, alterId: 0, cipher: auto, tls: true}\n",
            " - {name: Server, type: trojan, server: example.invalid, port: 443, password: fixture, sni: example.invalid}\n",
            " - {name: Server, type: ss, server: example.invalid, port: 443, cipher: aes-128-gcm, password: fixture}\n"
        };
        async Task NativeCheck(string text, string label)
        {
            var path = Path.Combine(home, "check.json"); File.WriteAllText(path, text);
            var info = CoreProcessManager.CreateStartInfo(executable, path, home); info.ArgumentList[0] = "check";
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(deadline.Token);
            var result = await stdout + await stderr;
            Check(process.ExitCode == 0, label + (process.ExitCode == 0 ? "" : ": " + result));
        }
        foreach (var (fixture, index) in fixtures.Select((value, index) => (value, index)))
        {
            var doc = ClashConfigDocument.Parse(Path.Combine(home, "fixture.yaml"), prefix + fixture + suffix);
            await NativeCheck(SingBoxConfig.Build(doc, api.Port, api.Secret, true), "native sing-box accepts supported proxy fixture " + index);
        }
        var document = ClashConfigDocument.Parse(Path.Combine(home, "fixture.yaml"), prefix + fixtures[0] + suffix);
        foreach (var encoding in new[] { "xudp", "packetaddr", "", "none" })
        {
            var encoded = ClashConfigDocument.Parse(Path.Combine(home, "encoding.yaml"), prefix + fixtures[2].Replace("tls: true", "packet-encoding: '" + encoding + "', tls: true") + suffix);
            var runtimeText = SingBoxConfig.Build(encoded, api.Port, api.Secret, true);
            using var json = JsonDocument.Parse(runtimeText);
            Check(json.RootElement.GetProperty("outbounds")[1].GetProperty("packet_encoding").GetString() == (encoding == "none" ? "" : encoding), "explicit VLESS packet encoding is preserved: " + (encoding.Length == 0 ? "disabled" : encoding));
            await NativeCheck(runtimeText, "real sing-box validates explicit VLESS packet encoding");
        }
        var original = document.BuildText(document.Rules);
        passed += await RunNetworkCompatibilityChecks(home, api, NativeCheck);
        passed += await RunYouTubeTcpFallbackChecks(home, api, NativeCheck);
        var dnsProfile = ClashConfigDocument.Parse(Path.Combine(home, "dns-custom.yaml"), prefix + fixtures[0] + suffix +
            "dns:\n ipv6: false\n nameserver: [https://9.9.9.9/dns-query, https://8.8.8.8/dns-query]\n nameserver-policy: {'+.policy.example': 'https://1.0.0.1/dns-query'}\n direct-nameserver: [8.8.4.4]\n");
        var customDnsText = SingBoxConfig.Build(dnsProfile, api.Port, api.Secret, true);
        using (var dnsJson = JsonDocument.Parse(customDnsText))
        {
            var dnsRoot = dnsJson.RootElement.GetProperty("dns");
            var servers = dnsRoot.GetProperty("servers").EnumerateArray().ToArray(); var dnsRules = dnsRoot.GetProperty("rules").EnumerateArray().ToArray();
            Check(servers.Any(server => server.TryGetProperty("server", out var host) && host.GetString() == "9.9.9.9")
                && servers.Any(server => server.TryGetProperty("server", out var host) && host.GetString() == "8.8.8.8"), "sing-box retains both configured DNS upstreams rather than silently replacing them");
            Check(dnsRules.Any(rule => rule.TryGetProperty("domain_suffix", out var names) && names[0].GetString() == "policy.example"), "sing-box preserves supported per-domain DNS policy");
            Check(dnsRules.Any(rule => rule.TryGetProperty("race", out var race) && race.GetBoolean())
                && servers.Where(server => server.TryGetProperty("tag", out var name) && name.GetString()!.Contains("remote-")).All(server => server.GetProperty("detour").GetString()!.EndsWith("-dns-upstream"))
                && dnsJson.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().Any(rule =>
                    rule.TryGetProperty("inbound", out var inbound) && inbound[0].GetString()!.EndsWith("-dns-upstream-in")
                    && rule.TryGetProperty("outbound", out var outbound) && outbound.GetString() == "VPN"
                    && rule.EnumerateObject().Count() == 3),
                "independent DNS upstreams race through rule-based routing whose full-TUN fallback remains VPN, with no direct DNS fallback");
            Check(dnsRules[0].GetProperty("query_type")[0].GetString() == "AAAA" && dnsRules[0].GetProperty("action").GetString() == "predefined", "disabled IPv6 DNS is honored by sing-box including its fake-IP path");
            var cache = dnsJson.RootElement.GetProperty("experimental").GetProperty("cache_file");
            Check(cache.GetProperty("enabled").GetBoolean() && cache.GetProperty("store_fakeip").GetBoolean(), "sing-box enables its persistent fake-IP cache (abrupt exit remains an upstream limitation)");
            using var second = JsonDocument.Parse(SingBoxConfig.Build(dnsProfile, api.Port, new string('A', 64), false));
            Check(cache.GetProperty("path").GetString() == second.RootElement.GetProperty("experimental").GetProperty("cache_file").GetProperty("path").GetString(),
                "fake-IP cache location survives session-secret and TUN-mode changes");
        }
        await NativeCheck(customDnsText, "real sing-box accepts resolver races, DNS policies and persistent fake-IP cache");
        var aliasProfile = ClashConfigDocument.Parse(Path.Combine(home, "aliases.yaml"), prefix + fixtures[0] +
            " - {name: Bypass, type: direct}\n - {name: Blocked, type: reject}\n" + suffix.Replace("rules:\n", " - {name: BlockGroup, type: select, proxies: [Blocked]}\nrules:\n")
                .Replace("DOMAIN-SUFFIX,direct.example,DIRECT", "DOMAIN-SUFFIX,direct.example,Bypass")
                .Replace("DOMAIN,blocked.example,REJECT", "DOMAIN,blocked.example,BlockGroup"));
        foreach (var fullTunnel in new[] { false, true })
        {
            var aliasText = SingBoxConfig.Build(aliasProfile, api.Port, api.Secret, fullTunnel);
            await NativeCheck(aliasText, "real sing-box accepts named direct and blocking exceptions, full TUN=" + fullTunnel);
            using var aliases = JsonDocument.Parse(aliasText);
            var aliasRules = aliases.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
            Check(aliasRules.Any(rule => rule.TryGetProperty("domain", out var value) && value[0].GetString() == "blocked.example" && rule.GetProperty("action").GetString() == "reject")
                && aliasRules.Any(rule => rule.TryGetProperty("domain_suffix", out var value) && value[0].GetString() == "direct.example" && rule.GetProperty("outbound").GetString() == "DIRECT"),
                "named exceptions retain both direct and blocking semantics, full TUN=" + fullTunnel);
        }
        var aliasPath = Path.Combine(home, "alias-validation.yaml");
        File.WriteAllText(aliasPath, aliasProfile.BuildText(aliasProfile.Rules));
        Check((await CoreProcessManager.ValidateAsync(executable, aliasPath, deadline.Token)).Success,
            "actual YAML pre-validation accepts named blocking aliases before full-TUN startup");
        Check((await CoreProcessManager.ValidateAsync(executable, aliasPath, deadline.Token, fullTunnel: true)).Success,
            "actual YAML pre-validation uses full-TUN rules when that mode is requested");
        // Use an explicit minimal fixture, independent of YAML reformatting.
        var rejectDrop = prefix + fixtures[0] + "proxy-groups:\n - {name: VPN, type: select, proxies: [Server]}\n - {name: DropGroup, type: select, proxies: [REJECT-DROP]}\nrules:\n - DOMAIN,drop.example,DropGroup\n - MATCH,DropGroup\n";
        var dropText = SingBoxConfig.Build(ClashConfigDocument.Parse(Path.Combine(home, "drop-alias.yaml"), rejectDrop), api.Port, api.Secret, false);
        using (var drop = JsonDocument.Parse(dropText))
        {
            var dropRules = drop.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
            // Other protective drop rules (for example the adapter's own IP)
            // are expected; identify the two user rules instead of counting all drops.
            Check(dropRules.Any(rule => rule.TryGetProperty("domain", out var domain) && domain[0].GetString() == "drop.example"
                    && rule.GetProperty("action").GetString() == "reject" && rule.GetProperty("method").GetString() == "drop")
                && dropRules[^1].GetProperty("action").GetString() == "reject"
                && dropRules[^1].GetProperty("method").GetString() == "drop" && dropRules[^1].EnumerateObject().Count() == 2,
                "named REJECT-DROP preserves drop for domain and catch-all rules");
        }
        await NativeCheck(dropText, "real sing-box accepts named REJECT-DROP rules and catch-all");
        var mixedBlocked = ClashConfigDocument.Parse(Path.Combine(home, "mixed-block-alias.yaml"),
            prefix + fixtures[0] + " - {name: Blocked, type: reject}\nproxy-groups:\n - {name: VPN, type: select, proxies: [Server, Blocked]}\nrules:\n - MATCH,VPN\n");
        try { SingBoxConfig.Build(mixedBlocked, api.Port, api.Secret, false); throw new Exception("Mixed VPN/block selector was silently simplified"); }
        catch (InvalidDataException) { Check(true, "mixed VPN/block selector fails safely rather than losing a selectable blocking member"); }

        foreach (var headerName in new[] { "Host", "host", "HOST" })
        {
            var upgradeYaml = prefix + fixtures[5].Replace("headers: {Host: example.invalid}", "headers: {" + headerName + ": edge.example.invalid, x-fixture: fixture}")
                .Replace("tls: true", "tls: true, servername: tls.example.invalid") + suffix;
            var upgradeText = SingBoxConfig.Build(ClashConfigDocument.Parse(Path.Combine(home, "upgrade-host.yaml"), upgradeYaml), api.Port, api.Secret, false);
            using var upgrade = JsonDocument.Parse(upgradeText);
            var outbound = upgrade.RootElement.GetProperty("outbounds")[1]; var transport = outbound.GetProperty("transport");
            Check(transport.GetProperty("host").GetString() == "edge.example.invalid"
                && outbound.GetProperty("tls").GetProperty("server_name").GetString() == "tls.example.invalid"
                && transport.GetProperty("headers").GetProperty("X-Fixture").GetString() == "fixture"
                && !transport.GetProperty("headers").EnumerateObject().Any(header => header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)),
                "HTTPUpgrade preserves case-insensitive Host independently of explicit TLS SNI: " + headerName);
            await NativeCheck(upgradeText, "real sing-box accepts HTTPUpgrade Host header spelling " + headerName);
        }
        foreach (var upgrade in new[] { true, false })
        {
            var duplicateHeaders = prefix + fixtures[5].Replace("headers: {Host: example.invalid}", "headers: {Host: example.invalid, host: alternate.invalid}") + suffix;
            if (!upgrade) duplicateHeaders = duplicateHeaders.Replace("v2ray-http-upgrade: true", "v2ray-http-upgrade: false");
            try { SingBoxConfig.Build(ClashConfigDocument.Parse(Path.Combine(home, "duplicate-host.yaml"), duplicateHeaders), api.Port, api.Secret, false); throw new Exception("Ambiguous duplicate Host accepted"); }
            catch (InvalidDataException error) { Check(!error.Message.Contains("alternate.invalid"), "case-colliding HTTP headers are rejected without leaking values, upgrade=" + upgrade); }
        }
        var duplicateCustomHeader = prefix + fixtures[2].Replace("headers: {Host: example.invalid}", "headers: {X-Fixture: first, x-fixture: second}") + suffix;
        try { SingBoxConfig.Build(ClashConfigDocument.Parse(Path.Combine(home, "duplicate-custom-header.yaml"), duplicateCustomHeader), api.Port, api.Secret, false); throw new Exception("Case-colliding custom headers accepted"); }
        catch (InvalidDataException) { Check(true, "duplicate header rejection applies to every HTTP field, not just Host"); }
        var lowercaseWs = prefix + fixtures[2].Replace("Host: example.invalid", "host: edge.example.invalid") + suffix;
        using (var lowercase = JsonDocument.Parse(SingBoxConfig.Build(ClashConfigDocument.Parse(Path.Combine(home, "ws-lowercase-host.yaml"), lowercaseWs), api.Port, api.Secret, false)))
        {
            var outbound = lowercase.RootElement.GetProperty("outbounds")[1];
            Check(outbound.GetProperty("transport").GetProperty("headers").GetProperty("Host").GetString() == "edge.example.invalid",
                "ordinary WebSocket retains a canonical Host header after case-insensitive parsing");
        }
        var unsupportedDns = ClashConfigDocument.Parse(Path.Combine(home, "advanced-dns.yaml"), prefix + fixtures[0] + suffix + "dns:\n nameserver-policy: {'geosite:private': '1.1.1.1'}\n");
        try { SingBoxConfig.Build(unsupportedDns, api.Port, api.Secret, false); throw new Exception("Advanced DNS policy silently dropped"); }
        catch (InvalidDataException error) { Check(error.Message.Contains("nameserver-policy") && !error.Message.Contains("private"), "untranslatable DNS policy fails safely rather than silently changing its meaning"); }
        using (var full = JsonDocument.Parse(SingBoxConfig.Build(document, api.Port, api.Secret, true)))
        {
            var root = full.RootElement;
            var tun = root.GetProperty("inbounds").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "tun");
            var route = root.GetProperty("route");
            Check(tun.GetProperty("type").GetString() == "tun" && tun.GetProperty("stack").GetString() == "gvisor" && tun.GetProperty("mtu").GetInt32() == 1280, "native TUN uses gvisor and conservative MTU");
            Check(tun.GetProperty("auto_route").GetBoolean() && tun.GetProperty("strict_route").GetBoolean() && tun.GetProperty("dns_mode").GetString() == "hijack", "native TUN owns routing and DNS hijack");
            Check(route.GetProperty("final").GetString() == "VPN", "full tunnel sends unmatched traffic through VPN");
            var rules = route.GetProperty("rules").EnumerateArray().ToArray();
            Check(rules.Any(rule => rule.TryGetProperty("process_path_regex", out var processes) && System.Text.RegularExpressions.Regex.IsMatch(@"C:\Apps\sample.EXE", processes[0].GetString()!) && rule.GetProperty("outbound").GetString() == "DIRECT"), "full tunnel retains case-insensitive direct application exceptions");
            Check(rules.Any(rule => rule.TryGetProperty("domain_suffix", out var domains) && domains[0].GetString() == "direct.example" && rule.GetProperty("outbound").GetString() == "DIRECT"), "full tunnel retains direct domain exceptions");
            Check(rules.Any(rule => rule.TryGetProperty("domain", out var domains) && domains[0].GetString() == "blocked.example" && rule.GetProperty("action").GetString() == "reject"), "full tunnel retains blocking exceptions");
        }
        Check(document.BuildText(document.Rules) == original, "sing-box generation leaves saved YAML untouched");
        foreach (var unsupported in new[] { "plugin: fixture", "smux: {enabled: true}" })
        {
            var doc = ClashConfigDocument.Parse(Path.Combine(home, "bad.yaml"), prefix + fixtures[0].Replace("port: 9999", "port: 9999, " + unsupported) + suffix);
            try { SingBoxConfig.Build(doc, api.Port, api.Secret, false); throw new Exception("Unsupported proxy silently accepted"); }
            catch (InvalidDataException error) { Check(error.Message.Contains(unsupported.Split(':')[0]) && !error.Message.Contains("fixture"), "unsupported proxy reports the parameter name, without its value"); }
        }
        var unknown = ClashConfigDocument.Parse(Path.Combine(home, "bad-rule.yaml"), prefix + fixtures[0] + suffix.Replace("MATCH,DIRECT", "GEOIP,CN,DIRECT\n - MATCH,DIRECT"));
        try { SingBoxConfig.Build(unknown, api.Port, api.Secret, false); throw new Exception("Unsupported rule silently accepted"); }
        catch (InvalidDataException) { Check(true, "unsupported routing rule fails closed"); }
        await NativeCheck(SingBoxConfig.Build(ClashConfigDocument.Parse(Path.Combine(home, "empty.yaml"), BundledResources.TemplateText), api.Port, api.Secret, false), "empty editable profile can still be validated");
        var yamlPath = Path.Combine(home, "selected.yaml"); File.WriteAllText(yamlPath, prefix + fixtures[0] + suffix);
        var validation = await CoreProcessManager.ValidateAsync(executable, yamlPath, deadline.Token);
        Check(validation.Success, "existing YAML import/apply validation uses sing-box JSON automatically");
        using var portReservation = new TcpListener(IPAddress.Loopback, 0); portReservation.Start();
        var speedPort = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        var runtime = Path.Combine(home, "runtime.json");
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var hits = 0;
        using var upstreamLife = new CancellationTokenSource();
        var accept = Task.Run(async () =>
        {
            try
            {
                while (!upstreamLife.IsCancellationRequested)
                {
                    using var socket = await upstream.AcceptTcpClientAsync(upstreamLife.Token);
                    var hello = new byte[3];
                    if (await socket.GetStream().ReadAsync(hello, upstreamLife.Token) > 0 && hello[0] == 5) Interlocked.Increment(ref hits);
                    // Refuse before DNS/TLS: no external request can leave this fixture.
                }
            }
            catch (OperationCanceledException) { }
        });
        var speedDoc = ClashConfigDocument.Parse(Path.Combine(home, "speed.yaml"), prefix + fixtures[0].Replace("9999", upstreamPort.ToString()) + suffix.Replace("MATCH,DIRECT", "MATCH,REJECT"));
        var text = SingBoxConfig.Build(speedDoc, api.Port, api.Secret, false, speedPort, includeTun: false);
        File.WriteAllText(runtime, text);
        using (var json = JsonDocument.Parse(text))
        {
            Check(json.RootElement.GetProperty("inbounds").EnumerateArray().All(node => node.GetProperty("type").GetString() != "tun"), "network smoke test never creates a TUN adapter");
            var forced = json.RootElement.GetProperty("route").GetProperty("rules")[0];
            Check(forced.GetProperty("outbound").GetString() == "VPN" && forced.GetProperty("inbound").GetArrayLength() == 1, "speed measurement is forced through VPN independently of direct fallback");
        }
        var core = new CoreProcessManager();
        try
        {
            await core.StartAsync(CoreProcessManager.CreateStartInfo(executable, runtime, home), deadline.Token);
            Check((await api.ReadVersionAsync(deadline.Token)).Contains(CoreBackend.SingBoxVersion), "real sing-box responds through authenticated diagnostics API");
            var snapshot = await api.ReadAsync("VPN", deadline.Token);
            Check(snapshot.Selection == "Server" && snapshot.Connections.Count == 0, "real sing-box supports selected connection and connections table");
            var traffic = await api.ReadTrafficAsync(deadline.Token);
            Check(traffic.Upload >= 0 && traffic.Download >= 0, "real sing-box supports existing live traffic telemetry");
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var response = await http.GetAsync($"http://127.0.0.1:{api.Port}/version", deadline.Token);
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated local API access is denied");
            using var speed = new VpnSpeedTest(speedPort, api.Secret, socksProxy: true);
            try { await speed.MeasureAsync(deadline.Token); throw new Exception("Refused fixture returned a speed result"); }
            catch (HttpRequestException) { Check(hits > 0, "real sing-box speed listener authenticates and uses VPN even when normal rules block traffic"); }
        }
        finally
        {
            Check(await core.StopAsync() && !core.HasTrackedProcess, "real sing-box is stopped and ownership released");
            upstreamLife.Cancel(); upstream.Stop(); await accept;
        }
        var settings = AppSettings.Load(Path.Combine(home, "settings.json"));
        settings.ConfigPath = yamlPath; settings.CorePath = executable; settings.AutoCheckUpdates = false;
        var owner = new MainWindow(settings, Path.Combine(home, "ui.log")) { ShowInTaskbar = false };
        owner.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var saves = new List<SettingsDialogSelection>();
        Exception? uiError = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            var dialog = owner.OwnedWindows.OfType<Window>().FirstOrDefault(window => window.Title == "НАСТРОЙКИ");
            if (dialog is null) return; timer.Stop();
            try
            {
                var picker = Descendants<ComboBox>(dialog).Single(combo => combo.Name == "CoreBackendPicker");
                var save = Descendants<Button>(dialog).Single(button => Equals(button.Content, "СОХРАНИТЬ"));
                Check(picker.SelectedIndex == 1, "settings reflect existing sing-box selection");
                picker.SelectedIndex = 0; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(saves.Count == 1 && Path.GetFileName(saves[0].CorePath) == "mihomo.exe", "Mihomo rollback choice is saved deterministically");
                picker.SelectedIndex = 1; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(saves.Count == 2 && CoreBackend.IsSingBox(saves[1].CorePath), "sing-box selection is saved without asynchronous picker races");
                Check(dialog.IsVisible, "saving engine choice keeps settings open");
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "VPN-профиль")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                picker.BringIntoView(); dialog.UpdateLayout(); CaptureInteraction(dialog, Path.Combine(home, "engine-picker.png"));
            }
            catch (Exception error) { uiError = error; }
            finally { dialog.Close(); }
        };
        try
        {
            timer.Start();
            UtilityDialogs.ShowSettings(owner, yamlPath, executable, "Океан", false, 1, false,
                selection => { saves.Add(selection); return Task.FromResult<SettingsDialogSelection?>(selection); });
            if (uiError is not null) throw uiError;
        }
        finally
        {
            timer.Stop();
            typeof(MainWindow).GetField("_allowClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(owner, true);
            owner.Close();
        }
        return passed + await RunSingBoxProcessChecks(scratch);
    }
}
