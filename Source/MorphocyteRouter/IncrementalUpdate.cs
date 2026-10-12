using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MorphocyteRouter;

internal sealed class UpdateIndex
{
    public int SchemaVersion { get; set; }
    public ReleaseManifest Manifest { get; set; } = new();
    public List<UpdateComponent> Packages { get; set; } = new();
}

internal sealed class UpdateComponent
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public List<string> Files { get; set; } = new();
}

// The index is verified against GitHub's asset digest before any field is used.
// Package URLs are derived from that same release, never accepted from JSON.
internal static class IncrementalUpdate
{
    internal const int MaximumIndexBytes = 2 * 1024 * 1024;
    internal const string ReusedFilesName = "reused-files.json";

    internal static UpdateIndex ParseIndex(byte[] bytes, string version)
    {
        if (bytes.Length > MaximumIndexBytes) throw new InvalidDataException("Слишком большой список обновления.");
        UpdateIndex index;
        try { index = JsonSerializer.Deserialize<UpdateIndex>(bytes) ?? throw new JsonException(); }
        catch (JsonException error) { throw new InvalidDataException("Повреждён список обновления.", error); }
        if (index.SchemaVersion != 1 || index.Manifest is null || index.Packages is null || index.Packages.Count is < 1 or > 32)
            throw new InvalidDataException("Неподдерживаемый список пакетов обновления.");
        index.Manifest = UpdatePackage.ParseManifest(JsonSerializer.SerializeToUtf8Bytes(index.Manifest));
        if (index.Manifest.Version != version) throw new InvalidDataException("Версия списка обновления не совпадает с релизом.");
        var remaining = index.Manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var package in index.Packages)
        {
            if (package is null || !Regex.IsMatch(package.Sha256 ?? "", @"\A[0-9a-f]{64}\z") ||
                package.Name != $"MorphocyteOS-part-{package.Sha256}.zip" || !names.Add(package.Name) ||
                package.Size is <= 0 or > 536870912 || package.Files is null || package.Files.Count is < 1 or > UpdatePackage.MaximumFiles)
                throw new InvalidDataException("Небезопасный пакет обновления.");
            total += package.Size;
            if (total > UpdatePackage.MaximumExpandedBytes) throw new InvalidDataException("Пакеты превышают допустимый размер.");
            foreach (var path in package.Files)
                if (path is null || !remaining.Remove(path)) throw new InvalidDataException("Повторный или неизвестный файл пакета обновления.");
        }
        if (remaining.Count != 0) throw new InvalidDataException("В списке пакетов отсутствуют необходимые файлы.");
        return index;
    }

    internal static string InstalledPath(string directory, string relative, string executableName)
    {
        if (!relative.Equals("MorphocyteOS.exe", StringComparison.OrdinalIgnoreCase)) return UpdatePackage.ResolveFile(directory, relative);
        if (executableName != Path.GetFileName(executableName) || !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            executableName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            executableName.Equals("installer.exe", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Неподдерживаемый файл приложения.");
        var original = UpdatePackage.ResolveFile(directory, "MorphocyteOS.exe");
        var path = Path.Combine(Path.GetDirectoryName(original)!, executableName);
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Файл приложения является ссылкой.");
        return path;
    }

    internal static bool Matches(string path, ReleaseFile file) => File.Exists(path) && new FileInfo(path).Length == file.Size &&
        UpdatePackage.Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);

    internal static void ExtractNeeded(string archivePath, string payload, UpdateComponent package,
        ReleaseManifest manifest, HashSet<string> needed, CancellationToken token)
    {
        var expected = package.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > UpdatePackage.MaximumFiles * 2) throw new InvalidDataException("Слишком много записей пакета.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || !expected.Contains(entry.FullName) ||
                !entries.TryAdd(entry.FullName, entry) || files[entry.FullName].Size != entry.Length)
                throw new InvalidDataException("Содержимое пакета отличается от списка файлов.");
        }
        if (entries.Count != expected.Count) throw new InvalidDataException("Пакет содержит не все необходимые файлы.");
        foreach (var (relative, entry) in entries)
        {
            if (!needed.Contains(relative)) continue;
            token.ThrowIfCancellationRequested();
            var target = UpdatePackage.ResolveFile(payload, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    received += read;
                    if (received > files[relative].Size) throw new InvalidDataException("Файл превышает заявленный размер.");
                    output.Write(buffer, 0, read);
                }
                if (received != files[relative].Size) throw new InvalidDataException("Файл пакета извлечён не полностью.");
            }
            if (!Matches(target, files[relative])) throw new InvalidDataException("Контрольная сумма файла пакета не совпадает.");
        }
    }

    // Unchanged files stay in the installation; they are not duplicated into the cache.
    // Both the running updater and the independent installer revalidate these bytes.
    internal static HashSet<string> ReadReused(string payload, ReleaseManifest manifest)
    {
        _ = UpdatePackage.ResolveFile(payload, UpdatePackage.ManifestName);
        var path = Path.Combine(payload, ReusedFilesName);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > MaximumIndexBytes)
            throw new InvalidDataException("Небезопасный список повторно используемых файлов.");
        List<string> paths;
        try { paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllBytes(path)) ?? throw new JsonException(); }
        catch (JsonException error) { throw new InvalidDataException("Повреждён список повторно используемых файлов.", error); }
        var known = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count > UpdatePackage.MaximumFiles || paths.Any(item => item is null || !known.Contains(item) || !reused.Add(item)))
            throw new InvalidDataException("Небезопасный список повторно используемых файлов.");
        return reused;
    }

    internal static void VerifyPayload(string payload, ReleaseManifest manifest, string installDirectory, string executableName)
    {
        var reused = ReadReused(payload, manifest);
        foreach (var file in manifest.Files)
        {
            var path = reused.Contains(file.Path) ? InstalledPath(installDirectory, file.Path, executableName) : UpdatePackage.ResolveFile(payload, file.Path);
            if (!Matches(path, file)) throw new InvalidDataException("Файл изменился после подготовки обновления: " + file.Path);
        }
    }
}
