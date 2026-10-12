using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MorphocyteRouter;

internal record UpdateProgress(string Message, double Fraction);
internal record PreparedUpdate(string Directory, ReleaseManifest Manifest);

internal static class UpdateDownloader
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    internal static bool IsDownloadHost(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");

    internal static Task<PreparedUpdate> PrepareAsync(UpdateCheckResult release, string cacheRoot,
        IProgress<UpdateProgress>? progress, CancellationToken token, string? installDirectory = null, string executableName = "MorphocyteOS.exe")
        => PrepareAsync(Client, release, cacheRoot, progress, token, installDirectory, executableName);

    internal static async Task<PreparedUpdate> PrepareAsync(HttpClient client, UpdateCheckResult release, string cacheRoot,
        IProgress<UpdateProgress>? progress, CancellationToken token, string? installDirectory = null, string executableName = "MorphocyteOS.exe")
    {
        if (release.IndexAsset is not null && installDirectory is not null)
            return await PrepareIncrementalAsync(client, release, cacheRoot, installDirectory, executableName, progress, token).ConfigureAwait(false);
        if (!release.UpdateAvailable || release.Asset is not { } asset || release.LatestVersion is null || release.Repository is null)
            throw new InvalidOperationException("Нет проверенного архива обновления.");
        if (!IsDownloadHost(new Uri(asset.Url)) || asset.Size is <= 0 or > 536870912)
            throw new InvalidDataException("Небезопасный источник обновления.");
        var operation = Path.Combine(Path.GetFullPath(cacheRoot), Guid.NewGuid().ToString("N"));
        _ = UpdatePackage.ResolveFile(cacheRoot, UpdatePackage.ManifestName);
        Directory.CreateDirectory(operation);
        var archivePath = Path.Combine(operation, "package.zip");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            var uri = new Uri(asset.Url);
            HttpResponseMessage? response = null;
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!IsDownloadHost(uri)) throw new InvalidDataException("GitHub перенаправил загрузку на недоверенный адрес.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("MorphocyteOS-Updater/1.1");
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    response.Dispose(); response = null;
                    if (location is null || redirect == 5) throw new IOException("Не удалось получить файл релиза GitHub.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                break;
            }
            using (response)
            {
                if (response is null || response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("GitHub не смог отдать архив обновления. Попробуй позже.");
                if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                    throw new InvalidDataException("Размер архива не совпадает с метаданными GitHub.");
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                var lastReport = DateTime.UtcNow;
                progress?.Report(new("Загружаю архив с GitHub…", 0));
                while (true)
                {
                    var count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > asset.Size) throw new InvalidDataException("Архив превышает заявленный размер.");
                    hasher.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 100)
                    {
                        progress?.Report(new($"Загружено {received / 1048576d:0.0} из {asset.Size / 1048576d:0.0} МБ", received / (double)asset.Size * .85));
                        lastReport = DateTime.UtcNow;
                    }
                }
                if (received != asset.Size || !Convert.ToHexString(hasher.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма архива не совпадает. Обновление не будет установлено.");
            }
            progress?.Report(new("Проверяю и распаковываю файлы…", .9));
            var payload = Path.Combine(operation, "payload");
            var manifest = await Task.Run(() => UpdatePackage.Extract(archivePath, payload, release.LatestVersion, deadline.Token), deadline.Token).ConfigureAwait(false);
            var sourceFile = Path.Combine(payload, "release-source.json");
            if (!File.Exists(sourceFile) || ReleaseUpdateService.ParseSource(await File.ReadAllBytesAsync(sourceFile, deadline.Token)).Repository != release.Repository)
                throw new InvalidDataException("Архив относится к другому источнику обновлений.");
            File.Delete(archivePath); // Verified payload replaces the downloaded ZIP; do not keep both copies.
            progress?.Report(new("Обновление проверено и готово к установке", 1));
            return new PreparedUpdate(operation, manifest);
        }
        catch
        {
            // This exact generated directory contains only our uninstalled download.
            try { DeleteOperation(cacheRoot, operation); } catch { /* Preserve the original download error. */ }
            throw;
        }
    }

    private static async Task<PreparedUpdate> PrepareIncrementalAsync(HttpClient client, UpdateCheckResult release, string cacheRoot,
        string installDirectory, string executableName, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        if (!release.UpdateAvailable || release.IndexAsset is not { } indexAsset || release.LatestVersion is null || release.Repository is null ||
            indexAsset.Name != $"MorphocyteOS-{release.LatestVersion}-update.json" || indexAsset.Size is <= 0 or > IncrementalUpdate.MaximumIndexBytes)
            throw new InvalidDataException("Нет проверенного списка обновления.");
        var indexUri = new Uri(indexAsset.Url);
        // Only an index published in this exact repository/release can select components.
        var releasePrefix = $"/{release.Repository}/releases/download/";
        var segments = indexUri.AbsolutePath.Split('/');
        if (indexUri.Scheme != "https" || indexUri.Host != "github.com" || !indexUri.IsDefaultPort ||
            indexUri.UserInfo.Length != 0 || indexUri.Query.Length != 0 || indexUri.Fragment.Length != 0 ||
            !indexUri.AbsolutePath.StartsWith(releasePrefix, StringComparison.Ordinal) || segments.Length != 7 ||
            segments[^1] != Uri.EscapeDataString(indexAsset.Name) ||
            !ReleaseUpdateService.SemanticVersion.TryParse(Uri.UnescapeDataString(segments[^2]), out var tagVersion) ||
            !ReleaseUpdateService.SemanticVersion.TryParse(release.LatestVersion, out var expectedVersion) || tagVersion!.CompareTo(expectedVersion) != 0)
            throw new InvalidDataException("Небезопасный источник списка обновления.");
        _ = UpdatePackage.ResolveFile(cacheRoot, UpdatePackage.ManifestName);
        var operation = Path.Combine(Path.GetFullPath(cacheRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            var indexPath = Path.Combine(operation, "index.json");
            await DownloadAssetAsync(client, indexAsset, indexPath, progress, 0, .03, deadline.Token).ConfigureAwait(false);
            var index = IncrementalUpdate.ParseIndex(await File.ReadAllBytesAsync(indexPath, deadline.Token).ConfigureAwait(false), release.LatestVersion);
            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reused = new List<string>();
            progress?.Report(new("Сравниваю установленные файлы…", .04));
            foreach (var file in index.Manifest.Files)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (IncrementalUpdate.Matches(IncrementalUpdate.InstalledPath(installDirectory, file.Path, executableName), file)) reused.Add(file.Path);
                else needed.Add(file.Path);
            }
            var packages = index.Packages.Where(package => package.Files.Any(needed.Contains)).ToArray();
            var total = packages.Sum(package => package.Size);
            long completed = 0;
            var payload = Path.Combine(operation, "payload");
            Directory.CreateDirectory(payload);
            foreach (var package in packages)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var url = new Uri(indexUri, Uri.EscapeDataString(package.Name)).AbsoluteUri;
                var asset = new UpdateAsset(package.Name, url, package.Sha256, package.Size);
                var archivePath = Path.Combine(operation, "component.zip");
                await DownloadAssetAsync(client, asset, archivePath, progress, .05 + .8 * completed / Math.Max(total, 1),
                    .8 * package.Size / Math.Max(total, 1), deadline.Token).ConfigureAwait(false);
                await Task.Run(() => IncrementalUpdate.ExtractNeeded(archivePath, payload, package, index.Manifest, needed, deadline.Token), deadline.Token).ConfigureAwait(false);
                File.Delete(archivePath); // Keep at most one compressed component on disk.
                completed += package.Size;
            }
            await File.WriteAllBytesAsync(UpdatePackage.ResolveFile(payload, UpdatePackage.ManifestName),
                JsonSerializer.SerializeToUtf8Bytes(index.Manifest), deadline.Token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(payload, IncrementalUpdate.ReusedFilesName),
                JsonSerializer.SerializeToUtf8Bytes(reused), deadline.Token).ConfigureAwait(false);
            IncrementalUpdate.VerifyPayload(payload, index.Manifest, installDirectory, executableName);
            var sourcePath = reused.Contains("release-source.json", StringComparer.OrdinalIgnoreCase)
                ? UpdatePackage.ResolveFile(installDirectory, "release-source.json") : UpdatePackage.ResolveFile(payload, "release-source.json");
            if (!File.Exists(sourcePath) || ReleaseUpdateService.ParseSource(await File.ReadAllBytesAsync(sourcePath, deadline.Token).ConfigureAwait(false)).Repository != release.Repository)
                throw new InvalidDataException("Пакеты относятся к другому источнику обновлений.");
            File.Delete(indexPath);
            progress?.Report(new($"Готово: обновление проверено, загружено {total / 1048576d:0.0} МБ", 1));
            return new PreparedUpdate(operation, index.Manifest);
        }
        catch
        {
            // Never fall back to a huge download after a corrupt index/component or cancellation.
            try { DeleteOperation(cacheRoot, operation); } catch { }
            throw;
        }
    }

    private static async Task DownloadAssetAsync(HttpClient client, UpdateAsset asset, string destination,
        IProgress<UpdateProgress>? progress, double start, double span, CancellationToken token)
    {
        var uri = new Uri(asset.Url);
        if (!IsDownloadHost(uri) || asset.Size is <= 0 or > 536870912 ||
            !System.Text.RegularExpressions.Regex.IsMatch(asset.Sha256, @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("Небезопасный файл обновления.");
        HttpResponseMessage? response = null;
        try
        {
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                token.ThrowIfCancellationRequested();
                if (!IsDownloadHost(uri)) throw new InvalidDataException("GitHub перенаправил загрузку на недоверенный адрес.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("MorphocyteOS-Updater/2.0");
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if ((int)response.StatusCode is < 300 or >= 400) break;
                var location = response.Headers.Location;
                response.Dispose(); response = null;
                if (location is null || redirect == 5) throw new IOException("Не удалось получить файл релиза GitHub.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            }
            if (response is null || response.StatusCode != HttpStatusCode.OK) throw new IOException("Не удалось загрузить пакет обновления. Попробуй позже.");
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new InvalidDataException("Размер пакета не совпадает со списком обновления.");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long received = 0;
            var reported = DateTime.UtcNow;
            progress?.Report(new("Загружаю изменившиеся компоненты…", start));
            while (true)
            {
                var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                received += count;
                if (received > asset.Size) throw new InvalidDataException("Пакет превышает заявленный размер.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                if ((DateTime.UtcNow - reported).TotalMilliseconds < 100) continue;
                progress?.Report(new($"Пакет: {received / 1048576d:0.0} из {asset.Size / 1048576d:0.0} МБ", start + span * received / asset.Size));
                reported = DateTime.UtcNow;
            }
            if (received != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма пакета не совпадает. Обновление отменено.");
        }
        finally { response?.Dispose(); }
    }

    internal static void DeleteOperation(string cacheRoot, string operation)
    {
        var root = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(operation);
        if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), @"\A[a-f0-9]{32}\z"))
            throw new InvalidDataException("Недопустимая папка подготовки обновления.");
        if (!Directory.Exists(full)) return;
        _ = UpdatePackage.ResolveFile(root, UpdatePackage.ManifestName);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Недопустимая ссылка в папке обновления.");
        // Do not follow any junction introduced after preparation.
        var pending = new Stack<string>(); pending.Push(full);
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Недопустимая ссылка в папке обновления.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        Directory.Delete(full, true);
    }
}
