using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal partial class Program
{
    private static async Task<int> RunTunnelAndSpeedChecks(string scratch, string fakeExe)
    {
        var count = 0;
        count += await RunSpeedTransportChecks(scratch);
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        const string original = "mode: rule\nipv6: false\nproxies:\n - {name: Server, type: socks5, server: 127.0.0.1, port: 9999}\nproxy-groups:\n - {name: VPN, type: select, proxies: [DIRECT, Server, REJECT]}\nlisteners:\n - {name: user-listener, type: socks, listen: 127.0.0.1, port: 19876}\ntun:\n enable: true\n auto-route: false\n route-exclude-address: [0.0.0.0/0]\n exclude-interface: [ethernet]\ndns:\n enable: true\n nameserver: [https://1.1.1.1/dns-query]\n nameserver-policy: {example.invalid: 1.1.1.1}\nrules:\n - DOMAIN,example.invalid,DIRECT\n - PROCESS-NAME,ProcessFixture.exe,REJECT\n - MATCH,DIRECT\n";
        var path = Path.Combine(scratch, "tunnel-profile.yaml");
        File.WriteAllText(path, original);
        var document = ClashConfigDocument.Load(path);
        using var api = new CoreDiagnostics();
        var full = document.BuildRuntimeText(api.Port, api.Secret, true, api.Port == 19877 ? 19878 : 19877);
        YamlMappingNode Root(string yaml) { var stream = new YamlStream(); stream.Load(new StringReader(yaml)); return (YamlMappingNode)stream.Documents[0].RootNode; }
        YamlNode Value(YamlMappingNode root, string key) => root.Children[new YamlScalarNode(key)];
        var root = Root(full);
        var tun = (YamlMappingNode)Value(root, "tun");
        Check(((YamlSequenceNode)Value(root, "rules")).Children.Select(node => node.ToString()).SequenceEqual(new[] { "DOMAIN,example.invalid,DIRECT", "PROCESS-NAME,ProcessFixture.exe,REJECT", "MATCH,VPN" }), "TUN retains explicit DIRECT and REJECT exceptions before its VPN fallback");
        Check(new[] { "enable", "auto-route", "strict-route", "auto-detect-interface" }.All(key => Value(tun, key).ToString() == "true"), "full tunnel enables system routes with strict routing and loop avoidance");
        Check(!tun.Children.ContainsKey(new YamlScalarNode("route-exclude-address")) && !tun.Children.ContainsKey(new YamlScalarNode("exclude-interface")), "profile split-route exclusions cannot bypass full tunnel");
        Check(Value(root, "ipv6").ToString() == "true" && Value(tun, "inet6-address") is YamlSequenceNode && ((YamlSequenceNode)Value(tun, "dns-hijack")).Children.Count == 2, "full tunnel configures IPv6 and TCP/UDP DNS interception");
        var dns = (YamlMappingNode)Value(root, "dns");
        Check(Value(dns, "respect-rules").ToString() == "true" && Value(dns, "proxy-server-nameserver") is YamlSequenceNode
            && dns.Children.ContainsKey(new YamlScalarNode("nameserver-policy")), "normal DNS follows routing rules while server bootstrap and custom domain policies are retained");
        Check(((YamlSequenceNode)Value(dns, "nameserver")).Children.Select(node => node.ToString()).SequenceEqual(
            new[] { "https://1.1.1.1/dns-query", "https://dns.google/dns-query" }), "single default resolver gains an independent encrypted resolver without plaintext fallback");
        Check(Value(dns, "ipv6").ToString() == "false" && Value(dns, "prefer-h3").ToString() == "false",
            "full tunnel does not re-enable DNS IPv6 against a disabled profile or use HTTP3 for rule-routed DNS");
        Check(Value(tun, "mtu").ToString() == "1280" && Value(tun, "stack").ToString() == "gvisor", "default full TUN uses a conservative MTU and firewall-independent userspace TCP");
        var customText = original.Replace(" auto-route: false", " auto-route: false\n stack: system\n mtu: 1400")
            .Replace(" nameserver: [https://1.1.1.1/dns-query]", " nameserver: [https://resolver.example/dns-query]\n fallback: [tls://9.9.9.9]\n fallback-filter: {geoip: false}");
        var custom = Root(ClashConfigDocument.Parse(path, customText).BuildRuntimeText(api.Port, api.Secret, true));
        var customDns = (YamlMappingNode)Value(custom, "dns"); var customTun = (YamlMappingNode)Value(custom, "tun");
        Check(Value(customDns, "nameserver").ToString().Contains("resolver.example") && customDns.Children.ContainsKey(new YamlScalarNode("fallback"))
            && customDns.Children.ContainsKey(new YamlScalarNode("fallback-filter")), "TUN keeps custom DNS servers, fallback and filtering rather than replacing a user's network configuration");
        Check(Value(customTun, "mtu").ToString() == "1400" && Value(customTun, "stack").ToString() == "system", "explicit custom MTU and non-default stack are not overwritten");
        var emptyDns = (YamlMappingNode)Value(Root(ClashConfigDocument.Parse(path, original.Replace(" nameserver: [https://1.1.1.1/dns-query]",
            " nameserver: []\n default-nameserver: []\n proxy-server-nameserver: []")).BuildRuntimeText(api.Port, api.Secret, true)), "dns");
        Check(new[] { "nameserver", "default-nameserver", "proxy-server-nameserver" }.All(key => Value(emptyDns, key) is YamlSequenceNode { Children.Count: 2 }),
            "empty resolver lists gain usable defaults and cannot cause bootstrap recursion or validation failure");
        var dnsIpv6 = (YamlMappingNode)Value(Root(ClashConfigDocument.Parse(path, original.Replace("ipv6: false", "ipv6: true")
            .Replace(" enable: true\n nameserver:", " enable: true\n ipv6: true\n nameserver:")).BuildRuntimeText(api.Port, api.Secret, true)), "dns");
        Check(Value(dnsIpv6, "ipv6").ToString() == "true", "an explicitly enabled IPv6 DNS profile remains enabled");
        var mixed = (YamlMappingNode)Value(Root(ClashConfigDocument.Parse(path, original.Replace(" auto-route: false", " auto-route: false\n stack: mixed"))
            .BuildRuntimeText(api.Port, api.Secret, true)), "tun");
        Check(Value(mixed, "stack").ToString() == "gvisor", "existing default mixed-stack profiles use userspace TCP in full TUN");
        var group = (YamlMappingNode)((YamlSequenceNode)Value(root, "proxy-groups")).Children.Single();
        Check(((YamlSequenceNode)Value(group, "proxies")).Children.Single().ToString() == "Server", "full-tunnel groups do not retain cached direct/blocking choices");
        var listeners = (YamlSequenceNode)Value(root, "listeners");
        var speed = (YamlMappingNode)listeners.Children.Last();
        Check(listeners.Children.Count == 2 && Value(speed, "listen").ToString() == "127.0.0.1" && Value(speed, "proxy").ToString() == "VPN", "speed listener preserves user listeners and forces VPN independently of routing rules");
        Check(((YamlSequenceNode)Value(speed, "users")).Children.Single() is YamlMappingNode user && Value(user, "password").ToString() == api.Secret, "speed listener uses per-session authentication rather than an exposed anonymous proxy");
        var normal = Root(document.BuildRuntimeText(api.Port, api.Secret));
        Check(((YamlSequenceNode)Value(normal, "rules")).Children.Count == 3 && Value((YamlMappingNode)Value(normal, "tun"), "auto-route").ToString() == "false", "disabling full tunnel restores original rules and TUN settings");
        Check(File.ReadAllText(path) == original && document.SourceHash == ClashConfigDocument.Hash(original), "runtime tunnel and speed listener never alter source YAML");
        var extraRules = original.Replace(" - MATCH,DIRECT", " - IP-CIDR,192.0.2.0/24,DIRECT,no-resolve\n - DOMAIN,via.example,VPN\n - MATCH,DIRECT");
        var extraRuntime = Root(ClashConfigDocument.Parse(path, extraRules).BuildRuntimeText(api.Port, api.Secret, true));
        Check(((YamlSequenceNode)Value(extraRuntime, "rules")).Children.Select(node => node.ToString()).SequenceEqual(new[] { "DOMAIN,example.invalid,DIRECT", "PROCESS-NAME,ProcessFixture.exe,REJECT", "IP-CIDR,192.0.2.0/24,DIRECT,no-resolve", "MATCH,VPN" }), "TUN preserves no-resolve direct exceptions and suppresses redundant explicit VPN rules");
        var nested = original.Replace("proxies: [DIRECT, Server, REJECT]", "proxies: [DirectOnly, Nested]")
            .Replace("listeners:", " - {name: DirectOnly, type: select, proxies: [DIRECT]}\n - {name: Nested, type: select, proxies: [Server, DIRECT]}\nlisteners:");
        var nestedRoot = Root(ClashConfigDocument.Parse(path, nested).BuildRuntimeText(api.Port, api.Secret, true));
        var nestedGroups = ((YamlSequenceNode)Value(nestedRoot, "proxy-groups")).Children.Cast<YamlMappingNode>().ToArray();
        Check(((YamlSequenceNode)Value(nestedGroups[0], "proxies")).Children.Single().ToString() == "Nested"
            && ((YamlSequenceNode)Value(nestedGroups[1], "proxies")).Children.Single().ToString() == "DIRECT"
            && ((YamlSequenceNode)Value(nestedGroups[2], "proxies")).Children.Single().ToString() == "Server",
            "full tunnel prunes nested bypasses without breaking unrelated direct-only groups");
        try { ClashConfigDocument.Parse(path, original.Replace("proxies: [DIRECT, Server, REJECT]", "proxies: [DIRECT, REJECT]")).BuildRuntimeText(api.Port, api.Secret, true); throw new Exception("Direct-only VPN accepted"); }
        catch (InvalidDataException) { Check(true, "full tunnel refuses a VPN group with no actual VPN choice"); }
        try { document.BuildRuntimeText(api.Port, api.Secret, false, api.Port); throw new Exception("Port collision accepted"); }
        catch (ArgumentException) { Check(true, "speed listener cannot reuse the controller port"); }
        var runtime = Path.Combine(scratch, "full-tunnel-runtime.yaml");
        var aliasText = original.Replace("proxy-groups:", " - {name: Bypass, type: direct}\nproxy-groups:\n - {name: DirectGroup, type: select, proxies: [Bypass]}\n - {name: BlockGroup, type: select, proxies: [REJECT]}\n")
            .Replace(" - MATCH,DIRECT", " - DOMAIN,alias-direct.example,Bypass\n - DOMAIN,group-direct.example,DirectGroup\n - DOMAIN,group-block.example,BlockGroup\n - MATCH,DIRECT");
        var aliasRuntime = ClashConfigDocument.Parse(path, aliasText).BuildRuntimeText(api.Port, api.Secret, true);
        var aliasRules = ((YamlSequenceNode)Value(Root(aliasRuntime), "rules")).Children.Select(node => node.ToString()).ToArray();
        Check(aliasRules.Contains("DOMAIN,alias-direct.example,DIRECT") && aliasRules.Contains("DOMAIN,group-direct.example,DIRECT")
            && aliasRules.Contains("DOMAIN,group-block.example,REJECT"), "full TUN retains deterministic direct and blocking aliases, including nested selector groups");
        File.WriteAllText(runtime, aliasRuntime);
        Check((await CoreProcessManager.ValidateAsync(Path.Combine(AppContext.BaseDirectory, "mihomo.exe"), runtime, default)).Success,
            "real Mihomo validates full TUN with preserved custom exception aliases");
        File.WriteAllText(runtime, full);
        var validation = await CoreProcessManager.ValidateAsync(Path.Combine(AppContext.BaseDirectory, "mihomo.exe"), runtime, default);
        Check(validation.Success, "bundled real Mihomo accepts full-tunnel and authenticated speed-listener YAML without starting VPN");
        using var directApi = new CoreDiagnostics(new DiagnosticHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"type\":\"Direct\"}") }));
        Check(await directApi.ReadSelectionAsync("CustomDirect", default) == "DIRECT", "renamed direct proxies are detected before a speed test");
        Check(VpnSpeedTest.FormatCapacity(125) == $"{1d:0.0} кбит/с"
            && VpnSpeedTest.FormatCapacity(12_500_000) == $"{100d:0.0} Мбит/с"
            && VpnSpeedTest.FormatCapacity(125_000_000) == $"{1d:0.0} Гбит/с",
            "VPN capacity converts streamed bytes per second into decimal bit-rate units, not live load percentages");
        Check(new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity }
            .All(value => VpnSpeedTest.FormatCapacity(value) == "—"), "invalid or missing capacity never produces a fabricated maximum speed");

        VpnSpeedTest.DownloadSamples Sample(double headerDelay, bool slowTail = false)
        {
            var samples = new VpnSpeedTest.DownloadSamples();
            samples.Record(64 * 1024, headerDelay);
            for (var bin = 0; bin < 25; bin++)
                samples.Record(120_000, headerDelay + bin * .1 + .05);
            if (slowTail) samples.Record(1024, headerDelay + 12);
            return samples;
        }
        var normalRate = Sample(0).ReadRate();
        Check(normalRate == 1_200_000 && Sample(4).ReadRate() == normalRate,
            "DNS/TLS/response-header delay is outside the shared aggregate data-transfer clock");
        Check(Sample(0, true).ReadRate() == normalRate,
            "a stalled final stream does not dilute a completed sustained one-second measurement");
        var burst = new VpnSpeedTest.DownloadSamples();
        burst.Record(64 * 1024, 0); burst.Record(1_000_000, .02);
        Check(burst.ReadRate() == 0, "a prefetched buffer or short burst cannot masquerade as maximum VPN speed");
        var sparse = new VpnSpeedTest.DownloadSamples();
        sparse.Record(64 * 1024, 0); sparse.Record(100_000, 1);
        Check(sparse.ReadRate() == 0, "a long duration without enough measured bytes is not a calibration");

        using var test = new VpnSpeedTest(19877, api.Secret, new SpeedHandler());
        var measurementProgress = new SpeedProgressRecorder();
        var result = await test.MeasureAsync(default, measurementProgress);
        var percentages = measurementProgress.Values.ToArray();
        Check(percentages.First() == 0 && percentages.Last() == 100 && percentages.Zip(percentages.Skip(1), (a, b) => a <= b).All(value => value),
            "speed measurement emits monotonic progress and completes only after a successful result");
        Check(result.DownloadedBytes == VpnSpeedTest.MaxBytes && result.DownloadBytesPerSecond > 0 && double.IsFinite(result.DownloadBytesPerSecond), "parallel speed test counts streamed bytes and measures a finite aggregate download rate");
        using var cancellation = new CancellationTokenSource(30);
        using var cancelTest = new VpnSpeedTest(19877, api.Secret, new SpeedHandler());
        var cancelledProgress = new SpeedProgressRecorder();
        try { await cancelTest.MeasureAsync(cancellation.Token, cancelledProgress); throw new Exception("Cancelled speed test accepted"); }
        catch (OperationCanceledException) { Check(true, "user cancellation ends all test transfers without storing a partial calibration"); }
        Check(!cancelledProgress.Values.Contains(100), "a cancelled measurement never emits a successful 100 percent");
        using var badTest = new VpnSpeedTest(19877, api.Secret, new DiagnosticHandler(_ => new(HttpStatusCode.Redirect) { Content = new StringContent("not a speed payload") }));
        try { await badTest.MeasureAsync(default); throw new Exception("Redirect accepted"); }
        catch (HttpRequestException) { Check(true, "failed or redirected download is not treated as a speed measurement"); }
        using var shortTest = new VpnSpeedTest(19877, api.Secret, new ShortSpeedHandler());
        var shortResult = await shortTest.MeasureAsync(default);
        Check(shortResult.DownloadedBytes == VpnSpeedTest.WarmupBytes * VpnSpeedTest.Streams + 8_000_000
            && shortResult.DownloadBytesPerSecond > 0, "short valid binary responses are measured within the request-allocation byte cap");
        using var retryTest = new VpnSpeedTest(19877, api.Secret, new ShortSpeedHandler(true));
        try { await retryTest.MeasureAsync(default); throw new Exception("Warm-up was reported as a completed speed measurement"); }
        catch (HttpRequestException) { Check(true, "warm-up success alone is not reported when all measurement requests fail"); }
        using var partialTest = new VpnSpeedTest(19877, api.Secret, new PartialSpeedHandler());
        var partialResult = await partialTest.MeasureAsync(default);
        Check(partialResult.DownloadedBytes == VpnSpeedTest.WarmupBytes * VpnSpeedTest.Streams + VpnSpeedTest.RequestBytes * VpnSpeedTest.Streams
            && partialResult.DownloadBytesPerSecond > 0, "a later service failure preserves a valid measured transfer, not warm-up timing");
        var parallelHandler = new ParallelSpeedHandler();
        using var parallelTest = new VpnSpeedTest(19877, api.Secret, parallelHandler);
        var parallelResult = await parallelTest.MeasureAsync(default);
        Check(parallelResult.DownloadedBytes <= VpnSpeedTest.MaxBytes && parallelHandler.MaximumActive == VpnSpeedTest.Streams
            && parallelHandler.MeasurementRequests > VpnSpeedTest.Streams,
            "measurement workers refill parallel requests while preserving four-flow and 64 MB limits");
        Check(VpnSpeedTest.FailureMessage(new HttpRequestException("private-token", null, HttpStatusCode.TooManyRequests)).Contains("ограничил")
            && !VpnSpeedTest.FailureMessage(new IOException("private-token")).Contains("private-token"), "speed errors identify safe failure categories without exposing raw exception data");

        var settingsPath = Path.Combine(scratch, "tunnel-settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.ConfigPath = path; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false;
        var window = new MainWindow(settings, Path.Combine(scratch, "tunnel.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        async Task Mode(bool enabled) => await (Task)typeof(MainWindow).GetMethod("RunExclusiveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, new object[] { (Func<Task>)(() => (Task)typeof(MainWindow).GetMethod("ChangeTunnelModeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { enabled })!) })!;
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release();
            settings.PutDraft(path, document.SourceHash, document.Rules);
            await Mode(true);
            Check(settings.FullTunnel && AppSettings.Load(settingsPath).FullTunnel && !((CoreProcessManager)Field("_core")).HasTrackedProcess, "TUN preference persists without auto-starting a stopped VPN");
            Check(settings.GetDraft(path) is not null && File.ReadAllText(path) == original, "switching TUN does not apply pending YAML edits");
            Check(((CheckBox)Field("FullTunnelCheck")).IsChecked == true && !((Button)Field("SpeedTestButton")).IsEnabled && Field("_speedTestLifetime") is null, "TUN controls reflect saved preference and never start speed tests automatically");
            window.UpdateLayout();
            var toggle = (CheckBox)Field("FullTunnelCheck");
            Check(toggle.ToolTip is null, "TUN toggle has no explanatory hover popup");
            var tools = (FrameworkElement)Field("SidebarTools");
            var toggleBounds = toggle.TransformToAncestor(window).TransformBounds(new Rect(toggle.RenderSize));
            var toolsBounds = tools.TransformToAncestor(window).TransformBounds(new Rect(tools.RenderSize));
            Check(toggleBounds.Top >= 0 && !toggleBounds.IntersectsWith(toolsBounds)
                && Math.Abs((toggleBounds.Top + toggleBounds.Bottom) / 2 - (toolsBounds.Top + toolsBounds.Bottom) / 2) < 1,
                "TUN and sidebar tools share a fixed footer without overlapping the adaptive connection metrics");
            var uiRules = (System.Collections.ObjectModel.ObservableCollection<DomainRule>)Field("_rules");
            var list = (ListView)Field("RulesList");
            await window.ChangeRuleRouteAsync(uiRules[0], "VPN");
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var faded = Descendants<Border>(list).Single(border => border.Name == "RuleSurface" && ReferenceEquals(border.DataContext, uiRules[0]));
            var changeButton = Descendants<Button>(faded).First(button => ReferenceEquals(button.Tag, uiRules[0]) && button.Content is StackPanel);
            Check(Math.Abs(faded.Opacity - .48) < .01 && changeButton.IsEnabled && faded.IsHitTestVisible, "TUN dims VPN rows without disabling route selection or other interactions");
            Check(Descendants<Border>(list).Single(border => border.Name == "RuleSurface" && ReferenceEquals(border.DataContext, uiRules[1])).Opacity == 1,
                "blocking exceptions retain normal appearance while TUN is enabled");
            Check(Descendants<System.Windows.Shapes.Ellipse>(toggle).Any(thumb => thumb.Name == "ToggleThumb"), "TUN uses an animated round-thumb switch rather than a checkbox mark");
            await Task.Delay(250); // Wait for the existing folder re-expansion animation.
            CaptureInteraction(window, Path.Combine(scratch, "tunnel-muted-vpn.png"));
            await window.ChangeRuleRouteAsync(uiRules[0], "DIRECT");
            window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var restoredRow = Descendants<Border>(list).Single(border => border.Name == "RuleSurface" && ReferenceEquals(border.DataContext, uiRules[0]));
            Check(restoredRow.Opacity == 1 && uiRules[0].RouteLabel == "НАПРЯМУЮ", "changing a VPN row to DIRECT restores its normal appearance immediately while TUN stays on");
            await Mode(false);
            Check(!settings.FullTunnel && !AppSettings.Load(settingsPath).FullTunnel, "returning to rules saves the off state");
            CaptureInteraction(window, Path.Combine(scratch, "tunnel-card.png"));
            File.WriteAllText(path, "morphocyte-fake-api: true\n" + original);
            await (Task)typeof(MainWindow).GetMethod("StartCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { false })!;
            var pendingRules = (System.Collections.ObjectModel.ObservableCollection<DomainRule>)Field("_rules");
            await window.ChangeRuleRouteAsync(pendingRules[0], "REJECT");
            var core = (CoreProcessManager)Field("_core");
            var oldPid = core.TrackedProcessId;
            await Mode(true);
            Check(core.IsRunning && core.TrackedProcessId != oldPid && settings.FullTunnel, "changing TUN while connected restarts only the owned fake core");
            Check(settings.GetDraft(path) is not null && pendingRules[0].Route == "REJECT" && (bool)Field("_dirty") && File.ReadAllText(path) == "morphocyte-fake-api: true\n" + original, "live mode switch preserves unapplied editor edits and the complete original YAML");
            var activeConfig = (string)Field("_runtimeConfigPath");
            Check(((YamlSequenceNode)Value(Root(File.ReadAllText(activeConfig)), "rules")).Children.Last().ToString() == "MATCH,VPN", "active runtime really uses default VPN with exceptions rather than only changing the toggle");
            oldPid = core.TrackedProcessId;
            await Mode(false);
            Check(core.IsRunning && core.TrackedProcessId != oldPid && !settings.FullTunnel && ((YamlSequenceNode)Value(Root(File.ReadAllText((string)Field("_runtimeConfigPath"))), "rules")).Children.Count == 3, "turning TUN off restarts the core with the original selective rules");
            var capacityField = typeof(MainWindow).GetField("_downloadCapacity", BindingFlags.NonPublic | BindingFlags.Instance)!;
            capacityField.SetValue(window, 12_500_000d);
            typeof(MainWindow).GetMethod("RefreshBandwidthReadout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(((TextBlock)Field("StabilityText")).Text == $"{100d:0.0} Мбит/с"
                && ((TextBlock)Field("CapacityLabel")).Text == "МАКС. СКОРОСТЬ ↓", "connection card renders the measured maximum VPN download speed instead of load");
            ((TextBlock)Field("DownloadSpeedText")).Text = "0 Б/с";
            typeof(MainWindow).GetMethod("RefreshBandwidthReadout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(((TextBlock)Field("StabilityText")).Text == $"{100d:0.0} Мбит/с", "idle current traffic does not lower the last measured channel capacity");
            var scale = (FrameworkElement)Field("Workspace");
            foreach (var factor in new[] { 1d, 1.25, 1.5 })
            {
                scale.LayoutTransform = new System.Windows.Media.ScaleTransform(factor, factor);
                foreach (var capacity in new[] { 12_500_000d, 124_875_000d, 125_000_000d })
                {
                    capacityField.SetValue(window, capacity);
                    typeof(MainWindow).GetMethod("RefreshBandwidthReadout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                    window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var label = (TextBlock)Field("CapacityLabel");
                    var readout = (TextBlock)Field("StabilityText");
                    var fitting = (Viewbox)readout.Parent;
                    var labelWidth = new System.Windows.Media.FormattedText(label.Text, System.Globalization.CultureInfo.CurrentCulture,
                        label.FlowDirection, new System.Windows.Media.Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                        label.FontSize, label.Foreground, System.Windows.Media.VisualTreeHelper.GetDpi(label).PixelsPerDip).WidthIncludingTrailingWhitespace;
                    Check(labelWidth <= label.ActualWidth + 1 && fitting.ActualWidth <= ((FrameworkElement)fitting.Parent).ActualWidth + 1
                        && fitting.StretchDirection == StretchDirection.DownOnly,
                        $"maximum-speed label and adaptive bit-rate value fit their sidebar column at {factor * 100:0}% scale");
                }
                ((TextBlock)Field("StabilityText")).BringIntoView();
                window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                CaptureInteraction(window, Path.Combine(scratch, $"maximum-capacity-{factor * 100:0}.png"));
            }
            scale.LayoutTransform = System.Windows.Media.Transform.Identity;
            await core.StopAsync();
            typeof(MainWindow).GetMethod("ResetDiagnostics", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(capacityField.GetValue(window) is null && ((TextBlock)Field("StabilityText")).Text == "—", "stopping VPN removes stale measured capacity");
        }
        finally
        {
            await ((CoreProcessManager)Field("_core")).StopAsync();
            typeof(MainWindow).GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            window.Close();
        }
        return count;
    }

    private sealed class SpeedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri!.Scheme != "https" || request.RequestUri.Host != "speed.cloudflare.com" || request.Headers.Authorization is not null)
                throw new Exception("Unexpected speed request or controller credentials leaked externally");
            var bytes = int.Parse(System.Text.RegularExpressions.Regex.Match(request.RequestUri.Query, @"bytes=(\d+)").Groups[1].Value);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SyntheticSpeedStream(bytes, 2)) };
            response.Content.Headers.ContentLength = bytes;
            return Task.FromResult(response);
        }
    }

    private sealed class ShortSpeedHandler(bool failLarge = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (failLarge && !request.RequestUri!.Query.Contains("bytes=" + VpnSpeedTest.WarmupBytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SyntheticSpeedStream(1_000_000, 10)) };
            response.Content.Headers.ContentLength = 1_000_000;
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class PartialSpeedHandler : HttpMessageHandler
    {
        private int _measurements;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var bytes = int.Parse(System.Text.RegularExpressions.Regex.Match(request.RequestUri!.Query, @"bytes=(\d+)").Groups[1].Value);
            if (bytes != VpnSpeedTest.WarmupBytes && Interlocked.Increment(ref _measurements) > VpnSpeedTest.Streams)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SyntheticSpeedStream(bytes, 5)) });
        }
    }

    private sealed class ParallelSpeedHandler : HttpMessageHandler
    {
        private int _active, _maximum, _measurements;
        internal int MaximumActive => _maximum;
        internal int MeasurementRequests => _measurements;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var bytes = int.Parse(System.Text.RegularExpressions.Regex.Match(request.RequestUri!.Query, @"bytes=(\d+)").Groups[1].Value);
            if (bytes != VpnSpeedTest.WarmupBytes) Interlocked.Increment(ref _measurements);
            var active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _maximum); }
            while (active > previous && Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TrackedSpeedStream(bytes, () => Interlocked.Decrement(ref _active))) });
        }
    }

    private sealed class TrackedSpeedStream(int length, Action disposed) : SyntheticSpeedStream(length, 3)
    {
        private int _disposed;
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) disposed();
            base.Dispose(disposing);
        }
    }

    private class SyntheticSpeedStream : Stream
    {
        private readonly int _length, _delay;
        private int _remaining;
        internal SyntheticSpeedStream(int length, int delay = 1)
        { _length = length; _remaining = length; _delay = delay; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => Length - _remaining; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(_delay, token);
            var count = Math.Min(buffer.Length, _remaining); buffer.Span[..count].Clear(); _remaining -= count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
