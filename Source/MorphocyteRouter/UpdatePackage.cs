using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MorphocyteRouter;

internal sealed class ReleaseManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public List<ReleaseFile> Files { get; set; } = new();
}

internal sealed class ReleaseFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal static class UpdatePackage
{
    internal const string ManifestName = "release-manifest.json";
    internal const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    internal const int MaximumFiles = 4096;
    private static readonly HashSet<string> RootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "MorphocyteOS.exe", "mihomo.exe", "release-source.json", "README.md", "LICENSE.txt", "THIRD-PARTY-NOTICES.md",
        "MorphocyteOS.dll", "MorphocyteOS.deps.json", "MorphocyteOS.runtimeconfig.json", "YamlDotNet.dll"
    };

    internal static bool IsManagedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') || path.Length > 240) return false;
        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.') || segment.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            Regex.IsMatch(segment, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase))) return false;
        var file = segments[^1];
        if (file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || file.Contains(".bak", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("settings.json", StringComparison.OrdinalIgnoreCase) || file.Equals("portable.flag", StringComparison.OrdinalIgnoreCase)) return false;
        return RootFiles.Contains(path) || path.StartsWith("Licenses/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("ThirdPartySource/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveFile(string directory, string relative)
    {
        if (relative != ManifestName && !IsManagedPath(relative)) throw new InvalidDataException("Недопустимый путь файла обновления.");
        var root = System.IO.Path.GetFullPath(directory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл обновления выходит за пределы папки приложения.");
        for (var check = full; check is not null; check = System.IO.Path.GetDirectoryName(check))
        {
            if ((File.Exists(check) || Directory.Exists(check)) &&
                (File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Обновление через символьные ссылки или точки подключения запрещено.");
            if (check.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return full;
    }

    internal static ReleaseManifest ParseManifest(byte[] bytes, bool requireCore = true)
    {
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Слишком большой список файлов обновления.");
        ReleaseManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ReleaseManifest>(bytes) ?? throw new JsonException(); }
        catch (JsonException error) { throw new InvalidDataException("Повреждён список файлов обновления.", error); }
        if (manifest.SchemaVersion != 1 || !ReleaseUpdateService.SemanticVersion.TryParse(manifest.Version, out _) ||
            manifest.Files is null || manifest.Files.Count is < 1 or > MaximumFiles)
            throw new InvalidDataException("Неподдерживаемый список файлов обновления.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || !IsManagedPath(file.Path) || !paths.Add(file.Path) || file.Size < 0 ||
                file.Size > MaximumExpandedBytes || !Regex.IsMatch(file.Sha256 ?? "", @"\A[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("Небезопасная запись в списке файлов обновления.");
            total += file.Size;
            if (total > MaximumExpandedBytes) throw new InvalidDataException("Обновление превышает допустимый размер.");
        }
        if (!paths.Contains("MorphocyteOS.exe") || (requireCore && !paths.Contains("mihomo.exe")))
            throw new InvalidDataException("В обновлении отсутствует приложение или комплектное ядро.");
        return manifest;
    }

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static void VerifyFiles(string directory, ReleaseManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            var path = ResolveFile(directory, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || !Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма файла обновления не совпадает: " + file.Path);
        }
    }

    internal static ReleaseManifest Extract(string archivePath, string destination, string expectedVersion, CancellationToken token)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Папка подготовки обновления должна быть пустой.");
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumFiles * 2) throw new InvalidDataException("Слишком много файлов в архиве.");
        var candidates = archive.Entries.Where(entry => entry.FullName.EndsWith('/' + ManifestName, StringComparison.Ordinal) ||
            entry.FullName == ManifestName).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("В архиве отсутствует однозначный список файлов обновления.");
        var manifestEntry = candidates[0];
        if (manifestEntry.Length > 2 * 1024 * 1024) throw new InvalidDataException("Слишком большой список файлов.");
        var prefix = manifestEntry.FullName[..^ManifestName.Length];
        if (prefix.Length > 0 && (prefix.TrimEnd('/').Contains('/') || prefix.Contains('\\') || prefix.Contains("..")))
            throw new InvalidDataException("Неподдерживаемая структура архива.");
        using var memory = new MemoryStream();
        using (var source = manifestEntry.Open()) CopyBounded(source, memory, manifestEntry.Length, token);
        var manifest = ParseManifest(memory.ToArray());
        if (!manifest.Version.Equals(expectedVersion, StringComparison.Ordinal)) throw new InvalidDataException("Версия архива не совпадает с релизом.");
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("В архиве обнаружена небезопасная запись.");
            var relative = entry.FullName[prefix.Length..];
            if (relative == ManifestName) continue;
            if (!expected.TryGetValue(relative, out var file) || file.Size != entry.Length || !entries.TryAdd(relative, entry))
                throw new InvalidDataException("Содержимое архива отличается от списка файлов: " + relative);
        }
        if (entries.Count != expected.Count) throw new InvalidDataException("Обновление содержит не все необходимые файлы.");
        Directory.CreateDirectory(destination);
        foreach (var pair in entries)
        {
            token.ThrowIfCancellationRequested();
            var target = ResolveFile(destination, pair.Key);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using var input = pair.Value.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBounded(input, output, expected[pair.Key].Size, token);
            if (output.Length != expected[pair.Key].Size) throw new InvalidDataException("Размер извлечённого файла не совпадает.");
        }
        File.WriteAllBytes(ResolveFile(destination, ManifestName), memory.ToArray());
        VerifyFiles(destination, manifest);
        return manifest;
    }

    private static void CopyBounded(Stream input, Stream output, long expected, CancellationToken token)
    {
        var buffer = new byte[81920];
        long count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            count += read;
            if (count > expected) throw new InvalidDataException("Файл превышает заявленный размер.");
            output.Write(buffer, 0, read);
        }
        if (count != expected) throw new InvalidDataException("Файл извлечён не полностью.");
    }
}
