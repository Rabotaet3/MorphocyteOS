using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal partial class Program
{
    private sealed class SpeedProgressRecorder : IProgress<int>
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<int> Values = new();
        public void Report(int value) => Values.Enqueue(value);
    }
    private static async Task<int> RunRelease210Checks(string scratch, string fakeExe)
    {
        var passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        void Reject(Action action, string label) { try { action(); } catch (InvalidDataException) { Check(true, label); return; } throw new Exception("FAIL: " + label); }
        var home = Path.Combine(scratch, "release210"); Directory.CreateDirectory(home);
        var profile = Path.Combine(home, "original.yaml");
        const string yaml = "mode: rule\ntun:\n enable: true\nproxies:\n - {name: Alpha, type: socks5, server: 192.0.2.20, port: 1080}\n - {name: Beta, type: socks5, server: 192.0.2.21, port: 1080}\nproxy-groups:\n - {name: VPN, type: select, proxies: [Alpha, Beta]}\n - {name: Banking, type: select, proxies: [DIRECT, Beta]}\nrules:\n - DOMAIN,bank.fixture.invalid,Banking\n - DOMAIN,fixture.invalid,VPN\n - MATCH,DIRECT\n";
        File.WriteAllText(profile, yaml);
        var document = ClashConfigDocument.Load(profile);
        using var json = JsonDocument.Parse(SingBoxConfig.Build(document, 16953, new string('A',64), false, selectedServer: "Beta"));
        var groups = json.RootElement.GetProperty("outbounds").EnumerateArray().Where(item => item.GetProperty("type").GetString() == "selector").ToArray();
        Check(groups.Single(item => item.GetProperty("tag").GetString() == "VPN").GetProperty("default").GetString() == "Beta"
            && groups.Single(item => item.GetProperty("tag").GetString() == "Banking").GetProperty("default").GetString() == "DIRECT",
            "main VPN selection never overrides an independent DIRECT selector");
        var refreshed = SubscriptionRefresh.Merge(profile, yaml, new ImportedProfile(yaml, "fixture", false), "Beta");
        var y = new YamlStream(); y.Load(new StringReader(refreshed.Yaml));
        var bank = ((YamlSequenceNode)((YamlMappingNode)y.Documents[0].RootNode).Children[new YamlScalarNode("proxy-groups")]).Children
            .OfType<YamlMappingNode>().Single(item => item.Children[new YamlScalarNode("name")].ToString() == "Banking");
        Check(((YamlSequenceNode)bank.Children[new YamlScalarNode("proxies")]).Children[0].ToString() == "DIRECT" && refreshed.Added == 0 && refreshed.Removed == 0,
            "unchanged subscription refresh preserves unrelated selector order");

        foreach (var field in new[] { "skip-cert-verify: 1", "skip_cert_verify: true", "Skip-Cert-Verify: TRUE" })
        {
            var imported = SubscriptionImporter.ParseContent("proxies:\n - {name: Test, type: trojan, server: 192.0.2.1, port: 443, password: dummy, " + field + "}\n");
            Check(imported.InsecureTls && imported.Yaml.Contains("skip-cert-verify: true"), "TLS aliases are canonicalized before confirmation: " + field);
        }
        foreach (var field in new[] { "private_key: C:/fixture.pem", "Private-Key: C:/fixture.pem", "dialer_proxy: DIRECT", "skip-cert-verify: false, Skip_Cert_Verify: true" })
            Reject(() => SubscriptionImporter.ParseContent("proxies:\n - {name: Test, type: trojan, server: 192.0.2.1, port: 443, password: dummy, " + field + "}\n"),
                "normalized forbidden or ambiguous subscription options are rejected: " + field);
        var vmess = SubscriptionImporter.ParseContent("proxies:\n - {name: VMess, type: vmess, server: 192.0.2.1, port: 443, uuid: 00000000-0000-0000-0000-000000000001, alterId: 0, cipher: auto}\n");
        using var vmessConfig = JsonDocument.Parse(SingBoxConfig.Build(ClashConfigDocument.Parse(profile, vmess.Yaml), 16953, new string('A',64), false));
        Check(vmess.Yaml.Contains("alterId: 0") && vmessConfig.RootElement.GetProperty("outbounds").EnumerateArray().Any(item => item.GetProperty("type").GetString() == "vmess"),
            "TLS normalization preserves the canonical VMess alterId spelling and native conversion");
        var websocket = SubscriptionImporter.ParseContent("proxies:\n - {name: WS, type: vless, server: 192.0.2.1, port: 443, uuid: 00000000-0000-0000-0000-000000000001, network: ws, ws-opts: {headers: {X_Custom-Header: fixture}}}\n");
        Check(websocket.Yaml.Contains("X_Custom-Header"), "security option normalization never rewrites HTTP header names");

        var fast = new VpnSpeedTest.DownloadSamples();
        for (var index = 0; index < 1000; index++) fast.Record(64_000, index * .0002);
        Check(fast.ReadRate() > 100_000_000, "a complete large fast transfer is not misreported as insufficient data");
        Check(VpnSpeedTest.ProgressPercent(0, TimeSpan.Zero) == 0
            && VpnSpeedTest.ProgressPercent(0, TimeSpan.FromSeconds(7.5)) == 50
            && VpnSpeedTest.ProgressPercent(VpnSpeedTest.MaxBytes / 2, TimeSpan.FromSeconds(1)) == 50
            && VpnSpeedTest.ProgressPercent(VpnSpeedTest.MaxBytes, TimeSpan.FromSeconds(15)) == 99,
            "speed progress advances on transferred bytes or elapsed deadline but reserves completion");

        var settingsPath = Path.Combine(home, "settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.ConfigPath = profile; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false;
        settings.FullTunnel = true; settings.PreferredRoute = "REJECT"; settings.ProfileSelections[profile] = "Beta"; settings.Save();
        var window = new MainWindow(settings, Path.Combine(home, "test.log"));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        Task Load() => (Task)typeof(MainWindow).GetMethod("LoadConfigAsync", flags)!.Invoke(window, [profile])!;
        try
        {
            await Load();
            var selection = new SettingsDialogSelection(profile, fakeExe, settings.Theme, 1, false);
            var package = await window.CaptureConfigurationAsync(selection);
            Check(package.Options.FullTunnel == true && package.Options.SelectedServer == "Beta",
                "settings export captures full tunnel and selected server even without a subscription URL");
            var roundtrip = ConfigurationTransfer.Deserialize(Encoding.UTF8.GetBytes(ConfigurationTransfer.Serialize(package)));
            Check(roundtrip.Options.FullTunnel == true && roundtrip.Options.SelectedServer == "Beta", "connection state survives transfer serialization");
            settings.ProfileSelections[profile] = "RemovedServer";
            Check((await window.CaptureConfigurationAsync(selection)).Options.SelectedServer is null,
                "an offline-removed server does not prevent exporting the remaining valid profile");
            settings.ProfileSelections[profile] = "DIRECT";
            Check((await window.CaptureConfigurationAsync(selection)).Options.SelectedServer == "DIRECT",
                "a deliberate DIRECT selection remains transferable in a rules-mode profile");
            settings.ProfileSelections[profile] = "Beta";
            var legacy = ConfigurationTransfer.Deserialize(Encoding.UTF8.GetBytes(ConfigurationTransfer.Serialize(package)
                .Replace("\"FullTunnel\": true", "\"FullTunnel\": null").Replace("\"SelectedServer\": \"Beta\"", "\"SelectedServer\": null")));
            Check(legacy.Options.FullTunnel is null && legacy.Options.SelectedServer is null, "legacy transfers preserve the destination's existing tunnel mode");
            typeof(MainWindow).GetField("_setStartupRegistration", flags)!.SetValue(window, (Action<bool>)(enabled => { if (enabled) throw new IOException("Fixture registration failure"); }));
            var failedChoice = selection with { Imported = new ImportedProfile(yaml, "fixture", false), LaunchAtSignIn = true };
            try { await (Task<SettingsDialogSelection>)typeof(MainWindow).GetMethod("ApplySettingsSelectionCoreAsync", flags)!.Invoke(window, [failedChoice])!; throw new Exception("Failure fixture accepted"); }
            catch (IOException) { }
            Check(settings.PreferredRoute == "REJECT" && ((ComboBoxItem)((ComboBox)Field("RouteCombo")).SelectedItem).Tag!.ToString() == "REJECT",
                "failed import restores the real new-rule selector as well as saved settings");
            typeof(MainWindow).GetField("_setStartupRegistration", flags)!.SetValue(window, (Action<bool>)(_ => { }));
            settings.FullTunnel = false;
            await (Task<SettingsDialogSelection>)typeof(MainWindow).GetMethod("ApplySettingsSelectionCoreAsync", flags)!.Invoke(window, [selection with { Transfer = roundtrip }])!;
            Check(settings.FullTunnel && settings.ProfileSelections[settings.ConfigPath] == "Beta"
                && AppSettings.Load(settingsPath).ProfileSelections[settings.ConfigPath] == "Beta",
                "actual settings import persists full TUN and a URL-independent server selection");
            Check(typeof(MainWindow).GetMethod("ScheduleAutoInstall", flags) is null
                && typeof(MainWindow).GetMethod("InstallAvailableUpdateAsync", flags | BindingFlags.Public)!.GetParameters().Length == 1,
                "no automatic installer entry point survives the manual-update policy");
            var progress = (TextBlock)Field("SpeedProgressText");
            progress.Text = "100 %"; progress.Visibility = Visibility.Visible;
            typeof(MainWindow).GetMethod("RefreshBandwidthReadout", flags)!.Invoke(window, null);
            Check(progress.Visibility == Visibility.Hidden, "invalidating a speed result clears its stale completed percentage");
            progress.Text = "100 %"; progress.Visibility = Visibility.Visible;
            Check(progress.FontSize == ((TextBlock)Field("ConnectionStateText")).FontSize && Grid.GetColumn(progress) == 1,
                "speed percentage is plain small text in the speed column below its value");
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var operations = (SemaphoreSlim)Field("_operations"); await operations.WaitAsync(); operations.Release();
            var available = new UpdateCheckResult(true, true, "Fixture", LatestVersion: "9.0.0", Asset: new UpdateAsset("fixture.zip", "https://github.com/sample/app/releases/download/v9.0.0/fixture.zip", new string('a', 64), 1024), Repository: "sample/app");
            settings.AutoCheckUpdates = true;
            typeof(MainWindow).GetMethod("SetAvailableUpdate", flags)!.Invoke(window, [available]);
            typeof(MainWindow).GetMethod("UpdateControls", flags)!.Invoke(window, null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(window.AvailableUpdate == available && !Directory.Exists(Path.Combine(settings.StorageDirectory, ".update-cache"))
                && !((TextBlock)Field("UpdateOfferText")).Text.Contains("автоматически"),
                "discovering an update while idle only displays an offer and creates no download operation");
            settings.AutoCheckUpdates = false;
            typeof(MainWindow).GetMethod("SetAvailableUpdate", flags)!.Invoke(window, new object?[] { null });
            progress.Text = "42 %"; progress.Visibility = Visibility.Visible;
            foreach (var theme in new[] { "Тёмная морфоцитная", "Светлая морфоцитная" })
            {
                ThemeManager.Apply(theme); window.UpdateLayout();
                CaptureInteraction(window, Path.Combine(home, theme.StartsWith("Светлая") ? "progress-light.png" : "progress-dark.png"));
                Check(progress.ActualWidth > 0 && progress.ActualHeight > 0, "speed percentage remains visible and unclipped: " + theme);
            }
        }
        finally { typeof(MainWindow).GetField("_allowClose", flags)!.SetValue(window, true); window.Close(); }
        passed += await RunNativeSelectionChecks(home, yaml);
        return passed;
    }

    private static async Task<int> RunNativeSelectionChecks(string home, string yaml)
    {
        var passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        var engines = new[] { Path.Combine(AppContext.BaseDirectory, "mihomo.exe"), await CoreBackend.EnsureSingBoxAsync() };
        foreach (var engine in engines)
        foreach (var nested in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var api = new CoreDiagnostics();
            var fixture = yaml.Replace("tun:\n enable: true\n", "tun:\n enable: false\n");
            if (nested) fixture = fixture.Replace("{name: VPN, type: select, proxies: [Alpha, Beta]}",
                "{name: VPN, type: select, proxies: [Nested]}\n - {name: Nested, type: select, proxies: [Alpha, Beta]}");
            var profile = ClashConfigDocument.Parse(Path.Combine(home, "selector-" + nested + ".yaml"), fixture);
            var sing = CoreBackend.IsSingBox(engine);
            var runtimePath = Path.Combine(home, (sing ? "sing" : "mihomo") + "-selector-" + nested + (sing ? ".json" : ".yaml"));
            File.WriteAllText(runtimePath, sing ? SingBoxConfig.Build(profile, api.Port, api.Secret, false, includeTun: false)
                : profile.BuildRuntimeText(api.Port, api.Secret));
            var info = CoreProcessManager.CreateStartInfo(engine, runtimePath, home);
            using var process = System.Diagnostics.Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            try
            {
                while (true)
                {
                    try { await api.ReadVersionAsync(deadline.Token); break; }
                    catch (System.Net.Http.HttpRequestException) when (!process.HasExited) { await Task.Delay(50, deadline.Token); }
                }
                Check(await api.RestoreSelectionAsync("VPN", "Beta", deadline.Token)
                    && await api.ReadSelectionAsync("VPN", deadline.Token) == "Beta"
                    && await api.ReadSelectionAsync("Banking", deadline.Token) == "DIRECT",
                    "real " + (sing ? "sing-box" : "Mihomo") + " restores a " + (nested ? "nested" : "main") + " selection without changing independent groups");
                await api.RestoreSelectionAsync("VPN", "Alpha", deadline.Token);
                Check(await api.RestoreSelectionAsync("VPN", "Beta", deadline.Token)
                    && !await api.RestoreSelectionAsync("VPN", "RemovedServer", deadline.Token)
                    && await api.ReadSelectionAsync("VPN", deadline.Token) == "Beta",
                    "saved selection overrides a stale native cache, while a removed server safely retains the current selection");
            }
            finally
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); await output; await errors;
            }
        }
        return passed;
    }
}
