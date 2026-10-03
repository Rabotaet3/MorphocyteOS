using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MorphocyteRouter;

if (args.Length == 2 && args[0] == "--apply-update") return await UpdateInstaller.RunAsync(args[1]);
if (args.Length == 2 && args[0] == "--live-release-check")
{
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    var release = await ReleaseUpdateService.CheckAsync(client, "Rabotaet3/MorphocyteOS", "0.0.0");
    if (!release.UpdateAvailable || release.Asset is null) throw new Exception("Live release unavailable: " + release.Message);
    var prepared = await UpdateDownloader.PrepareAsync(client, release, Path.GetFullPath(args[1]), null, default);
    Console.WriteLine($"Live GitHub release {prepared.Manifest.Version}: downloaded and verified {prepared.Manifest.Files.Count} hashed files. No installation performed.");
    UpdateDownloader.DeleteOperation(Path.GetFullPath(args[1]), prepared.Directory);
    return 0;
}
if (args.Length == 2 && args[0] == "--fixture-parent")
{
    File.WriteAllText(args[1], "ready");
    while (!File.Exists(args[1] + ".exit")) await Task.Delay(25);
    return 0;
}
if (args.Length == 2 && args[0] == "--update-result")
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "restarted.txt"), "restarted");
    return 0;
}
if (args.Length == 3 && args[0] == "--verify-package")
{
    var validationRoot = Path.Combine(Path.GetTempPath(), "MorphocyteOS-package-qa-" + Guid.NewGuid().ToString("N"));
    try
    {
        var validated = UpdatePackage.Extract(args[1], validationRoot, args[2], default);
        Console.WriteLine($"Real release ZIP verified: {validated.Version}, {validated.Files.Count} hashed files, correct paths and no extra state.");
    }
    finally { if (Directory.Exists(validationRoot)) Directory.Delete(validationRoot, true); }
    return 0;
}
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}
void Reject(Action action, string message)
{
    try { action(); } catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { Check(true, message); return; }
    throw new Exception("FAIL (accepted): " + message);
}
async Task RejectAsync(Func<Task> action, string message)
{
    try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException) { Check(true, message); return; }
    throw new Exception("FAIL (accepted): " + message);
}
string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
var scratch = Path.Combine(Path.GetTempPath(), "MorphocyteOS-updater-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var sourceJson = Bytes("{\"githubRepository\":\"sample-org/sample-app\"}");
Dictionary<string, byte[]> Files(string version) => new(StringComparer.OrdinalIgnoreCase)
{
    ["MorphocyteOS.exe"] = Bytes("app-" + version), ["mihomo.exe"] = Bytes("core-" + version),
    ["release-source.json"] = sourceJson, ["LICENSE.txt"] = Bytes("license"),
    ["Licenses/Example.txt"] = Bytes("license dependency"), ["ThirdPartySource/example.txt"] = Bytes("source"),
    ["README.md"] = Bytes("readme-" + version)
};
ReleaseManifest Manifest(string version, Dictionary<string, byte[]> files) => new()
{
    Version = version, Files = files.Select(pair => new ReleaseFile { Path = pair.Key, Size = pair.Value.Length, Sha256 = Hash(pair.Value) }).ToList()
};
string Stage(string name, string version, Dictionary<string, byte[]> files, string exeName = "MorphocyteOS.exe")
{
    var root = Path.Combine(scratch, name); Directory.CreateDirectory(root);
    foreach (var pair in files)
    {
        var path = Path.Combine(root, (pair.Key == "MorphocyteOS.exe" ? exeName : pair.Key).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, pair.Value);
    }
    File.WriteAllText(Path.Combine(root, UpdatePackage.ManifestName), JsonSerializer.Serialize(Manifest(version, files)));
    return root;
}
byte[] Zip(string version, Dictionary<string, byte[]> files, string prefix = "", string? extra = null, bool duplicate = false)
{
    using var memory = new MemoryStream();
    using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
    {
        void Entry(string name, byte[] data) { using var stream = zip.CreateEntry(prefix + name).Open(); stream.Write(data); }
        Entry(UpdatePackage.ManifestName, JsonSerializer.SerializeToUtf8Bytes(Manifest(version, files)));
        foreach (var pair in files) Entry(pair.Key, pair.Value);
        if (extra is not null) Entry(extra, Bytes("unexpected"));
        if (duplicate) Entry("MorphocyteOS.exe", files["MorphocyteOS.exe"]);
    }
    return memory.ToArray();
}
UpdatePlan Plan(string root, string exeName = "MorphocyteOS.exe") => new()
{
    InstallDirectory = root, ExecutableName = exeName, OriginalExecutableHash = UpdatePackage.Hash(Path.Combine(root, exeName)),
    ParentId = Environment.ProcessId, ParentStartTicks = DateTime.UtcNow.Ticks, Version = "1.1.0"
};
var nextFiles = Files("1.1.0");
var next = Stage("payload", "1.1.0", nextFiles);
var zipBytes = Zip("1.1.0", nextFiles, "MorphocyteOS-1.1.0-win-x64/");
UpdateCheckResult Release(byte[] content) => new(true, true, "new", LatestVersion: "1.1.0", Repository: "sample-org/sample-app",
    Asset: new("MorphocyteOS-1.1.0-win-x64.zip", "https://github.com/sample-org/sample-app/releases/download/v1.1.0/MorphocyteOS-1.1.0-win-x64.zip", Hash(content), content.Length));
var cache = Path.Combine(scratch, "cache");
try
{
    foreach (var path in new[] { "../settings.json", "/MorphocyteOS.exe", "C:/MorphocyteOS.exe", "Licenses/../profile.yaml", "Licenses/CON.txt", "Licenses/profile.yaml", "Licenses/settings.json", "Licenses/local.bak", "Licenses/a.txt:stream", "Licenses/a. ", "settings.json", "portable.flag" })
        Check(!UpdatePackage.IsManagedPath(path), "private or unsafe path rejected: " + path);
    Check(UpdatePackage.IsManagedPath("Licenses/notice.txt"), "license files are allowed");
    var sourceArchive = Path.Combine(scratch, "package.zip"); File.WriteAllBytes(sourceArchive, zipBytes);
    var extracted = UpdatePackage.Extract(sourceArchive, Path.Combine(scratch, "extracted"), "1.1.0", default);
    Check(extracted.Files.Count == nextFiles.Count, "wrapped GitHub archive is verified and extracted");
    foreach (var extra in new[] { "../outside.txt", "profile.yaml", "settings.json", "unexpected.exe" })
    {
        File.WriteAllBytes(sourceArchive, Zip("1.1.0", nextFiles, extra: extra));
        Reject(() => UpdatePackage.Extract(sourceArchive, Path.Combine(scratch, Guid.NewGuid().ToString("N")), "1.1.0", default), "unexpected archive entry rejected: " + extra);
    }
    File.WriteAllBytes(sourceArchive, Zip("1.1.0", nextFiles, duplicate: true));
    Reject(() => UpdatePackage.Extract(sourceArchive, Path.Combine(scratch, "duplicate"), "1.1.0", default), "duplicate archive entries rejected");
    File.WriteAllBytes(sourceArchive, zipBytes);
    Reject(() => UpdatePackage.Extract(sourceArchive, Path.Combine(scratch, "wrong-version"), "1.2.0", default), "archive version mismatch rejected");
    var tampered = JsonSerializer.SerializeToUtf8Bytes(Manifest("1.1.0", nextFiles));
    Reject(() => UpdatePackage.ParseManifest(new byte[2 * 1024 * 1024 + 1]), "manifest size bounded");
    var badManifest = Manifest("1.1.0", nextFiles); badManifest.Files.Add(badManifest.Files[0]);
    Reject(() => UpdatePackage.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(badManifest)), "duplicate manifest paths rejected");
    using (var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) }))))
    {
        var download = await UpdateDownloader.PrepareAsync(client, Release(zipBytes), cache, null, default);
        Check(download.Manifest.Version == "1.1.0" && File.Exists(Path.Combine(download.Directory, "payload", "MorphocyteOS.exe")), "direct download validates GitHub digest and repository");
        UpdateDownloader.DeleteOperation(cache, download.Directory);
        Check(!Directory.Exists(download.Directory), "cancelled preparation cleans only its own directory");
        await RejectAsync(() => UpdateDownloader.PrepareAsync(client, Release(zipBytes) with { Asset = Release(zipBytes).Asset! with { Sha256 = new string('0', 64) } }, cache, null, default), "mismatched archive digest rejected");
        await RejectAsync(() => UpdateDownloader.PrepareAsync(client, Release(zipBytes) with { Asset = Release(zipBytes).Asset! with { Size = zipBytes.Length + 1 } }, cache, null, default), "wrong declared archive size rejected");
        await RejectAsync(() => UpdateDownloader.PrepareAsync(client, Release(zipBytes) with { Repository = "other/repo" }, cache, null, default), "wrong archive repository rejected");
    }
    using (var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://untrusted.example/update.zip") } }))))
        await RejectAsync(() => UpdateDownloader.PrepareAsync(client, Release(zipBytes), cache, null, default), "redirect to untrusted host rejected");
    using (var client = new HttpClient(new StubHandler(async (_, token) => { await Task.Delay(10000, token); return new(HttpStatusCode.OK); })))
    {
        using var cancel = new CancellationTokenSource(30);
        await RejectAsync(() => UpdateDownloader.PrepareAsync(client, Release(zipBytes), cache, null, cancel.Token), "download cancellation propagated");
    }
    Check(!Directory.EnumerateDirectories(cache).Any(), "all failed preparations leave no downloaded files");
    foreach (var host in new[] { "http://github.com/a", "https://github.com:444/a", "https://user@github.com/a", "https://github.com.attacker.example/a" })
        Check(!UpdateDownloader.IsDownloadHost(new Uri(host)), "unsafe download endpoint rejected: " + host);

    var oldFiles = Files("1.0.0"); oldFiles["Licenses/obsolete.txt"] = Bytes("obsolete owned file");
    var installed = Stage("installed", "1.0.0", oldFiles, "MorphocyteOS-test.exe");
    File.WriteAllText(Path.Combine(installed, "private.yaml"), "secret user profile");
    File.WriteAllText(Path.Combine(installed, "settings.json"), "local settings");
    File.WriteAllText(Path.Combine(installed, "portable.flag"), "portable");
    File.WriteAllText(Path.Combine(installed, "notes.txt"), "user notes");
    File.WriteAllText(Path.Combine(installed, "Licenses", "unowned.txt"), "custom notice");
    File.WriteAllText(Path.Combine(installed, "mihomo.exe"), "custom core");
    var plan = Plan(installed, "MorphocyteOS-test.exe"); plan.ProtectedFiles.Add(Path.Combine(installed, "mihomo.exe"));
    File.Delete(Path.Combine(installed, "ThirdPartySource", "example.txt"));
    UpdateInstaller.Apply(next, plan, Path.Combine(scratch, "rollback-success"));
    Check(File.ReadAllBytes(Path.Combine(installed, "MorphocyteOS-test.exe")).SequenceEqual(nextFiles["MorphocyteOS.exe"]), "renamed test EXE updates in place");
    Check(File.Exists(Path.Combine(installed, "ThirdPartySource", "example.txt")), "missing managed files installed");
    Check(!File.Exists(Path.Combine(installed, "Licenses", "obsolete.txt")), "obsolete owned file removed");
    Check(File.ReadAllText(Path.Combine(installed, "mihomo.exe")) == "custom core", "chosen custom core preserved");
    Check(File.ReadAllText(Path.Combine(installed, "private.yaml")) == "secret user profile" && File.ReadAllText(Path.Combine(installed, "settings.json")) == "local settings"
        && File.ReadAllText(Path.Combine(installed, "notes.txt")) == "user notes" && File.Exists(Path.Combine(installed, "portable.flag"))
        && File.Exists(Path.Combine(installed, "Licenses", "unowned.txt")), "profiles, settings, portable flag and unowned files preserved");
    Check(!UpdatePackage.ParseManifest(File.ReadAllBytes(Path.Combine(installed, UpdatePackage.ManifestName)), false).Files.Any(file => file.Path == "mihomo.exe"), "protected core is excluded from future cleanup ownership");

    // Test rollback after every changed file and after manifest replacement.
    var rollbackOld = Files("1.0.0"); rollbackOld.Remove("ThirdPartySource/example.txt"); rollbackOld["Licenses/obsolete.txt"] = Bytes("obsolete");
    for (var failAt = 1; failAt <= 6; failAt++)
    {
        var root = Stage("rollback-" + failAt, "1.0.0", rollbackOld);
        var snapshot = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes);
        var injectedAt = failAt;
        Reject(() => UpdateInstaller.Apply(next, Plan(root), Path.Combine(scratch, "backup-" + failAt), count => { if (count == injectedAt) throw new IOException("injected failure"); }), "rollback triggered at mutation " + failAt);
        var after = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Check(after.Length == snapshot.Count && snapshot.All(pair => File.ReadAllBytes(Path.Combine(root, pair.Key)).SequenceEqual(pair.Value)), "rollback restores exact old files and manifest at mutation " + failAt);
    }
    var modified = Stage("modified", "1.0.0", oldFiles); File.WriteAllText(Path.Combine(modified, "README.md"), "user changed readme");
    Reject(() => UpdateInstaller.Apply(next, Plan(modified), Path.Combine(scratch, "backup-modified")), "changed owned files are not overwritten");
    Check(File.ReadAllText(Path.Combine(modified, "README.md")) == "user changed readme" && File.ReadAllText(Path.Combine(modified, "MorphocyteOS.exe")) == "app-1.0.0", "ownership rejection occurs before mutation");
    var collisionFiles = Files("1.0.0"); collisionFiles.Remove("README.md");
    var collision = Stage("collision", "1.0.0", collisionFiles); File.WriteAllText(Path.Combine(collision, "README.md"), "unowned");
    Reject(() => UpdateInstaller.Apply(next, Plan(collision), Path.Combine(scratch, "backup-collision")), "unowned filename collision cancels update");
    var locked = Stage("locked", "1.0.0", oldFiles);
    using (var held = new FileStream(Path.Combine(locked, "mihomo.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        Reject(() => UpdateInstaller.Apply(next, Plan(locked), Path.Combine(scratch, "backup-lock")), "live core lock cancels before mutation");
    Check(File.ReadAllText(Path.Combine(locked, "MorphocyteOS.exe")) == "app-1.0.0", "locked update leaves current app intact");
    var noManifest = Stage("no-manifest", "1.0.0", oldFiles); File.Delete(Path.Combine(noManifest, UpdatePackage.ManifestName));
    Reject(() => UpdateInstaller.Apply(next, Plan(noManifest), Path.Combine(scratch, "backup-missing")), "no ownership manifest means no unsafe cleanup");
    Reject(() => UpdateDownloader.DeleteOperation(cache, scratch), "cleanup cannot target an ancestor directory");
    Check(File.Exists(Path.Combine(installed, "private.yaml")), "unsafe cleanup leaves profiles intact");
    // Exercise the real helper: wait for the old EXE, apply files, report and restart.
    var processExe = Environment.ProcessPath!;
    if (!Path.GetFileNameWithoutExtension(processExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        var workerOld = Files("1.0.0"); workerOld["MorphocyteOS.exe"] = File.ReadAllBytes(processExe);
        var workerNew = Files("1.1.0"); workerNew["MorphocyteOS.exe"] = workerOld["MorphocyteOS.exe"];
        var workerRoot = Stage("helper-installed", "1.0.0", workerOld);
        var operationName = Guid.NewGuid().ToString("N");
        var operation = Path.Combine(scratch, operationName);
        Stage(operationName + "/payload", "1.1.0", workerNew);
        File.Copy(processExe, Path.Combine(operation, "installer.exe"));
        foreach (var sidecar in Directory.EnumerateFiles(AppContext.BaseDirectory, "UpdaterTests.*").Where(file => !file.EndsWith(".exe") && !file.EndsWith(".pdb")))
        {
            File.Copy(sidecar, Path.Combine(workerRoot, Path.GetFileName(sidecar)));
            File.Copy(sidecar, Path.Combine(operation, Path.GetFileName(sidecar)));
        }
        var ready = Path.Combine(workerRoot, "ready.txt");
        var parentInfo = new ProcessStartInfo(Path.Combine(workerRoot, "MorphocyteOS.exe")) { UseShellExecute = false, CreateNoWindow = true };
        parentInfo.ArgumentList.Add("--fixture-parent"); parentInfo.ArgumentList.Add(ready);
        using var parent = Process.Start(parentInfo)!;
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(ready) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            Check(File.Exists(ready) && !parent.HasExited, "test parent process is alive before helper installation");
            var workerPlan = Plan(workerRoot); workerPlan.ParentId = parent.Id; workerPlan.ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks;
            var planPath = Path.Combine(operation, "plan.json"); File.WriteAllText(planPath, JsonSerializer.Serialize(workerPlan));
            var helperInfo = new ProcessStartInfo(Path.Combine(operation, "installer.exe")) { UseShellExecute = false, CreateNoWindow = true };
            helperInfo.ArgumentList.Add("--apply-update"); helperInfo.ArgumentList.Add(planPath);
            using var helper = Process.Start(helperInfo)!;
            await Task.Delay(500);
            Check(!helper.HasExited && File.ReadAllText(Path.Combine(workerRoot, "README.md")) == "readme-1.0.0", "helper waits for old process without killing it or changing files");
            File.WriteAllText(ready + ".exit", "exit");
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            timer.Restart();
            while (!File.Exists(Path.Combine(workerRoot, "restarted.txt")) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            Check(helper.ExitCode == 0 && JsonSerializer.Deserialize<UpdateOutcome>(File.ReadAllText(Path.Combine(operation, "result.json")))?.Success == true, "headless helper completes real file transaction and records success");
            Check(File.Exists(Path.Combine(workerRoot, "restarted.txt")) && File.ReadAllText(Path.Combine(workerRoot, "README.md")) == "readme-1.1.0", "helper relaunches the updated EXE with update result");
        }
        finally { File.WriteAllText(ready + ".exit", "exit"); if (!parent.HasExited) await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    Console.WriteLine($"Passed {checks} updater checks. No external requests or live VPN changes.");
}
finally
{
    // This exact random temporary tree is created solely by this test invocation.
    if (Path.GetDirectoryName(scratch) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
        && Path.GetFileName(scratch).StartsWith("MorphocyteOS-updater-tests-", StringComparison.Ordinal)) Directory.Delete(scratch, true);
}
return 0;

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
}
