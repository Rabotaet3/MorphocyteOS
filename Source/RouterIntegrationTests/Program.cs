using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
using System.Xml.Linq;
using System.Windows.Markup;
using MorphocyteRouter;

internal partial class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Theme.xaml")).Root!.Element(ns + "Application.Resources")!;
        var dictionary = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), resources.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var code = 1;
        _ = app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Run(args[0], args.Contains("--improvements-only"), args.Contains("--visual-only")); code = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return code;
    }
    static async Task Run(string fakeExe, bool improvementsOnly = false, bool visualOnly = false)
    {
        var scratch = Path.Combine(AppContext.BaseDirectory, "integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var config = Path.Combine(scratch, "profile.yaml");
        var initial = "proxies:\n  - name: VPN\n    type: socks5\n    server: 127.0.0.1\n    port: 9999\ntun:\n  enable: true\nrules:\n  - DOMAIN-SUFFIX,private.example,DIRECT\n  - PROCESS-NAME,SampleBrowser.exe,VPN\n  - MATCH,DIRECT\n";
        File.WriteAllText(config, initial);
        var settingsPath = Path.Combine(scratch, "settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.AutoCheckUpdates = false;
        settings.ConfigPath = config;
        settings.CorePath = Path.GetFullPath(fakeExe);
        settings.ShowEventLog = false;
        var window = new MainWindow(settings, Path.Combine(scratch, "test.log"));
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Task Call(string name, params object[] values) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, values)!;
            async Task WaitForUiOperation()
            {
                var gate = (SemaphoreSlim)Field("_operations");
                await gate.WaitAsync();
                gate.Release();
            }
        var core = (CoreProcessManager)Field("_core");
        var rules = (ObservableCollection<DomainRule>)Field("_rules");
        var passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
        try
        {
            if (visualOnly) { await RunVisualLayoutChecks(scratch, fakeExe); return; }
            passed += await RunImprovementChecks(scratch, fakeExe);
            if (improvementsOnly) { Console.WriteLine($"TOTAL: {passed} improvement checks passed."); return; }
            passed += await RunVisualLayoutChecks(scratch, fakeExe);
            passed += await RunFreshLaunchChecks(scratch);
            passed += await RunProfileImportChecks(scratch, fakeExe);
            passed += await RunConfigurationTransferChecks(scratch, fakeExe);
            passed += await RunRuleInteractionChecks(scratch, fakeExe);
            passed += await RunProcessAndUpdateChecks(scratch, fakeExe);
            passed += await RunDesktopFeatureChecks(scratch, fakeExe);
            passed += await RunRealDiagnosticsChecks(scratch);
            passed += await RunTelemetryChecks();
            var workspace = (FrameworkElement)Field("Workspace");
            var busyField = typeof(MainWindow).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            busyField.SetValue(window, true);
            typeof(MainWindow).GetMethod("UpdateControls", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Check(workspace.IsEnabled && !workspace.IsHitTestVisible, "busy lifecycle blocks input without switching the rules list to the Windows disabled palette");
            busyField.SetValue(window, false);
            typeof(MainWindow).GetMethod("UpdateControls", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

            // Smoke-test the styled process picker in a real WPF dispatcher and close it like Cancel.
            var pickerOwner = new Window { Width = 200, Height = 100, ShowInTaskbar = false };
            pickerOwner.Show();
            var pickerType = typeof(MainWindow).Assembly.GetType("MorphocyteRouter.RunningProcessDialog")!;
            var showPicker = pickerType.GetMethod("ShowAsync", BindingFlags.Static | BindingFlags.Public)!;
            var pickerSeen = false;
            var pickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            pickerTimer.Tick += (_, _) =>
            {
                var pickerWindow = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.Title == "Запущенные приложения");
                if (pickerWindow is null) return;
                pickerSeen = true;
                pickerTimer.Stop();
                CaptureInteraction(pickerWindow, Path.Combine(scratch, "running-process-picker.png"));
                pickerWindow.Close();
            };
            pickerTimer.Start();
            var pickerTask = (Task<IReadOnlyList<string>>)showPicker.Invoke(null, new object[] { pickerOwner })!;
            await pickerTask;
            var ownerReturned = pickerOwner.IsVisible && pickerOwner.WindowState != WindowState.Minimized;
            pickerOwner.Close();
            Check(pickerSeen && ownerReturned, "styled running-process picker cancels and returns to the visible owner window");

            await Call("LoadConfigAsync", config);
            Check(rules[0].Route == "DIRECT", "profile imported with direct exception");
            var routeCombo = (ComboBox)Field("RouteCombo");
            Check(routeCombo.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, "DIRECT"))
                && routeCombo.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, "REJECT")),
                "UI exposes direct and reject actions");
            var victim = rules[0];
            typeof(MainWindow).GetMethod("DeleteRule_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { new Button { Tag = victim }, new RoutedEventArgs() });
            await WaitForUiOperation();
            Check(!rules.Contains(victim), "delete removes rule through actual UI handler");
            typeof(MainWindow).GetMethod("UndoDelete_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { new Button(), new RoutedEventArgs() });
            await WaitForUiOperation();
            Check(rules.Any(rule => rule.Value == victim.Value && rule.Route == "DIRECT"), "undo restores deleted rule");
            var secondRan = false;
            var running = Call("RunExclusiveAsync", (Func<Task>)(() => Task.Delay(80)));
            await Call("RunExclusiveAsync", (Func<Task>)(() => { secondRan = true; return Task.CompletedTask; }));
            await running;
            Check(!secondRan, "UI gate rejects overlapping operation");
            rules.Add(new DomainRule { Kind = "DOMAIN-SUFFIX", Value = "new.example", Route = "VPN" });
            await Call("RecordEditAsync");
            Check(AppSettings.Load(settingsPath).GetDraft(config)!.Rules.Count == 3, "actual UI edit saves draft");
            await Call("ApplyChangesAsync", true);
            Check(ClashConfigDocument.Load(config).Rules[0].Route == "DIRECT", "actual Apply preserves direct route");
            Check(AppSettings.Load(settingsPath).GetDraft(config) is null, "apply clears only applied draft");
            var disabledRule = rules.First(rule => rule.Route == "DIRECT");
            disabledRule.Folder = "Исключения";
            typeof(MainWindow).GetMethod("RuleEnabled_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { new CheckBox { DataContext = disabledRule }, new RoutedEventArgs() });
            await WaitForUiOperation();
            await Call("ApplyChangesAsync", true);
            Check(!ClashConfigDocument.Load(config).Rules.Any(rule => rule.Value == disabledRule.Value),
                "disabled rule omitted from applied config");
            Check(rules.Any(rule => rule.Value == disabledRule.Value && !rule.Enabled),
                "disabled rule remains visible in manager after apply");
            await Call("LoadConfigAsync", config);
            Check(rules.Any(rule => rule.Value == disabledRule.Value && !rule.Enabled && rule.Folder == "Исключения"),
                "disabled state and folder survive profile reload");
            disabledRule = rules.First(rule => rule.Value == disabledRule.Value);
            typeof(MainWindow).GetMethod("RuleEnabled_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { new CheckBox { DataContext = disabledRule }, new RoutedEventArgs() });
            await WaitForUiOperation();
            await Call("ApplyChangesAsync", true);
            Check(ClashConfigDocument.Load(config).Rules.Any(rule => rule.Value == disabledRule.Value),
                "reenabled rule returns to applied config");
            var sibling = rules.First(rule => rule.Value == "new.example");
            sibling.Folder = "Исключения";
            await Call("RecordEditAsync");
            var rulesView = (System.ComponentModel.ICollectionView)Field("_rulesView");
            var folderGroup = rulesView.Groups!.OfType<System.Windows.Data.CollectionViewGroup>().Single(group => Equals(group.Name, "Исключения"));
            var folderToggle = new CheckBox { DataContext = folderGroup };
            typeof(MainWindow).GetMethod("FolderEnabled_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { folderToggle, new RoutedEventArgs() });
            await WaitForUiOperation();
            Check(folderGroup.Items.Cast<DomainRule>().All(rule => !rule.Enabled), "one folder toggle disables every rule in that folder");
            folderGroup = rulesView.Groups!.OfType<System.Windows.Data.CollectionViewGroup>().Single(group => Equals(group.Name, "Исключения"));
            folderToggle = new CheckBox { DataContext = folderGroup };
            typeof(MainWindow).GetMethod("FolderEnabled_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { folderToggle, new RoutedEventArgs() });
            await WaitForUiOperation();
            Check(folderGroup.Items.Cast<DomainRule>().All(rule => rule.Enabled), "folder toggle re-enables all folder rules");
            Check(AppSettings.Load(settingsPath).GetDraft(config)!.Rules.Where(rule => rule.Folder == "Исключения").All(rule => rule.Enabled),
                "folder-wide toggle persists its per-rule states");
            await Call("StartCoreAsync", true);
            Check(core.IsRunning, "fake core started by actual UI path");
            var before = File.ReadAllText(config);
            rules.Add(new DomainRule { Kind = "DOMAIN-SUFFIX", Value = "INVALID.example", Route = "VPN" });
            await Call("RecordEditAsync");
            var rejected = false;
            try { await Call("ApplyChangesAsync", true); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && core.IsRunning && File.ReadAllText(config) == before, "invalid candidate leaves running core and YAML intact");
            rules.Remove(rules.Last());
            rules.Add(new DomainRule { Kind = "DOMAIN-SUFFIX", Value = "startup-fail.example", Route = "VPN" });
            await Call("RecordEditAsync");
            rejected = false;
            try { await Call("ApplyChangesAsync", true); } catch (IOException) { rejected = true; }
            Check(rejected && core.IsRunning && File.ReadAllText(config) == before, "failed restart rolls back YAML and restores old core");
            Check(AppSettings.Load(settingsPath).GetDraft(config)!.Rules.Any(rule => rule.Value == "startup-fail.example"), "rollback retains editable draft");
            Check(Directory.GetFiles(scratch, "profile.yaml.failed-*").Length == 0,
                "failed restart retains its draft without accumulating rejected YAML copies");
            rules.Remove(rules.Last());
            await Call("RecordEditAsync");
            await Call("StopCoreAsync");

            var removableGroup = rulesView.Groups!.OfType<System.Windows.Data.CollectionViewGroup>().Single(group => Equals(group.Name, "Исключения"));
            var retainedRules = removableGroup.Items.OfType<DomainRule>().ToArray();
            typeof(MainWindow).GetMethod("RemoveFolder_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { new Button { Tag = removableGroup }, new RoutedEventArgs() });
            await WaitForUiOperation();
            Check(retainedRules.All(rule => rules.Contains(rule) && string.IsNullOrWhiteSpace(rule.Folder)), "deleting a folder moves its rules to the general group instead of deleting them");

            // A close request while validation is in flight must not start a core afterwards.
            File.WriteAllText(config, before + "# slow-validation\n");
            await Call("LoadConfigAsync", config);
            var start = Call("RunExclusiveAsync", (Func<Task>)(() => Call("StartCoreAsync", false)));
            await Task.Delay(70);
            typeof(MainWindow).GetMethod("MainWindow_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object?[] { window, new CancelEventArgs() });
            await start;
            await Task.Delay(300);
            Check(!core.IsRunning && (bool)Field("_allowClose"), "close cancels validation and prevents late core start");
            Console.WriteLine($"TOTAL: {passed} UI workflow checks passed.");
        }
        finally { await core.StopAsync(); }
    }

}
