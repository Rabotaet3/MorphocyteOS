using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

// Download only after an explicit import or refresh. The app stores the source separately using Windows data protection.
internal static class SubscriptionImporter
{
    internal const int MaxBytes = 1_048_576;
    internal const int MaxNodes = 200;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HttpClient Client = CreateClient();
    private static readonly HttpClient DnsClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5), ConnectCallback = ConnectResolverAsync
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectPublicAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static async Task<ImportedProfile> ImportAsync(string input, CancellationToken token,
        HttpClient? client = null, TimeSpan? timeout = null, string? hardwareId = null, bool allowPartial = false, bool headersOnly = false)
    {
        var text = input.Trim();
        if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Для подписки нужна HTTPS-ссылка: HTTP не защищает ключи подключения.");
            return await Task.Run(() => ProfileImporter.Parse(text), token);
        }
        var origin = ValidateUrl(text);
        if (hardwareId is not null && !Guid.TryParseExact(hardwareId, "D", out _))
            throw new InvalidDataException("Некорректный HWID MorphocyteOS.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            var http = client ?? Client;
            var (body, metadata) = await DownloadViewAsync(http, origin, hardwareId, desktop: false, headersOnly, deadline.Token).ConfigureAwait(false);
            if (headersOnly) return new ImportedProfile("", "", false) { Metadata = metadata };
            var tryDesktop = NeedsDesktopJson(body, hardwareId is not null);
            ImportedProfile? parsed = null;
            try { parsed = await Task.Run(() => ParseContent(body, allowPartial), deadline.Token).ConfigureAwait(false); }
            catch (InvalidDataException) when (tryDesktop) { }
            if (tryDesktop)
            {
                try
                {
                    var (desktop, desktopMetadata) = await DownloadViewAsync(http, origin, hardwareId, desktop: true, headersOnly: false, deadline.Token).ConfigureAwait(false);
                    if (parsed is null || IsJsonView(DecodeSubscriptionBody(desktop)))
                    {
                        var additional = await Task.Run(() => ParseContent(desktop, allowPartial), deadline.Token).ConfigureAwait(false);
                        parsed = parsed is null ? additional : Finish(SubscriptionFormats.Merge(parsed, additional),
                            parsed.InsecureTls || additional.InsecureTls) with
                        {
                            CompatibilityWarnings = additional.CompatibilityWarnings.Concat(parsed.CompatibilityWarnings).ToArray()
                        };
                        metadata = desktopMetadata;
                    }
                }
                catch (Exception error) when (parsed is not null && !token.IsCancellationRequested &&
                    error is InvalidDataException or HttpRequestException or IOException or JsonException or DecoderFallbackException or UriFormatException or OperationCanceledException)
                {
                    parsed = parsed with { CompatibilityWarnings = parsed.CompatibilityWarnings.Append(
                        "Не удалось проверить дополнительный JSON-список провайдера. В обычном списке может быть меньше серверов.").ToArray() };
                }
            }
            token.ThrowIfCancellationRequested();
            return parsed! with { SubscriptionUrl = text, SendHardwareId = hardwareId is not null, AllowPartialSubscription = allowPartial, Metadata = metadata };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidDataException("Сервер подписки не ответил за отведённое время. Попробуй ещё раз."); }
        catch (HttpRequestException)
        { throw new InvalidDataException("Не удалось безопасно загрузить подписку. Проверь интернет, адрес и сертификат сервера."); }
        catch (IOException)
        { throw new InvalidDataException("Соединение прервалось. Подписка не импортирована; текущий профиль не изменён."); }
        catch (DecoderFallbackException)
        { throw new InvalidDataException("Сервер вернул текст в неподдерживаемой кодировке. Нужна подписка UTF-8."); }
        catch (UriFormatException)
        { throw new InvalidDataException("Сервер подписки вернул некорректное перенаправление."); }
    }

    private static async Task<(string Body, SubscriptionMetadata Metadata)> DownloadViewAsync(HttpClient client, Uri origin,
        string? hardwareId, bool desktop, bool headersOnly, CancellationToken token)
    {
        var uri = origin;
        for (var redirects = 0; ; redirects++)
        {
            token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd((desktop ? "Happ/compatible (Windows) " : "") + "MorphocyteOS/" + BuildInfo.Version);
            request.Headers.Add("x-device-os", "Windows");
            request.Headers.Accept.ParseAdd("application/yaml, application/json, text/plain, */*");
            if (hardwareId is not null) request.Headers.Add("x-hwid", hardwareId);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 5 || response.Headers.Location is not { } location)
                    throw new InvalidDataException("Сервер подписки возвращает слишком много или некорректные перенаправления.");
                uri = ValidateUrl(new Uri(uri, location).AbsoluteUri);
                if (hardwareId is not null && (uri.IdnHost != origin.IdnHost || uri.Port != origin.Port))
                    throw new InvalidDataException("Подписка перенаправляет на другой сервер. HWID ему не передан; укажи конечную HTTPS-ссылку отдельно.");
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException($"Сервер подписки ответил HTTP {(int)response.StatusCode}. Проверь ссылку и срок действия подписки.");
            var metadata = SubscriptionMetadata.FromResponse(response);
            if (headersOnly) return ("", metadata);
            if (response.Content.Headers.ContentLength is > MaxBytes)
                throw new InvalidDataException("Подписка слишком большая: допустимо до 1 МБ.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (data.Length + count > MaxBytes) throw new InvalidDataException("Подписка слишком большая: допустимо до 1 МБ.");
                data.Write(buffer, 0, count);
            }
            return (Utf8.GetString(data.ToArray()), metadata);
        }
    }

    internal static Uri ValidateUrl(string text)
    {
        if (text.Length > 8192 || text.Any(char.IsWhiteSpace) || text.Any(char.IsControl)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Port is < 1 or > 65535 || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new InvalidDataException("Вставь корректную HTTPS-ссылку без пробелов, логина и фрагмента после #.");
        var host = uri.IdnHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') && !host.Contains(':')
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && !IsPublicAddress(address))
            throw new InvalidDataException("Подписка должна находиться на публичном HTTPS-сервере, не в локальной сети.");
        return uri;
    }

    // Check DNS results at connection time and connect to the checked address directly.
    // This also rejects redirects and DNS rebinding to local services/metadata endpoints.
    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        if (addresses.Any(IsFakeIp) && addresses.All(address => IsFakeIp(address) || IsPublicAddress(address)))
            addresses = await ResolveFakeIpAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Subscription host is not public.");
        return await ConnectAddressesAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
    }

    private static ValueTask<Stream> ConnectResolverAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var ips = context.DnsEndPoint.Host switch
        {
            "cloudflare-dns.com" => new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1") },
            "dns.google" => new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4") },
            _ => throw new HttpRequestException("Untrusted DNS resolver.")
        };
        return ConnectAddressesAsync(ips, 443, token);
    }

    private static bool IsFakeIp(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && b[0] == 198 && b[1] is 18 or 19;
    }

    // Fake-IP is a VPN routing token, not the origin address. Resolve it via pinned
    // HTTPS resolvers; never simply whitelist the benchmark/local address range.
    private static async Task<IPAddress[]> ResolveFakeIpAsync(string host, CancellationToken token)
    {
        foreach (var resolver in new[] { "https://cloudflare-dns.com/dns-query", "https://dns.google/resolve" })
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var addresses = new List<IPAddress>();
                foreach (var kind in new[] { "A", "AAAA" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, resolver + "?name=" + Uri.EscapeDataString(host) + "&type=" + kind);
                    request.Headers.Accept.ParseAdd("application/dns-json");
                    using var response = await DnsClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 32768) throw new HttpRequestException("DNS resolution failed.");
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                    using var data = new MemoryStream();
                    var buffer = new byte[4096];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                    {
                        if (data.Length + count > 32768) throw new HttpRequestException("DNS response too large.");
                        data.Write(buffer, 0, count);
                    }
                    addresses.AddRange(ParseDnsAnswers(Utf8.GetString(data.ToArray())));
                }
                if (addresses.Count > 0) return addresses.Distinct().ToArray();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or DecoderFallbackException) { }
        }
        token.ThrowIfCancellationRequested();
        throw new HttpRequestException("Cannot resolve VPN Fake-IP safely.");
    }

    internal static IPAddress[] ParseDnsAnswers(string text)
    {
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) || code != 0)
            throw new HttpRequestException("DNS returned an error.");
        if (!root.TryGetProperty("Answer", out var answer)) return Array.Empty<IPAddress>();
        if (answer.ValueKind != JsonValueKind.Array || answer.GetArrayLength() > 256) throw new HttpRequestException("Invalid DNS response.");
        var addresses = new List<IPAddress>();
        foreach (var item in answer.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var kind) && kind is 1 or 28)
            {
                if (!item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String || !IPAddress.TryParse(data.GetString(), out var address) || !IsPublicAddress(address))
                    throw new HttpRequestException("DNS returned a non-public address.");
                addresses.Add(address);
            }
        return addresses.ToArray();
    }

    private static async ValueTask<Stream> ConnectAddressesAsync(IPAddress[] addresses, int port, CancellationToken token)
    {
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Subscription host connection failed.");
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && (b[1] == 168 || b[1] == 0))
                && !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100))
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xE0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x02)
            && !(b[0] == 0x20 && b[1] == 0x01 && (b[2] == 0 && b[3] == 0 || b[2] == 0x0d && b[3] == 0xb8));
    }

    internal static ImportedProfile ParseContent(string text, bool allowPartial = false)
    {
        text = text.Trim().TrimStart('\uFEFF').Trim();
        if (text.Length == 0) throw new InvalidDataException("Сервер вернул пустую подписку.");
        if (text.Length > MaxBytes || text.Length > ProfileImporter.MaxInputLength && !text.StartsWith('[')) throw new InvalidDataException("В подписке слишком много текста.");
        if (text.StartsWith('<')) throw new InvalidDataException("Ссылка ведёт на веб-страницу, а не на подписку. Нужна прямая ссылка на конфигурацию.");
        text = DecodeSubscriptionBody(text);
        if (text.StartsWith('{')) return ProfileImporter.Parse(text);
        if (text.StartsWith('[')) return ParseJsonProfiles(text, allowPartial);
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        if (lines.Length > 0 && Regex.IsMatch(lines[0], @"\A[A-Za-z][A-Za-z0-9+.-]*://")) return ParseLinks(lines, allowPartial);
        return ParseYaml(text);
    }

    private static string DecodeSubscriptionBody(string text)
    {
        var body = text.Trim().TrimStart('\uFEFF').Trim();
        if (!Regex.IsMatch(body, @"\A[A-Za-z0-9+/_=\-\s]+\z")) return body;
        try
        {
            var encoded = string.Concat(body.Where(ch => !char.IsWhiteSpace(ch))).Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            return Utf8.GetString(Convert.FromBase64String(encoded)).Trim().TrimStart('\uFEFF').Trim();
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        { throw new InvalidDataException("Не удалось прочитать Base64-подписку."); }
    }

    private static bool IsJsonView(string text)
    {
        var body = text.TrimStart().TrimStart('\uFEFF').TrimStart();
        return body.StartsWith('[') || body.StartsWith('{');
    }

    private static bool NeedsDesktopJson(string text, bool probeLinkList)
    {
        try
        {
            if (text.Length > MaxBytes) return false;
            text = DecodeSubscriptionBody(text);
            if (IsJsonView(text)) return false;
            var links = 0;
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Uri.TryCreate(line.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("vless" or "hysteria2" or "hy2")) continue;
                if (SubscriptionInformation.IsSection(uri) || probeLinkList && ++links >= 2) return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static ImportedProfile ParseJsonProfiles(string text,bool allowPartial)
    {
        using var json=JsonDocument.Parse(text,new JsonDocumentOptions { MaxDepth=48 });
        if(json.RootElement.GetArrayLength() is 0 or >MaxNodes) throw new InvalidDataException("JSON-подписка не содержит допустимого количества серверов.");
        var nodes=new List<YamlMappingNode>(); var warnings=new List<string>(); var index=0;
        foreach(var profile in json.RootElement.EnumerateArray())
        {
            index++;
            try
            {
                if(XrayFullProfile.IsFull(profile))
                {
                    var imported=XrayFullProfile.Import(profile.GetRawText()); var root=LoadYaml(imported.Yaml);
                    nodes.Add((YamlMappingNode)((YamlSequenceNode)root.Children[new YamlScalarNode("proxies")]).Children[0]);
                }
                else nodes.Add(XrayJsonConnection.Import(profile,index));
            }
            catch(Exception error) when(error is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                if(!allowPartial) throw new InvalidDataException($"Запись {index}: некорректный или неподдерживаемый JSON-профиль. Подписка не применена.");
                warnings.Add($"Запись {index}: неподдерживаемый JSON-профиль; запись не импортирована.");
            }
        }
        return Finish(nodes,false) with { AllowPartialSubscription=allowPartial,CompatibilityWarnings=warnings.ToArray() };
    }

    private static ImportedProfile ParseLinks(string[] lines, bool allowPartial)
    {
        if (lines.Any(line => line.Contains("HWID", StringComparison.OrdinalIgnoreCase)
            || Uri.TryCreate(line, UriKind.Absolute, out var link) && Uri.UnescapeDataString(link.Fragment).Contains("HWID", StringComparison.OrdinalIgnoreCase))
            && lines.All(line => Uri.TryCreate(line, UriKind.Absolute, out var link) && link.IsLoopback))
            throw new InvalidDataException("Провайдер вернул заглушку вместо серверов: требуется HWID. MorphocyteOS передаёт свой ID автоматически. Проверь свободные места устройств в подписке или обратись к провайдеру.");
        if (lines.Length > MaxNodes) throw new InvalidDataException("В подписке больше 200 серверов. Нужен меньший список.");
        var nodes = new List<YamlMappingNode>();
        var insecure = false;
        var warnings = new List<string>();
        var headings=0;
        var needsName = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if(Uri.TryCreate(line,UriKind.Absolute,out var section) && SubscriptionInformation.IsSection(section)) { headings++; continue; }
            ImportedProfile imported;
            try { imported = ProfileImporter.Parse(line); }
            catch (Exception error) when (allowPartial && error is InvalidDataException or UriFormatException)
            {
                warnings.Add($"Запись {index + 1}: неподдерживаемый тип ссылки или параметры; запись не импортирована.");
                continue;
            }
            insecure |= imported.InsecureTls;
            var root = LoadYaml(imported.Yaml);
            var node = (YamlMappingNode)((YamlSequenceNode)root.Children[new YamlScalarNode("proxies")]).Children[0];
            needsName = imported.NeedsName;
            if (needsName)
                node.Children[new YamlScalarNode("name")] = new YamlScalarNode($"Сервер {nodes.Count + 1}");
            nodes.Add(node);
        }
        if (nodes.Count == 0) throw new InvalidDataException("В подписке нет совместимых серверов. Текущий профиль сохранён; параметры не были упрощены.");
        return Finish(nodes, insecure, headings) with { NeedsName = lines.Length == 1 && needsName,
            AllowPartialSubscription = allowPartial, CompatibilityWarnings = warnings.ToArray() };
    }

    private static ImportedProfile ParseYaml(string text)
    {
        var root = LoadYaml(text);
        if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var raw) || raw is not YamlSequenceNode list || list.Children.Count == 0)
            throw new InvalidDataException("В YAML нет списка серверов proxies. Подписки только с proxy-providers пока не импортируются; выбери формат Clash/Mihomo со списком серверов.");
        if (list.Children.Count > MaxNodes) throw new InvalidDataException("В подписке больше 200 серверов. Нужен меньший список.");
        var nodes = new List<YamlMappingNode>();
        var insecure = false;
        var types = new HashSet<string>(new[] { "vless", "vmess", "ss", "ssr", "trojan", "socks5", "http", "hysteria", "hysteria2", "tuic", "anytls", "snell" }, StringComparer.Ordinal);
        foreach (var rawNode in list.Children)
        {
            if (rawNode is not YamlMappingNode node) throw new InvalidDataException("Некорректный сервер в подписке.");
            NormalizeOptions(node);
            if (!types.Contains(Scalar(node, "type") ?? "")
                || string.IsNullOrWhiteSpace(Scalar(node, "server")) || !int.TryParse(Scalar(node, "port"), out var port) || port is < 1 or > 65535)
                throw new InvalidDataException("В YAML есть неподдерживаемый или некорректный сервер. Список не импортирован частично.");
            CheckNode(node);
            insecure |= string.Equals(Scalar(node, "skip-cert-verify"), "true", StringComparison.OrdinalIgnoreCase);
            nodes.Add(node);
        }
        var needsName = nodes.Count == 1 && string.IsNullOrWhiteSpace(Scalar(nodes[0], "name"));
        return Finish(nodes, insecure) with { NeedsName = needsName };
    }

    private static ImportedProfile Finish(List<YamlMappingNode> nodes, bool insecure, int headings=0)
    {
        headings+=nodes.RemoveAll(SubscriptionInformation.IsSection);
        if(nodes.Count==0) throw new InvalidDataException("В подписке найдены только служебные разделители. Нет серверов для подключения.");
        var names = new HashSet<string>(new[] { "VPN", "DIRECT", "REJECT", "GLOBAL" }, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var name = Scalar(node, "name");
            if (string.IsNullOrWhiteSpace(name)) name = $"Сервер {names.Count - 3}";
            if (name.Length > 256 || name.Any(char.IsControl) || name.Contains(',')) throw new InvalidDataException("Недопустимое имя сервера в подписке: нельзя использовать управляющие символы или запятые.");
            var unique = name;
            for (var number = 2; !names.Add(unique); number++) unique = $"{name} ({number})";
            node.Children[new YamlScalarNode("name")] = new YamlScalarNode(unique);
        }
        return ProfileImporter.BuildConnections(nodes, $"Подписка · серверов: {nodes.Count}"+
            (headings>0?$" · служебных разделителей: {headings}":""), insecure);
    }

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;

    private static void NormalizeOptions(YamlNode node)
    {
        if (node is YamlSequenceNode list) { foreach (var item in list.Children) NormalizeOptions(item); return; }
        if (node is not YamlMappingNode mapping) return;
        var normalized = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: { } key }) throw new InvalidDataException("Недопустимый ключ сервера.");
            var name = key.Replace('_', '-').ToLowerInvariant();
            // Clash's one mixed-case option keeps its canonical spelling.
            if (name == "alterid") name = "alterId";
            var value = pair.Value;
            if (name == "skip-cert-verify")
            {
                var scalar = (value as YamlScalarNode)?.Value;
                var flag = bool.TryParse(scalar, out var boolean) ? boolean
                    : long.TryParse(scalar, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
                        ? number != 0 : throw new InvalidDataException("Некорректный переключатель проверки сертификата.");
                value = new YamlScalarNode(flag ? "true" : "false");
            }
            // HTTP header names and application data are not decoder option keys.
            if (name is not ("headers" or "payload")) NormalizeOptions(value);
            if (!normalized.TryAdd(name, value)) throw new InvalidDataException("В подписке неоднозначные повторяющиеся параметры сервера.");
        }
        mapping.Children.Clear();
        foreach (var pair in normalized) mapping.Add(pair.Key, pair.Value);
    }

    private static void CheckNode(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
            foreach (var pair in mapping.Children)
            {
                if (pair.Key is not YamlScalarNode key || key.Value is null) throw new InvalidDataException("Недопустимый ключ сервера.");
                var canonical = key.Value.Replace('_', '-').ToLowerInvariant();
                if (canonical is "dialer-proxy" or "certificate" or "private-key" or "client-certificate" or "client-key" or "ca" or "ca-str"
                    || canonical.EndsWith("-path", StringComparison.Ordinal) || canonical.EndsWith("-file", StringComparison.Ordinal))
                    throw new InvalidDataException("Подписка содержит зависимости от других прокси или файлов. Такой сервер нужно настроить вручную.");
                CheckNode(pair.Value);
            }
        else if (node is YamlSequenceNode sequence) foreach (var child in sequence.Children) CheckNode(child);
    }

    private static YamlMappingNode LoadYaml(string text)
    {
        try
        {
            var parser = new Parser(new StringReader(text));
            var depth = 0;
            var events = 0;
            while (parser.MoveNext())
            {
                if (++events > 100_000 || parser.Current is AnchorAlias || parser.Current is NodeEvent n && (!n.Anchor.IsEmpty || !n.Tag.IsEmpty))
                    throw new InvalidDataException("Слишком сложный YAML: якоря, ссылки и специальные теги подписок не поддерживаются.");
                if (parser.Current is MappingStart or SequenceStart && ++depth > 32) throw new InvalidDataException("Слишком глубокая структура YAML.");
                if (parser.Current is MappingEnd or SequenceEnd) depth--;
            }
            var yaml = new YamlStream(); yaml.Load(new StringReader(text));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidDataException("Нужен один YAML-документ со списком серверов.");
            return root;
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException)
        { throw new InvalidDataException("Не удалось прочитать YAML подписки. Проверь формат Clash/Mihomo."); }
    }
}
