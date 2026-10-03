using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal partial class Program
{
    private static async Task<int> RunProfileImportChecks(string scratch, string fakeExe)
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        void Reject(string text, string label)
        {
            try { _ = ProfileImporter.Parse(text); }
            catch (InvalidDataException) { Check(true, label); return; }
            throw new Exception("FAIL: accepted " + label);
        }
        const string id = "12345678-1234-4234-8234-123456789abc";
        const string link = "vless://" + id + "@192.0.2.50:8443?encryption=none&security=tls&sni=vpn.example&type=ws&host=vpn.example&path=%2Fws50#Sample";
        var imported = ProfileImporter.Parse(link);
        YamlMappingNode Proxy(string text)
        {
            var yaml = new YamlStream(); yaml.Load(new StringReader(text));
            return (YamlMappingNode)((YamlSequenceNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("proxies")]).Children[0];
        }
        string Field(YamlMappingNode map, string key) => ((YamlScalarNode)map.Children[new YamlScalarNode(key)]).Value!;
        var proxy = Proxy(imported.Yaml);
        Check(Field(proxy, "uuid") == id && Field(proxy, "server") == "192.0.2.50" && Field(proxy, "port") == "8443", "VLESS import preserves UUID, server and port");
        var ws = (YamlMappingNode)proxy.Children[new YamlScalarNode("ws-opts")];
        Check(Field(ws, "path") == "/ws50" && Field((YamlMappingNode)ws.Children[new YamlScalarNode("headers")], "Host") == "vpn.example",
            "WebSocket path is URL-decoded once and Host has no spurious slash");
        Check(Field(proxy, "servername") == "vpn.example" && Field(proxy, "tls") == "true" && Field(proxy, "skip-cert-verify") == "false" && !imported.InsecureTls,
            "TLS, SNI and certificate verification are preserved securely");
        var parsed = ClashConfigDocument.Parse(Path.Combine(scratch, "parse-import.yaml"), imported.Yaml);
        Check(parsed.HasVpnConnection && parsed.ValidateForRouting().Count == 0 && parsed.Rules.Count == 0 && imported.Yaml.Contains("MATCH,DIRECT"),
            "generated YAML enables TUN and process lookup with direct fallback and no hidden app rules");
        const string json = """
        {"dns":{"servers":["119.29.29.29"]},"outbounds":[{"protocol":"vless","settings":{"vnext":[{"address":"192.0.2.50","port":8443,"users":[{"id":"12345678-1234-4234-8234-123456789abc","encryption":"none"}]}]},"streamSettings":{"network":"ws","security":"tls","tlsSettings":{"allowInsecure":false,"serverName":"vpn.example"},"wsSettings":{"path":"/ws50","host":"vpn.example","headers":{}}},"mux":{"enabled":false}},{"protocol":"freedom"}],"routing":{"rules":[{"outboundTag":"proxy","domain":["geosite:google"]}]}}
        """;
        var jsonImport = ProfileImporter.Parse(json);
        Check(jsonImport.NeedsName, "Xray connection without display name requests a user name");
        Check(jsonImport.Yaml == ProfileImporter.Parse(link.Split('#')[0]).Yaml, "Xray JSON and an unnamed VLESS URI produce identical connection profiles");
        Check(Field(proxy, "name") == "Sample", "single VLESS URI preserves its server label");
        var labelledRoot = new YamlStream(); labelledRoot.Load(new StringReader(imported.Yaml));
        var labelledGroup = (YamlMappingNode)((YamlSequenceNode)((YamlMappingNode)labelledRoot.Documents[0].RootNode).Children[new YamlScalarNode("proxy-groups")]).Children[0];
        Check(((YamlScalarNode)((YamlSequenceNode)labelledGroup.Children[new YamlScalarNode("proxies")]).Children[0]).Value == "Sample", "VPN group references the imported server label");
        Check(!jsonImport.Yaml.Contains("119.29.29.29") && !jsonImport.Yaml.Contains("geosite:google"), "Xray DNS/routing are not silently carried into system routing");
        Check(ProfileImporter.Parse("```json\n" + json + "\n```").Yaml == jsonImport.Yaml, "JSON code fences can be pasted directly");
        Check(Field(Proxy(ProfileImporter.Parse(link + "&unused").Yaml), "name") == "Sample&unused", "URI fragment is a label, not a connection query");
        var encodedName = Uri.EscapeDataString("🇳🇱 — Нидерланды");
        Check(Field(Proxy(ProfileImporter.Parse(link.Split('#')[0] + "#" + encodedName).Yaml), "name") == "🇳🇱 — Нидерланды", "emoji and Cyrillic server labels are decoded once");
        Check(Field(Proxy(ProfileImporter.Parse(link.Split('#')[0] + "#%252Fname").Yaml), "name") == "%2Fname", "server label is not URL-decoded twice");
        Check(Field(Proxy(ProfileImporter.Parse(link.Split('#')[0] + "#%20%20").Yaml), "name") == "Подключение", "empty server label keeps neutral fallback");
        Check(Field(Proxy(ProfileImporter.Parse(link.Split('#')[0] + "#VPN").Yaml), "name") == "VPN (2)", "server label does not collide with VPN policy group");
        Reject(link.Split('#')[0] + "#bad%0Aname", "control characters in single-link label rejected");
        Reject(link.Split('#')[0] + "#bad%2Cname", "comma in single-link label rejected before creating broken routing");
        Reject(link.Split('#')[0] + "#" + new string('x', 257), "overlong single-link label rejected");
        Reject(link.Replace("id-does-not-occur", "x").Replace(id, "not-a-uuid"), "invalid UUID is rejected without an invalid profile");
        Reject(link.Replace("8443", "99999"), "invalid server port is rejected");
        Reject(link.Replace("path=%2Fws50", "path=%ZZ"), "malformed percent escaping is rejected");
        Reject(link.Replace("host=vpn.example", "host=vpn.example%5C"), "trailing backslash in WebSocket Host is rejected");
        Reject(link.Replace("host=vpn.example", "host=vpn.example%2F"), "slash in WebSocket Host is rejected");
        Reject(link.Replace("host=vpn.example", "host=vpn.example%0D%0AX-Test%3Asecret"), "header CRLF injection is rejected");
        Reject(link.Replace("path=%2Fws50", "path=ws50"), "WebSocket path requires a leading slash");
        Reject(link.Replace("&host=", "&unknown=1&host="), "unknown link parameters are not silently discarded");
        Reject(link.Replace("&host=", "&type=ws&host="), "duplicate link parameters are rejected");
        Reject(link.Replace("type=ws", "type=xhttp"), "unsupported transports are explicitly rejected");
        Reject(link.Replace("encryption=none", "encryption=unsupported"), "unsupported VLESS encryption is rejected");
        Reject("https://subscription.example/token", "HTTPS subscriptions are not downloaded unexpectedly");
        Reject("{bad json secret-token", "invalid JSON reports a safe error");
        Reject(json.Replace("\"port\":8443", "\"port\":\"8443\""), "string JSON ports are rejected with a friendly validation error");
        Reject(json.Replace("\"enabled\":false", "\"enabled\":true"), "enabled Xray mux is not silently discarded");
        Reject(json.Replace("\"headers\":{}", "\"headers\":{\"Host\":\"different.example\"}"), "conflicting WebSocket Host representations are rejected");
        Reject(json.Replace("\"allowInsecure\":false", "\"allowInsecure\":false,\"allowInsecure\":true"), "duplicate JSON keys are rejected");
        Reject(json.Replace("\"allowInsecure\":false", "\"allowInsecure\":false,\"pinnedPeerCertificateChainSha256\":[\"custom-pin\"]"), "unsupported TLS pinning is not silently discarded");
        Reject(new string('x', ProfileImporter.MaxInputLength + 1), "oversized pasted profiles are bounded");
        var insecure = ProfileImporter.Parse(link.Replace("#Sample", "&allowInsecure=1#Sample"));
        Check(insecure.InsecureTls && Field(Proxy(insecure.Yaml), "skip-cert-verify") == "true", "explicit insecure TLS requires a visible confirmation in the dialog");
        var tcp = ProfileImporter.Parse("vless://" + id + "@192.0.2.50:443?security=tls&sni=vpn.example&type=tcp&flow=xtls-rprx-vision");
        Check(Field(Proxy(tcp.Yaml), "flow") == "xtls-rprx-vision", "TCP Vision flow is retained");
        var grpc = ProfileImporter.Parse("vless://" + id + "@192.0.2.50:443?security=tls&type=grpc&sni=vpn.example&serviceName=my-service");
        Check(Field((YamlMappingNode)Proxy(grpc.Yaml).Children[new YamlScalarNode("grpc-opts")], "grpc-service-name") == "my-service", "gRPC service name is retained");
        var upgrade = ProfileImporter.Parse(link.Replace("type=ws", "type=httpupgrade"));
        Check(Field((YamlMappingNode)Proxy(upgrade.Yaml).Children[new YamlScalarNode("ws-opts")], "v2ray-http-upgrade") == "true", "HTTPUpgrade maps to Mihomo WebSocket upgrade mode");
        var reality = ProfileImporter.Parse("vless://" + id + "@192.0.2.50:443?security=reality&type=tcp&sni=vpn.example&flow=xtls-rprx-vision&pbk=" + new string('A', 43) + "&sid=abcd");
        Check(Field((YamlMappingNode)Proxy(reality.Yaml).Children[new YamlScalarNode("reality-opts")], "short-id") == "abcd", "Reality public key and short ID are retained");
        var core = await BundledResources.EnsureCoreAsync();
        Check(Path.GetFullPath(core) == Path.Combine(AppContext.BaseDirectory, BundledResources.CoreFileName)
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(core))) == BundledResources.CoreHash,
            "official Mihomo executable is loaded from beside the app and verified by SHA-256");
        Check(await BundledResources.EnsureCoreAsync() == core,
            "subsequent core resolution reuses the verified side-by-side executable");
        var existingSettings = AppSettings.Load(Path.Combine(scratch, "existing-settings.json"));
        existingSettings.ConfigPath = Path.Combine(scratch, "existing-custom.yaml");
        existingSettings.CorePath = fakeExe;
        File.WriteAllText(existingSettings.ConfigPath, "custom content must not be replaced");
        await BundledResources.PrepareDefaultsAsync(existingSettings, CancellationToken.None);
        Check(existingSettings.CorePath == fakeExe && File.ReadAllText(existingSettings.ConfigPath) == "custom content must not be replaced", "automatic defaults preserve existing custom core and profile");
        var failedStorage = Path.Combine(scratch, "failed-import");
        var rejected = false;
        try { await ProfileStorage.CreateAsync(new ImportedProfile("INVALID", "Bad", false), fakeExe, failedStorage, CancellationToken.None); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.EnumerateFiles(Path.Combine(failedStorage, "Profiles"), "*.yaml").Any(), "failed validation removes its candidate without publishing a broken profile");
        foreach (var item in new[] { imported, tcp, grpc, upgrade, reality, ProfileImporter.Parse(link.Split('#')[0] + "#" + encodedName), ProfileImporter.Parse(link.Split('#')[0] + "#VPN"), new ImportedProfile(BundledResources.TemplateText, "Template", false) })
        {
            var named = item.NeedsName ? ProfileImporter.NameConnection(item, "Тестовый сервер") : item;
            var path = await ProfileStorage.CreateAsync(named, core, Path.Combine(scratch, "real-core-imports"), CancellationToken.None);
            Check(File.Exists(path), "actual bundled Mihomo validates imported " + item.Description + " without starting VPN");
        }
        var settings = AppSettings.Load(Path.Combine(scratch, "import-ui", "settings.json"));
        settings.AutoCheckUpdates = false;
        settings.CorePath = fakeExe;
        var window = new MainWindow(settings, Path.Combine(scratch, "import-ui.log")) { ShowInTaskbar = false };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)typeof(MainWindow).GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            await gate.WaitAsync(); gate.Release();
            var oldTemplate = settings.ConfigPath;
            var oldText = File.ReadAllText(oldTemplate);
            var seen = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault(value => value.Title == "ИМПОРТ ПРОФИЛЯ");
                if (dialog is null) return;
                timer.Stop(); seen = true;
                var input = Descendants<TextBox>(dialog).Single(value => value.Name == "ImportProfileInput");
                var submit = Descendants<Button>(dialog).Single(value => Equals(value.Content, "СОЗДАТЬ ПРОФИЛЬ"));
                input.Text = "https://127.0.0.1/private-token";
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(dialog.IsVisible && input.IsEnabled && submit.IsEnabled, "HTTPS validation error keeps import dialog usable");
                Check(Descendants<TextBlock>(dialog).Any(value => value.Text.Contains("публичном HTTPS-сервере"))
                    && !Descendants<TextBlock>(dialog).Any(value => value.Text.Contains("private-token")), "HTTPS import shows safe error without leaking the URL");
                input.Text = link;
                dialog.UpdateLayout(); CaptureInteraction(dialog, Path.Combine(scratch, "profile-import-dialog.png"));
                Descendants<Button>(dialog).Single(value => Equals(value.Content, "СОЗДАТЬ ПРОФИЛЬ")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            typeof(MainWindow).GetMethod("ImportProfile_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            await gate.WaitAsync(); gate.Release();
            timer.Stop();
            Check(seen && window.OwnedWindows.Count == 0 && window.WindowState != WindowState.Minimized, "import modal submits and restores its visible owner without leftover blur");
            Check(settings.ConfigPath != oldTemplate && File.ReadAllText(oldTemplate) == oldText && File.Exists(settings.ConfigPath), "actual UI import creates a new YAML without overwriting the old template");
            Check(ClashConfigDocument.Load(settings.ConfigPath).HasVpnConnection && settings.PreferredRoute == "VPN", "UI activates the imported profile and selects VPN for new rules");
            Check(AppSettings.Load(Path.Combine(scratch, "import-ui", "settings.json")).ConfigPath == settings.ConfigPath, "imported profile path is persisted across launches");
            var savedPath = settings.ConfigPath;
            var cancelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            cancelTimer.Tick += (_, _) => { var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault(value => value.Title == "ИМПОРТ ПРОФИЛЯ"); if (dialog is null) return; cancelTimer.Stop(); dialog.Close(); };
            cancelTimer.Start();
            typeof(MainWindow).GetMethod("ImportProfile_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            cancelTimer.Stop();
            Check(settings.ConfigPath == savedPath && window.IsVisible && window.WindowState != WindowState.Minimized, "cancelled import does not switch profiles or minimize the app");
            var settingsSeen = false;
            var settingsDialogStayedOpenAfterSave = false;
            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            DispatcherTimer? closeSettingsAfterSave = null;
            settingsTimer.Tick += (_, _) =>
            {
                var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault(value => value.Title == "НАСТРОЙКИ");
                if (dialog is null) return;
                settingsTimer.Stop(); settingsSeen = true;
                Descendants<Button>(dialog).Single(value => Equals(value.Content, "VPN-профиль")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog.UpdateLayout();
                Check(Descendants<Button>(dialog).Any(value => Equals(value.Content, "Выбрать ядро") && value.IsVisible)
                    && Descendants<Button>(dialog).Any(value => Equals(value.Content, "Использовать ядро из комплекта") && value.IsVisible), "profile settings expose custom and bundled core options");
                Check(Descendants<Button>(dialog).Any(value => Equals(value.Content, "Импорт профиля") && value.IsVisible)
                    && Descendants<Button>(dialog).Any(value => Equals(value.Content, "Создать шаблон") && value.IsVisible), "profile settings expose import and neutral-template creation");
                CaptureInteraction(dialog, Path.Combine(scratch, "profile-settings-dialog.png"));
                Descendants<Button>(dialog).Single(value => Equals(value.Content, "Создать шаблон")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Descendants<Button>(dialog).Single(value => Equals(value.Content, "СОХРАНИТЬ")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                settingsDialogStayedOpenAfterSave = dialog.IsVisible;
                closeSettingsAfterSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                closeSettingsAfterSave.Tick += (_, _) =>
                {
                    if (gate.CurrentCount == 0) return;
                    closeSettingsAfterSave.Stop();
                    if (dialog.IsVisible) dialog.Close();
                };
                closeSettingsAfterSave.Start();
            };
            settingsTimer.Start();
            typeof(MainWindow).GetMethod("SettingsButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            await gate.WaitAsync(); gate.Release(); settingsTimer.Stop(); closeSettingsAfterSave?.Stop();
            Check(settingsSeen && settingsDialogStayedOpenAfterSave && settings.ConfigPath != savedPath && File.Exists(savedPath) && !ClashConfigDocument.Load(settings.ConfigPath).HasVpnConnection,
                "saving a new template from settings preserves the existing connected profile");
            count += await RunConnectionNameChecks(window, settings, link.Split('#')[0]);
        }
        finally { typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true); window.Close(); }
        return count;
    }

    private static async Task<int> RunConnectionNameChecks(MainWindow window, AppSettings settings, string link)
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        var oldPath = settings.ConfigPath;
        var oldText = File.ReadAllText(oldPath);
        var phase = 0;
        Exception? failure = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (DateTime.UtcNow > deadline) throw new Exception("Name dialog test timed out");
                var import = window.OwnedWindows.Cast<Window>().FirstOrDefault(value => value.Title == "ИМПОРТ ПРОФИЛЯ");
                var naming = import?.OwnedWindows.Cast<Window>().FirstOrDefault(value => value.Title == "НАЗВАНИЕ ПОДКЛЮЧЕНИЯ");
                if (phase == 0 && import is not null)
                {
                    Descendants<TextBox>(import).Single(value => value.Name == "ImportProfileInput").Text = link;
                    phase = 1;
                    Descendants<Button>(import).Single(value => Equals(value.Content, "СОЗДАТЬ ПРОФИЛЬ")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (phase == 1 && naming is not null)
                {
                    var input = Descendants<TextBox>(naming).Single(value => value.Name == "ConnectionNameInput");
                    var accept = Descendants<Button>(naming).Single(value => Equals(value.Content, "ПРОДОЛЖИТЬ"));
                    Check(input.Text == "", "missing link name opens an empty naming prompt instead of silently naming the server");
                    accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(naming.IsVisible, "empty name keeps naming dialog open");
                    input.Text = "VPN";
                    accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(naming.IsVisible, "reserved name keeps naming dialog open with validation");
                    phase = 2;
                    Descendants<Button>(naming).Single(value => Equals(value.Content, "ОТМЕНА")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (phase == 2 && import is not null && naming is null)
                {
                    var submit = Descendants<Button>(import).Single(value => Equals(value.Content, "СОЗДАТЬ ПРОФИЛЬ"));
                    if (!submit.IsEnabled) return;
                    Check(settings.ConfigPath == oldPath && File.ReadAllText(oldPath) == oldText, "cancelled name prompt leaves selected profile unchanged");
                    phase = 3;
                    submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (phase == 3 && naming is not null)
                {
                    Descendants<TextBox>(naming).Single(value => value.Name == "ConnectionNameInput").Text = "🇳🇱 Мой VPN";
                    phase = 4;
                    Descendants<Button>(naming).Single(value => Equals(value.Content, "ПРОДОЛЖИТЬ")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (phase == 4 && import is null) timer.Stop();
            }
            catch (Exception ex)
            {
                failure = ex; timer.Stop();
                var import = window.OwnedWindows.Cast<Window>().FirstOrDefault(value => value.Title == "ИМПОРТ ПРОФИЛЯ");
                foreach (var nested in import?.OwnedWindows.Cast<Window>().ToArray() ?? Array.Empty<Window>()) nested.Close();
                import?.Close();
            }
        };
        timer.Start();
        try
        {
            typeof(MainWindow).GetMethod("ImportProfile_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            var gate = (SemaphoreSlim)typeof(MainWindow).GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            await gate.WaitAsync(); gate.Release();
            if (failure is not null) throw failure;
            Check(phase == 4 && settings.ConfigPath != oldPath && File.ReadAllText(oldPath) == oldText, "named import creates a separate profile and preserves previous YAML");
            var parsed = new YamlStream(); parsed.Load(new StringReader(File.ReadAllText(settings.ConfigPath)));
            var root = (YamlMappingNode)parsed.Documents[0].RootNode;
            var node = (YamlMappingNode)((YamlSequenceNode)root.Children[new YamlScalarNode("proxies")]).Children[0];
            var group = (YamlMappingNode)((YamlSequenceNode)root.Children[new YamlScalarNode("proxy-groups")]).Children[0];
            Check(((YamlScalarNode)node.Children[new YamlScalarNode("name")]).Value == "🇳🇱 Мой VPN"
                && ((YamlScalarNode)((YamlSequenceNode)group.Children[new YamlScalarNode("proxies")]).Children[0]).Value == "🇳🇱 Мой VPN", "name entered through the real UI reaches the server and VPN group");
        }
        finally { timer.Stop(); }
        return count;
    }
}
