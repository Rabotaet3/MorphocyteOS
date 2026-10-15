using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunUiReliabilityChecks(string scratch, string fakeExe)
    {
        var root = Path.Combine(scratch, "ui-reliability"); Directory.CreateDirectory(root);
        var profile = Path.Combine(root, "profile.yaml");
        const string yaml = "morphocyte-fake-api: true\nmode: rule\ntun:\n  enable: true\nproxies:\n - {name: Server, type: socks5, server: 127.0.0.1, port: 9999}\nproxy-groups:\n - {name: VPN, type: select, proxies: [Server]}\nrules:\n - DOMAIN,fixture.example,VPN\n - MATCH,DIRECT\n";
        File.WriteAllText(profile, yaml);
        var storage = Path.Combine(root, "settings.json");
        var settings = AppSettings.Load(storage);
        settings.ConfigPath = profile; settings.CorePath = Path.GetFullPath(fakeExe);
        settings.Theme = "Аметист"; settings.AutoCheckUpdates = false; settings.AutoReconnect = true;
        settings.Save();
        var window = new MainWindow(settings, Path.Combine(root, "test.log")) { ShowInTaskbar = false };
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        Exception? unexpectedNotice = null;
        var noticeGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        noticeGuard.Tick += (_, _) =>
        {
            // Only close notices owned by this synthetic fixture window, never
            // other applications or a real user's MorphocyteOS window.
            foreach (var notice in window.OwnedWindows.OfType<Window>().Where(dialog => dialog.IsVisible && dialog.Title == "MorphocyteOS").ToArray())
            {
                unexpectedNotice = new InvalidOperationException("UI fixture operation failed: " + ((TextBlock)Field("LastIssueText")!).Text);
                notice.Close();
            }
        };
        async Task Exclusive(Func<Task> action)
        {
            unexpectedNotice = null;
            await (Task)Call("RunExclusiveAsync", action)!;
            if (unexpectedNotice is { } error) throw error;
        }
        async Task Start() => await Exclusive(() => (Task)Call("StartCoreAsync", false)!);
        async Task Until(Func<bool> condition, int milliseconds = 12000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (!condition()) throw new Exception("UI reliability fixture timed out");
        }
        async Task<bool> Reject(Func<Task> operation)
        {
            try { await operation(); return false; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or YamlDotNet.Core.YamlException) { return true; }
        }
        var core = (CoreProcessManager)Field("_core")!;
        var passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            passed++; Console.WriteLine("PASS: " + description);
        }
        var startupChanges = new List<bool>();
        var registered = false;
        Set("_setStartupRegistration", (Action<bool>)(enabled => { startupChanges.Add(enabled); registered = enabled; }));
        SettingsDialogSelection Choice(string? path = null, string? engine = null, bool launch = true) =>
            new(path ?? profile, engine ?? settings.CorePath, "Океан", 1.25, true, LaunchAtSignIn: launch,
                AutoConnectOnStartup: false, AutoReconnect: false);
        try
        {
            noticeGuard.Start();
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations")!; await gate.WaitAsync(); gate.Release();
            await Start();
            Check(core.IsRunning, "UI reliability tests use only the owned loopback fake core, no TUN, real profile or startup task");
            var originalPid = core.TrackedProcessId;
            var badProfile = Path.Combine(root, "invalid.yaml"); File.WriteAllText(badProfile, "INVALID: [\n");
            Check(await Reject(() => (Task)Call("LoadConfigAsync", badProfile)!) && window.CanReconnect
                && core.IsRunning && core.TrackedProcessId == originalPid && settings.ConfigPath == profile,
                "failed YAML selection preserves the running core and its automatic-recovery intent");

            var diskBefore = File.ReadAllText(storage);
            Check(await Reject(() => (Task<SettingsDialogSelection>)Call("ApplySettingsSelectionCoreAsync", Choice(badProfile))!)
                && startupChanges.Count == 0 && settings.Theme == "Аметист" && settings.InterfaceScale == 1
                && File.ReadAllText(storage) == diskBefore && core.TrackedProcessId == originalPid && window.CanReconnect,
                "invalid profile is rejected before committing theme, scale, startup, settings or stopping the existing VPN");
            Check(await Reject(() => (Task<SettingsDialogSelection>)Call("ApplySettingsSelectionCoreAsync", Choice(engine: Path.Combine(root, "missing.exe")))!)
                && startupChanges.Count == 0 && File.ReadAllText(storage) == diskBefore && window.CanReconnect,
                "an attempted core change while connected does not partially save other preferences");

            var blockedStorage = Path.Combine(root, "blocked-storage"); Directory.CreateDirectory(blockedStorage);
            var storageField = typeof(AppSettings).GetField("_storagePath", flags)!;
            storageField.SetValue(settings, blockedStorage);
            try
            {
                Check(await Reject(() => (Task<SettingsDialogSelection>)Call("ApplySettingsSelectionCoreAsync", Choice())!)
                    && startupChanges.SequenceEqual(new[] { true, false }) && !registered
                    && settings.Theme == "Аметист" && settings.InterfaceScale == 1 && !settings.LaunchAtSignIn
                    && settings.AutoReconnect && !settings.AutoCheckUpdates && ThemeManager.CurrentTheme == "Аметист"
                    && UiScaleManager.CurrentScale == 1 && core.TrackedProcessId == originalPid && window.CanReconnect,
                    "settings write failure rolls back theme, scale, preferences and the mocked Windows startup task");
            }
            finally { storageField.SetValue(settings, storage); }
            Check(File.ReadAllText(storage) == diskBefore, "failed atomic settings save leaves the prior persisted settings unchanged");

            startupChanges.Clear();
            Set("_setStartupRegistration", (Action<bool>)(enabled =>
            {
                startupChanges.Add(enabled); registered = enabled;
                if (enabled) throw new IOException("Fixture task registration failed after changing the task");
            }));
            Check(await Reject(() => (Task<SettingsDialogSelection>)Call("ApplySettingsSelectionCoreAsync", Choice())!)
                && startupChanges.SequenceEqual(new[] { true, false }) && !registered && !settings.LaunchAtSignIn
                && settings.Theme == "Аметист" && File.ReadAllText(storage) == diskBefore,
                "partially failing startup registration is rolled back without touching the real Windows registry or scheduler");

            await window.ChangeRuleRouteAsync(((ObservableCollection<DomainRule>)Field("_rules")!)[0], "REJECT");
            diskBefore = File.ReadAllText(storage);
            var historyCount = ((System.Collections.ICollection)Field("_undoRules")!).Count;
            var otherProfile = Path.Combine(root, "other.yaml");
            File.WriteAllText(otherProfile, yaml.Replace("fixture.example", "other.example"));
            startupChanges.Clear();
            Check(await Reject(() => (Task<SettingsDialogSelection>)Call("ApplySettingsSelectionCoreAsync", Choice(otherProfile))!)
                && startupChanges.SequenceEqual(new[] { true, false }) && !registered
                && settings.ConfigPath == profile && core.IsRunning && core.TrackedProcessId != originalPid
                && ((ObservableCollection<DomainRule>)Field("_rules")!)[0].Route == "REJECT"
                && settings.GetDraft(profile)!.Rules[0].Route == "REJECT" && (bool)Field("_dirty")!
                && ((System.Collections.ICollection)Field("_undoRules")!).Count == historyCount && window.CanReconnect,
                "failure after a profile was loaded restores the original profile, running VPN, draft and undo history together");
            Check(File.ReadAllText(storage) == diskBefore && File.ReadAllText(profile) == yaml
                && File.ReadAllText(otherProfile) == yaml.Replace("fixture.example", "other.example"),
                "settings rollback restores persisted state without modifying either source YAML");
            originalPid = core.TrackedProcessId;
            Set("_setStartupRegistration", (Action<bool>)(enabled => { startupChanges.Add(enabled); registered = enabled; }));

            // A busy operation must defer, not discard, its child's unexpected exit.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var busyOperation = Exclusive(async () => await release.Task);
            using (var process = Process.GetProcessById(originalPid!.Value))
            {
                process.Kill(); await process.WaitForExitAsync();
            }
            await Until(() => (bool)Field("_pendingUnexpectedCoreExit")!);
            Check((bool)Field("_busy")! && !core.IsRunning && Field("_recoveryLifetime") is null,
                "an unexpected exit during a busy UI operation is retained and does not overlap that operation");
            release.SetResult(); await busyOperation;
            await Until(() => core.IsRunning && core.TrackedProcessId != originalPid && !(bool)Field("_busy")!);
            Check(window.CanReconnect && ((Queue<DateTime>)Field("_unexpectedCoreExits")!).Count == 1,
                "releasing the busy gate reconciles the dead core and restores the requested VPN exactly once");

            var recoveredPid = core.TrackedProcessId;
            await Exclusive(async () => { await (Task<bool>)Call("StopCoreAsync")!; await (Task)Call("StartCoreAsync", false)!; });
            await Task.Delay(3500);
            Check(core.IsRunning && core.TrackedProcessId != recoveredPid && Field("_recoveryLifetime") is null
                && ((Queue<DateTime>)Field("_unexpectedCoreExits")!).Count == 1,
                "intentional stop/restart remains distinct from a crash even when its UI callback is delayed until gate release");

            var notifications = 0;
            const string privateMessage = "https://private.example/secret-subscription-token";
            // The stage catches its first progress failure and tries to report it;
            // fail both progress notifications, but leave initial/final updates safe.
            Action failingProgress = () => { if (++notifications is 2 or 3) throw new InvalidOperationException(privateMessage); };
            window.DiagnosticChanged += failingProgress;
            try { await window.RunConnectionDiagnosticAsync(); }
            finally { window.DiagnosticChanged -= failingProgress; }
            var messages = (Queue<string>)Field("_logLines")!;
            Check(window.DiagnosticStatus.Contains("Подробности — в журнале")
                && messages.Any(message => message.Contains("Диагностика подключения: проверка не завершена (InvalidOperationException)"))
                && messages.All(message => !message.Contains(privateMessage)) && !window.DiagnosticRunning,
                "a failed diagnostic records a safe exception classification without exposing private exception payloads");
            var initialNotification = true;
            Action failingInitial = () =>
            {
                if (initialNotification) { initialNotification = false; throw new InvalidOperationException(privateMessage); }
            };
            window.DiagnosticChanged += failingInitial;
            try { await window.RunConnectionDiagnosticAsync(); }
            finally { window.DiagnosticChanged -= failingInitial; }
            Check(!window.DiagnosticRunning && Field("_manualDiagnosticLifetime") is null
                && window.DiagnosticStatus.Contains("Подробности — в журнале"),
                "an initial diagnostic-window update failure still releases running state and its cancellation lifetime");
            Action failingFinal = () => { if (!window.DiagnosticRunning) throw new InvalidOperationException(privateMessage); };
            window.DiagnosticChanged += failingFinal;
            try { await window.RunConnectionDiagnosticAsync(); }
            finally { window.DiagnosticChanged -= failingFinal; }
            Check(!window.DiagnosticRunning && Field("_manualDiagnosticLifetime") is null
                && messages.Any(message => message.Contains("не удалось обновить окно (InvalidOperationException)"))
                && messages.All(message => !message.Contains(privateMessage)),
                "a final diagnostic-window update failure is safely logged instead of escaping its async handler");
            await window.RunConnectionDiagnosticAsync();
            Check(!window.DiagnosticRunning && window.DiagnosticSteps.Count > 0 && window.DiagnosticStatus.Contains("завершена"),
                "the diagnostic can be repeated after its failed progress callback");

            var successful = await (Task<SettingsDialogSelection>)Call("ApplySettingsSelectionCoreAsync", Choice(launch: false))!;
            var persisted = AppSettings.Load(storage);
            Check(successful.Theme == "Океан" && persisted.Theme == "Океан" && persisted.InterfaceScale == 1.25
                && ThemeManager.CurrentTheme == "Океан" && UiScaleManager.CurrentScale == 1.25
                && !persisted.AutoReconnect && !registered && core.IsRunning,
                "successful settings commit keeps persisted preferences, displayed theme and mocked startup state consistent");

            Call("CancelConnectionIntent");
            await Exclusive(async () => { await (Task<bool>)Call("StopCoreAsync")!; });
            await Task.Delay(3000);
            Check(!core.IsRunning && Field("_recoveryLifetime") is null, "manual stop never resurrects the fake core after deferred crash recovery");
        }
        finally
        {
            noticeGuard.Stop();
            Call("CancelConnectionIntent"); Set("_closing", true);
            await core.StopAsync(); Set("_allowClose", true); window.Close();
            UiScaleManager.CurrentScale = 1; ThemeManager.Apply(null);
        }
        return passed;
    }
}
