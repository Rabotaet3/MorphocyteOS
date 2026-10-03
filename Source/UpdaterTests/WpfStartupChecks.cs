using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MorphocyteRouter;

internal static class WpfStartupChecks
{
    internal static async Task<int> RunAsync(string application, string dotnet)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "MorphocyteOS-wpf-updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        try
        {
            foreach (var scenario in new[] { "success", "locked", "damaged", "wrong-parent" })
            {
                var installed = Path.Combine(scratch, scenario, "Морфоцит-ВПН-Обход с пробелами");
                var operation = Path.Combine(scratch, scenario, Guid.NewGuid().ToString("N"));
                var payload = Path.Combine(operation, "payload");
                Directory.CreateDirectory(installed); Directory.CreateDirectory(payload);
                var worker = Environment.ProcessPath!;
                if (Path.GetFileNameWithoutExtension(worker).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new Exception("Run through the test apphost.");
                var original = new Dictionary<string, byte[]> { ["MorphocyteOS.exe"] = File.ReadAllBytes(worker), ["mihomo.exe"] = Encoding.UTF8.GetBytes("old core"), ["README.md"] = Encoding.UTF8.GetBytes("old readme") };
                var incoming = original.ToDictionary(pair => pair.Key, pair => pair.Value);
                incoming["mihomo.exe"] = Encoding.UTF8.GetBytes("new core"); incoming["README.md"] = Encoding.UTF8.GetBytes("new readme");
                void Stage(string root, string version, Dictionary<string, byte[]> files)
                {
                    foreach (var file in files) File.WriteAllBytes(Path.Combine(root, file.Key), file.Value);
                    var manifest = new ReleaseManifest { Version = version, Files = files.Select(file => new ReleaseFile
                        { Path = file.Key, Size = file.Value.Length, Sha256 = UpdatePackage.Hash(Path.Combine(root, file.Key)) }).ToList() };
                    File.WriteAllText(Path.Combine(root, UpdatePackage.ManifestName), JsonSerializer.Serialize(manifest));
                }
                Stage(installed, "1.0.0", original); Stage(payload, "1.1.0", incoming);
                File.WriteAllText(Path.Combine(installed, "private.yaml"), "fixture personal configuration");
                foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "UpdaterTests.*").Where(file => !file.EndsWith(".exe") && !file.EndsWith(".pdb")))
                    File.Copy(file, Path.Combine(installed, Path.GetFileName(file)));
                var bundled = Path.GetExtension(application).Equals(".exe", StringComparison.OrdinalIgnoreCase);
                if (bundled) File.Copy(application, Path.Combine(operation, "installer.exe"));
                else
                    foreach (var file in Directory.GetFiles(Path.GetDirectoryName(application)!).Where(file => Path.GetExtension(file) is ".dll" or ".json"))
                        File.Copy(file, Path.Combine(operation, Path.GetFileName(file)));
                // A regressed StartupUri must never load the user's actual settings or VPN.
                File.WriteAllText(Path.Combine(operation, "portable.flag"), "");
                var readyParent = Path.Combine(installed, "parent-ready.txt");
                var parentInfo = new ProcessStartInfo(Path.Combine(installed, "MorphocyteOS.exe")) { UseShellExecute = false, CreateNoWindow = true };
                parentInfo.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
                parentInfo.Environment["DOTNET_ROOT_X64"] = Path.GetDirectoryName(dotnet)!;
                parentInfo.ArgumentList.Add("--fixture-parent"); parentInfo.ArgumentList.Add(readyParent);
                using var parent = Process.Start(parentInfo)!;
                Process? helper = null; FileStream? held = null;
                try
                {
                    var timer = Stopwatch.StartNew();
                    while (!File.Exists(readyParent) && !parent.HasExited && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
                    Check(File.Exists(readyParent) && !parent.HasExited, scenario + ": parent is alive in the renamed installation folder");
                    var plan = new UpdatePlan { InstallDirectory = installed, ExecutableName = "MorphocyteOS.exe",
                        OriginalExecutableHash = UpdatePackage.Hash(Path.Combine(installed, "MorphocyteOS.exe")), ParentId = parent.Id,
                        ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks, Version = "1.1.0", HandoffToken = Guid.NewGuid().ToString("N") };
                    if (scenario == "wrong-parent") plan.ParentStartTicks++;
                    var planPath = Path.Combine(operation, "plan.json"); File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
                    if (scenario == "damaged") File.WriteAllText(Path.Combine(payload, "README.md"), "corrupt download");
                    var helperInfo = new ProcessStartInfo(bundled ? Path.Combine(operation, "installer.exe") : dotnet)
                        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = operation };
                    if (!bundled) helperInfo.ArgumentList.Add(Path.Combine(operation, Path.GetFileName(application)));
                    helperInfo.ArgumentList.Add("--apply-update"); helperInfo.ArgumentList.Add(planPath);
                    helper = Process.Start(helperInfo)!;
                    if (scenario is "damaged" or "wrong-parent")
                    {
                        var rejected = false;
                        try { await UpdateInstaller.WaitForReadyAsync(helper, Path.Combine(operation, "ready.txt"), plan.HandoffToken, default); }
                        catch (IOException) { rejected = true; }
                        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                        Check(rejected && !parent.HasExited && !File.Exists(Path.Combine(operation, "ready.txt"))
                            && File.ReadAllText(Path.Combine(installed, "README.md")) == "old readme", scenario + ": WPF helper rejects unsafe handoff before the parent closes or files change");
                        continue;
                    }
                    await UpdateInstaller.WaitForReadyAsync(helper, Path.Combine(operation, "ready.txt"), plan.HandoffToken, default);
                    Check(!helper.HasExited && helper.MainWindowHandle == IntPtr.Zero && !parent.HasExited
                        && File.ReadAllText(Path.Combine(installed, "README.md")) == "old readme", scenario + ": real WPF startup acknowledges readiness without opening a window or changing live files");
                    if (scenario == "locked") held = new FileStream(Path.Combine(installed, "README.md"), FileMode.Open, FileAccess.Read, FileShare.None);
                    File.WriteAllText(readyParent + ".exit", "exit");
                    await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    held?.Dispose(); held = null;
                    var outcome = JsonSerializer.Deserialize<UpdateOutcome>(File.ReadAllText(Path.Combine(operation, "result.json")))!;
                    Check(outcome.Success == (scenario == "success") && helper.ExitCode == (scenario == "success" ? 0 : 1), scenario + ": WPF helper records the correct installation result");
                    timer.Restart();
                    while (!File.Exists(Path.Combine(installed, "restarted.txt")) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
                    Check(File.Exists(Path.Combine(installed, "restarted.txt")), scenario + ": updated or restored app restarts from the same renamed folder");
                    Check(File.ReadAllText(Path.Combine(installed, "README.md")) == (scenario == "success" ? "new readme" : "old readme")
                        && File.ReadAllText(Path.Combine(installed, "private.yaml")) == "fixture personal configuration", scenario + ": owned files update in place while the user's configuration survives");
                }
                finally
                {
                    held?.Dispose(); File.WriteAllText(readyParent + ".exit", "exit");
                    if (!parent.HasExited) await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    if (helper is not null) { if (!helper.HasExited) { helper.Kill(true); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } helper.Dispose(); }
                }
            }
            Console.WriteLine($"Passed {checks} real WPF updater checks. No live VPN or installed user files changed.");
            return 0;
        }
        finally
        {
            var resolved = Path.GetFullPath(scratch);
            if (Path.GetDirectoryName(resolved) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(resolved).StartsWith("MorphocyteOS-wpf-updater-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
        }
    }
}
