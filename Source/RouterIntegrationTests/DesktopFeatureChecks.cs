using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal partial class Program
{
    private static async Task<int> RunDesktopFeatureChecks(string scratch, string fakeExe)
    {
        var config = Path.Combine(scratch, "desktop-features.yaml");
        const string yaml = "proxies:\n  - {name: Server, type: socks5, server: 127.0.0.1, port: 9999}\nproxy-groups:\n  - {name: Primary, type: select, proxies: [Server]}\ntun:\n  enable: true\nrules:\n  - DOMAIN-SUFFIX,first.example,DIRECT\n  - PROCESS-NAME,Example.exe,Primary\n  - DOMAIN-SUFFIX,untouched.example,DIRECT\n  - MATCH,DIRECT\n";
        File.WriteAllText(config, yaml);
        var settings = AppSettings.Load(Path.Combine(scratch, "desktop-features-settings.json"));
        settings.ConfigPath = config; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false; settings.Theme = "Аметист";
        var window = new MainWindow(settings, Path.Combine(scratch, "desktop-features.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Invoke(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        var core = (CoreProcessManager)Field("_core");
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations");
            await gate.WaitAsync(); gate.Release();
            var rules = (ObservableCollection<DomainRule>)Field("_rules");
            var list = (ListView)Field("RulesList");
            list.SelectedItems.Add(rules[0]); list.SelectedItems.Add(rules[1]);
            await window.ChangeRuleRouteAsync(rules[0], "REJECT");
            Check(rules.Take(2).All(rule => rule.Route == "REJECT") && rules[2].Route == "DIRECT", "inline route change affects the selected group only");
            Check(File.ReadAllText(config) == yaml && settings.GetDraft(config)!.Rules.Take(2).All(rule => rule.Route == "REJECT"), "inline routing persists a draft without modifying YAML");
            list.SelectedItems.Clear();
            await window.ChangeRuleRouteAsync(rules[0], "VPN");
            Check(rules[0].Route == "Primary", "VPN action resolves to the profile policy group without showing server choices");
            window.UpdateLayout();
            var routeButton = Descendants<Button>(list).First(button => ReferenceEquals(button.Tag, rules[0]) && button.Content is StackPanel);
            typeof(MainWindow).GetMethod("RuleRoute_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { routeButton, new RoutedEventArgs() });
            Check(routeButton.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(new object[] { "VPN", "Напрямую", "Блокировать" }), "inline menu contains exactly three routing actions");
            await Task.Delay(180);
            routeButton.ContextMenu.UpdateLayout();
            Check(routeButton.ContextMenu.Placement == System.Windows.Controls.Primitives.PlacementMode.Bottom &&
                Descendants<System.Windows.Shapes.Path>(routeButton).Any(), "routing dropdown opens below its button with a visible left chevron");
            CaptureInteraction(routeButton.ContextMenu, Path.Combine(scratch, "routing-dropdown.png"));
            routeButton.ContextMenu.IsOpen = false;
            Check(new[] { "DownloadSpeedText", "UploadSpeedText", "PingText", "StabilityText" }.All(name => ((TextBlock)Field(name)).Text == "—") &&
                typeof(MainWindow).GetField("ProfileNameText", BindingFlags.Instance | BindingFlags.NonPublic) is null,
                "connection card starts with honest empty metrics and omits the generated YAML filename");
            var health = (ConnectionHealth)Field("_health");
            health.Record(42); health.Record(null); health.Record(38);
            Invoke("RefreshHealthReadout");
            ((TextBlock)Field("DownloadSpeedText")).Text = ConnectionHealth.FormatRate(1_572_864);
            ((TextBlock)Field("UploadSpeedText")).Text = ConnectionHealth.FormatRate(16_384);
            Check(((TextBlock)Field("PingText")).Text == "38 мс" && ((TextBlock)Field("StabilityText")).Text == "—", "ping remains independent from bandwidth and load waits for a manual measurement");
            await Task.Delay(200);
            CaptureInteraction(window, Path.Combine(scratch, "desktop-routes.png"));
            ((Button)Field("JournalTabButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await gate.WaitAsync(); gate.Release();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(((FrameworkElement)Field("JournalPage")).IsVisible && !((FrameworkElement)Field("RoutesPage")).IsVisible && settings.ShowEventLog, "journal tab replaces routes and persists selection");
            using var json = JsonDocument.Parse("{\"connections\":[{\"metadata\":{\"process\":\"Example.exe\",\"host\":\"first.example\",\"destinationPort\":\"443\",\"network\":\"udp\"},\"rule\":\"ProcessName\",\"rulePayload\":\"Example.exe\",\"chains\":[\"Server\",\"Primary\"]}]}");
            var rows = CoreDiagnostics.ParseConnections(json.RootElement);
            Check(rows.Single() is { Process: "Example.exe", Destination: "first.example:443", Network: "UDP", Rule: "ProcessName Example.exe", Route: "Server ← Primary" }, "connection diagnostics uses actual process, matching rule and proxy chain");
            using var emptyConnections = JsonDocument.Parse("{\"connections\":null}");
            Check(CoreDiagnostics.ParseConnections(emptyConnections.RootElement).Count == 0, "null connection slice from Mihomo represents an empty list");
            var document = ClashConfigDocument.Parse(config, yaml);
            using var diagnostics = new CoreDiagnostics();
            var runtime = document.BuildRuntimeText(diagnostics.Port, diagnostics.Secret);
            var runtimeYaml = new YamlStream(); runtimeYaml.Load(new StringReader(runtime));
            var root = (YamlMappingNode)runtimeYaml.Documents[0].RootNode;
            Check(((YamlScalarNode)root.Children[new YamlScalarNode("external-controller")]).Value == $"127.0.0.1:{diagnostics.Port}" &&
                ((YamlScalarNode)root.Children[new YamlScalarNode("secret")]).Value == diagnostics.Secret &&
                ClashConfigDocument.Parse(config, runtime).Rules.Select(rule => rule.Route).SequenceEqual(document.Rules.Select(rule => rule.Route)),
                "local controller has a random secret and runtime copy retains routing");
            // Exercise the real tray without installing a Windows startup task.
            Invoke("InitializeDesktopIntegration");
            window.HideToTray();
            Check(!window.IsVisible && !core.IsRunning, "close-to-tray keeps the application alive without starting VPN");
            window.RestoreFromTray();
            Check(window.IsVisible, "tray restores the existing window");
            var available = new UpdateCheckResult(true, true, "Fixture", LatestVersion: "9.0.0", Asset: new UpdateAsset("fixture.zip", "https://github.com/sample/app/releases/download/v9.0.0/fixture.zip", new string('a', 64), 1024), Repository: "sample/app");
            typeof(MainWindow).GetField("_availableUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, available);
            settings.AutoCheckUpdates = true;
            Check(window.CanInstallAutomatically, "automatic update is eligible when VPN is stopped and the app is idle");
            settings.AutoUpdateBlockedVersion = "9.0.0";
            Check(!window.CanInstallAutomatically, "a version that failed installation is not automatically retried after rollback");
            settings.AutoUpdateBlockedVersion = "";
            typeof(MainWindow).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Check(!window.CanInstallAutomatically, "automatic update waits for a running UI operation");
            typeof(MainWindow).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            typeof(MainWindow).GetField("_dialogBackdropVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Check(!window.CanInstallAutomatically, "automatic update waits for settings and other modal dialogs");
            typeof(MainWindow).GetField("_dialogBackdropVisible", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            settings.AutoCheckUpdates = false;
            settings.LaunchAtSignIn = true; settings.AutoConnectOnStartup = true; settings.Save();
            var restored = AppSettings.Load(Path.Combine(scratch, "desktop-features-settings.json"));
            Check(restored.LaunchAtSignIn && restored.AutoConnectOnStartup, "startup preferences survive settings reload");
            await (Task)typeof(MainWindow).GetMethod("StartCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { false })!;
            Check(core.IsRunning && File.ReadAllText(config) == yaml && settings.GetDraft(config) is not null,
                "startup connection uses the saved YAML and retains unapplied routing drafts");
            settings.AutoCheckUpdates = true;
            Check(!window.CanInstallAutomatically, "automatic update never interrupts the running VPN");
            settings.AutoCheckUpdates = false;
            var activeConfig = (string)Field("_runtimeConfigPath");
            Check(ClashConfigDocument.Load(activeConfig).Rules[0].Route == "DIRECT" && rules[0].Route == "Primary",
                "runtime routing comes from saved profile while editor retains its pending changes");
            window.RequestExit();
            await Task.Delay(300);
            await gate.WaitAsync(); gate.Release();
            Check(!window.IsVisible && !core.HasTrackedProcess, "explicit tray Exit closes and confirms core shutdown");
        }
        finally
        {
            await core.StopAsync();
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
        }
        return count;
    }

    private static async Task<int> RunRealDiagnosticsChecks(string scratch)
    {
        var corePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Packaging/Engine/mihomo.exe"));
        var dataHome = Path.Combine(scratch, "real-core-home");
        Directory.CreateDirectory(dataHome);
        var profile = Path.Combine(dataHome, "profile.yaml");
        // No TUN, DNS listener or external request. Only the authenticated loopback API is started.
        const string yaml = "mode: rule\nlog-level: warning\ngeodata-mode: false\ntun:\n  enable: false\ndns:\n  enable: false\nproxies:\n  - {name: Server, type: socks5, server: 127.0.0.1, port: 9999}\nproxy-groups:\n  - {name: VPN, type: select, proxies: [Server]}\nrules:\n  - MATCH,DIRECT\n";
        File.WriteAllText(profile, yaml);
        using var diagnostics = new CoreDiagnostics();
        var runtime = Path.Combine(scratch, "real-core-runtime.yaml");
        File.WriteAllText(runtime, ClashConfigDocument.Load(profile).BuildRuntimeText(diagnostics.Port, diagnostics.Secret));
        var core = new CoreProcessManager();
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        core.Output += output.Enqueue;
        try
        {
            await core.StartAsync(CoreProcessManager.CreateStartInfo(corePath, runtime, dataHome));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var snapshot = await diagnostics.ReadAsync("VPN", deadline.Token);
            var traffic = await diagnostics.ReadTrafficAsync(deadline.Token);
            if (traffic.Upload != 0 || traffic.Download != 0) throw new Exception("Idle core must report zero traffic");
            Console.WriteLine("PASS: real Mihomo traffic stream reports live byte rates without opening TUN");
            if (snapshot.Version.Length == 0 || snapshot.Selection != "Server" || snapshot.Connections.Count != 0)
                throw new Exception("Real core diagnostics mismatch: " + string.Join("\n", output));
            Console.WriteLine("PASS: bundled Mihomo serves version, selection and connections from the runtime copy outside its data home");
            using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false });
            using var unauthorized = await client.GetAsync($"http://127.0.0.1:{diagnostics.Port}/version", deadline.Token);
            if (unauthorized.StatusCode != System.Net.HttpStatusCode.Unauthorized) throw new Exception("Local API accepted a request without its secret");
            Console.WriteLine("PASS: real diagnostic API rejects requests without the random bearer secret");
            if (File.ReadAllText(profile) != yaml) throw new Exception("Runtime diagnostics modified the original profile");
            Console.WriteLine("PASS: real core diagnostics leaves the original profile unchanged");
            return 4;
        }
        finally { await core.StopAsync(); }
    }
}
