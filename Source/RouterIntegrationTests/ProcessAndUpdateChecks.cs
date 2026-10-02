using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunProcessAndUpdateChecks(string scratch, string fakeExe)
    {
        var config = Path.Combine(scratch, "process-selection.yaml");
        const string yaml = "proxies:\n  - name: VPN\n    type: socks5\n    server: 127.0.0.1\n    port: 9999\ntun:\n  enable: true\nrules:\n  - DOMAIN-SUFFIX,example.com,VPN\n  - MATCH,DIRECT\n";
        File.WriteAllText(config, yaml);
        var settings = AppSettings.Load(Path.Combine(scratch, "process-selection-settings.json"));
        settings.ConfigPath = config; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false;
        var window = new MainWindow(settings, Path.Combine(scratch, "process-selection.log")) { ShowInTaskbar = false };
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            window.Show();
            var gate = (SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var rules = (ObservableCollection<DomainRule>)Field("_rules");
            await (Task)Method("AddApplicationsAsync").Invoke(window, new object[] { new[] { "FirstApp.exe", "SecondApp.exe", "firstapp.EXE" } })!;
            Check(rules.Count == 3 && rules.Count(rule => rule.Kind == "PROCESS-NAME") == 2, "multiple selected applications enter the draft immediately with case-insensitive deduplication");
            Check(File.ReadAllText(config) == yaml && settings.GetDraft(config)?.Rules.Count == 3 && (bool)Field("_dirty"), "choosing applications persists draft but never applies YAML");
            await (Task)Method("AddApplicationsAsync").Invoke(window, new object[] { new[] { "FirstApp.exe" } })!;
            Check(rules.Count == 3, "selecting an existing application does not duplicate its rule");
            Check((Field("_core") as CoreProcessManager)?.IsRunning == false, "application selection does not start or restart the core");
            var assembly = typeof(MainWindow).Assembly;
            var pickerType = assembly.GetType("MorphocyteRouter.RunningProcessDialog")!;
            var appType = pickerType.GetNestedType("RunningApp", BindingFlags.NonPublic)!;
            var pickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            var extended = false;
            pickerTimer.Tick += (_, _) =>
            {
                var picker = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.Title == "Запущенные приложения");
                if (picker is null) return;
                pickerTimer.Stop();
                var list = Descendants<ListBox>(picker).Single(); extended = list.SelectionMode == SelectionMode.Extended;
                var data = Array.CreateInstance(appType, 3);
                for (var i = 0; i < 3; i++) data.SetValue(Activator.CreateInstance(appType, "Selected" + i + ".exe", "Test fixture " + i, 100 + i), i);
                list.ItemsSource = data; list.SelectedItems.Add(data.GetValue(0)); list.SelectedItems.Add(data.GetValue(2));
                CaptureInteraction(picker, Path.Combine(scratch, "process-multiselect.png"));
                Descendants<Button>(picker).Single(button => Equals(button.Content, "ВЫБРАТЬ")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            pickerTimer.Start();
            var picked = await (Task<IReadOnlyList<string>>)pickerType.GetMethod("ShowAsync")!.Invoke(null, new object[] { window })!;
            Check(extended && picked.SequenceEqual(new[] { "Selected0.exe", "Selected2.exe" }), "process picker supports Explorer-style multiple selection and returns all selected executables");
            var resultType = assembly.GetType("MorphocyteRouter.UpdateCheckResult")!;
            var assetType = assembly.GetType("MorphocyteRouter.UpdateAsset")!;
            var asset = Activator.CreateInstance(assetType, "MorphocyteOS-1.2.0-win-x64.zip", "https://github.com/sample-org/sample-app/releases/download/v1.2.0/MorphocyteOS-1.2.0-win-x64.zip", new string('a', 64), 1024L);
            var newer = Activator.CreateInstance(resultType, true, true, "Доступна версия 1.2.0.", "https://github.com/sample-org/sample-app/releases/tag/v1.2.0", "1.2.0", asset, "sample-org/sample-app");
            Method("SetAvailableUpdate").Invoke(window, new[] { newer });
            var download = (Button)Field("DownloadUpdateButton");
            Check(download.Visibility == Visibility.Visible && Equals(download.Content, "Загрузить актуальную версию: 1.2.0"), "new verified release displays exact versioned download button");
            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            var settingsUpdated = false;
            settingsTimer.Tick += async (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.Title == "НАСТРОЙКИ");
                if (dialog is null) return;
                settingsTimer.Stop();
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "Обновления")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(250); // Let the page's appearance animation finish before visual assertions.
                dialog.UpdateLayout();
                settingsUpdated = Descendants<Button>(dialog).Any(button => Equals(button.Content, "Загрузить актуальную версию: 1.2.0") && button.IsVisible)
                    && !Descendants<TextBlock>(dialog).Any(text => text.Text.Contains("предложит открыть страницу загрузки"));
                CaptureInteraction(dialog, Path.Combine(scratch, "settings-download-update.png"));
                Method("SetAvailableUpdate").Invoke(window, new object?[] { null });
                settingsUpdated &= Descendants<Button>(dialog).Where(button => button.Content?.ToString()?.StartsWith("Загрузить актуальную версию") == true).All(button => button.Visibility == Visibility.Collapsed);
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "VPN-профиль")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog.UpdateLayout();
                settingsUpdated &= Descendants<TextBlock>(dialog).Any(text => text.Text == "Установленное ядро:");
                dialog.Close();
            };
            settingsTimer.Start();
            Method("SettingsButton_Click").Invoke(window, new object[] { new Button(), new RoutedEventArgs() });
            Check(settingsUpdated, "settings share update state, hide unavailable download, use current auto-update explanation and renamed core heading");
            Check(download.Visibility == Visibility.Collapsed, "no available update hides main download button completely");
            Check(!Descendants<TextBlock>(window).Any(text => text.Text.StartsWith("Направляй сайты и приложения"))
                && !Descendants<Image>(window).Any(image => image.Width == 52), "removed subtitle and extra sidebar logo do not appear");
            await Task.Delay(250);
            CaptureInteraction(window, Path.Combine(scratch, "compact-main-window.png"));
            Method("SetAvailableUpdate").Invoke(window, new[] { newer });
            window.Width = 980; window.UpdateLayout();
            await Task.Delay(150);
            CaptureInteraction(window, Path.Combine(scratch, "compact-update-available.png"));
            Method("SetAvailableUpdate").Invoke(window, new object?[] { null });
            await (Task)Method("ApplyChangesAsync").Invoke(window, new object[] { false })!;
            Check(ClashConfigDocument.Load(config).Rules.Count == 3 && settings.GetDraft(config) is null, "only explicit Apply writes selected applications to YAML and clears draft");
            return count;
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            window.Close();
        }
    }
}
