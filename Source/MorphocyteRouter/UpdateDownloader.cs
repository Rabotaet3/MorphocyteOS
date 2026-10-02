using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

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
        IProgress<UpdateProgress>? progress, CancellationToken token) => PrepareAsync(Client, release, cacheRoot, progress, token);

    internal static async Task<PreparedUpdate> PrepareAsync(HttpClient client, UpdateCheckResult release, string cacheRoot,
        IProgress<UpdateProgress>? progress, CancellationToken token)
    {
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
