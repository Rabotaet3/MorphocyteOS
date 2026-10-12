using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MorphocyteRouter;

internal static class UpdateCacheCleanup
{
    private const int MaximumResultBytes = 32768;

    internal static async Task<UpdateOutcome?> ReadOutcomeAsync(string cacheRoot, string resultPath, CancellationToken token = default)
    {
        resultPath = Path.GetFullPath(resultPath);
        var operation = Path.GetDirectoryName(resultPath)!;
        ValidateOperation(cacheRoot, operation);
        if (Path.GetFileName(resultPath) != "result.json" || !File.Exists(resultPath)) return null;
        RejectLink(resultPath);
        await using var stream = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length is <= 0 or > MaximumResultBytes) return null;
        // The bounded read also rejects a concurrently expanded/replaced result.
        var bytes = new byte[MaximumResultBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        return length > MaximumResultBytes ? null : JsonSerializer.Deserialize<UpdateOutcome>(bytes.AsSpan(0, length));
    }

    internal static async Task CleanCompletedAsync(string cacheRoot, IEnumerable<string> protectedFiles,
        CancellationToken token = default, TimeSpan? timeout = null)
    {
        var root = NormalizeDirectory(cacheRoot);
        var protectedPaths = protectedFiles.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();
        var budget = timeout ?? TimeSpan.FromSeconds(20);
        var clock = Stopwatch.StartNew();
        if (!Directory.Exists(root)) return;
        RejectLink(root);
        foreach (var operation in Directory.EnumerateDirectories(root).ToArray())
        {
            if (clock.Elapsed >= budget) break;
            token.ThrowIfCancellationRequested();
            if (!Regex.IsMatch(Path.GetFileName(operation), @"\A[a-f0-9]{32}\z") ||
                protectedPaths.Any(path => path.Equals(operation, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(operation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var outcome = await ReadOutcomeAsync(root, Path.Combine(operation, "result.json"), token).ConfigureAwait(false);
                // Failed rollback and unfinished downloads retain their evidence/recovery files.
                if (outcome?.Success != true) continue;
                if (!await WaitForHelperAsync(operation, outcome, budget - clock.Elapsed, token).ConfigureAwait(false)) continue;
                var retry = 0;
                while (clock.Elapsed < budget)
                {
                    token.ThrowIfCancellationRequested();
                    try { DeleteCompletedOperation(root, operation); break; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    // A legacy helper or antivirus can briefly retain installer.exe.
                    // Keep result.json intact so the next startup can retry too.
                    var delay = TimeSpan.FromMilliseconds(Math.Min(1000, 100 * (1 << Math.Min(retry++, 4))));
                    var remaining = budget - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero) break;
                    await Task.Delay(delay < remaining ? delay : remaining, token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { /* An unverified or still occupied operation is never forcibly removed. */ }
        }
        TryPruneEmptyRoot(root);
    }

    internal static void DeleteCompletedOperation(string cacheRoot, string operation)
    {
        var root = NormalizeDirectory(cacheRoot);
        var full = Path.GetFullPath(operation);
        ValidateOperation(root, full);
        if (!Directory.Exists(full)) { TryPruneEmptyRoot(root); return; }
        // Validate the entire tree before removing even the helper. Never follow a junction.
        ValidateTree(full);
        var result = Path.Combine(full, "result.json");
        using (var stream = new FileStream(result, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length is <= 0 or > MaximumResultBytes || JsonSerializer.Deserialize<UpdateOutcome>(stream)?.Success != true)
                throw new InvalidDataException("Нет подтверждения успешной установки обновления.");
        }
        // On Windows a running helper cannot be deleted. Do this first, not a
        // recursive Directory.Delete that could erase the success marker first.
        var installer = Path.Combine(full, "installer.exe");
        if (File.Exists(installer)) { RejectLink(installer); File.Delete(installer); }
        foreach (var entry in Directory.EnumerateFileSystemEntries(full).ToArray())
        {
            if (entry.Equals(result, StringComparison.OrdinalIgnoreCase)) continue;
            DeleteEntry(entry);
        }
        // Only remove the marker after all bulk payload, archive and rollback data.
        RejectLink(result);
        File.Delete(result);
        Directory.Delete(full, false);
        TryPruneEmptyRoot(root);
    }

    private static async Task<bool> WaitForHelperAsync(string operation, UpdateOutcome outcome, TimeSpan remaining, CancellationToken token)
    {
        if (outcome.HelperProcessId <= 0 || outcome.HelperStartTicks <= 0) return true;
        if (remaining <= TimeSpan.Zero) return false;
        try
        {
            using var helper = Process.GetProcessById(outcome.HelperProcessId);
            if (helper.HasExited || helper.StartTime.ToUniversalTime().Ticks != outcome.HelperStartTicks) return true;
            if (!string.Equals(helper.MainModule?.FileName, Path.Combine(operation, "installer.exe"), StringComparison.OrdinalIgnoreCase))
                return false;
            await helper.WaitForExitAsync(token).WaitAsync(remaining, token).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException) { return true; /* The exact helper has already exited. */ }
        catch (InvalidOperationException) { return true; /* The process exited during inspection. */ }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (TimeoutException) { return false; }
    }

    private static void ValidateOperation(string cacheRoot, string operation)
    {
        var root = NormalizeDirectory(cacheRoot);
        var full = Path.GetFullPath(operation);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(full), @"\A[a-f0-9]{32}\z"))
            throw new InvalidDataException("Недопустимая папка подготовки обновления.");
        _ = UpdatePackage.ResolveFile(root, UpdatePackage.ManifestName);
        RejectLink(full);
    }

    private static string NormalizeDirectory(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    private static void RejectLink(string path)
    {
        try
        {
            // File.Exists can report false for a dangling link; attributes still
            // identify it and must not allow it to slip into the deletion walk.
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Недопустимая ссылка в папке обновления.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void ValidateTree(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLink(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static void DeleteEntry(string path)
    {
        var pending = new Stack<(string Path, bool Visited)>(); pending.Push((path, false));
        while (pending.Count > 0)
        {
            var entry = pending.Pop();
            RejectLink(entry.Path);
            if ((File.GetAttributes(entry.Path) & FileAttributes.Directory) == 0) { File.Delete(entry.Path); continue; }
            if (entry.Visited) { Directory.Delete(entry.Path, false); continue; }
            pending.Push((entry.Path, true));
            foreach (var child in Directory.EnumerateFileSystemEntries(entry.Path).ToArray()) pending.Push((child, false));
        }
    }

    private static void TryPruneEmptyRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            RejectLink(root);
            // Non-recursive deletion: a new download or a failed rollback wins the race.
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
