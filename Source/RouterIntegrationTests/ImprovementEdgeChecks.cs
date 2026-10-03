using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunImprovementEdgeChecks(string scratch, string fakeExe)
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        const string yaml = "proxies:\n  - {name: Alpha, type: socks5, server: 192.0.2.20, port: 1080}\n  - {name: Beta, type: socks5, server: 192.0.2.21, port: 1080}\nproxy-groups:\n  - {name: VPN, type: select, proxies: [Alpha, Beta, DIRECT]}\ntun:\n  enable: true\nrules:\n  - DOMAIN-SUFFIX,keep.example,VPN\n  - MATCH,DIRECT\n";
        const string fresh = "proxies:\n  - {name: Beta, type: socks5, server: 192.0.2.22, port: 1080}\n  - {name: Gamma, type: socks5, server: 192.0.2.23, port: 1080}\n";
        var path = Path.Combine(scratch, "edge-profile.yaml"); File.WriteAllText(path, yaml);
        var settingsPath = Path.Combine(scratch, "edge-settings.json");
        var settings = AppSettings.Load(settingsPath);
        Check(settings.AutoReconnect, "connection recovery is enabled by default for existing and new settings");
        settings.ConfigPath = path; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false;
        settings.ProfileSubscriptions[path] = SubscriptionSource.Create("https://subscription.example/edge-token");
        settings.ProfileSubscriptions[path].SelectedServer = "Beta"; settings.Save();
        var fallback = SubscriptionRefresh.Merge(path, yaml, SubscriptionImporter.ParseContent(fresh), "");
        Check(fallback.SelectionRemoved && fallback.SelectedServer == "Beta", "offline refresh detects a removed default server and chooses a VPN server ahead of DIRECT");
        try { new SubscriptionSource { ProtectedUrl = "damaged-secret" }.ReadUrl(); throw new Exception("Corrupt source accepted"); }
        catch (InvalidDataException error) { Check(!error.Message.Contains("damaged-secret"), "damaged subscription metadata reports a generic error without exposing its contents"); }
        var window = new MainWindow(settings, Path.Combine(scratch, "edge.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void SetField(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        Task Call(string name, params object[] values) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, values)!;
        ObservableCollection<DomainRule> Rules() => (ObservableCollection<DomainRule>)Field("_rules");
        var core = (CoreProcessManager)Field("_core");
        DispatcherTimer CloseDialog(string title, string? button = null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(item => item != window && item.Title == title);
                if (dialog is null) return;
                timer.Stop();
                if (button is null) dialog.Close();
                else Descendants<Button>(dialog).Single(item => Equals(item.Content, button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start(); return timer;
        }
        try
        {
            window.Show(); var gate = (SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release();
            await window.ChangeRuleRouteAsync(Rules()[0], "REJECT");
            var historyCount = ((ICollection)Field("_undoRules")).Count;
            typeof(AppSettings).GetField("_storagePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, scratch);
            var notice = CloseDialog("MorphocyteOS");
            try { await window.UndoRuleEditAsync(); }
            finally { notice.Stop(); typeof(AppSettings).GetField("_storagePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, settingsPath); }
            Check(Rules()[0].Route == "REJECT" && settings.GetDraft(path)!.Rules[0].Route == "REJECT"
                && ((ICollection)Field("_undoRules")).Count == historyCount && ((ICollection)Field("_redoRules")).Count == 0,
                "failed undo persistence rolls back editor, draft and history cursors together");
            await window.UndoRuleEditAsync();
            Check(Rules()[0].Route == "VPN", "undo remains usable after a failed settings write");
            for (var i = 0; i < 55; i++) await window.ChangeRuleRouteAsync(Rules()[0], i % 2 == 0 ? "REJECT" : "DIRECT");
            Check(((ICollection)Field("_undoRules")).Count == 50, "rule history remains bounded after a long editing session");
            await Call("ApplyChangesAsync", false);
            await window.ChangeRuleRouteAsync(Rules()[0], "VPN");
            var savedBefore = File.ReadAllText(path); var draftBefore = settings.GetDraft(path)!.Rules[0].Route;
            await Call("StartCoreAsync", false);
            using var good = new HttpClient(new DiagnosticHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(fresh, Encoding.UTF8) }));
            Check(await window.RefreshSubscriptionAsync(default, good) && core.IsRunning,
                "refreshing an active subscription restarts the owned core after validation");
            Check(settings.GetDraft(path)!.Rules[0].Route == draftBefore && Rules()[0].Route == draftBefore
                && File.ReadAllText(path).Contains("keep.example,REJECT"),
                "active subscription refresh does not apply pending routing edits");
            var stableYaml = File.ReadAllText(path); var stableDraft = settings.GetDraft(path)!.SourceHash;
            using var failing = new HttpClient(new DiagnosticHandler(_ => new(HttpStatusCode.OK)
                { Content = new StringContent(fresh.Replace("192.0.2.22", "startup-fail.example"), Encoding.UTF8) }));
            var failed = false;
            try { await Call("RefreshSubscriptionCoreAsync", CancellationToken.None, failing); }
            catch (IOException) { failed = true; }
            Check(failed && core.IsRunning && File.ReadAllText(path) == stableYaml && settings.GetDraft(path)!.SourceHash == stableDraft,
                "failed subscription restart restores the old profile, owned core and unchanged draft");
            using var cancelled = new CancellationTokenSource();
            Action cancelOnStop = () => cancelled.Cancel(); core.Exited += cancelOnStop;
            try { Check(!await window.RefreshSubscriptionAsync(cancelled.Token, good), "subscription cancellation after stopping the core is reported as cancelled"); }
            finally { core.Exited -= cancelOnStop; }
            Check(core.IsRunning && File.ReadAllText(path) == stableYaml && settings.GetDraft(path)!.SourceHash == stableDraft,
                "cancelling refresh after stop restarts the prior VPN without committing new servers");
            var blockedSettings = Path.Combine(scratch, "blocked-settings"); Directory.CreateDirectory(blockedSettings);
            typeof(AppSettings).GetField("_storagePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, blockedSettings);
            failed = false;
            try { await Call("RefreshSubscriptionCoreAsync", CancellationToken.None, good); }
            catch (IOException) { failed = true; }
            finally { typeof(AppSettings).GetField("_storagePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, settingsPath); }
            Check(failed && core.IsRunning && File.ReadAllText(path) == stableYaml && settings.GetDraft(path)!.SourceHash == stableDraft,
                "settings write failure during refresh still restores the previous VPN and draft");
            var pid = core.TrackedProcessId;
            var outcome = await (Task<RecoveryAttemptResult>)typeof(MainWindow).GetMethod("TryRestoreConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { CancellationToken.None })!;
            Check(outcome == RecoveryAttemptResult.Retry && core.IsRunning && core.TrackedProcessId != pid,
                "network recovery restarts its unresponsive owned core instead of mistaking it for a foreign process");
            Check(File.ReadAllText(path) == stableYaml && settings.GetDraft(path)!.SourceHash == stableDraft && Rules()[0].Route == draftBefore,
                "actual recovery preserves saved YAML and unapplied rules");
            var foreign = new CoreProcessManager();
            try
            {
                await foreign.StartAsync(CoreProcessManager.CreateStartInfo(fakeExe, path, scratch));
                var ownedPid = core.TrackedProcessId; var foreignPid = foreign.TrackedProcessId;
                var stopped = await (Task<RecoveryAttemptResult>)typeof(MainWindow).GetMethod("TryRestoreConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object[] { CancellationToken.None })!;
                Check(stopped == RecoveryAttemptResult.Stop && core.TrackedProcessId == ownedPid && foreign.IsRunning && foreign.TrackedProcessId == foreignPid,
                    "background recovery leaves both cores untouched when another application's core is running");
            }
            finally { await foreign.StopAsync(); }
            var previousDiagnostics = (CoreDiagnostics)Field("_diagnostics");
            using var healthy = new CoreDiagnostics(new DiagnosticHandler(request => new(HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/delay") ? "{\"delay\":32}" : "{\"now\":\"\"}") }));
            SetField("_diagnostics", healthy);
            try
            {
                var ownedPid = core.TrackedProcessId;
                var verified = await (Task<RecoveryAttemptResult>)typeof(MainWindow).GetMethod("TryRestoreConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object[] { CancellationToken.None })!;
                Check(verified == RecoveryAttemptResult.Connected && core.TrackedProcessId == ownedPid,
                    "actual recovery checks a healthy VPN without restarting its process");
            }
            finally { SetField("_diagnostics", previousDiagnostics); }
            await Call("StopCoreAsync");
            using (var pendingRecovery = new CancellationTokenSource())
            {
                SetField("_recoveryLifetime", pendingRecovery);
                typeof(MainWindow).GetMethod("UpdateControls", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Check(Equals(((Button)Field("PowerButton")).Content, "ОТМЕНИТЬ ВОССТАНОВЛЕНИЕ"),
                    "a stopped core with pending recovery displays a cancellation action rather than Start VPN");
                typeof(MainWindow).GetMethod("CancelConnectionIntent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Check(pendingRecovery.IsCancellationRequested && !window.CanReconnect,
                    "manual stop cancels the pending recovery token as well as connection intent");
                SetField("_recoveryLifetime", null!);
            }
            typeof(MainWindow).GetMethod("SelectMainPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { true });
            await window.RunConnectionDiagnosticAsync();
            foreach (var scale in new[] { 1d, 1.5d })
            {
                window.ApplyInterfaceScale(scale); window.Width = window.MinWidth; window.Height = window.MinHeight;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                var copy = (Button)Field("CopyDiagnosticButton"); var page = (FrameworkElement)Field("JournalPage");
                var bounds = copy.TransformToAncestor(page).TransformBounds(new Rect(copy.RenderSize));
                Check(bounds.Right <= page.ActualWidth && bounds.Bottom <= page.ActualHeight && copy.IsVisible,
                    $"journal diagnostic actions fit inside the minimum window at {scale * 100:0}% scale");
                ((ScrollViewer)Field("SidebarScrollViewer")).ScrollToBottom(); window.UpdateLayout();
                CaptureInteraction(window, Path.Combine(scratch, $"improvements-journal-{scale * 100:0}.png"));
            }
            window.ApplyInterfaceScale(1);
            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            settingsTimer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(item => item.Title == "НАСТРОЙКИ");
                if (dialog is null) return; settingsTimer.Stop();
                var option = Descendants<CheckBox>(dialog).Single(item => Descendants<TextBlock>(item).Any(label => label.Text.StartsWith("Восстанавливать VPN")));
                Check(option.IsChecked == true, "settings display the default automatic-recovery preference");
                foreach (var scale in new[] { 1d, 1.5d })
                {
                    UiScaleManager.Apply(dialog, (FrameworkElement)dialog.Content, scale);
                    Descendants<Button>(dialog).Single(item => Equals(item.Content, "VPN-профиль")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialog.UpdateLayout();
                    Descendants<TextBlock>(dialog).Single(item => item.Text == "ПОДПИСКА").BringIntoView(); dialog.UpdateLayout();
                    CaptureInteraction(dialog, Path.Combine(scratch, $"improvements-subscription-settings-{scale * 100:0}.png"));
                    Descendants<ScrollViewer>(dialog).Single(item => item.Name == "SettingsScroll").ScrollToBottom();
                    dialog.UpdateLayout(); CaptureInteraction(dialog, Path.Combine(scratch, $"improvements-recovery-settings-{scale * 100:0}.png"));
                }
                dialog.Close();
            };
            settingsTimer.Start();
            typeof(MainWindow).GetMethod("SettingsButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { new Button(), new RoutedEventArgs() });
            settingsTimer.Stop();
        }
        finally { await core.StopAsync(); SetField("_allowClose", true); window.Close(); }
        using var negative = new CoreDiagnostics(new DiagnosticHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"delay\":-1}") }));
        try { await negative.CheckProxyAsync("VPN", default); throw new Exception("Negative delay accepted"); }
        catch (InvalidDataException) { Check(true, "core probe refuses an invalid negative delay"); }
        var requested = true;
        Check(!await ConnectionRecovery.RunAsync(_ => { requested = false; return Task.FromResult(RecoveryAttemptResult.Connected); }, () => requested, default),
            "a connection request cleared during a probe is not reported as recovered");
        return count;
    }
}
