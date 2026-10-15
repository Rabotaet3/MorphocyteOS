using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace MorphocyteRouter;

internal record ConnectionActivity(string Process, string Destination, string Network, string Rule, string Route,
    string Id = "", long Upload = 0, long Download = 0);
internal record CoreSnapshot(string Version, IReadOnlyList<ConnectionActivity> Connections, string Selection);
internal record TrafficSample(long Upload, long Download);

internal sealed class CoreDiagnostics : IDisposable
{
    private static readonly string[] ProbeUrls = ["https://www.gstatic.com/generate_204", "https://cp.cloudflare.com/generate_204"];
    private readonly HttpClient _client;
    internal int Port { get; }
    internal string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    internal CoreDiagnostics(HttpMessageHandler? handler = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _client = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = TimeSpan.FromSeconds(10)
        };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
    }

    internal async Task<string> ReadVersionAsync(CancellationToken token)
    {
        using var version = await ReadJsonAsync("version", token);
        var value = Text(version.RootElement, "version");
        if (value.Length == 0) throw new InvalidDataException("Ядро не сообщило версию.");
        return value;
    }

    internal async Task<bool> CheckDnsAsync(CancellationToken token)
    {
        using var response = await ReadJsonAsync("dns/query?name=www.gstatic.com&type=A", token, allowDnsDisabled: true);
        var root = response.RootElement;
        if (Text(root, "message") == "DNS section is disabled") return false;
        if (root.TryGetProperty("Status", out var status) && status.TryGetInt32(out var code) && code == 0
            && root.TryGetProperty("Answer", out var answers) && answers.ValueKind == JsonValueKind.Array
            && answers.EnumerateArray().Any(answer => answer.TryGetProperty("type", out var type)
                && type.TryGetInt32(out var kind) && kind is 1 or 28)) return true;
        throw new IOException("DNS ядра не разрешил проверочное имя.");
    }
    internal async Task<CoreSnapshot> ReadAsync(string? group, CancellationToken token)
    {
        using var version = await ReadJsonAsync("version", token);
        var connections = await ReadConnectionsAsync(token);
        var selection = string.IsNullOrWhiteSpace(group) ? "" : await ReadSelectionAsync(group, token);
        return new CoreSnapshot(Text(version.RootElement, "version"), connections.Take(500).ToArray(), selection);
    }

    internal async Task<IReadOnlyList<ConnectionActivity>> ReadConnectionsAsync(CancellationToken token)
    {
        using var json = await ReadJsonAsync("connections", token, maxBytes: 8_388_608);
        return ParseConnections(json.RootElement);
    }

    internal async Task<string> ReadSelectionAsync(string route, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 12; depth++)
        {
            if (route is "DIRECT" or "REJECT") return route;
            if (!visited.Add(route)) throw new InvalidDataException("Циклический выбор VPN-групп.");
            using var proxy = await ReadJsonAsync("proxies/" + Uri.EscapeDataString(route), token);
            var kind = Text(proxy.RootElement, "type");
            if (kind.Equals("Direct", StringComparison.OrdinalIgnoreCase)) return "DIRECT";
            if (kind.Equals("Reject", StringComparison.OrdinalIgnoreCase)) return "REJECT";
            var selected = Text(proxy.RootElement, "now");
            if (selected.Length == 0) return route;
            route = selected;
        }
        throw new InvalidDataException("Слишком много вложенных VPN-групп.");
    }

    // Restore only selectors on a path below the main VPN group. Never apply a
    // remembered leaf to an unrelated group that happens to contain that server.
    internal async Task<bool> RestoreSelectionAsync(string group, string selection, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task<List<(string Group, string Choice)>?> Find(string route, int depth)
        {
            if (route == selection) return [];
            if (route is "DIRECT" or "REJECT" || depth >= 12 || visited.Count >= 512 || !visited.Add(route)) return null;
            using var proxy = await ReadJsonAsync("proxies/" + Uri.EscapeDataString(route), token);
            if (!Text(proxy.RootElement, "type").Equals("Selector", StringComparison.OrdinalIgnoreCase)
                || !proxy.RootElement.TryGetProperty("all", out var all) || all.ValueKind != JsonValueKind.Array) return null;
            current[route] = Text(proxy.RootElement, "now");
            var members = all.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).Take(512).ToArray();
            if (members.Contains(selection)) return [(route, selection)];
            foreach (var member in members.OrderByDescending(member => member == current[route]))
                if (await Find(member, depth + 1) is { } path) { path.Add((route, member)); return path; }
            return null;
        }
        if (await Find(group, 0) is not { } selections) return false;
        foreach (var (route, choice) in selections)
        {
            if (current.GetValueOrDefault(route) == choice) continue;
            using var request = new HttpRequestMessage(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(route)) {
                Content = new StringContent(JsonSerializer.Serialize(new { name = choice }), System.Text.Encoding.UTF8, "application/json")
            };
            using var response = await _client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }
        return true;
    }

    internal async Task<int> CheckProxyAsync(string route, CancellationToken token)
    {
        foreach (var url in ProbeUrls)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var json = await ReadJsonAsync("proxies/" + Uri.EscapeDataString(route) + "/delay?timeout=4000&url=" +
                    Uri.EscapeDataString(url), deadline.Token, maxBytes: 32768);
                var delay = json.RootElement.GetProperty("delay").GetInt32();
                if (delay is < 0 or > 120000) throw new InvalidDataException("Некорректная задержка ответа ядра.");
                return delay;
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or OperationCanceledException) { }
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("Проверочные HTTPS-запросы через VPN не прошли. Проверь сервер и журнал.");
    }

    // A manual OS-path probe: no local proxy, controller secret, cookies, redirects or
    // relaxed certificate checks. A success confirms HTTPS reachability, not that every
    // application is routed through TUN (explicit DIRECT rules may still apply).
    internal static async Task CheckSystemHttpsAsync(CancellationToken token, HttpMessageHandler? handler = null)
    {
        using var client = new HttpClient(handler ?? new SocketsHttpHandler {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(4)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var url in ProbeUrls)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode == HttpStatusCode.NoContent) return;
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or OperationCanceledException) { }
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("Системное HTTPS-подключение недоступно.");
    }

    internal async Task<TrafficSample> ReadTrafficAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var response = await _client.GetAsync("traffic", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var frame = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var length = await stream.ReadAsync(buffer, deadline.Token);
            if (length == 0) throw new IOException("Поток трафика завершился без показателей.");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, length);
            var take = newline < 0 ? length : newline;
            if (frame.Length + take > 32768) throw new IOException("Слишком большой ответ трафика.");
            frame.Write(buffer, 0, take);
            if (newline < 0) continue;
            using var json = JsonDocument.Parse(frame.ToArray());
            if (!json.RootElement.TryGetProperty("up", out var up) || !up.TryGetInt64(out var upload) || upload < 0 ||
                !json.RootElement.TryGetProperty("down", out var down) || !down.TryGetInt64(out var download) || download < 0)
                throw new InvalidDataException("Некорректные показатели трафика.");
            return new TrafficSample(upload, download);
        }
    }

    private async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken token, bool allowDnsDisabled = false, int maxBytes = 2_097_152)
    {
        using var response = await _client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        if (!(allowDnsDisabled && response.StatusCode == HttpStatusCode.InternalServerError)) response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("Слишком большой ответ ядра.");
        using var memory = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > maxBytes) throw new IOException("Слишком большой ответ ядра.");
            memory.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    internal static IReadOnlyList<ConnectionActivity> ParseConnections(JsonElement root)
    {
        if (!root.TryGetProperty("connections", out var array))
            throw new InvalidDataException("Ядро не вернуло список соединений.");
        // Mihomo serializes an empty Go slice as null until the first connection.
        if (array.ValueKind == JsonValueKind.Null) return Array.Empty<ConnectionActivity>();
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Некорректный список соединений ядра.");
        var output = new List<ConnectionActivity>();
        foreach (var connection in array.EnumerateArray().Take(10000))
        {
            if (!connection.TryGetProperty("metadata", out var metadata)) continue;
            var destination = Text(metadata, "host");
            if (destination.Length == 0) destination = Text(metadata, "destinationIP");
            var chains = connection.TryGetProperty("chains", out var chain) && chain.ValueKind == JsonValueKind.Array
                ? string.Join(" ← ", chain.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())) : "";
            var process = Text(metadata, "process");
            if (process.Length == 0) process = Path.GetFileName(Text(metadata, "processPath"));
            output.Add(new ConnectionActivity(process, destination + ":" + Text(metadata, "destinationPort"),
                Text(metadata, "network").ToUpperInvariant(), Text(connection, "rule") + " " + Text(connection, "rulePayload"), chains,
                Text(connection, "id"), Bytes(connection, "upload"), Bytes(connection, "download")));
        }
        return output;
    }

    private static long Bytes(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var number) ? Math.Max(0, number) : 0;

    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
        ? field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : field.ValueKind == JsonValueKind.Number ? field.ToString() : "" : "";

    public void Dispose() => _client.Dispose();
}
