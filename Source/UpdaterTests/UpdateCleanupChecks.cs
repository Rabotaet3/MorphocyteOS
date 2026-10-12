using System.Diagnostics;
using System.Text.Json;
using MorphocyteRouter;

internal static class UpdateCleanupChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string scratch)
    {
        var tests = Path.Combine(scratch, "cleanup");
        Directory.CreateDirectory(tests);
        string Operation(string root, bool? success)
        {
            var operation = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(operation, "payload", "Licenses"));
            Directory.CreateDirectory(Path.Combine(operation, "rollback"));
            File.WriteAllText(Path.Combine(operation, "package.zip"), "downloaded archive");
            File.WriteAllText(Path.Combine(operation, "installer.exe"), "helper fixture");
            File.WriteAllText(Path.Combine(operation, "payload", "Licenses", "license.txt"), "license");
            File.WriteAllText(Path.Combine(operation, "rollback", "MorphocyteOS.exe"), "old app");
            if (success is not null) File.WriteAllText(Path.Combine(operation, "result.json"), JsonSerializer.Serialize(new UpdateOutcome(success.Value, "fixture")));
            return operation;
        }
        async Task Sweep(string root, params string[] protectedFiles) =>
            await UpdateCacheCleanup.CleanCompletedAsync(root, protectedFiles, timeout: TimeSpan.FromMilliseconds(250));
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            { check(true, message); return; }
            throw new Exception("FAIL (accepted): " + message);
        }
        var successRoot = Path.Combine(tests, "successful", ".update-cache");
        var successful = Operation(successRoot, true);
        await Sweep(successRoot);
        check(!Directory.Exists(successful) && !Directory.Exists(successRoot), "successful update removes archive, payload, rollback and empty cache root");

        var retainedRoot = Path.Combine(tests, "retained", ".update-cache");
        var failed = Operation(retainedRoot, false);
        var incomplete = Operation(retainedRoot, null);
        var old = Operation(retainedRoot, true);
        Directory.CreateDirectory(Path.Combine(retainedRoot, "unrelated"));
        await Sweep(retainedRoot);
        check(!Directory.Exists(old) && File.Exists(Path.Combine(failed, "rollback", "MorphocyteOS.exe")) &&
            File.Exists(Path.Combine(incomplete, "package.zip")) && Directory.Exists(Path.Combine(retainedRoot, "unrelated")),
            "cleanup retains failed rollback, unfinished downloads and non-operation directories");
        Reject(() => UpdateCacheCleanup.DeleteCompletedOperation(retainedRoot, failed), "direct cleanup rejects a failed update");
        Reject(() => UpdateCacheCleanup.DeleteCompletedOperation(retainedRoot, tests), "cleanup cannot target an ancestor or installation directory");

        var protectedRoot = Path.Combine(tests, "protected", ".update-cache");
        var protectedOperation = Operation(protectedRoot, true);
        var selectedCore = Path.Combine(protectedOperation, "payload", "Licenses", "license.txt");
        await Sweep(protectedRoot, selectedCore);
        check(File.Exists(selectedCore) && File.Exists(Path.Combine(protectedOperation, "package.zip")), "a selected profile or core inside a completed operation is never removed");

        var lockedRoot = Path.Combine(tests, "locked", ".update-cache");
        var locked = Operation(lockedRoot, true);
        using (var held = new FileStream(Path.Combine(locked, "installer.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var timer = Stopwatch.StartNew();
            await Sweep(lockedRoot);
            check(timer.Elapsed < TimeSpan.FromSeconds(3) && File.Exists(Path.Combine(locked, "result.json")) &&
                File.Exists(Path.Combine(locked, "package.zip")), "locked helper cleanup is bounded and preserves the success marker and payload");
        }
        await Sweep(lockedRoot);
        check(!Directory.Exists(lockedRoot), "a later sweep removes a formerly locked successful update and cache root");

        var partialRoot = Path.Combine(tests, "partial", ".update-cache");
        var partial = Operation(partialRoot, true);
        using (var held = new FileStream(Path.Combine(partial, "rollback", "MorphocyteOS.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Sweep(partialRoot);
            check(File.Exists(Path.Combine(partial, "result.json")) && File.Exists(Path.Combine(partial, "rollback", "MorphocyteOS.exe")),
                "partial cleanup never erases the success marker before the last occupied recovery file");
        }
        await Sweep(partialRoot);
        check(!Directory.Exists(partialRoot), "partially cleaned operations remain recoverable for the next cleanup attempt");

        var invalidRoot = Path.Combine(tests, "invalid", ".update-cache");
        var oversized = Operation(invalidRoot, true);
        File.WriteAllText(Path.Combine(oversized, "result.json"), new string(' ', 32769));
        var malformed = Operation(invalidRoot, true);
        File.WriteAllText(Path.Combine(malformed, "result.json"), "not JSON");
        await Sweep(invalidRoot);
        check(File.Exists(Path.Combine(oversized, "package.zip")) && File.Exists(Path.Combine(malformed, "package.zip")),
            "oversized and malformed results cannot authorize deletion");

        var reusedRoot = Path.Combine(tests, "reused", ".update-cache");
        var reused = Operation(reusedRoot, true);
        using (var process = Process.GetCurrentProcess())
            File.WriteAllText(Path.Combine(reused, "result.json"), JsonSerializer.Serialize(new UpdateOutcome(true, "fixture")
                { HelperProcessId = process.Id, HelperStartTicks = process.StartTime.ToUniversalTime().Ticks - 1 }));
        await Sweep(reusedRoot);
        check(!Directory.Exists(reusedRoot), "a reused helper PID never waits for or interferes with an unrelated process");

        var mismatchedRoot = Path.Combine(tests, "mismatched", ".update-cache");
        var mismatched = Operation(mismatchedRoot, true);
        using (var process = Process.GetCurrentProcess())
            File.WriteAllText(Path.Combine(mismatched, "result.json"), JsonSerializer.Serialize(new UpdateOutcome(true, "fixture")
                { HelperProcessId = process.Id, HelperStartTicks = process.StartTime.ToUniversalTime().Ticks }));
        await Sweep(mismatchedRoot);
        check(File.Exists(Path.Combine(mismatched, "package.zip")), "matching PID metadata pointing at a different executable cannot authorize cleanup");

        var helperRoot = Path.Combine(tests, "live-helper", ".update-cache");
        var helperOperation = Operation(helperRoot, true);
        var executable = Environment.ProcessPath!;
        if (!Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(executable, Path.Combine(helperOperation, "installer.exe"), true);
            foreach (var sidecar in Directory.EnumerateFiles(AppContext.BaseDirectory, "UpdaterTests.*")
                .Where(file => !file.EndsWith(".exe") && !file.EndsWith(".pdb")))
                File.Copy(sidecar, Path.Combine(helperOperation, Path.GetFileName(sidecar)));
            var ready = Path.Combine(tests, "helper-ready.txt");
            var launch = new ProcessStartInfo(Path.Combine(helperOperation, "installer.exe"))
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = helperOperation };
            launch.ArgumentList.Add("--fixture-parent"); launch.ArgumentList.Add(ready);
            using var helper = Process.Start(launch)!;
            try
            {
                var timer = Stopwatch.StartNew();
                while (!File.Exists(ready) && !helper.HasExited && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
                if (!File.Exists(ready) || helper.HasExited) throw new Exception("Unable to launch isolated cleanup helper fixture.");
                File.WriteAllText(Path.Combine(helperOperation, "result.json"), JsonSerializer.Serialize(new UpdateOutcome(true, "fixture")
                    { HelperProcessId = helper.Id, HelperStartTicks = helper.StartTime.ToUniversalTime().Ticks }));
                await Sweep(helperRoot);
                check(!helper.HasExited && File.Exists(Path.Combine(helperOperation, "package.zip")),
                    "cleanup waits for the identified live installer without killing it or removing its files");
                File.WriteAllText(ready + ".exit", "exit");
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await Sweep(helperRoot);
                check(!Directory.Exists(helperRoot), "cleanup removes every successful operation file immediately after the identified helper exits");
            }
            finally
            {
                File.WriteAllText(ready + ".exit", "exit");
                if (!helper.HasExited) await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // Junction creation is not privileged on Windows. The target is our own
        // temporary fixture, and the cleaner must reject it before deleting anything.
        if (OperatingSystem.IsWindows())
        {
            var linkRoot = Path.Combine(tests, "junction", ".update-cache");
            var linked = Operation(linkRoot, true);
            var target = Path.Combine(tests, "junction-target"); Directory.CreateDirectory(target);
            var outsideFile = Path.Combine(target, "keep.txt"); File.WriteAllText(outsideFile, "outside operation");
            var junction = Path.Combine(linked, "outside");
            var create = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            create.ArgumentList.Add("/d"); create.ArgumentList.Add("/c"); create.ArgumentList.Add("mklink"); create.ArgumentList.Add("/J");
            create.ArgumentList.Add(junction); create.ArgumentList.Add(target);
            using var command = Process.Start(create)!;
            await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (command.ExitCode != 0) throw new Exception("Unable to create isolated junction fixture: " + await command.StandardError.ReadToEndAsync());
            try
            {
                await Sweep(linkRoot);
                check(File.Exists(outsideFile) && File.Exists(Path.Combine(linked, "package.zip")) && File.Exists(Path.Combine(linked, "installer.exe")),
                    "a junction anywhere in a successful cache blocks all deletion and preserves the external target");
            }
            finally { Directory.Delete(junction, false); }
        }
    }
}
