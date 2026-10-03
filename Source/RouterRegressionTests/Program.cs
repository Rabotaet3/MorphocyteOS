using System.Diagnostics;
using System.Text.Json;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

if (args.Length == 3 && args[0] == "--copy-profile")
{
    var source = ClashConfigDocument.Load(args[1]);
    File.WriteAllText(args[2], source.BuildText(source.Rules));
    Console.WriteLine($"Candidate prepared: {source.Rules.Count} editable, {source.PreservedRuleCount} preserved rules.");
    return;
}

// Parent regression tests terminate this owner process abruptly to verify that
// the Windows kill-on-close job also cleans up its detached fake core.
if (args.Length == 1 && args[0] == "--job-owner")
{
    var host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot identify the regression-test host.");
    var coreInfo = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        coreInfo.ArgumentList.Add(typeof(ClashConfigDocument).Assembly.Location);
    coreInfo.ArgumentList.Add("--fake-core");
    var owner = new CoreProcessManager();
    await owner.StartAsync(coreInfo);
    Console.WriteLine(owner.TrackedProcessId);
    await Task.Delay(TimeSpan.FromMinutes(2));
    return;
}

// The same tiny executable acts as an inert fake core for process-lifecycle tests.
if (args.Contains("--fake-core"))
{
    if (args.Contains("-t"))
    {
        var config = args[Array.IndexOf(args, "-f") + 1];
        Console.WriteLine("fake validator");
        Environment.Exit(File.ReadAllText(config).Contains("INVALID") ? 2 : 0);
    }
    if (Environment.GetEnvironmentVariable("MORPHOCYTE_FAKE_STARTUP_ERROR") == "1")
        Console.Error.WriteLine("Start Mixed(http+socks) server error: listen tcp 127.0.0.1:7890: address already in use");
    if (Environment.GetEnvironmentVariable("MORPHOCYTE_FAKE_LATE_STARTUP_ERROR") == "1")
    {
        await Task.Delay(2100);
        Console.Error.WriteLine("Start TUN listening error: configure tun interface: Cannot create a file when that file already exists.");
    }
    await Task.Delay(TimeSpan.FromMinutes(2));
    return;
}
var scratch = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("FAIL: accepted " + name); }
var configText = """
# keep header
mixed-port: 7890
proxies:
  - name: VPN NODE
    type: socks5
    server: 127.0.0.1
    port: 9999
tun:
  enable: true
rules:
  - IP-CIDR,192.168.0.0/16,DIRECT,no-resolve
  - 'DOMAIN-SUFFIX,example.com,DIRECT'
  - "DOMAIN,blocked.example,REJECT"
  - PROCESS-NAME,application.exe,VPN NODE
  - AND,((DOMAIN,custom.example),(NETWORK,UDP)),DIRECT
  - MATCH,DIRECT
# tail
dns:
  enable: false
""".Replace("\r\n", "\n", StringComparison.Ordinal);
string[] ReadRules(string yaml)
{
    var stream = new YamlStream();
    stream.Load(new StringReader(yaml));
    return ((YamlSequenceNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("rules")])
        .Children.Cast<YamlScalarNode>().Select(node => node.Value!).ToArray();
}
var path = Path.Combine(scratch, "profile.yaml");
File.WriteAllText(path, configText);
var document = ClashConfigDocument.Load(path);
var startupArguments = CoreProcessManager.CreateStartInfo("mihomo.exe", path);
Check(startupArguments.WorkingDirectory == scratch
    && startupArguments.ArgumentList.SequenceEqual(new[] { "-d", scratch, "-f", path }),
    "Mihomo data home and YAML path are explicitly bound to the selected profile folder");
var candidateArguments = CoreProcessManager.CreateStartInfo("mihomo.exe", Path.Combine(scratch, ".morphocyte-check-candidate.yaml"));
Check(candidateArguments.ArgumentList[1] == startupArguments.ArgumentList[1],
    "candidate validation resolves providers and data from the same profile folder");
if (OperatingSystem.IsWindows())
{
    var systemShell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    if (File.Exists(systemShell))
    {
        var copiedCoreDirectory = Path.Combine(scratch, "copied-core");
        Directory.CreateDirectory(copiedCoreDirectory);
        var copiedCore = Path.Combine(copiedCoreDirectory, "mihomo.exe");
        File.Copy(systemShell, copiedCore);
        var duplicateInfo = new ProcessStartInfo(copiedCore) { UseShellExecute = false, CreateNoWindow = true };
        duplicateInfo.ArgumentList.Add("/c");
        duplicateInfo.ArgumentList.Add("ping -n 30 127.0.0.1 > nul");
        using var duplicate = Process.Start(duplicateInfo) ?? throw new InvalidOperationException("Could not start the inert duplicate-process fixture.");
        var selectedCoreInAnotherFolder = Path.Combine(scratch, "selected-core", "mihomo.exe");
        var duplicates = CoreProcessManager.FindOtherInstances(selectedCoreInAnotherFolder);
        var detectedCopy = duplicates.SingleOrDefault(instance => instance.ProcessId == duplicate.Id);
        Check(detectedCopy.ProcessId == duplicate.Id && detectedCopy.ExecutablePath == Path.GetFullPath(copiedCore),
            "duplicate Mihomo detection also finds a same-named executable from another folder");
        await CoreProcessManager.StopOtherInstancesAsync(selectedCoreInAnotherFolder, new[] { detectedCopy });
        Check(duplicate.HasExited, "duplicate shutdown revalidates the executable path before stopping the PID");
    }

    var testHost = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot identify the regression-test host.");
    var ownerInfo = new ProcessStartInfo(testHost)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    if (Path.GetFileNameWithoutExtension(testHost).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        ownerInfo.ArgumentList.Add(typeof(ClashConfigDocument).Assembly.Location);
    ownerInfo.ArgumentList.Add("--job-owner");
    using var ownerProcess = Process.Start(ownerInfo) ?? throw new InvalidOperationException("Could not start the process-lifetime test owner.");
    var corePidText = await ownerProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
    if (!int.TryParse(corePidText, out var orphanCandidatePid)) throw new InvalidOperationException("The job-owner process did not report its fake core PID.");
    ownerProcess.Kill();
    await ownerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    var orphanCoreExited = false;
    for (var wait = 0; wait < 40; wait++)
    {
        try { using var orphanCandidate = Process.GetProcessById(orphanCandidatePid); orphanCoreExited = orphanCandidate.HasExited; }
        catch (ArgumentException) { orphanCoreExited = true; }
        if (orphanCoreExited) break;
        await Task.Delay(100);
    }
    if (!orphanCoreExited)
    {
        try { using var orphanCandidate = Process.GetProcessById(orphanCandidatePid); orphanCandidate.Kill(true); } catch { }
    }
    Check(orphanCoreExited, "Windows closes Mihomo when the app process is terminated unexpectedly");
}
Check(document.Rules.Count == 3, "quoted YAML and named route parsed");
Check(document.Routes.Single() == "VPN NODE", "route names with spaces intact");
var withoutProcessRule = ClashConfigDocument.Parse(path, configText.Replace("  - PROCESS-NAME,application.exe,VPN NODE\n", "", StringComparison.Ordinal));
Check(!withoutProcessRule.Rules.Any(rule => rule.Kind == "PROCESS-NAME" && rule.Value.Equals("application.exe", StringComparison.OrdinalIgnoreCase))
    && ReadRules(withoutProcessRule.BuildText(withoutProcessRule.Rules)).Last() == "MATCH,DIRECT",
    "a removed application process rule stays removed and falls through to DIRECT");
Check(document.ValidateForRouting().Count == 0, "rule mode and enabled TUN permit application routing");
var globalProfile = ClashConfigDocument.Parse(path, "mode: global\n" + configText);
Check(globalProfile.Validate().Count == 0 && globalProfile.ValidateForRouting().Any(message => message.Contains("mode: rule")),
    "global-mode profile remains editable but cannot claim to apply rule routing");
var disabledTun = ClashConfigDocument.Parse(path, configText.Replace("tun:\n  enable: true", "tun:\n  enable: false", StringComparison.Ordinal));
Check(disabledTun.Validate().Count == 0 && disabledTun.ValidateForRouting().Any(message => message.Contains("TUN")),
    "disabled TUN remains editable but is rejected for automatic application routing");
var output = document.BuildText(document.Rules);
Check(ReadRules(output).SequenceEqual(ReadRules(configText)), "rule priority and DIRECT/REJECT/AND unchanged");
Check(output[..output.IndexOf("rules:")] == configText[..configText.IndexOf("rules:")], "unrelated config prefix preserved");
Check(output.Contains("dns:\n  enable: false"), "section after rules preserved");
var additions = document.Rules.Select(rule => rule.Copy()).ToList();
additions.Add(new DomainRule { Kind = "DOMAIN-SUFFIX", Value = "new.example", Route = "VPN NODE" });
var appended = ReadRules(document.BuildText(additions));
Check(appended[^2] == "DOMAIN-SUFFIX,new.example,VPN NODE" && appended[^1] == "MATCH,DIRECT", "new rule inserted before fallback");
var withException = document.Rules.Select(rule => rule.Copy()).ToList();
withException.Add(new DomainRule { Kind = "DOMAIN-SUFFIX", Value = "outside.example", Route = "DIRECT" });
var exceptionRules = ReadRules(document.BuildText(withException));
Check(Array.IndexOf(exceptionRules, "DOMAIN-SUFFIX,outside.example,DIRECT") < Array.IndexOf(exceptionRules, "PROCESS-NAME,application.exe,VPN NODE"),
    "new direct exception inserted before broad process rule");
var temporarilyDisabled = document.Rules.Select(rule => rule.Copy()).ToList();
temporarilyDisabled[0].Enabled = false;
Check(!ReadRules(document.BuildText(temporarilyDisabled)).Contains("DOMAIN-SUFFIX,example.com,DIRECT"),
    "disabled rule omitted from applied YAML while UI state can retain it");
var deleted = ReadRules(document.BuildText(document.Rules.Where(rule => rule.Value != "example.com")));
Check(!deleted.Any(rule => rule.Contains("example.com")) && deleted[0].StartsWith("IP-CIDR"), "delete preserves opaque rule position");
var backup = document.CommitText(document.BuildText(additions));
Check(File.ReadAllText(backup) == configText, "backup contains exact old config");
document = ClashConfigDocument.Load(path);
document.CommitText(document.BuildText(document.Rules)); // Same-second saves must be safe.
Check(ReadRules(File.ReadAllText(path)).SequenceEqual(appended), "roundtrip save stable");
for (var i = 0; i < 5; i++)
{
    document = ClashConfigDocument.Load(path);
    document.CommitText(document.BuildText(document.Rules));
}
Check(Directory.GetFiles(scratch, "profile.yaml.backup-*").Length == 3,
    "YAML backup retention keeps only the three newest snapshots");
var datedPath = Path.Combine(scratch, "old-timestamp-profile.yaml");
File.WriteAllText(datedPath, configText);
string? currentDatedBackup = null;
for (var i = 0; i < 5; i++)
{
    foreach (var oldBackup in Directory.GetFiles(scratch, "old-timestamp-profile.yaml.backup-*"))
        File.SetLastWriteTimeUtc(oldBackup, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    File.SetLastWriteTimeUtc(datedPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    var datedDocument = ClashConfigDocument.Load(datedPath);
    currentDatedBackup = datedDocument.CommitText(datedDocument.BuildText(datedDocument.Rules));
    Check(File.Exists(currentDatedBackup), "current rollback backup survives old source timestamps and rapid saves");
}
Check(Directory.GetFiles(scratch, "old-timestamp-profile.yaml.backup-*").Length == 3,
    "five rapid saves retain three backups despite inherited misleading LastWriteTime");
var brieflyLockedBackup = datedPath + ".backup-19900101-000000-000-locked";
File.WriteAllText(brieflyLockedBackup, configText);
var backupLock = new FileStream(brieflyLockedBackup, FileMode.Open, FileAccess.Read, FileShare.Read);
var releaseBackupLock = Task.Run(async () => { await Task.Delay(80); backupLock.Dispose(); });
try
{
    var datedDocument = ClashConfigDocument.Load(datedPath);
    var protectedBackup = datedDocument.CommitText(datedDocument.BuildText(datedDocument.Rules));
    await releaseBackupLock;
    Check(!File.Exists(brieflyLockedBackup) && File.Exists(protectedBackup)
        && Directory.GetFiles(scratch, "old-timestamp-profile.yaml.backup-*").Length == 3,
        "backup pruning retries a transient sharing lock without removing the rollback snapshot");
}
finally { backupLock.Dispose(); }
document = ClashConfigDocument.Load(path);
File.AppendAllText(path, "\n# external edit\n");
Reject(() => document.CommitText(document.BuildText(document.Rules)), "external change refuses overwrite");
Check(File.ReadAllText(path).EndsWith("# external edit\n"), "external edit retained");
foreach (var fixture in new[]
{
    "proxies: []\ntun: {}\nrules: []\n",
    "proxies: []\ntun: {}\nrules: ['DOMAIN,example.com,DIRECT','MATCH,DIRECT']\n",
    "proxies: []\ntun: {}\n'rules':\n- DOMAIN,example.com,DIRECT\n- MATCH,DIRECT\n...\n",
    "```yaml\n" + configText + "\n```"
})
{
    var parsed = ClashConfigDocument.Parse(path, fixture);
    _ = ClashConfigDocument.Parse(path, parsed.BuildText(parsed.Rules));
    Check(true, "inline/empty/quoted-key/fenced YAML roundtrip");
}
Check(ClashConfigDocument.NormalizeDomain("https://example.com/path", "DOMAIN-SUFFIX") == "example.com", "full URL normalized for domain");
Check(ClashConfigDocument.NormalizeDomain("application.exe", "PROCESS-NAME") == "application.exe", "valid executable accepted");
Reject(() => ClashConfigDocument.NormalizeDomain("Application", "PROCESS-NAME"), "missing exe rejected");
Reject(() => ClashConfigDocument.NormalizeDomain("https://example.com", "DOMAIN-KEYWORD"), "URL rejected in keyword mode");
Reject(() => ClashConfigDocument.NormalizeDomain("abc\nmode: global", "PROCESS-NAME"), "multiline input rejected");
Reject(() => ClashConfigDocument.NormalizeDomain("test # comment", "DOMAIN-KEYWORD"), "comment input rejected");

var settingsPath = Path.Combine(scratch, "settings.json");
var a = Path.Combine(scratch, "a.yaml");
var b = Path.Combine(scratch, "b.yaml");
var settings = AppSettings.Load(settingsPath);
settings.PutDraft(a, "hashA", additions);
settings.Save();
settings.PutDraft(b, "hashB", Array.Empty<DomainRule>());
settings.Save();
settings = AppSettings.Load(settingsPath);
Check(settings.GetDraft(a)!.Rules.Count == additions.Count && settings.GetDraft(b)!.Rules.Count == 0, "separate profile drafts including empty");
settings.RemoveDraft(b);
settings.Save();
Check(AppSettings.Load(settingsPath).GetDraft(a) is not null, "apply B leaves draft A");
settings.Save(); // Backup has both latest valid settings.
File.WriteAllText(settingsPath, "{broken");
settings = AppSettings.Load(settingsPath);
Check(settings.GetDraft(a) is not null && settings.RecoveryMessage is not null, "corrupt settings recover backup");
settings.Save();
Check(AppSettings.Load(settingsPath).GetDraft(a) is not null, "recovered settings remain readable");
var damagedSettings = Path.Combine(scratch, "damaged.json");
File.WriteAllText(damagedSettings, "{broken primary");
File.WriteAllText(damagedSettings + ".bak", "{broken backup");
var reset = AppSettings.Load(damagedSettings);
Check(reset.ConfigPath.Length == 0 && reset.RecoveryMessage is not null
    && Directory.GetFiles(scratch, "damaged.json*.corrupt-*").Length == 2,
    "both damaged settings files are preserved while safe defaults allow startup");
reset.Save();
Check(AppSettings.Load(damagedSettings).ConfigPath.Length == 0, "reset settings can be persisted and reloaded");
var nullSettings = Path.Combine(scratch, "null-settings.json");
File.WriteAllText(nullSettings, "{\"ConfigPath\":null,\"CorePath\":null,\"Theme\":null,\"InterfaceScale\":9,\"ProfileDrafts\":{\"ignored\":null}}");
var normalized = AppSettings.Load(nullSettings);
Check(normalized.ConfigPath == "" && normalized.CorePath == "" && normalized.Theme.Length > 0
    && normalized.InterfaceScale == 1.5 && normalized.ProfileDrafts.Count == 0,
    "nullable and out-of-range saved settings are normalized before use");
Check(AppSettings.NormalizeScale(double.NaN) == 1 && AppSettings.NormalizeScale(0.1) == 0.8,
    "UI scale rejects non-finite values and uses readable bounds");
normalized.InterfaceScale = 1.2;
normalized.AutoCheckUpdates = true;
normalized.Save();
Check(AppSettings.Load(nullSettings) is { InterfaceScale: 1.2, AutoCheckUpdates: true },
    "interface scale and automatic update checks survive restart");
var oldSettings = Path.Combine(scratch, "legacy.json");
File.WriteAllText(oldSettings, JsonSerializer.Serialize(new { HasRuleDraft = true, DraftConfigPath = a, DraftRules = Array.Empty<DomainRule>() }));
var migrated = AppSettings.Load(oldSettings);
Check(migrated.GetDraft(a) is { Rules.Count: 0 }, "legacy empty draft migration");

ProcessStartInfo FakeInfo()
{
    var hostPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot identify the regression-test host.");
    var info = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(hostPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(typeof(ClashConfigDocument).Assembly.Location);
    info.ArgumentList.Add("--fake-core");
    return info;
}
var manager = new CoreProcessManager();
try
{
    var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => { try { await manager.StartAsync(FakeInfo()); return true; } catch (InvalidOperationException) { return false; } }));
    Check(results.Count(value => value) == 1 && manager.IsRunning, "concurrent start owns one process");
    Check(await manager.StopAsync() && !manager.HasTrackedProcess, "stop confirms exit and releases ownership");
    var allowStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var terminationCalls = 0;
    var delayed = new CoreProcessManager(async process =>
    {
        Interlocked.Increment(ref terminationCalls);
        await allowStop.Task;
        if (!process.HasExited) process.Kill(true);
        await process.WaitForExitAsync();
    }, TimeSpan.FromMilliseconds(80));
    try
    {
        await delayed.StartAsync(FakeInfo());
        Check(!await delayed.StopAsync() && delayed.HasTrackedProcess && delayed.IsRunning, "stop timeout retains live process");
        var rejected = false;
        try { await delayed.StartAsync(FakeInfo()); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "restart forbidden after unconfirmed stop");
        Check(!await delayed.StopAsync() && terminationCalls == 1, "retry shares pending stop");
        allowStop.TrySetResult();
        await Task.Delay(200);
        Check(await delayed.StopAsync(), "late stop can be confirmed");
    }
    finally { allowStop.TrySetResult(); await delayed.StopAsync(); }
    var attempts = 0;
    var failing = new CoreProcessManager(process =>
    {
        if (Interlocked.Increment(ref attempts) == 1) return Task.FromException(new IOException("simulated denied stop"));
        return Task.Run(() => { if (!process.HasExited) process.Kill(true); process.WaitForExit(); });
    });
    try
    {
        await failing.StartAsync(FakeInfo());
        Check(!await failing.StopAsync() && failing.HasTrackedProcess, "stop failure retains ownership");
        Check(await failing.StopAsync(), "failed stop can be retried");
    }
    finally { await failing.StopAsync(); }

    var startupGuard = new CoreProcessManager();
    var conflictingInfo = FakeInfo();
    conflictingInfo.Environment["MORPHOCYTE_FAKE_STARTUP_ERROR"] = "1";
    var startupRejected = false;
    try
    {
        try { await startupGuard.StartAsync(conflictingInfo); }
        catch (IOException ex) { startupRejected = ex.Message.Contains("не поднял сетевые интерфейсы", StringComparison.OrdinalIgnoreCase); }
        Check(startupRejected && !startupGuard.IsRunning && !startupGuard.HasTrackedProcess,
            "listener bind errors are not reported as a working VPN and the failed core is stopped");
    }
    finally { await startupGuard.StopAsync(); }

    var lateGuard = new CoreProcessManager();
    var lateFailed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    lateGuard.StartupFailed += message => lateFailed.TrySetResult(message);
    var lateInfo = FakeInfo();
    lateInfo.Environment["MORPHOCYTE_FAKE_LATE_STARTUP_ERROR"] = "1";
    try
    {
        await lateGuard.StartAsync(lateInfo);
        var lateFailure = await lateFailed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Check(lateFailure.Contains("TUN", StringComparison.Ordinal) && !lateGuard.IsRunning && !lateGuard.HasTrackedProcess,
            "late network startup failure stops the owned child and reports a failed VPN");
    }
    finally { await lateGuard.StopAsync(); }
}
finally { await manager.StopAsync(); }
Console.WriteLine($"TOTAL: {passed} checks passed. Fixtures: {scratch}");
