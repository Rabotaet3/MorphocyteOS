using System.IO;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MorphocyteRouter;

internal record UpdateAsset(string Name, string Url, string Sha256, long Size);
internal record UpdateCheckResult(bool IsConfigured, bool UpdateAvailable, string Message,
    string? ReleaseUrl = null, string? LatestVersion = null, UpdateAsset? Asset = null, string? Repository = null);

/// <summary>Checks configured public GitHub releases. Never downloads or executes binaries.</summary>
internal static class ReleaseUpdateService
{
    internal const int MaximumResponseBytes = 256 * 1024;
    private const int MaximumSourceBytes = 16 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private static readonly Regex RepositoryPattern = new(
        @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant);

    public static string SourceStatus => ReadSource().Message;

    public static Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var source = ReadSource();
        return source.Repository is null
            ? Task.FromResult(new UpdateCheckResult(false, false, source.Message))
            : CheckAsync(Client, source.Repository, BuildInfo.Version, cancellationToken);
    }

    // Dependency-injected overload keeps networking and timeout behavior testable without external requests.
    internal static async Task<UpdateCheckResult> CheckAsync(HttpClient client, string? repository,
        string currentVersion, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        if (!IsValidRepository(repository))
            return new(false, false, "Источник обновлений пока не настроен. Проверка версии временно недоступна.");
        if (!SemanticVersion.TryParse(currentVersion, out var current))
            return new(true, false, "Не удалось определить версию этой сборки. Проверка обновлений недоступна.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? CheckTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri($"https://api.github.com/repos/{repository}/releases/latest"));
            request.Headers.UserAgent.ParseAdd("MorphocyteOS-ReleaseCheck/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(true, false, "Публичный стабильный релиз пока не найден. Он ещё не опубликован или репозиторий недоступен.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new(true, false, "GitHub временно ограничил проверку релизов. Попробуй позже.");
            if (!response.IsSuccessStatusCode)
                return new(true, false, $"Сервис релизов не ответил успешно (HTTP {(int)response.StatusCode}). Попробуй позже.");

            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return InvalidResponse();
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(chunk.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > MaximumResponseBytes) return InvalidResponse();
                buffer.Write(chunk, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !IsFalse(root, "draft") || !IsFalse(root, "prerelease")
                || !TryGetString(root, "tag_name", out var tag) || !SemanticVersion.TryParse(tag, out var latest)
                || latest!.IsPrerelease || !TryGetString(root, "html_url", out var releaseUrl)
                || !IsTrustedReleaseUrl(releaseUrl!, repository!))
                return InvalidResponse();

            var version = tag!.TrimStart('v', 'V');
            if (latest.CompareTo(current) > 0)
            {
                var asset = FindAsset(root, repository!, tag!, version);
                return new(true, true, asset is null
                    ? $"Доступна версия {version}, но проверенный архив для Windows x64 пока недоступен."
                    : $"Доступна версия {version}. Нажми «Обновить», чтобы установить её и перезапустить приложение.",
                    releaseUrl, version, asset, repository);
            }
            return new(true, false, latest.CompareTo(current) == 0
                ? $"Установлена актуальная сборка ({currentVersion}). Последний стабильный релиз: {version}."
                : $"Эта сборка ({currentVersion}) новее опубликованного релиза {version}. Обновление не требуется.", releaseUrl, version);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(true, false, "Проверка заняла слишком много времени. Проверь подключение и попробуй позже.");
        }
        catch (HttpRequestException)
        {
            return new(true, false, "Не удалось подключиться к сервису релизов. Проверь интернет или попробуй позже.");
        }
        catch (IOException)
        {
            return new(true, false, "Соединение с сервисом релизов прервалось. Попробуй позже.");
        }
        catch (JsonException) { return InvalidResponse(); }
    }

    private static UpdateCheckResult InvalidResponse() => new(true, false,
        "Сервис вернул непонятные данные о релизе. Проверить версию пока не удалось.");

    private static UpdateAsset? FindAsset(JsonElement root, string repository, string tag, string version)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var expected = $"MorphocyteOS-{version}-win-x64.zip";
        UpdateAsset? found = null;
        foreach (var item in assets.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryGetString(item, "name", out var name) || name != expected) continue;
            if (found is not null || !TryGetString(item, "browser_download_url", out var url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.AbsolutePath != $"/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(expected)}" ||
                !TryGetString(item, "digest", out var digest) || !Regex.IsMatch(digest!, @"\Asha256:[a-fA-F0-9]{64}\z") ||
                !item.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes <= 0 || bytes > 512L * 1024 * 1024)
                return null;
            found = new UpdateAsset(expected, url!, digest![7..].ToLowerInvariant(), bytes);
        }
        return found;
    }

    private static (string? Repository, string Message) ReadSource()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "release-source.json");
        try
        {
            if (!File.Exists(path)) return MissingSource();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumSourceBytes)
                return (null, "Файл настроек обновлений повреждён. Проверка версии недоступна.");
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) != 0)
            {
                if (buffer.Length + count > MaximumSourceBytes)
                    return (null, "Файл настроек обновлений повреждён. Проверка версии недоступна.");
                buffer.Write(chunk, 0, count);
            }
            return ParseSource(buffer.ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, "Не удалось прочитать настройки обновлений. Проверка версии недоступна.");
        }
    }

    internal static (string? Repository, string Message) ParseSource(byte[] bytes)
    {
        if (bytes.Length > MaximumSourceBytes)
            return (null, "Файл настроек обновлений повреждён. Проверка версии недоступна.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "Неверный формат настроек обновлений. Проверка версии недоступна.");
            if (!document.RootElement.TryGetProperty("githubRepository", out var property)
                || property.ValueKind == JsonValueKind.Null) return MissingSource();
            if (property.ValueKind != JsonValueKind.String)
                return (null, "Неверно настроен источник обновлений. Проверка версии недоступна.");
            var repository = property.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(repository)) return MissingSource();
            if (!IsValidRepository(repository))
                return (null, "Неверно настроен источник обновлений. Проверка версии недоступна.");
            return (repository, $"Площадка релизов: GitHub · {repository}");
        }
        catch (JsonException)
        {
            return (null, "Файл настроек обновлений повреждён. Проверка версии недоступна.");
        }
    }

    private static (string?, string) MissingSource() => (null,
        "Источник обновлений пока не настроен. Проверка версии временно недоступна.");

    private static bool IsValidRepository(string? repository) => repository is not null
        && RepositoryPattern.IsMatch(repository)
        && !repository.EndsWith("/.", StringComparison.Ordinal)
        && !repository.EndsWith("/..", StringComparison.Ordinal);

    private static bool IsFalse(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.False;

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 512;
    }

    private static bool IsTrustedReleaseUrl(string text, string repository)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !url.IsDefaultPort
            || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            return false;
        var prefix = $"/{repository}/releases/tag/";
        return url.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && url.AbsolutePath.Length > prefix.Length;
    }

    internal sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        private static readonly Regex Pattern = new(
            @"\A[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
            RegexOptions.CultureInvariant);
        private readonly BigInteger[] _core;
        private readonly string[] _prerelease;
        public bool IsPrerelease => _prerelease.Length > 0;

        private SemanticVersion(BigInteger[] core, string[] prerelease) { _core = core; _prerelease = prerelease; }

        public static bool TryParse(string? text, out SemanticVersion? version)
        {
            version = null;
            if (text is null || text.Length > 128) return false;
            var match = Pattern.Match(text);
            if (!match.Success) return false;
            var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
            if (prerelease.Any(part => part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit))) return false;
            version = new(new[] { BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value),
                BigInteger.Parse(match.Groups[3].Value) }, prerelease);
            return true;
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;
            for (var index = 0; index < _core.Length; index++)
            {
                var compare = _core[index].CompareTo(other._core[index]);
                if (compare != 0) return compare;
            }
            if (!IsPrerelease || !other.IsPrerelease)
                return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
            for (var index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
            {
                var left = _prerelease[index]; var right = other._prerelease[index];
                var leftNumber = left.All(char.IsAsciiDigit); var rightNumber = right.All(char.IsAsciiDigit);
                var compare = leftNumber && rightNumber ? BigInteger.Parse(left).CompareTo(BigInteger.Parse(right))
                    : leftNumber != rightNumber ? leftNumber ? -1 : 1 : string.CompareOrdinal(left, right);
                if (compare != 0) return compare;
            }
            return _prerelease.Length.CompareTo(other._prerelease.Length);
        }
    }
}
