using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal enum DiagnosticState { Information, Success, Warning, Failure }
internal sealed record DiagnosticStep(string Name, string Result, DiagnosticState State = DiagnosticState.Information);

internal static class ConnectionDiagnostic
{
    internal static async Task<IReadOnlyList<DiagnosticStep>> RunAsync(bool running, ClashConfigDocument? document,
        CoreDiagnostics? diagnostics, CancellationToken token, Action<DiagnosticStep>? progress = null,
        Func<string, int, CancellationToken, Task>? connect = null, bool fullTunnel = false,
        Func<CancellationToken, Task>? systemConnect = null)
    {
        using var overallDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        overallDeadline.CancelAfter(TimeSpan.FromSeconds(45)); token = overallDeadline.Token;
        var results = new List<DiagnosticStep>();
        void Add(string name, string result, DiagnosticState state = DiagnosticState.Information) { var step = new DiagnosticStep(name, result, state); results.Add(step); progress?.Invoke(step); }
        if (!running || diagnostics is null || document is null)
        {
            Add("Ядро", "VPN выключен. Запусти его перед проверкой.");
            return results;
        }
        try { await diagnostics.ReadVersionAsync(token); Add("Ядро", "Работает, локальный API отвечает.", DiagnosticState.Success); }
        catch (Exception) when (!token.IsCancellationRequested)
        { Add("Ядро", "Процесс работает, но локальный API недоступен. Проверь журнал.", DiagnosticState.Failure); return results; }
        string selection;
        try
        {
            if (document.VpnRoute is not { } route) throw new InvalidDataException();
            selection = await diagnostics.ReadSelectionAsync(route, token);
            if (selection is "DIRECT" or "REJECT")
            { Add("Маршрут", "В группе VPN выбрано прямое подключение или блокировка.", DiagnosticState.Warning); return results; }
            Add("Маршрут", "VPN-подключение выбрано.", DiagnosticState.Success);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        { Add("Маршрут", "Не удалось определить VPN-подключение.", DiagnosticState.Failure); return results; }

        var endpoint = Endpoint(document.Path, selection);
        if (endpoint is { } target && target.Type is not ("hysteria" or "hysteria2" or "tuic" or "wireguard"))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                if (connect is not null) await connect(target.Host, target.Port, deadline.Token);
                else { using var socket = new TcpClient(); await socket.ConnectAsync(target.Host, target.Port, deadline.Token); }
                Add("Сервер", "TCP-порт доступен. Это ещё не проверка авторизации VPN.", DiagnosticState.Success);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            { Add("Сервер", "TCP-порт не ответил. Итоговую доступность проверит HTTPS-запрос.", DiagnosticState.Warning); }
        }
        else Add("Сервер", "Отдельная TCP-проверка не подходит этому подключению; проверяем через ядро.");
        try
        {
            var dns = await diagnostics.CheckDnsAsync(token);
            Add("DNS", dns ? "DNS ядра отвечает." : "DNS ядра выключен; разрешение имени проверит HTTPS-запрос.", dns ? DiagnosticState.Success : DiagnosticState.Information);
        }
        catch (Exception) when (!token.IsCancellationRequested) { Add("DNS", "DNS ядра не разрешил проверочное имя.", DiagnosticState.Warning); }
        try
        {
            var delay = await diagnostics.CheckProxyAsync(document.VpnRoute!, token);
            Add("HTTPS через VPN", $"Запрос выполнен, {delay} мс.", DiagnosticState.Success);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        { Add("HTTPS через VPN", "Запрос не прошёл. Проверь сервер, настройки подключения и журнал.", DiagnosticState.Failure); }
        try
        {
            var first = await diagnostics.ReadConnectionsAsync(token);
            await Task.Delay(1500, token);
            var second = await diagnostics.ReadConnectionsAsync(token);
            var video = VideoTransferStep(first, second);
            Add(video.Name, video.Result, video.State);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        { Add("Видеосоединения", "Не удалось прочитать счётчики. Короткая HTTPS-проверка не подтверждает передачу видео.", DiagnosticState.Information); }
        if (fullTunnel)
        {
            try
            {
                await (systemConnect is null ? CoreDiagnostics.CheckSystemHttpsAsync(token) : systemConnect(token));
                Add("Системное подключение", "HTTPS через системную сеть доступен. Проверка выполнена без локального прокси.", DiagnosticState.Success);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            { Add("Системное подключение", "HTTPS через системную сеть не прошёл. Проверь DNS, маршруты и другие запущенные VPN.", DiagnosticState.Failure); }
        }
        token.ThrowIfCancellationRequested();
        return results;
    }

    internal static DiagnosticStep VideoTransferStep(IReadOnlyList<ConnectionActivity> first, IReadOnlyList<ConnectionActivity> second)
    {
        bool Video(ConnectionActivity connection)
        {
            var host = connection.Destination[..Math.Max(0, connection.Destination.LastIndexOf(':'))];
            return new[] { "googlevideo.com", "youtube.com", "ytimg.com", "vimeo.com", "vimeocdn.com" }
                .Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        }
        var videos = second.Where(Video).ToArray();
        if (videos.Length == 0) return new("Видеосоединения", "Распознанных активных видеосоединений нет. Передача видео этой проверкой не подтверждена.");
        var before = first.Where(connection => connection.Id.Length > 0).GroupBy(connection => connection.Id).ToDictionary(group => group.Key, group => group.First());
        var stalled = videos.Count(connection => connection.Id.Length > 0 && before.TryGetValue(connection.Id, out var previous)
            && connection.Upload > previous.Upload && connection.Download == previous.Download);
        var receiving = videos.Count(connection => connection.Id.Length > 0 && before.TryGetValue(connection.Id, out var previous) && connection.Download > previous.Download);
        var direct = videos.Count(connection => connection.Route.Split(" ← ").Any(tag => tag.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)));
        // Report counts only: raw hosts, process paths, rule values and API IDs may
        // contain private information and are deliberately excluded from the report.
        return new("Видеосоединения", $"Активных: {videos.Length}; напрямую: {direct}; получают данные: {receiving}; отправляют без новых ответов: {stalled}."
            + (stalled > 0 ? " Возможна задержка; проверь маршрут зависшего соединения." : " Это наблюдение, не полный тест видеозагрузки."),
            stalled > 0 ? DiagnosticState.Warning : DiagnosticState.Information);
    }

    private static (string Host, int Port, string Type)? Endpoint(string path, string selected)
    {
        try
        {
            var yaml = new YamlStream(); yaml.Load(new StringReader(File.ReadAllText(path)));
            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var value) || value is not YamlSequenceNode nodes) return null;
            foreach (var node in nodes.Children.OfType<YamlMappingNode>())
            {
                string Get(string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var field) ? (field as YamlScalarNode)?.Value ?? "" : "";
                if (Get("name") == selected && int.TryParse(Get("port"), out var port) && port is > 0 and <= 65535 && Get("server").Length > 0)
                    return (Get("server"), port, Get("type"));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException) { }
        return null;
    }

    // Deliberately whitelist report fields. Raw YAML, API responses, URLs and logs can contain credentials.
    internal static string Report(IReadOnlyList<DiagnosticStep> results, bool running, DateTimeOffset? checkedAt = null)
    {
        var text = new StringBuilder();
        text.AppendLine("MorphocyteOS " + BuildInfo.Version);
        text.AppendLine("Windows " + Environment.OSVersion.Version + " · " + RuntimeInformation.OSArchitecture);
        text.AppendLine("Проверка: " + (checkedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz"));
        text.AppendLine("Ядро: " + (running ? "процесс запущен" : "выключено"));
        foreach (var step in results) text.AppendLine(step.Name + ": " + step.Result);
        return text.ToString();
    }
}
