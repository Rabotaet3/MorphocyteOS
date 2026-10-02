using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MorphocyteRouter;

internal sealed class UpdatePlan
{
    public string InstallDirectory { get; set; } = "";
    public string ExecutableName { get; set; } = "MorphocyteOS.exe";
    public string OriginalExecutableHash { get; set; } = "";
    public int ParentId { get; set; }
    public long ParentStartTicks { get; set; }
    public string Version { get; set; } = "";
    public List<string> ProtectedFiles { get; set; } = new();
}

internal record UpdateOutcome(bool Success, string Message);

internal static class UpdateInstaller
{
    internal static string Start(PreparedUpdate prepared, string installDirectory, string executableName, IEnumerable<string> protectedFiles)
    {
        using var parent = Process.GetCurrentProcess();
        var executable = Path.Combine(Path.GetFullPath(installDirectory), executableName);
        var helper = Path.Combine(prepared.Directory, "installer.exe");
        File.Copy(executable, helper, false);
        var plan = new UpdatePlan
        {
            InstallDirectory = Path.GetFullPath(installDirectory), ExecutableName = executableName,
            OriginalExecutableHash = UpdatePackage.Hash(executable), ParentId = parent.Id,
            ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks, Version = prepared.Manifest.Version,
            ProtectedFiles = protectedFiles.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        var planPath = Path.Combine(prepared.Directory, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var info = new ProcessStartInfo(helper) { UseShellExecute = true, WorkingDirectory = prepared.Directory, WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--apply-update"); info.ArgumentList.Add(planPath);
        using var child = Process.Start(info) ?? throw new IOException("Не удалось запустить установку обновления.");
        return planPath;
    }

    internal static async Task<int> RunAsync(string planPath)
    {
        var operation = Path.GetFullPath(Path.GetDirectoryName(planPath)!);
        if (!Path.GetFileName(planPath).Equals("plan.json", StringComparison.Ordinal) ||
            !operation.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(operation), @"\A[a-f0-9]{32}\z")) return 1;
        UpdatePlan? plan = null;
        var safeToRestart = false;
        UpdateOutcome outcome;
        try
        {
            if (new FileInfo(planPath).Length > 32768)
                throw new InvalidDataException("Неподдерживаемый план обновления.");
            plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath)) ?? throw new InvalidDataException("Пустой план обновления.");
            ValidatePlan(plan);
            try
            {
                using var parent = Process.GetProcessById(plan.ParentId);
                try
                {
                    if (!parent.HasExited)
                    {
                        if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks ||
                            !string.Equals(parent.MainModule?.FileName, Path.Combine(plan.InstallDirectory, plan.ExecutableName), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Процесс приложения изменился. Обновление отменено.");
                        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
                    }
                }
                catch (InvalidOperationException) when (parent.HasExited) { }
                catch (System.ComponentModel.Win32Exception) when (parent.HasExited) { }
            }
            catch (ArgumentException) { /* The original app has already exited. */ }
            var executable = Path.Combine(plan.InstallDirectory, plan.ExecutableName);
            if (!UpdatePackage.Hash(executable).Equals(plan.OriginalExecutableHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Файл приложения изменился после подготовки обновления.");
            safeToRestart = true;
            Apply(Path.Combine(operation, "payload"), plan, Path.Combine(operation, "rollback"));
            outcome = new(true, "Обновление установлено: " + plan.Version);
        }
        catch (Exception error)
        {
            outcome = new(false, "Обновление не установлено: " + error.Message);
        }
        var resultPath = Path.Combine(operation, "result.json");
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(outcome));
        if (safeToRestart && plan is not null)
        {
            var executable = Path.Combine(plan.InstallDirectory, plan.ExecutableName);
            if (File.Exists(executable))
            {
                var restart = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = plan.InstallDirectory };
                restart.ArgumentList.Add("--update-result"); restart.ArgumentList.Add(resultPath);
                try { using var process = Process.Start(restart); } catch { return 2; }
            }
        }
        return outcome.Success ? 0 : 1;
    }

    private static void ValidatePlan(UpdatePlan plan)
    {
        if (plan.ExecutableName != Path.GetFileName(plan.ExecutableName) ||
            !plan.ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            plan.ExecutableName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || plan.ParentId <= 0 || plan.ParentStartTicks <= 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(plan.OriginalExecutableHash, @"\A[a-fA-F0-9]{64}\z") ||
            !ReleaseUpdateService.SemanticVersion.TryParse(plan.Version, out _)) throw new InvalidDataException("Небезопасный план обновления.");
        plan.InstallDirectory = Path.GetFullPath(plan.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (plan.InstallDirectory.Equals(Path.GetPathRoot(plan.InstallDirectory)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Помести приложение в отдельную папку перед обновлением.");
        if (plan.ProtectedFiles is null || plan.ProtectedFiles.Count > 32) throw new InvalidDataException("Небезопасный список сохраняемых файлов.");
        plan.ProtectedFiles = plan.ProtectedFiles.Select(Path.GetFullPath).ToList();
        // A copied installer is never part of the target release's file list.
        if (plan.ExecutableName.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase) || plan.ExecutableName.Equals("installer.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Неподдерживаемый файл приложения.");
    }

    internal static void Apply(string payload, UpdatePlan plan, string rollbackDirectory, Action<int>? afterMutation = null)
    {
        ValidatePlan(plan);
        var incomingBytes = File.ReadAllBytes(UpdatePackage.ResolveFile(payload, UpdatePackage.ManifestName));
        var incoming = UpdatePackage.ParseManifest(incomingBytes);
        if (incoming.Version != plan.Version) throw new InvalidDataException("Версия плана не совпадает с архивом.");
        UpdatePackage.VerifyFiles(payload, incoming);
        var currentManifestPath = UpdatePackage.ResolveFile(plan.InstallDirectory, UpdatePackage.ManifestName);
        if (!File.Exists(currentManifestPath))
            throw new IOException("Для безопасного обновления нужен список файлов установленного комплекта. Распакуй новый комплект вручную один раз.");
        var current = UpdatePackage.ParseManifest(File.ReadAllBytes(currentManifestPath), false);
        ReleaseUpdateService.SemanticVersion.TryParse(incoming.Version, out var nextVersion);
        ReleaseUpdateService.SemanticVersion.TryParse(current.Version, out var currentVersion);
        if (nextVersion!.CompareTo(currentVersion) <= 0) throw new IOException("Обновление не должно понижать или повторять установленную версию.");
        var oldFiles = current.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var newFiles = incoming.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var protectedFiles = plan.ProtectedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Target(string relative)
        {
            var isApp = relative.Equals("MorphocyteOS.exe", StringComparison.OrdinalIgnoreCase);
            var actual = isApp ? plan.ExecutableName : relative;
            if (isApp)
            {
                // The test EXE may be renamed, but never replaced outside this installation.
                var target = Path.Combine(plan.InstallDirectory, actual);
                _ = UpdatePackage.ResolveFile(plan.InstallDirectory, "MorphocyteOS.exe");
                if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Файл приложения является ссылкой.");
                return target;
            }
            return UpdatePackage.ResolveFile(plan.InstallDirectory, actual);
        }
        bool Protected(string path) => protectedFiles.Contains(Path.GetFullPath(path));
        var changed = new List<(string Relative, string Target, string? Source)>();
        var retained = new List<ReleaseFile>();
        foreach (var file in incoming.Files)
        {
            var target = Target(file.Path);
            if (Protected(target))
            {
                if (file.Path.Equals("MorphocyteOS.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Файл приложения пересекается с выбранным пользовательским файлом.");
                continue;
            }
            if (File.Exists(target))
            {
                if (!oldFiles.TryGetValue(file.Path, out var previous)) throw new IOException("Файл не принадлежит установленному комплекту: " + file.Path);
                if (!UpdatePackage.Hash(target).Equals(previous.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Установленный файл изменён пользователем; обновление отменено: " + file.Path);
            }
            retained.Add(file);
            if (!File.Exists(target) || !UpdatePackage.Hash(target).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                changed.Add((file.Path, target, UpdatePackage.ResolveFile(payload, file.Path)));
        }
        foreach (var old in current.Files.Where(file => !newFiles.ContainsKey(file.Path)))
        {
            var target = Target(old.Path);
            if (Protected(target) || !File.Exists(target)) continue;
            if (!UpdatePackage.Hash(target).Equals(old.Sha256, StringComparison.OrdinalIgnoreCase))
                continue; // A modified obsolete file is user data, not cleanup material.
            changed.Add((old.Path, target, null));
        }
        // Preflight locks before the first mutation; never stop another app's core.
        foreach (var change in changed.Where(change => File.Exists(change.Target)))
            using (var probe = new FileStream(change.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (var probe = new FileStream(currentManifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        _ = UpdatePackage.ResolveFile(rollbackDirectory, UpdatePackage.ManifestName);
        if (Directory.Exists(rollbackDirectory) && Directory.EnumerateFileSystemEntries(rollbackDirectory).Any())
            throw new IOException("Папка отката должна быть пустой.");
        Directory.CreateDirectory(rollbackDirectory);
        var backups = new List<(string Target, string? Backup, string? InstalledHash)>();
        var temporaryFiles = new List<string>();
        var mutationCount = 0;
        try
        {
            foreach (var change in changed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(change.Target)!);
                string? backup = null;
                if (File.Exists(change.Target))
                {
                    if (!oldFiles.TryGetValue(change.Relative, out var owned) ||
                        !UpdatePackage.Hash(change.Target).Equals(owned.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Файл изменился во время обновления: " + change.Relative);
                    backup = Path.Combine(rollbackDirectory, backups.Count.ToString("D4"));
                    File.Copy(change.Target, backup, false);
                }
                if (change.Source is null) File.Delete(change.Target);
                else
                {
                    var temp = change.Target + ".update-" + Guid.NewGuid().ToString("N");
                    temporaryFiles.Add(temp);
                    File.Copy(change.Source, temp, false);
                    if (!UpdatePackage.Hash(temp).Equals(newFiles[change.Relative].Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Подготовленный файл обновления изменился: " + change.Relative);
                    if (backup is not null && !UpdatePackage.Hash(change.Target).Equals(oldFiles[change.Relative].Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Файл изменился во время обновления: " + change.Relative);
                    File.Move(temp, change.Target, backup is not null);
                }
                backups.Add((change.Target, backup, change.Source is null ? null : newFiles[change.Relative].Sha256));
                afterMutation?.Invoke(++mutationCount);
            }
            var manifestBackup = Path.Combine(rollbackDirectory, "manifest");
            File.Copy(currentManifestPath, manifestBackup, false);
            var updated = new ReleaseManifest { Version = incoming.Version, Files = retained };
            var manifestTemp = currentManifestPath + ".update-" + Guid.NewGuid().ToString("N");
            temporaryFiles.Add(manifestTemp);
            File.WriteAllText(manifestTemp, JsonSerializer.Serialize(updated));
            var manifestHash = UpdatePackage.Hash(manifestTemp);
            File.Move(manifestTemp, currentManifestPath, true);
            backups.Add((currentManifestPath, manifestBackup, manifestHash));
            afterMutation?.Invoke(++mutationCount);
        }
        catch (Exception installationError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var backup in backups.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(backup.Target) && (backup.InstalledHash is null ||
                        !UpdatePackage.Hash(backup.Target).Equals(backup.InstalledHash, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException("Файл изменился извне; сохранён вместе с резервной копией: " + backup.Target);
                    if (backup.Backup is null) File.Delete(backup.Target);
                    else
                    {
                        var restored = backup.Target + ".update-" + Guid.NewGuid().ToString("N");
                        temporaryFiles.Add(restored);
                        File.Copy(backup.Backup, restored, false);
                        File.Move(restored, backup.Target, true);
                    }
                }
                catch (Exception error) { rollbackErrors.Add(error); }
            }
            if (rollbackErrors.Count > 0) throw new AggregateException("Обновление и откат не завершены; резервные копии сохранены.", new[] { installationError }.Concat(rollbackErrors));
            throw new IOException("Обновление отменено; прежний комплект восстановлен.", installationError);
        }
        finally
        {
            foreach (var temp in temporaryFiles) { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
    }
}
