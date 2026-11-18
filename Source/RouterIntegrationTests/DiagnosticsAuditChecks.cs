using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunDiagnosticsAuditChecks(string scratch, string fakeExe)
    {
        var count = 0;
        var failures = new List<string>();
        void Check(bool ok, string message)
        {
            count++;
            Console.WriteLine((ok ? "PASS: " : "FAIL: ") + message);
            if (!ok) failures.Add(message);
        }
        var profile = Path.Combine(scratch, "audit-profile.yaml");
        File.WriteAllText(profile, "proxies:\n - { name: VPN, type: socks5, server: 127.0.0.1, port: 9 }\nrules:\n - MATCH,DIRECT\n");
        var settings = AppSettings.Load(Path.Combine(scratch, "audit-settings.json"));
        settings.ConfigPath = profile;
        settings.CorePath = fakeExe;
        settings.AutoCheckUpdates = false;
        var window = new MainWindow(settings, Path.Combine(scratch, "audit.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ((DispatcherTimer)Field("_logRefreshTimer")).Stop();
            var core = (CoreProcessManager)Field("_core");
            var emit = (Action<string>)typeof(CoreProcessManager).GetField("Output", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(core)!;
            var queue = (ConcurrentQueue<string>)Field("_pendingCoreOutput");
            Parallel.For(0, 20_000, index => emit("fixture-core-line-" + index));
            Check(queue.Count <= 5000, "a concurrent burst of 20000 core log lines keeps the pending queue bounded");
            var oversized = new string('x', 100_000);
            emit(oversized);
            Check(queue.Last().Length <= 8192, "one oversized core log line cannot retain an unbounded UI buffer");
            typeof(MainWindow).GetMethod("FlushCoreOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var lines = (Queue<string>)Field("_logLines");
            Check(lines.Any(line => line.Contains("пропущено строк", StringComparison.Ordinal)), "log overflow is explicitly recorded in the journal");
            Check(!core.HasTrackedProcess, "load checks never start a VPN core");
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
        }

        const string connection = "{\"metadata\":{\"host\":\"fixture.example\",\"destinationPort\":443,\"network\":\"tcp\",\"process\":\"Example.exe\"},\"chains\":[\"Server\",\"VPN\"],\"rule\":\"Match\"}";
        var snapshot = "{\"connections\":[" + string.Join(',', Enumerable.Repeat(connection, 10_000)) + "]}";
        using var api = new CoreDiagnostics(new DiagnosticHandler(request => new(HttpStatusCode.OK) {
            Content = new StringContent(request.RequestUri!.AbsolutePath switch {
                "/connections" => snapshot, "/version" => "{\"version\":\"fixture\"}", _ => "{\"type\":\"Socks5\"}"
            })
        }));
        var timer = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var display = await api.ReadAsync("Server", default);
        Console.WriteLine($"MEASURE: a 10000-connection snapshot allocated {GC.GetTotalAllocatedBytes(true) - allocated} bytes in {timer.Elapsed.TotalMilliseconds:F1} ms.");
        Check(display.Connections.Count == 500 && (await api.ReadConnectionsAsync(default)).Count == 10_000,
            "the table retains its 500-row limit while manual diagnostics can inspect every connection");

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        using var stalled = new CoreDiagnostics(new DiagnosticHandler(_ => new(HttpStatusCode.OK) {
            Content = new StreamContent(new SyntheticSpeedStream(1, 20_000))
        }));
        try { await stalled.ReadVersionAsync(watchdog.Token); Check(false, "a stalled controller response times out after headers"); }
        catch (OperationCanceledException)
        { Check(!watchdog.IsCancellationRequested, "a stalled controller response times out after headers"); }
        if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
        return count;
    }
}
