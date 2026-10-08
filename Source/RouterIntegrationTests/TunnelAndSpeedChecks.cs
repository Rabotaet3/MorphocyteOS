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
        Check(Value(dns, "respect-rules").ToString() == "true" && Value(dns, "proxy-server-nameserver") is YamlSequenceNode && !dns.Children.ContainsKey(new YamlScalarNode("nameserver-policy")), "normal DNS follows VPN while server bootstrap remains available");
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
        File.WriteAllText(runtime, full);
        var validation = await CoreProcessManager.ValidateAsync(Path.Combine(AppContext.BaseDirectory, "mihomo.exe"), runtime, default);
        Check(validation.Success, "bundled real Mihomo accepts full-tunnel and authenticated speed-listener YAML without starting VPN");
        using var directApi = new CoreDiagnostics(new DiagnosticHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"type\":\"Direct\"}") }));
        Check(await directApi.ReadSelectionAsync("CustomDirect", default) == "DIRECT", "renamed direct proxies are detected before a speed test");
        Check(VpnSpeedTest.FormatLoad(null, null) == "—" && VpnSpeedTest.FormatLoad(0, 1000) == "0%" && VpnSpeedTest.FormatLoad(250, 1000) == "25%" && VpnSpeedTest.FormatLoad(2000, 1000) == "100%", "bandwidth load uses measured capacity, handles zero and clamps above 100 percent");
        Check(VpnSpeedTest.FormatLoad(1, double.NaN) == "—" && VpnSpeedTest.FormatLoad(-1, 100) == "—", "invalid or missing bandwidth data never produces a fabricated percentage");

        using var test = new VpnSpeedTest(19877, api.Secret, new SpeedHandler());
        var result = await test.MeasureAsync(default);
        Check(result.DownloadedBytes == VpnSpeedTest.MaxBytes && result.DownloadBytesPerSecond > 0 && double.IsFinite(result.DownloadBytesPerSecond), "parallel speed test counts streamed bytes and measures a finite aggregate download rate");
        using var cancellation = new CancellationTokenSource(30);
        using var cancelTest = new VpnSpeedTest(19877, api.Secret, new SpeedHandler());
        try { await cancelTest.MeasureAsync(cancellation.Token); throw new Exception("Cancelled speed test accepted"); }
        catch (OperationCanceledException) { Check(true, "user cancellation ends all test transfers without storing a partial calibration"); }
        using var badTest = new VpnSpeedTest(19877, api.Secret, new DiagnosticHandler(_ => new(HttpStatusCode.Redirect) { Content = new StringContent("not a speed payload") }));
        try { await badTest.MeasureAsync(default); throw new Exception("Redirect accepted"); }
        catch (HttpRequestException) { Check(true, "failed or redirected download is not treated as a speed measurement"); }
        using var shortTest = new VpnSpeedTest(19877, api.Secret, new ShortSpeedHandler());
        var shortResult = await shortTest.MeasureAsync(default);
        Check(shortResult.DownloadedBytes == 8_000_000 && shortResult.DownloadBytesPerSecond > 0, "short valid binary responses are measured instead of failing an exact content-length assertion");
        using var retryTest = new VpnSpeedTest(19877, api.Secret, new ShortSpeedHandler(true));
        var retryResult = await retryTest.MeasureAsync(default);
        Check(retryResult.DownloadedBytes == 4_000_000 && retryResult.DownloadBytesPerSecond > 0, "a second-stage service failure retains the completed warm-up measurement");
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
            var tools = (FrameworkElement)Field("SidebarTools");
            var toggleTop = toggle.TranslatePoint(new Point(), window).Y;
            Check(toggleTop >= 0 && toggleTop + toggle.ActualHeight <= tools.TranslatePoint(new Point(), window).Y,
                "TUN stays visible above sidebar tools outside the scrollable connection metrics");
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
            capacityField.SetValue(window, 2000d);
            typeof(MainWindow).GetField("_currentDownload", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, 500L);
            typeof(MainWindow).GetMethod("RefreshBandwidthReadout", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(((TextBlock)Field("StabilityText")).Text == "25%", "connection card renders the measured-capacity load");
            await core.StopAsync();
            typeof(MainWindow).GetMethod("ResetDiagnostics", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(capacityField.GetValue(window) is null && ((TextBlock)Field("StabilityText")).Text == "—", "stopping VPN removes stale capacity and load");
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
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SyntheticSpeedStream(bytes)) };
            response.Content.Headers.ContentLength = bytes;
            return Task.FromResult(response);
        }
    }

    private sealed class ShortSpeedHandler(bool failLarge = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (failLarge && request.RequestUri!.Query.Contains("bytes=14000000")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SyntheticSpeedStream(1_000_000, 10)) };
            response.Content.Headers.ContentLength = 1_000_000;
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class SyntheticSpeedStream(int length, int delay = 1) : Stream
    {
        private int _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => Length - _remaining; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(delay, token);
            var count = Math.Min(buffer.Length, _remaining); buffer.Span[..count].Clear(); _remaining -= count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
