using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace MorphocyteRouter;

internal sealed record SpeedTestResult(double DownloadBytesPerSecond, long DownloadedBytes);

// A bounded, user-initiated download. No profile data, cookies or analytics are sent.
internal sealed class VpnSpeedTest : IDisposable
{
    internal const int Streams = 4;
    internal const int MaxBytes = 64_000_000;
    internal const int WarmupBytes = 262_144;
    internal const int RequestBytes = 8_000_000;
    private readonly HttpClient _client;

    internal VpnSpeedTest(int port, string password, HttpMessageHandler? handler = null, bool socksProxy = false)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Нет локального подключения для замера.");
        _client = new HttpClient(handler ?? new SocketsHttpHandler {
            UseProxy = true,
            Proxy = new WebProxy($"{(socksProxy ? "socks5" : "http")}://127.0.0.1:{port}") { Credentials = new NetworkCredential("morphocyte", password) },
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = Streams, ConnectTimeout = TimeSpan.FromSeconds(8), MaxResponseDrainSize = 0
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<SpeedTestResult> MeasureAsync(CancellationToken token, IProgress<int>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        long total = 0;
        Exception? failure = null;
        var samples = new DownloadSamples();
        var clock = Stopwatch.StartNew();
        using var reporting = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        async Task ReportProgressAsync()
        {
            try
            {
                while (true)
                {
                    progress!.Report(ProgressPercent(Interlocked.Read(ref total), clock.Elapsed));
                    await Task.Delay(100, reporting.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (reporting.IsCancellationRequested) { }
        }
        var progressTask = progress is null ? Task.CompletedTask : ReportProgressAsync();
        try
        {
        async Task<bool> Download(int index, int requestedBytes, bool measure, CancellationToken transferToken)
        {
            try
            {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://speed.cloudflare.com/__down?bytes={requestedBytes}&run={Guid.NewGuid():N}&stream={index}");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transferToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/json")
                throw new InvalidDataException("Сервис замера вернул страницу ошибки вместо тестовых данных.");
            await using var stream = await response.Content.ReadAsStreamAsync(transferToken).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            var received = 0;
            while (received < requestedBytes)
            {
                var bytes = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, requestedBytes - received)), transferToken).ConfigureAwait(false);
                if (bytes == 0)
                {
                    if (received < 262144) throw new IOException("Загрузка тестовых данных прервалась.");
                    break;
                }
                received += bytes;
                Interlocked.Add(ref total, bytes);
                if (measure) samples.Record(bytes, clock.Elapsed.TotalSeconds);
            }
            return true;
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            { Interlocked.CompareExchange(ref failure, error, null); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && transferToken.IsCancellationRequested) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { Interlocked.CompareExchange(ref failure, new TimeoutException(), null); }
            return false;
        }
        // Warm up each connection, but never report warm-up bytes as a calibration.
        // One stalled warm-up cannot consume the entire measurement deadline.
        using (var warmup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
        {
            warmup.CancelAfter(TimeSpan.FromSeconds(3));
            await Task.WhenAll(Enumerable.Range(0, Streams).Select(index => Download(index, WarmupBytes, false, warmup.Token))).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        if (total == 0 && failure is not null) throw failure;
        var remaining = MaxBytes - (int)Interlocked.Read(ref total);
        async Task Worker(int index)
        {
            while (!deadline.IsCancellationRequested)
            {
                // Faster workers take another chunk instead of waiting for the
                // slowest fixed-size stream. Allocated requests never exceed the cap.
                var grant = Math.Min(RequestBytes, Interlocked.Add(ref remaining, -RequestBytes) + RequestBytes);
                if (grant <= 0) break;
                if (!await Download(index, grant, true, deadline.Token).ConfigureAwait(false)) break;
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Streams).Select(Worker)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var capacity = samples.ReadRate();
        if (capacity <= 0)
        {
            if (failure is not null) throw failure;
            if (deadline.IsCancellationRequested) throw new TimeoutException();
            throw new IOException("Недостаточно данных для замера скорости.");
        }
        reporting.Cancel();
        await progressTask.ConfigureAwait(false);
        progress?.Report(100);
        return new SpeedTestResult(capacity, total);
        }
        finally { reporting.Cancel(); await progressTask.ConfigureAwait(false); }
    }

    internal static int ProgressPercent(long bytes, TimeSpan elapsed) => Math.Clamp((int)Math.Floor(
        Math.Max(Math.Clamp((double)bytes / MaxBytes, 0, 1), Math.Clamp(elapsed.TotalSeconds / 15, 0, 1)) * 100), 0, 99);

    // Aggregate all concurrent transfers on one clock. Fixed-size bins bound
    // memory even if a stream returns tiny reads. One-second windows suppress
    // packet/buffer spikes and exclude connection setup and the slow final tail.
    internal sealed class DownloadSamples
    {
        private const double BinSeconds = .1;
        private const int WindowBins = 10;
        private readonly object _gate = new();
        private readonly long[] _bins = new long[152];
        private double? _start;
        private double _last;
        private long _bytes;

        internal void Record(int bytes, double seconds)
        {
            if (bytes <= 0 || !double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException();
            lock (_gate)
            {
                if (_start is null) { _start = seconds; _last = seconds; return; }
                var elapsed = Math.Max(0, seconds - _start.Value);
                var bin = (int)Math.Floor(elapsed / BinSeconds);
                if (bin >= _bins.Length) return;
                _bins[bin] += bytes;
                _bytes += bytes;
                _last = Math.Max(_last, seconds);
            }
        }

        internal double ReadRate()
        {
            lock (_gate)
            {
                if (_start is null || _bytes < 262_144) return 0;
                var duration = _last - _start.Value;
                if (duration < .25)
                    return duration >= .001 && _bytes >= 16_000_000 ? _bytes / duration : 0;
                if (duration < 1) return _bytes / duration;
                var completeBins = Math.Min((int)Math.Floor(duration / BinSeconds), _bins.Length);
                long rolling = 0, best = 0;
                for (var index = 0; index < completeBins; index++)
                {
                    rolling += _bins[index];
                    if (index >= WindowBins) rolling -= _bins[index - WindowBins];
                    if (index >= WindowBins - 1) best = Math.Max(best, rolling);
                }
                return best / (WindowBins * BinSeconds);
            }
        }
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

    // VPN channel capacity is a measured bit rate, not a percentage of live traffic.
    internal static string FormatCapacity(double? bytesPerSecond) => bytesPerSecond switch
    {
        null => "—",
        var value when value <= 0 || !double.IsFinite(value.Value) => "—",
        < 125_000 => $"{bytesPerSecond / 125d:0.0} кбит/с",
        < 125_000_000 => $"{bytesPerSecond / 125_000d:0.0} Мбит/с",
        _ => $"{bytesPerSecond / 125_000_000d:0.0} Гбит/с"
    };

    public void Dispose() => _client.Dispose();
}
