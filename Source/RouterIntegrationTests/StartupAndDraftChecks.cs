using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunStartupAndDraftChecks(string scratch, string fakeExe)
    {
        var count = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name); count++;
        }
        var folder = Directory.CreateDirectory(Path.Combine(scratch, "startup-draft")).FullName;
        var path = Path.Combine(folder, "profile.yaml");
        var settingsPath = Path.Combine(folder, "settings.json");
        const string profile = """
            morphocyte-fake-api: true
            proxies:
              - {name: Server, type: socks5, server: 127.0.0.1, port: 12345}
            proxy-groups:
              - {name: VPN, type: select, proxies: [Server]}
            rules:
              - DOMAIN,test.example,VPN
              - MATCH,DIRECT
            """;
        File.WriteAllText(path, profile);
        var settings = AppSettings.Load(settingsPath);
        settings.AutoCheckUpdates = false;
        settings.ConfigPath = path;
        settings.CorePath = fakeExe;
        var initial = ClashConfigDocument.Load(path);
        var earlier = initial.Rules.Single().Copy();
        earlier.Route = "REJECT";
        settings.PutDraft(path, initial.SourceHash, [earlier]);
        File.WriteAllText(path, profile.Replace("DOMAIN,test.example,VPN", "DOMAIN,test.example,DIRECT"));

        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        async Task Idle(MainWindow window)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = Field<SemaphoreSlim>(window, "_operations");
            await gate.WaitAsync(); gate.Release();
        }
        async Task Close(MainWindow window)
        {
            await (Task<bool>)typeof(MainWindow).GetMethod("StopCoreAsync", flags)!.Invoke(window, null)!;
            typeof(MainWindow).GetField("_allowClose", flags)!.SetValue(window, true);
            window.Close();
        }
        var editor = new MainWindow(settings, Path.Combine(folder, "editor.log")) { ShowInTaskbar = false };
        try
        {
            editor.Show(); await Idle(editor);
            Check(!Field<CoreProcessManager>(editor, "_core").IsRunning, "manual opening starts no network core without opt-in");
            var rules = Field<ObservableCollection<DomainRule>>(editor, "_rules");
            var recover = Field<Button>(editor, "RecoverDraftButton");
            Check(rules.Single().Route == "DIRECT" && recover.Visibility == Visibility.Visible
                && AppSettings.Load(settingsPath).GetArchivedDrafts(path).Single().Rules.Single().Route == "REJECT",
                "opening externally edited YAML preserves both the current rules and the archived draft");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.Title == "СРАВНИТЬ ЧЕРНОВИК");
                if (dialog is null) return;
                timer.Stop();
                var choice = Descendants<CheckBox>(dialog).FirstOrDefault(option => option.Content is TextBlock label && label.Text.Contains("test.example"));
                if (choice is null) { dialog.Close(); return; }
                choice.IsChecked = true;
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "ПЕРЕНЕСТИ В ЧЕРНОВИК"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            recover.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle(editor);
            timer.Stop();
            var saved = AppSettings.Load(settingsPath);
            Check(File.ReadAllText(path).Contains("test.example,DIRECT") && saved.GetDraft(path)?.Rules.Single().Route == "REJECT"
                && saved.GetArchivedDrafts(path).Count == 1, "restoring a selected rule saves it as a new draft and preserves the externally edited YAML and old snapshot");
        }
        finally { await Close(editor); }

        var next = AppSettings.Load(settingsPath);
        next.AutoConnectOnOpen = true;
        next.Mode = ConnectionMode.FullTunnel;
        next.Save();
        foreach (var responds in new[] { true, false })
        {
            File.WriteAllText(path, responds ? profile : profile.Replace("morphocyte-fake-api: true", "morphocyte-fake-api: startup-only"));
            var window = new MainWindow(AppSettings.Load(settingsPath), Path.Combine(folder, responds ? "online.log" : "no-response.log")) { ShowInTaskbar = false };
            try
            {
                var watch = Stopwatch.StartNew();
                window.Show(); await Idle(window);
                var status = Field<TextBlock>(window, "StatusText");
                while (status.Text == "ПРОВЕРЯЮ VPN" && watch.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(50);
                Check(Field<CoreProcessManager>(window, "_core").IsRunning
                    && status.Text == (responds ? "VPN ОТВЕЧАЕТ" : "VPN НЕ ОТВЕЧАЕТ"),
                    "opt-in connects on app open and shows verified VPN health independently of the live process");
                Check(File.ReadAllText(path).Contains("test.example,VPN"), "autoconnect does not apply unapplied routing edits");
                Console.WriteLine($"LOCAL FIXTURE: opening through first health result took {watch.Elapsed.TotalMilliseconds:0} ms");
                if (responds)
                {
                    var checking = typeof(MainWindow).GetField("_checkingConnection", flags)!;
                    var updateControls = typeof(MainWindow).GetMethod("UpdateControls", flags)!;
                    var button = Field<Button>(window, "CheckConnectionButton");
                    checking.SetValue(window, true);
                    try
                    {
                        updateControls.Invoke(window, null);
                        Check(!button.IsEnabled && ((Border)button.Template.FindName("B", button)).Opacity == 1,
                            "the automatic health check prevents a duplicate check without dimming the manual button");
                    }
                    finally { checking.SetValue(window, false); updateControls.Invoke(window, null); }
                }
            }
            finally { await Close(window); }
        }
        var parsedConnection = new ConnectionTableRow("one", new ConnectionActivity("game.exe", "test.example:443", "TCP",
            "ProcessName game.exe", "DIRECT", DestinationIp: "192.0.2.9", Inbound: "tun"));
        Check(parsedConnection.DescribeConnection().Contains("192.0.2.9") && parsedConnection.DescribeConnection().Contains("ProcessName game.exe")
            && parsedConnection.DescribeConnection().Contains("Фактический маршрут: DIRECT"), "details identify the actual controller-reported rule, endpoint and route");

        var realCore = CoreBackend.BundledSingBoxPath;
        if (!File.Exists(realCore)) throw new FileNotFoundException("Bundled sing-box is required for local-proxy smoke test.", realCore);
        using var diagnostics = new CoreDiagnostics();
        var native = CoreBackend.RuntimeText(realCore, initial, diagnostics, false, 0, proxyOnly: true);
        using (var config = JsonDocument.Parse(native))
        {
            var inbounds = config.RootElement.GetProperty("inbounds").EnumerateArray().ToArray();
            Check(inbounds.All(inbound => inbound.GetProperty("listen").GetString() == "127.0.0.1")
                && inbounds.Single(inbound => inbound.GetProperty("type").GetString() == "mixed").GetProperty("listen_port").GetInt32() == CoreBackend.LocalProxyPort,
                "native local-proxy configuration contains only loopback listeners and no TUN inbound");
        }
        var runtime = Path.Combine(folder, "native-proxy.json");
        File.WriteAllText(runtime, native);
        var validated = await CoreProcessManager.ValidateAsync(realCore, runtime);
        Check(validated.Success, "the bundled sing-box accepts the exact local-proxy runtime configuration");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var core = new CoreProcessManager();
        try
        {
            using var plan = XrayBridgePlan.Create(initial);
            await core.StartWithBridgeAsync(CoreProcessManager.CreateStartInfo(realCore, runtime), plan, timeout.Token, diagnostics.WaitForControllerAsync);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, CoreBackend.LocalProxyPort, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var reply = new byte[2];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            Check(reply.SequenceEqual(new byte[] { 5, 0 }), "the bundled sing-box starts promptly and answers a local SOCKS5 handshake");
        }
        finally { await core.StopAsync(); }
        return count;
    }
}
