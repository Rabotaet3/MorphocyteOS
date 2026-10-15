using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using MorphocyteRouter;

internal static class CoreConsoleChecks
{
    internal static async Task<int> RunAsync(string scratch, string? wpfApplication = null)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var passed = 0;
        void Check(bool result, string label) { if (!result) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        Check(WindowsConsoleProcess.QuoteArgument("a\\b\\") == "\"a\\b\\\\\""
            && WindowsConsoleProcess.QuoteArgument("a\" b") == "\"a\\\" b\"", "native launch quotes spaces, quotes and trailing backslashes");
        var home = Path.Combine(scratch, "core-console"); Directory.CreateDirectory(home);
        var name = "RouterRegressionTests";
        foreach (var file in new[] { name + ".dll", name + ".deps.json", name + ".runtimeconfig.json", "YamlDotNet.dll" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(home, file));
        var fake = Path.Combine(home, "sing-box.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, name + ".exe"), fake);
        var marker = Path.Combine(home, "closed.txt");
        ProcessStartInfo Fake(bool ignore)
        {
            var info = new ProcessStartInfo(fake) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home };
            info.ArgumentList.Add("--fake-core");
            info.Environment["MORPHOCYTE_FAKE_GRACEFUL_MARKER"] = marker;
            info.Environment["MORPHOCYTE_FAKE_IGNORE_INTERRUPT"] = ignore ? "1" : "0";
            return info;
        }
        var graceful = new CoreProcessManager();
        var logs = new ConcurrentQueue<string>(); graceful.Output += logs.Enqueue;
        try
        {
            await graceful.StartAsync(Fake(false));
            var pid = graceful.TrackedProcessId!.Value;
            using (var owned = Process.GetProcessById(pid))
            {
                var badSignal = new[] { "--signal-owned-core", pid.ToString(), "0", fake };
                Check(WindowsConsoleProcess.SignalOwnedCore(badSignal) != 0 && !owned.HasExited,
                    "stale PID identity cannot signal or stop a core");
            }
            Check(await graceful.StopAsync() && File.ReadAllText(marker) == "closed normally" && !graceful.HasTrackedProcess,
                "owned hidden-console core receives an interrupt and closes normally");
            Check(!logs.Any(line => line.Contains("принудительная", StringComparison.Ordinal)), "normal core close does not force-kill");
        }
        finally { await graceful.StopAsync(); }
        File.Delete(marker);
        logs.Clear();
        try
        {
            await graceful.StartAsync(Fake(true));
            Check(await graceful.StopAsync() && !File.Exists(marker) && !graceful.HasTrackedProcess,
                "unresponsive owned core is force-stopped after a bounded graceful timeout");
            Check(logs.Any(line => line.Contains("принудительная", StringComparison.Ordinal)), "forced-stop fallback explicitly reports cache risk");
        }
        finally { await graceful.StopAsync(); }

        if (wpfApplication is not null)
        {
            File.Delete(marker);
            using var native = WindowsConsoleProcess.Start(Fake(false));
            using var job = ProcessLifetimeJob.Attach(native.Process);
            native.Resume((_, _) => { });
            try
            {
                await Task.Delay(1400);
                var host = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                host.ArgumentList.Add(Path.GetFullPath(wpfApplication));
                host.ArgumentList.Add("--signal-owned-core");
                host.ArgumentList.Add(native.Process.Id.ToString());
                host.ArgumentList.Add(native.Process.StartTime.ToUniversalTime().Ticks.ToString());
                host.ArgumentList.Add(fake);
                using var helper = Process.Start(host)!;
                try
                {
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    await native.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    Check(helper.ExitCode == 0 && File.ReadAllText(marker) == "closed normally",
                        "actual WPF application's headless helper closes only the owned fake core without loading user settings");
                }
                finally { if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } }
            }
            finally
            {
                if (!native.Process.HasExited) { native.Process.Kill(); await native.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                native.Process.Dispose();
            }
        }

        // Actual pinned engine; only a loopback API and synthetic .invalid DNS names.
        // No TUN interface, system proxy, real server or external DNS is used.
        var engine = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Packaging", "Engine", "sing-box.exe"));
        Check(File.Exists(engine), "real sing-box fixture is available");
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var profile = Path.Combine(home, "fakeip.json");
        var secret = Guid.NewGuid().ToString("N");
        File.WriteAllText(profile, JsonSerializer.Serialize(new
        {
            log = new { level = "error" },
            dns = new { servers = new object[] { new { type = "fakeip", tag = "fake", inet4_range = "198.18.0.0/16", inet6_range = "fc00::/18" },
                new { type = "local", tag = "unused" } }, final = "unused",
                rules = new[] { new { query_type = new[] { "A", "AAAA" }, server = "fake" } } },
            outbounds = new[] { new { type = "direct", tag = "direct" } },
            route = new { final = "direct", default_domain_resolver = new { server = "unused" } },
            experimental = new { cache_file = new { enabled = true, store_fakeip = true, path = "fakeip-test.db" },
                clash_api = new { external_controller = "127.0.0.1:" + port, secret } }
        }));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        async Task<string> Address(string domain)
        {
            using var response = await http.GetAsync("/dns/query?name=" + domain + "&type=A");
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new Exception("FakeIP fixture DNS failed: " + body);
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("Answer")[0].GetProperty("data").GetString()!;
        }
        var real = new CoreProcessManager(); real.Output += line => Console.WriteLine("FIXTURE: " + line);
        try
        {
            await real.StartAsync(CoreProcessManager.CreateStartInfo(engine, profile, home));
            var original = await Address("video.fixture.invalid");
            Check(original.StartsWith("198.18.", StringComparison.Ordinal), "real engine allocates synthetic FakeIP without external network");
            Check(await real.StopAsync(), "real engine closes gracefully before delayed FakeIP checkpoint");
            await real.StartAsync(CoreProcessManager.CreateStartInfo(engine, profile, home));
            var next = await Address("another.fixture.invalid");
            Check(await Address("video.fixture.invalid") == original && next != original,
                "FakeIP mapping and allocator survive an immediate stop/restart");
            Check(await real.StopAsync(), "restarted real engine closes normally");
            await real.StartAsync(CoreProcessManager.CreateStartInfo(engine, profile, home));
            Check(await Address("video.fixture.invalid") == original && await Address("another.fixture.invalid") == next,
                "repeated graceful restart preserves browser-cached FakeIP addresses");
        }
        finally { await real.StopAsync(); }
        return passed;
    }
}
