using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunRecoveryReliabilityChecks(string scratch, string fakeExe)
    {
        var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        var path = Path.Combine(scratch, "recovery-profile.yaml");
        const string yaml = "morphocyte-fake-api: true\nproxies:\n - {name: Server, type: socks5, server: 127.0.0.1, port: 9999}\nproxy-groups:\n - {name: VPN, type: select, proxies: [Server]}\nrules:\n - DOMAIN,fixture.example,VPN\n - MATCH,DIRECT\n";
        File.WriteAllText(path, yaml);
        var settings = AppSettings.Load(Path.Combine(scratch, "recovery-settings.json"));
        settings.ConfigPath = path; settings.CorePath = Path.GetFullPath(fakeExe);
        settings.AutoCheckUpdates = false; settings.AutoReconnect = true; settings.FullTunnel = true;
        var window = new MainWindow(settings, Path.Combine(scratch, "recovery.log")) { ShowInTaskbar = false };
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        var core = (CoreProcessManager)Field("_core")!;
        async Task Exclusive(Func<Task> action) => await (Task)Call("RunExclusiveAsync", action)!;
        async Task Until(Func<bool> condition, int milliseconds = 9000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!condition()) throw new Exception("Recovery fixture timed out");
        }
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations")!; await gate.WaitAsync(); gate.Release();
            await Exclusive(() => (Task)Call("StartCoreAsync", false)!);
            Check(core.IsRunning, "recovery fixture starts only a loopback fake core, without TUN or external traffic");
            await window.ChangeRuleRouteAsync(((System.Collections.ObjectModel.ObservableCollection<DomainRule>)Field("_rules")!)[0], "REJECT");
            var oldPid = core.TrackedProcessId;
            var probes = 0;
            Set("_recoverySystemProbe", (Func<CancellationToken, Task>)(_ => ++probes == 1
                ? Task.FromException(new IOException("Fixture OS path failure")) : Task.CompletedTask));
            var result = await (Task<RecoveryAttemptResult>)Call("TryRestoreConnectionAsync", CancellationToken.None)!;
            Check(result == RecoveryAttemptResult.Connected && probes == 2 && core.TrackedProcessId != oldPid,
                "healthy proxy but broken OS path restarts the owned core and verifies the repaired TUN path");
            Check(File.ReadAllText(path) == yaml && settings.GetDraft(path)?.Rules[0].Route == "REJECT" && (bool)Field("_dirty")!,
                "network recovery leaves source YAML and unapplied rule edits untouched");
            oldPid = core.TrackedProcessId;
            result = await (Task<RecoveryAttemptResult>)Call("TryRestoreConnectionAsync", CancellationToken.None)!;
            Check(result == RecoveryAttemptResult.Connected && core.TrackedProcessId == oldPid && probes == 3,
                "healthy proxy and OS paths do not trigger an unnecessary restart");
            settings.FullTunnel = false;
            result = await (Task<RecoveryAttemptResult>)Call("TryRestoreConnectionAsync", CancellationToken.None)!;
            Check(result == RecoveryAttemptResult.Connected && probes == 3, "selective routing recovery does not probe an unrelated system path");
            settings.FullTunnel = true;

            Set("_physicalNetworkStamp", "fixture-old");
            Set("_ignoreRecoveryNetworkUntil", DateTime.UtcNow.AddSeconds(15));
            Call("ScheduleNetworkRecovery", "fixture-new");
            var deferred = Field("_deferredNetworkLifetime");
            Check(deferred is CancellationTokenSource && Field("_recoveryLifetime") is null,
                "real network changes during the startup guard are deferred, not discarded");
            Call("ScheduleNetworkRecovery", "fixture-new");
            Check(ReferenceEquals(deferred, Field("_deferredNetworkLifetime")), "duplicate adapter notifications do not cancel or multiply recovery");
            Call("CancelConnectionIntent");
            await Until(() => Field("_deferredNetworkLifetime") is null);
            Check(Field("_recoveryLifetime") is null && !window.CanReconnect, "manual stop cancels deferred reconnection intent");

            Set("_connectionRequested", true);
            oldPid = core.TrackedProcessId;
            await core.StopAsync(); // Simulated unexpected exit of this fixture's own child.
            await Until(() => core.IsRunning && core.TrackedProcessId != oldPid && !(bool)Field("_busy")!);
            Check(window.CanReconnect && probes >= 4, "unexpected exit automatically restores and verifies the previously requested connection");
            Call("CancelConnectionIntent");
            await Exclusive(async () => { await (Task<bool>)Call("StopCoreAsync")!; });
            await Task.Delay(3000);
            Check(!core.IsRunning && Field("_recoveryLifetime") is null, "manual VPN stop never resurrects the core");
            Set("_connectionRequested", true);
            for (var attempt = 0; attempt < 4; attempt++) Call("RecoverUnexpectedCoreExit");
            Check(!window.CanReconnect, "repeated core crashes stop automatic recovery instead of creating an endless restart loop");
        }
        finally
        {
            Call("CancelConnectionIntent"); Set("_closing", true);
            await core.StopAsync(); Set("_allowClose", true); window.Close();
        }
        return passed;
    }
}
