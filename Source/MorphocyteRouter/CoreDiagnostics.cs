using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace MorphocyteRouter;

internal record ConnectionActivity(string Process, string Destination, string Network, string Rule, string Route);
internal record CoreSnapshot(string Version, IReadOnlyList<ConnectionActivity> Connections, string Selection);
internal record TrafficSample(long Upload, long Download);

internal sealed class CoreDiagnostics : IDisposable
{
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

    internal async Task<CoreSnapshot> ReadAsync(string? group, CancellationToken token)
    {
        using var version = await ReadJsonAsync("version", token);
        using var connections = await ReadJsonAsync("connections", token);
        var selection = string.IsNullOrWhiteSpace(group) ? "" : await ReadSelectionAsync(group, token);
        return new CoreSnapshot(Text(version.RootElement, "version"), ParseConnections(connections.RootElement), selection);
    }

    internal async Task<string> ReadSelectionAsync(string route, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 12; depth++)
        {
            if (route is "DIRECT" or "REJECT") return route;
            if (!visited.Add(route)) throw new InvalidDataException("Циклический выбор VPN-групп.");
            using var proxy = await ReadJsonAsync("proxies/" + Uri.EscapeDataString(route), token);
            var selected = Text(proxy.RootElement, "now");
            if (selected.Length == 0) return route;
            route = selected;
        }
        throw new InvalidDataException("Слишком много вложенных VPN-групп.");
    }

    internal async Task<int> CheckProxyAsync(string route, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await _client.GetAsync("proxies/" + Uri.EscapeDataString(route) + "/delay?timeout=8000&url=" +
            Uri.EscapeDataString("https://www.gstatic.com/generate_204"), deadline.Token);
        if (!response.IsSuccessStatusCode) throw new IOException("Проверочный HTTPS-запрос через VPN не прошёл. Проверь сервер и журнал.");
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        if (bytes.Length > 32768) throw new IOException("Некорректный ответ проверки.");
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.GetProperty("delay").GetInt32();
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

    private async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken token)
    {
        using var response = await _client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_097_152) throw new IOException("Слишком большой ответ ядра.");
        using var memory = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > 2_097_152) throw new IOException("Слишком большой ответ ядра.");
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
        foreach (var connection in array.EnumerateArray().Take(500))
        {
            if (!connection.TryGetProperty("metadata", out var metadata)) continue;
            var destination = Text(metadata, "host");
            if (destination.Length == 0) destination = Text(metadata, "destinationIP");
            var chains = connection.TryGetProperty("chains", out var chain) && chain.ValueKind == JsonValueKind.Array
                ? string.Join(" ← ", chain.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())) : "";
            output.Add(new ConnectionActivity(Text(metadata, "process"), destination + ":" + Text(metadata, "destinationPort"),
                Text(metadata, "network").ToUpperInvariant(), Text(connection, "rule") + " " + Text(connection, "rulePayload"), chains));
        }
        return output;
    }

    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
        ? field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : field.ValueKind == JsonValueKind.Number ? field.ToString() : "" : "";

    public void Dispose() => _client.Dispose();
}
