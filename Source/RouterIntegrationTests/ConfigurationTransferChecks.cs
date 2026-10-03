using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunConfigurationTransferChecks(string scratch, string fakeExe)
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        var sourceRoot = Path.Combine(scratch, "transfer-source");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "rules"));
        const string resource = "payload:\n  - 'DOMAIN-SUFFIX,provider.example'\n";
        File.WriteAllText(Path.Combine(sourceRoot, "rules", "local.yaml"), resource);
        var sourcePath = Path.Combine(sourceRoot, "source.yaml");
        const string yaml = "proxies:\n  - name: Alpha\n    type: socks5\n    server: 127.0.0.1\n    port: 1080\n    username: fixture-user\n    password: fixture-password\nproxy-groups:\n  - name: VPN\n    type: select\n    proxies: [Alpha]\ntun:\n  enable: true\nrule-providers:\n  local-list:\n    type: file\n    behavior: classical\n    path: ./rules/local.yaml\nrules:\n  - DOMAIN-SUFFIX,example.com,VPN\n  - PROCESS-NAME,Example.exe,VPN\n  - RULE-SET,local-list,DIRECT\n  - MATCH,DIRECT\n";
        File.WriteAllText(sourcePath, yaml);
        var sourceSettings = AppSettings.Load(Path.Combine(sourceRoot, "settings.json"));
        sourceSettings.ConfigPath = sourcePath; sourceSettings.CorePath = fakeExe; sourceSettings.AutoCheckUpdates = false;
        var source = new MainWindow(sourceSettings, Path.Combine(sourceRoot, "test.log")) { ShowInTaskbar = false };
        MainWindow? target = null;
        object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Ready(MainWindow window)
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field(window, "_operations"); await gate.WaitAsync(); gate.Release();
        }
        try
        {
            await Ready(source);
            var rules = (ObservableCollection<DomainRule>)Field(source, "_rules");
            rules[0].Folder = "Работа";
            rules[1].Folder = "Работа"; rules[1].Route = "DIRECT";
            rules.Add(new DomainRule { Kind = "DOMAIN", Value = "disabled.example", Route = "REJECT", Folder = "Архив", Enabled = false });
            await (Task)Method("RecordEditAsync").Invoke(source, null)!;
            await (Task)Method("StartCoreAsync").Invoke(source, new object[] { false })!;
            var selection = new SettingsDialogSelection(sourcePath, fakeExe, "Аметист", 1.25, false, LaunchAtSignIn: false, AutoConnectOnStartup: true);
            var package = await source.CaptureConfigurationAsync(selection);
            Check(package.HasDraft && package.Rules.Count == 3 && package.Rules[1].Route == "DIRECT" && !package.Rules[2].Enabled,
                "one-file export includes the current unapplied rules, routes and disabled entries");
            Check(package.FolderOrder.SequenceEqual(sourceSettings.GetFolderOrder(sourcePath))
                && package.FolderOrder.Contains("Работа") && package.FolderOrder.Contains("Архив") && package.Rules[0].Folder == "Работа",
                "export preserves folders and their display order");
            Check(package.Options.Theme == "Аметист" && package.Options.InterfaceScale == 1.25 && !package.Options.AutoCheckUpdates
                && !package.Options.LaunchAtSignIn && package.Options.AutoConnectOnStartup, "export includes selected appearance, update and startup preferences");
            Check(package.Yaml.Contains("fixture-password") && package.Yaml.Contains("RULE-SET,local-list,DIRECT")
                && package.Files.Count == 1 && Encoding.UTF8.GetString(package.Files[0].Content) == resource
                && !package.Yaml.Contains("./rules/local.yaml"), "complete YAML, connection credentials and local provider data share one portable file");
            var text = ConfigurationTransfer.Serialize(package);
            var exportedPath = Path.Combine(scratch, "configuration.morphocyte");
            AtomicFile.Write(exportedPath, text);
            var roundTrip = ConfigurationTransfer.Read(exportedPath);
            Check(roundTrip.Yaml == package.Yaml && roundTrip.Rules[1].SourceIndex == package.Rules[1].SourceIndex
                && roundTrip.Files[0].Content.SequenceEqual(package.Files[0].Content), "serialized transfer round-trips YAML, rule identity and resource bytes");
            Check(!text.Contains("CorePath") && !text.Contains("ConfigPath") && !text.Contains("ProfileDrafts")
                && File.ReadAllText(sourcePath) == yaml && (bool)Field(source, "_dirty"), "export omits machine-specific executable paths and never applies the draft or edits source YAML");
            Check(((CoreProcessManager)Field(source, "_core")).IsRunning, "export leaves the running VPN core connected");

            foreach (var mutation in new Action<JsonObject>[]
            {
                node => node["SchemaVersion"] = 999,
                node => node["Format"] = "OtherApplication",
                node => node["Rules"] = null,
                node => node["Rules"]![0]!["Kind"] = "MATCH",
                node => node["Rules"]![2]!["Value"] = "bad,value",
                node => node["Rules"]![0]!["Route"] = "MissingProxy",
                node => node["Files"]![0]!["Path"] = "../outside.yaml",
                node => node["Files"] = new JsonArray(),
                node => node["HasDraft"] = false,
                node => node["Options"]!["InterfaceScale"] = 999
            })
            {
                var node = JsonNode.Parse(text)!.AsObject(); mutation(node);
                var rejected = false;
                try { _ = ConfigurationTransfer.Deserialize(Encoding.UTF8.GetBytes(node.ToJsonString())); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected && File.ReadAllText(sourcePath) == yaml, "invalid transfer metadata, rules or resource paths are rejected before changing a profile");
            }
            var oversizedPath = Path.Combine(scratch, "oversized.morphocyte");
            using (var stream = File.Create(oversizedPath)) stream.SetLength(ConfigurationTransfer.MaxPackageBytes + 1L);
            var oversizedRejected = false;
            try { _ = ConfigurationTransfer.Read(oversizedPath); } catch (InvalidDataException) { oversizedRejected = true; }
            Check(oversizedRejected, "oversized transfer files are rejected before reading their payload into memory");
            File.Delete(oversizedPath);

            var realCore = Path.Combine(AppContext.BaseDirectory, "mihomo.exe");
            var targetRoot = Path.Combine(scratch, "transfer-target"); Directory.CreateDirectory(targetRoot);
            var oldPath = Path.Combine(targetRoot, "old.yaml"); File.WriteAllText(oldPath, yaml.Replace("./rules/local.yaml", Path.Combine(sourceRoot, "rules", "local.yaml").Replace('\\', '/')));
            var oldText = File.ReadAllText(oldPath);
            var targetSettings = AppSettings.Load(Path.Combine(targetRoot, "settings.json"));
            targetSettings.ConfigPath = oldPath; targetSettings.CorePath = realCore; targetSettings.AutoCheckUpdates = false;
            target = new MainWindow(targetSettings, Path.Combine(targetRoot, "test.log")) { ShowInTaskbar = false };
            await Ready(target);
            var importChoice = selection with { ConfigPath = oldPath, CorePath = realCore, Transfer = roundTrip };
            var imported = await (Task<SettingsDialogSelection?>)Method("SaveSettingsSelectionAsync").Invoke(target, new object[] { importChoice })!;
            Check(imported is not null && targetSettings.ConfigPath != oldPath && File.ReadAllText(oldPath) == oldText
                && File.ReadAllText(sourcePath) == yaml, "import creates a separate profile and leaves both old and source profiles intact");
            var importedFolder = Path.GetDirectoryName(targetSettings.ConfigPath)!;
            Check(File.ReadAllText(Path.Combine(importedFolder, roundTrip.Files[0].Path)) == resource
                && (await CoreProcessManager.ValidateAsync(realCore, targetSettings.ConfigPath)).Success,
                "imported YAML resolves its bundled provider relative to the new folder and passes real Mihomo validation");
            var importedRules = (ObservableCollection<DomainRule>)Field(target, "_rules");
            Check(importedRules.Count == 3 && importedRules[1].Route == "DIRECT" && !importedRules[2].Enabled
                && importedRules[2].Folder == "Архив" && (bool)Field(target, "_dirty")
                && ClashConfigDocument.Load(targetSettings.ConfigPath).Rules[1].Route == "VPN", "import restores edits as a draft without silently applying them to VPN");
            Check(targetSettings.Theme == "Аметист" && targetSettings.InterfaceScale == 1.25 && targetSettings.CorePath == realCore
                && targetSettings.GetFolderOrder(targetSettings.ConfigPath).SequenceEqual(package.FolderOrder), "import persists portable preferences while keeping the target computer's own core");
            await (Task)Method("ApplyChangesAsync").Invoke(target, new object[] { false })!;
            Check(ClashConfigDocument.Load(targetSettings.ConfigPath).Rules[1].Route == "DIRECT"
                && File.ReadAllText(targetSettings.ConfigPath).Contains("RULE-SET,local-list,DIRECT") && targetSettings.GetDraft(targetSettings.ConfigPath) is null,
                "explicit Apply commits transferred edits while retaining provider rules and clears the restored draft");
            var clean = await target.CaptureConfigurationAsync(imported! with { ConfigPath = targetSettings.ConfigPath });
            Check(!clean.HasDraft && clean.Rules.Any(rule => !rule.Enabled), "export after Apply keeps disabled entries without inventing an unapplied draft");
            var cleanImport = await (Task<SettingsDialogSelection?>)Method("SaveSettingsSelectionAsync").Invoke(target,
                new object[] { imported! with { ConfigPath = targetSettings.ConfigPath, Transfer = clean } })!;
            Check(cleanImport is not null && !(bool)Field(target, "_dirty") && ((ObservableCollection<DomainRule>)Field(target, "_rules")).Any(rule => !rule.Enabled),
                "clean transfer restores disabled UI entries and retains the applied configuration state");
            var activePath = targetSettings.ConfigPath;
            var invalid = ConfigurationTransfer.Deserialize(Encoding.UTF8.GetBytes(ConfigurationTransfer.Serialize(clean)));
            invalid.Yaml = invalid.Yaml.Replace("type: socks5", "type: fixture-invalid-proxy");
            var rejectedByCore = false;
            try { _ = await ConfigurationTransfer.CreateProfileAsync(invalid, realCore, targetRoot, CancellationToken.None); }
            catch (InvalidDataException) { rejectedByCore = true; }
            Check(rejectedByCore && targetSettings.ConfigPath == activePath && File.ReadAllText(oldPath) == oldText,
                "real-core rejection of an unsupported configuration leaves the selected profile unchanged");
            var staleOriginal = File.ReadAllText(sourcePath);
            File.AppendAllText(sourcePath, "# changed externally\n");
            var staleRejected = false;
            try { _ = await source.CaptureConfigurationAsync(selection); } catch (IOException) { staleRejected = true; }
            Check(staleRejected, "export refuses to combine externally changed YAML with a stale editor draft");
            File.WriteAllText(sourcePath, staleOriginal);
            var startupPreferences = await source.CaptureConfigurationAsync(selection with { LaunchAtSignIn = true, AutoConnectOnStartup = false });
            Check(startupPreferences.Options.LaunchAtSignIn && !startupPreferences.Options.AutoConnectOnStartup && !sourceSettings.LaunchAtSignIn,
                "export stores chosen startup preferences without registering or changing Windows startup");
            var customScaleChoice = await (Task<SettingsDialogSelection?>)Method("SaveSettingsSelectionAsync").Invoke(target,
                new object[] { imported! with { ConfigPath = targetSettings.ConfigPath, InterfaceScale = 1.13 } })!;
            Check(customScaleChoice?.InterfaceScale == 1.13, "portable scale settings retain valid intermediate values without rounding to a preset");

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            var uiSeen = false;
            timer.Tick += (_, _) =>
            {
                var dialog = target.OwnedWindows.OfType<Window>().FirstOrDefault(candidate => candidate.Title == "НАСТРОЙКИ");
                if (dialog is null) return;
                timer.Stop();
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "VPN-профиль")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog.UpdateLayout();
                var scroll = Descendants<ScrollViewer>(dialog).Single(view => view.Name == "SettingsScroll");
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "Экспорт настроек")).BringIntoView();
                dialog.UpdateLayout();
                uiSeen = Descendants<Button>(dialog).Any(button => Equals(button.Content, "Экспорт настроек") && button.IsVisible)
                    && Descendants<Button>(dialog).Any(button => Equals(button.Content, "Импорт настроек") && button.IsVisible) && scroll.VerticalOffset > 0
                    && Descendants<ComboBox>(dialog).Any(combo => Equals(combo.SelectedItem, "113%"));
                CaptureInteraction(dialog, Path.Combine(scratch, "configuration-transfer-settings.png"));
                dialog.Close();
            };
            timer.Start();
            Method("SettingsButton_Click").Invoke(target, new object[] { new Button(), new RoutedEventArgs() });
            timer.Stop();
            Check(uiSeen && targetSettings.ConfigPath == activePath, "profile settings expose export and staged import on the scrolling page; closing settings leaves the selected profile unchanged");
            return count;
        }
        finally
        {
            foreach (var window in new[] { source, target }.OfType<MainWindow>())
            {
                await ((CoreProcessManager)Field(window, "_core")).StopAsync();
                typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
            }
        }
    }
}
