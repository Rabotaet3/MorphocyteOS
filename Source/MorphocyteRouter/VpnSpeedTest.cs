using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace MorphocyteRouter;

internal sealed record SpeedTestResult(double DownloadBytesPerSecond, long DownloadedBytes);

// A bounded, user-initiated download. No profile data, cookies or analytics are sent.
internal sealed class VpnSpeedTest : IDisposable
{
    internal const int BytesPerStream = 16_000_000;
    internal const int Streams = 4;
    internal const int MaxBytes = BytesPerStream * Streams;
    private readonly HttpClient _client;

    internal VpnSpeedTest(int port, string password, HttpMessageHandler? handler = null, bool socksProxy = false)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Нет локального подключения для замера.");
        _client = new HttpClient(handler ?? new SocketsHttpHandler {
            UseProxy = true,
            Proxy = new WebProxy($"{(socksProxy ? "socks5" : "http")}://127.0.0.1:{port}") { Credentials = new NetworkCredential("morphocyte", password) },
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = Streams, ConnectTimeout = TimeSpan.FromSeconds(8)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<SpeedTestResult> MeasureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        long total = 0;
        double capacity = 0;
        Exception? failure = null;
        async Task Download(int index, int requestedBytes)
        {
            try
            {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://speed.cloudflare.com/__down?bytes={requestedBytes}&run={Guid.NewGuid():N}&stream={index}");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/json")
                throw new InvalidDataException("Сервис замера вернул страницу ошибки вместо тестовых данных.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            var received = 0;
            while (received < requestedBytes)
            {
                var bytes = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, requestedBytes - received)), deadline.Token).ConfigureAwait(false);
                if (bytes == 0)
                {
                    if (received < 262144) throw new IOException("Загрузка тестовых данных прервалась.");
                    break;
                }
                received += bytes;
                Interlocked.Add(ref total, bytes);
            }
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            { Interlocked.CompareExchange(ref failure, error, null); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !deadline.IsCancellationRequested)
            { Interlocked.CompareExchange(ref failure, new TimeoutException(), null); }
        }
        async Task Stage(int bytes)
        {
            var before = Interlocked.Read(ref total);
            var elapsed = Stopwatch.StartNew();
            try { await Task.WhenAll(Enumerable.Range(0, Streams).Select(index => Download(index, bytes))).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested) { }
            token.ThrowIfCancellationRequested();
            elapsed.Stop();
            var received = Interlocked.Read(ref total) - before;
            if (received >= 262144 && elapsed.Elapsed.TotalSeconds >= .01)
                capacity = Math.Max(capacity, received / elapsed.Elapsed.TotalSeconds);
        }
        // Warm up TLS / the VPN with small transfers. A later transient failure
        // must not discard a valid completed measurement from the first stage.
        await Stage(2_000_000).ConfigureAwait(false);
        if (total == 0 && failure is not null) throw failure;
        if (!deadline.IsCancellationRequested) await Stage(BytesPerStream - 2_000_000).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (capacity <= 0)
        {
            if (failure is not null) throw failure;
            if (deadline.IsCancellationRequested) throw new TimeoutException();
            throw new IOException("Недостаточно данных для замера скорости.");
        }
        return new SpeedTestResult(capacity, total);
    }

    internal static string FailureMessage(Exception error) => error switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } => "Ядро не открыло канал замера. Перезапусти VPN и повтори попытку.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "Сервис замера ограничил запросы с этого VPN. Попробуй позже или выбери другой сервер.",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "Не удалось установить защищённое соединение с сервисом замера.",
        HttpRequestException => "Сервис замера недоступен через выбранный VPN. Попробуй позже или другой сервер.",
        InvalidDataException => "Сервис замера вернул некорректные тестовые данные. Повтори позже.",
        TimeoutException or OperationCanceledException => "Сервис замера не ответил вовремя. Повтори позже или выбери другой VPN-сервер.",
        _ => "Слишком мало тестовых данных. Проверь VPN и повтори замер."
    };

    internal static string FormatLoad(long? download, double? capacity) => download is >= 0 && capacity is > 0 && double.IsFinite(capacity.Value)
        ? $"{Math.Clamp(download.Value * 100d / capacity.Value, 0, 100):0}%" : "—";

    public void Dispose() => _client.Dispose();
}
