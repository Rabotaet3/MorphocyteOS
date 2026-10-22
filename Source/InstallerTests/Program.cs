using System.Text.Json;
using MorphocyteRouter;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installer checks require Windows.");
var count = 0;
var fixtureRoot = Path.Combine(Path.GetTempPath(), "MorphocyteOS-installer-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
var helper = Path.Combine(fixtureRoot, "trusted-helper.exe");
File.WriteAllText(helper, "fixture helper, never executed");
try
{
    var install = NewInstall("clean");
    var custom = Path.Combine(install, "custom.exe");
    File.WriteAllText(custom, "user core");
    File.WriteAllText(Path.Combine(install, "profile.yaml"), "secret fixture profile");
    File.WriteAllText(Path.Combine(install, "portable.flag"), "");
    File.WriteAllText(Path.Combine(install, "settings.json"), "{\"Theme\":\"Аметист\"}");
    var taskCalls = 0;
    InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), path =>
    {
        Check(path == Path.Combine(install, "MorphocyteOS.exe"), "exact startup task executable"); taskCalls++;
    });
    Check(taskCalls == 1, "startup callback runs once");
    Check(!File.Exists(Path.Combine(install, "MorphocyteOS.exe")), "owned EXE removed");
    Check(!File.Exists(Path.Combine(install, "mihomo.exe")), "owned core removed");
    Check(!Directory.Exists(Path.Combine(install, "Licenses")), "only empty owned subdirectories trimmed");
    Check(!File.Exists(Path.Combine(install, UpdatePackage.ManifestName)), "manifest removed last on success");
    Check(!File.Exists(Path.Combine(install, InstallerMaintenance.MarkerName)), "ownership marker removed on success");
    Check(File.ReadAllText(custom) == "user core", "custom core preserved");
    Check(File.ReadAllText(Path.Combine(install, "profile.yaml")) == "secret fixture profile", "profile preserved");
    Check(File.Exists(Path.Combine(install, "portable.flag")), "portable flag preserved");
    Check(File.ReadAllText(Path.Combine(install, "settings.json")) == "{\"Theme\":\"Аметист\"}", "settings never mutated");

    install = NewInstall("selected");
    var selectedCore = Path.Combine(install, "mihomo.exe");
    var selectedProfile = Path.Combine(install, "Licenses", "selected.txt");
    AddManaged(install, "Licenses/selected.txt", "user-selected profile bytes");
    var selectedSettings = JsonSerializer.Serialize(new { CorePath = selectedCore, ConfigPath = selectedProfile });
    File.WriteAllText(Path.Combine(install, "settings.json"), selectedSettings);
    InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { });
    Check(!File.Exists(selectedCore), "selected unmodified bundled core removed with app");
    Check(File.ReadAllText(selectedProfile) == "user-selected profile bytes", "selected managed profile preserved");
    Check(File.ReadAllText(Path.Combine(install, "settings.json")) == selectedSettings, "selected settings unchanged");

    install = NewInstall("normal-singbox");
    AddManaged(install, "ThirdPartySource/Engine/sing-box.exe", "fixture bundled sing-box");
    var singBox = Path.Combine(install, "ThirdPartySource", "Engine", "sing-box.exe");
    File.WriteAllText(Path.Combine(install, "settings.json"), JsonSerializer.Serialize(new { CorePath = singBox }));
    InstallerMaintenance.ValidateTarget(install, InstallerMaintenance.TestAppId, helper, Settings(install), ReadManifest(install));
    Check(true, "repair accepts the selected normal nested bundled sing-box");
    InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { });
    Check(!File.Exists(singBox), "unmodified nested bundled sing-box removed normally");

    install = NewInstall("modified-singbox");
    AddManaged(install, "ThirdPartySource/Engine/sing-box.exe", "fixture bundled sing-box");
    singBox = Path.Combine(install, "ThirdPartySource", "Engine", "sing-box.exe");
    File.WriteAllText(singBox, "selected user-replaced core");
    File.WriteAllText(Path.Combine(install, "settings.json"), JsonSerializer.Serialize(new { CorePath = singBox }));
    InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { });
    Check(File.ReadAllText(singBox) == "selected user-replaced core", "selected modified nested core preserved");

    install = NewInstall("modified");
    File.WriteAllText(Path.Combine(install, "mihomo.exe"), "changed core");
    Throws(() => InstallerMaintenance.ValidateTarget(install, InstallerMaintenance.TestAppId, helper, Settings(install)), "repair refuses modified managed files");
    InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { });
    Check(File.ReadAllText(Path.Combine(install, "mihomo.exe")) == "changed core", "uninstall preserves modified owned bytes");

    install = NewInstall("updated-manifest");
    AddManaged(install, "ThirdPartySource/new-version.txt", "added by a future in-app update");
    InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { });
    Check(!File.Exists(Path.Combine(install, "ThirdPartySource", "new-version.txt")), "current manifest includes later update files");

    install = NewInstall("locked");
    using (var locked = new FileStream(Path.Combine(install, "MorphocyteOS.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
        Throws(() => InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { }), "locked files refuse cleanup");
    Check(File.Exists(Path.Combine(install, UpdatePackage.ManifestName)), "failed cleanup retains manifest");
    Check(File.Exists(Path.Combine(install, InstallerMaintenance.MarkerName)), "failed cleanup retains ownership marker");

    install = NewInstall("bad-settings");
    File.WriteAllText(Path.Combine(install, "settings.json"), "broken");
    Throws(() => InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { }), "corrupt settings refuse cleanup");
    Check(File.Exists(Path.Combine(install, "MorphocyteOS.exe")), "corrupt settings cannot delete app");
    Check(File.ReadAllText(Path.Combine(install, "settings.json")) == "broken", "corrupt settings not repaired or backed up");

    install = NewInstall("task-failure");
    Throws(() => InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => throw new IOException("injected task error")), "task errors stop before removal");
    Check(File.Exists(Path.Combine(install, "MorphocyteOS.exe")), "task errors preserve all owned bytes");

    install = NewInstall("wrong-owner");
    Throws(() => InstallerMaintenance.Cleanup(install, InstallerMaintenance.ProductionAppId, helper, Settings(install), _ => { }), "production cannot remove test installation");
    File.Delete(Path.Combine(install, InstallerMaintenance.MarkerName));
    Throws(() => InstallerMaintenance.ValidateTarget(install, InstallerMaintenance.TestAppId, helper, Settings(install)), "markerless nonempty target refused");
    Throws(() => InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { }), "markerless deletion refused");

    install = NewInstall("inside-helper");
    Throws(() => InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, Path.Combine(install, "MorphocyteOS.exe"), Settings(install), _ => { }), "helper may not run from installation");
    Throws(() => InstallerMaintenance.ValidateTarget(Path.GetPathRoot(install)!, InstallerMaintenance.TestAppId, helper, Array.Empty<string>()), "drive root refused");
    Throws(() => InstallerMaintenance.ValidateTarget(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), InstallerMaintenance.TestAppId, helper, Array.Empty<string>()), "user profile root refused");
    Throws(() => InstallerMaintenance.ValidateTarget(Path.GetTempPath(), InstallerMaintenance.TestAppId, helper, Array.Empty<string>()), "temporary root refused");
    Throws(() => InstallerMaintenance.ValidateTarget("relative", InstallerMaintenance.TestAppId, helper, Array.Empty<string>()), "relative target refused");
    Throws(() => InstallerMaintenance.ValidateTarget(install, Guid.NewGuid().ToString("D"), helper, Settings(install)), "unknown installer ID refused");
    var empty = Path.Combine(fixtureRoot, "empty");
    Directory.CreateDirectory(empty);
    InstallerMaintenance.ValidateTarget(empty, InstallerMaintenance.TestAppId, helper, Array.Empty<string>());
    Check(!Directory.EnumerateFileSystemEntries(empty).Any(), "empty target accepted without writes");
    var missing = Path.Combine(fixtureRoot, "missing");
    InstallerMaintenance.ValidateTarget(missing, InstallerMaintenance.TestAppId, helper, Array.Empty<string>());
    Check(!Directory.Exists(missing), "missing target accepted without creation");

    install = NewInstall("new-file-collision");
    var incoming = ReadManifest(install);
    var collision = Path.Combine(install, "Licenses", "user-owned.txt");
    File.WriteAllText(collision, "custom bytes");
    incoming.Files.Add(new ReleaseFile { Path = "Licenses/user-owned.txt", Size = 12, Sha256 = new string('0', 64) });
    Throws(() => InstallerMaintenance.ValidateTarget(install, InstallerMaintenance.TestAppId, helper, Settings(install), incoming), "new incoming file may not overwrite unowned path");
    Check(File.ReadAllText(collision) == "custom bytes", "new-file collision leaves custom bytes untouched");
    incoming = ReadManifest(install);
    incoming.Version = "1.2.6";
    Throws(() => InstallerMaintenance.ValidateTarget(install, InstallerMaintenance.TestAppId, helper, Settings(install), incoming), "older setup cannot downgrade installed version");
    incoming.Version = "1.2.7";
    InstallerMaintenance.ValidateTarget(install, InstallerMaintenance.TestAppId, helper, Settings(install), incoming);
    Check(true, "same-version repair accepts owned unmodified files");
    incoming.Version = "1.2.8";
    Throws(() => InstallerMaintenance.ValidateTarget(install, InstallerMaintenance.TestAppId, helper, Settings(install), incoming), "new setup upgrade uses app updater instead of orphaning old managed files");
    incoming = ReadManifest(install);
    incoming.Files.RemoveAll(file => file.Path == "Licenses/test.txt");
    Throws(() => InstallerMaintenance.ValidateTarget(install, InstallerMaintenance.TestAppId, helper, Settings(install), incoming), "same-version repair cannot orphan prior manifest files");
    Check(!InstallerMaintenance.RefreshRegistrationVersion(empty, "1.2.7"), "markerless directory cannot write registration");
    Check(!InstallerMaintenance.RefreshRegistrationVersion(install, "invalid-version"), "invalid version cannot write registration");
    var taskExecutable = Path.Combine(install, "MorphocyteOS.exe");
    Check(InstallerMaintenance.IsOwnedStartupAction("MorphocyteOS-S-1-5-21-10-20-30-1001", taskExecutable, taskExecutable), "exact SID task action recognized");
    Check(!InstallerMaintenance.IsOwnedStartupAction("MorphocyteOS-S-1-5-21-10-20-30-1001", helper, taskExecutable), "other installation's startup action preserved");
    Check(!InstallerMaintenance.IsOwnedStartupAction("MorphocyteOS-S-1-not-a-sid", taskExecutable, taskExecutable), "malformed SID task ignored");
    Check(!InstallerMaintenance.IsOwnedStartupAction("Unrelated-S-1-5-21-10-20-30-1001", taskExecutable, taskExecutable), "unrelated task name preserved");
    Check(!InstallerMaintenance.IsOwnedStartupAction("MorphocyteOS-S-1-5-21-10-20-30-1001", "MorphocyteOS.exe", taskExecutable), "relative startup action ignored");

    install = NewInstall("unsafe-manifest");
    var manifest = ReadManifest(install);
    manifest.Files.Add(new ReleaseFile { Path = "../escaped.txt", Size = 0, Sha256 = new string('0', 64) });
    WriteManifest(install, manifest);
    Throws(() => InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { }), "traversal manifest refused");
    Check(File.Exists(Path.Combine(install, "MorphocyteOS.exe")), "traversal refusal preserves app");

    install = NewInstall("junction");
    var outside = Path.Combine(fixtureRoot, "outside");
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "keep.txt"), "must not follow junction");
    var link = Path.Combine(install, "external");
    using (var command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
    {
        Arguments = $"/d /c mklink /J \"{link}\" \"{outside}\"", UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    })!) { command.WaitForExit(); Check(command.ExitCode == 0, "junction fixture created"); }
    Throws(() => InstallerMaintenance.Cleanup(install, InstallerMaintenance.TestAppId, helper, Settings(install), _ => { }), "junction tree refused");
    Check(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "must not follow junction", "junction destination untouched");
    Directory.Delete(link, false);

    using (var mutex = new Mutex(false, "Local\\MorphocyteOS.Router.SingleInstance"))
    {
        if (mutex.WaitOne(0))
        {
            try
            {
                var guarded = Task.Run(() => InstallerMaintenance.Run("--installer-cleanup", install, InstallerMaintenance.TestAppId)).GetAwaiter().GetResult();
                Check(guarded == 1, "live app mutex refuses uninstall");
                Check(File.Exists(Path.Combine(install, "MorphocyteOS.exe")), "live app never deleted or closed");
            }
            finally { mutex.ReleaseMutex(); }
        }
        else Check(true, "live user app mutex already held; destructive CLI skipped");
    }
    if (args.Length > 0)
    {
        if (args.Length != 2 || args[0] != "--verify-helper" || !File.Exists(args[1])) throw new ArgumentException("Expected --verify-helper <published-helper.exe>.");
        var helperDirectory = Path.Combine(fixtureRoot, "published-helper");
        Directory.CreateDirectory(helperDirectory);
        var publishedHelper = Path.Combine(helperDirectory, "MorphocyteOS-maintenance.exe");
        File.Copy(args[1], publishedHelper);
        Check(UpdatePackage.Hash(args[1]) == UpdatePackage.Hash(publishedHelper), "published helper copied exactly");
        install = NewInstall("published-helper-target");
        File.Copy(Path.Combine(install, UpdatePackage.ManifestName), Path.Combine(helperDirectory, "installer-payload-manifest.json"));
        File.WriteAllText(Path.Combine(install, "profile.yaml"), "published helper preserved profile");
        var originalFiles = Directory.GetFiles(install, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, UpdatePackage.Hash);
        // The release can be built while the user's VPN is running. Never ask
        // the live app to exit or weaken the production mutex for a fixture.
        var canCheckIdleHelper = false;
        using (var gate = new Mutex(false, "Local\\MorphocyteOS.Router.SingleInstance"))
        {
            try { canCheckIdleHelper = gate.WaitOne(0); }
            catch (AbandonedMutexException) { canCheckIdleHelper = true; }
            try
            {
                // Either our fixture or the actual app owns the gate. A child
                // process must refuse both operations without changing bytes.
                RunHelper("--installer-validate-target", install, 1);
                RunHelper("--installer-cleanup", install, 1);
                Check(Directory.GetFiles(install, "*", SearchOption.AllDirectories).Length == originalFiles.Count
                    && originalFiles.All(file => File.Exists(file.Key) && UpdatePackage.Hash(file.Key) == file.Value),
                    "published helper refuses a live app and leaves every fixture file unchanged");
            }
            finally { if (canCheckIdleHelper) gate.ReleaseMutex(); }
        }
        if (canCheckIdleHelper)
        {
            RunHelper("--installer-validate-target", install, 0);
            RunHelper("--installer-cleanup", install, 0);
            Check(!File.Exists(Path.Combine(install, "MorphocyteOS.exe")), "published helper removes only fixture-owned app");
            Check(File.ReadAllText(Path.Combine(install, "profile.yaml")) == "published helper preserved profile", "published helper leaves profile bytes");
            Check(!File.Exists(Path.Combine(install, UpdatePackage.ManifestName)), "published helper finishes ownership cleanup");
            RunHelper("--installer-cleanup", install, 1);
            RunHelper("--installer-validate-target", Path.GetPathRoot(install)!, 1);
        }
        else Console.WriteLine("SKIP: idle published-helper success paths require a separate session or a closed app. The live app/VPN was not changed; direct maintenance fixtures still ran.");

        void RunHelper(string mode, string target, int expectedCode)
        {
            var start = new System.Diagnostics.ProcessStartInfo(publishedHelper)
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = helperDirectory, RedirectStandardError = true };
            start.ArgumentList.Add(mode); start.ArgumentList.Add(target); start.ArgumentList.Add(InstallerMaintenance.TestAppId);
            using var process = System.Diagnostics.Process.Start(start)!;
            if (!process.WaitForExit(30000))
            {
                process.Kill(); process.WaitForExit();
                throw new IOException("Only the fixture helper child exceeded its deadline.");
            }
            var error = process.StandardError.ReadToEnd();
            if (process.ExitCode != expectedCode) throw new Exception($"Published helper {mode} returned {process.ExitCode}: {error}");
            Check(true, $"published helper {mode} returns {expectedCode}");
        }
    }
    Console.WriteLine($"Installer maintenance checks passed: {count}. All operations used isolated fixtures; no live app or VPN was launched.");
}
finally
{
    // This is the exact unique fixture root created above, never a computed user/install directory.
    if (!Path.GetFileName(fixtureRoot).StartsWith("MorphocyteOS-installer-checks-", StringComparison.Ordinal) ||
        !Path.GetDirectoryName(fixtureRoot)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unsafe fixture cleanup root.");
    Directory.Delete(fixtureRoot, true);
}

string NewInstall(string name)
{
    var root = Path.Combine(fixtureRoot, name);
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, InstallerMaintenance.MarkerName), JsonSerializer.Serialize(new
    { SchemaVersion = 1, AppId = InstallerMaintenance.TestAppId, TestMode = true }));
    WriteManifest(root, new ReleaseManifest { Version = "1.2.7", Files = new() });
    AddManaged(root, "MorphocyteOS.exe", "fixture application");
    AddManaged(root, "mihomo.exe", "fixture core");
    AddManaged(root, "Licenses/test.txt", "fixture license");
    return root;
}
string[] Settings(string root) => new[] { Path.Combine(root, "settings.json"), Path.Combine(root, "settings.json.bak") };
ReleaseManifest ReadManifest(string root) => JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllBytes(Path.Combine(root, UpdatePackage.ManifestName)))!;
void WriteManifest(string root, ReleaseManifest manifest) => File.WriteAllBytes(Path.Combine(root, UpdatePackage.ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest));
void AddManaged(string root, string relative, string content)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    var manifest = ReadManifest(root);
    manifest.Files.Add(new ReleaseFile { Path = relative, Size = new FileInfo(path).Length, Sha256 = UpdatePackage.Hash(path) });
    WriteManifest(root, manifest);
}
void Check(bool passed, string name) { if (!passed) throw new Exception("Check failed: " + name); count++; }
void Throws(Action action, string name)
{
    try { action(); }
    catch (Exception) { count++; return; }
    throw new Exception("Expected refusal: " + name);
}
