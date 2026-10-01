using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal sealed record ImportedProfile(string Yaml, string Description, bool InsecureTls);

// Connection import only. Xray routing/inbounds/DNS are not equivalent to Mihomo
// and are deliberately not silently translated into system-wide traffic rules.
internal static class ProfileImporter
{
    internal const int MaxInputLength = 262144;
    private const string NodeName = "Подключение";

    internal static ImportedProfile Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new InvalidDataException("Вставь VLESS-ссылку или JSON-конфигурацию Xray.");
        if (input.Length > MaxInputLength) throw new InvalidDataException("Профиль слишком большой (максимум 256 тысяч символов).");
        var text = input.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = text.IndexOf('\n');
            if (newline < 0 || !text.EndsWith("```", StringComparison.Ordinal)) throw new InvalidDataException("Незавершённая обёртка текста.");
            text = text[(newline + 1)..^3].Trim();
        }
        if (text.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return FromLink(text);
        if (text.StartsWith('{')) return FromJson(text);
        throw new InvalidDataException("Поддерживаются vless:// и JSON Xray с одним VLESS-подключением. HTTPS-подписки и другие протоколы пока не поддерживаются; готовый YAML можно выбрать в настройках.");
    }

    private static ImportedProfile FromLink(string text)
    {
        if (text.Any(char.IsWhiteSpace) || Regex.IsMatch(text, "%[^0-9A-Fa-f]|%[0-9A-Fa-f](?![0-9A-Fa-f])|%$"))
            throw new InvalidDataException("В ссылке есть пробелы или некорректное URL-кодирование.");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535 || uri.AbsolutePath is not ("" or "/"))
            throw new InvalidDataException("В ссылке должны быть корректные адрес и порт сервера.");
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(new[] { "encryption", "security", "sni", "allowInsecure", "insecure", "type", "host", "path", "flow", "fp", "alpn", "pbk", "sid", "serviceName", "mode", "headerType", "ed", "eh" }, StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split('=', 2);
            var key = Uri.UnescapeDataString(bits[0]);
            if (!allowed.Contains(key)) throw new InvalidDataException("В ссылке есть неподдерживаемый параметр. Импорт отменён, чтобы не потерять настройки подключения.");
            if (!query.TryAdd(key, Uri.UnescapeDataString(bits.Length == 2 ? bits[1] : "")))
                throw new InvalidDataException("В ссылке повторяется параметр подключения.");
        }
        string Get(string name, string fallback = "") => query.GetValueOrDefault(name, fallback);
        if (Get("encryption", "none") != "none") throw new InvalidDataException("Этот импорт поддерживает VLESS encryption=none.");
        var headerType = Get("headerType", "none");
        if (!string.IsNullOrWhiteSpace(headerType) && !headerType.Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TCP-обфускация этой ссылки пока не поддерживается.");
        var security = Get("security", "none").ToLowerInvariant();
        var network = Get("type", "tcp").ToLowerInvariant();
        var insecure = Flag(Get("allowInsecure", "0")) || Flag(Get("insecure", "0"));
        var node = MakeNode(uri.Host.Trim('[', ']'), uri.Port, Uri.UnescapeDataString(uri.UserInfo), network, security,
            Get("sni"), insecure, string.IsNullOrWhiteSpace(Get("fp")) ? security == "none" ? "" : "chrome" : Get("fp"),
            Get("alpn"), Get("flow"), Get("pbk"), Get("sid"));
        AddTransport(node, network, Get("path", "/"), Get("host"), Get("serviceName"), null);
        if (query.ContainsKey("mode") && Get("mode") != "gun") throw new InvalidDataException("Поддерживается обычный gRPC (mode=gun), без multiMode.");
        if (query.ContainsKey("ed") || query.ContainsKey("eh"))
        {
            if (network != "ws") throw new InvalidDataException("Early data поддерживается только для WebSocket.");
            if (!int.TryParse(Get("ed", "0"), out var bytes) || bytes is < 0 or > 65536) throw new InvalidDataException("Некорректный размер WebSocket early data.");
            var ws = (YamlMappingNode)node.Children[new YamlScalarNode("ws-opts")];
            Put(ws, "max-early-data", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Put(ws, "early-data-header-name", SafeText(Get("eh", "Sec-WebSocket-Protocol")));
        }
        return Build(node, network, security, insecure);
    }

    private static ImportedProfile FromJson(string text)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 }); }
        catch (JsonException) { throw new InvalidDataException("Не удалось прочитать JSON. Проверь скобки, кавычки и формат Xray."); }
        using (json)
        {
            var root = json.RootElement;
            RejectDuplicateJsonKeys(root);
            var outbounds = Property(root, "outbounds");
            if (outbounds.ValueKind != JsonValueKind.Array) throw new InvalidDataException("В JSON Xray отсутствует список outbounds.");
            var nodes = outbounds.EnumerateArray().Where(node => Text(node, "protocol") == "vless").ToArray();
            if (nodes.Length != 1) throw new InvalidDataException("Нужен JSON с одним VLESS-подключением. Для нескольких узлов используй готовый YAML.");
            var outbound = nodes[0];
            if (Bool(Property(outbound, "mux"), "enabled")) throw new InvalidDataException("Xray mux включён; его параметры не поддерживаются этим импортом.");
            if (Property(outbound, "proxySettings").ValueKind == JsonValueKind.Object) throw new InvalidDataException("Цепочки proxySettings Xray пока не поддерживаются.");
            var vnext = Property(Property(outbound, "settings"), "vnext");
            if (vnext.ValueKind != JsonValueKind.Array || vnext.GetArrayLength() != 1) throw new InvalidDataException("Ожидается один VLESS-сервер в settings.vnext.");
            var endpoint = vnext[0];
            var users = Property(endpoint, "users");
            if (users.ValueKind != JsonValueKind.Array || users.GetArrayLength() != 1) throw new InvalidDataException("Ожидается один пользователь VLESS.");
            var user = users[0];
            if (Text(user, "encryption", "none") != "none") throw new InvalidDataException("Этот импорт поддерживает VLESS encryption=none.");
            var port = Property(endpoint, "port");
            if (port.ValueKind != JsonValueKind.Number || !port.TryGetInt32(out var number)) throw new InvalidDataException("Порт VLESS должен быть целым числом.");
            var stream = Property(outbound, "streamSettings");
            CheckFields(stream, "network", "security", "tlsSettings", "realitySettings", "wsSettings", "grpcSettings", "httpupgradeSettings", "tcpSettings");
            if (Property(stream, "sockopt").ValueKind == JsonValueKind.Object) throw new InvalidDataException("Настройки Xray sockopt нужно перенести вручную в YAML; импорт отменён.");
            var network = Text(stream, "network", "tcp").ToLowerInvariant();
            var security = Text(stream, "security", "none").ToLowerInvariant();
            var tls = Property(stream, security == "reality" ? "realitySettings" : "tlsSettings");
            CheckFields(tls, "allowInsecure", "serverName", "fingerprint", "alpn", "publicKey", "shortId", "spiderX", "show");
            var alpnElement = Property(tls, "alpn");
            var alpn = alpnElement.ValueKind == JsonValueKind.Array
                ? string.Join(',', alpnElement.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException("ALPN должен быть списком строк."))) : "";
            var insecure = Bool(tls, "allowInsecure");
            var node = MakeNode(Text(endpoint, "address"), number, Text(user, "id"), network, security, Text(tls, "serverName"),
                insecure, Text(tls, "fingerprint", security == "none" ? "" : "chrome"), alpn, Text(user, "flow"), Text(tls, "publicKey"), Text(tls, "shortId"));
            var ws = Property(stream, network == "httpupgrade" ? "httpupgradeSettings" : "wsSettings");
            CheckFields(ws, "path", "host", "headers", "maxEarlyData", "earlyDataHeaderName");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var headerElement = Property(ws, "headers");
            if (headerElement.ValueKind == JsonValueKind.Object)
                foreach (var header in headerElement.EnumerateObject())
                {
                    if (header.Value.ValueKind != JsonValueKind.String || !Regex.IsMatch(header.Name, "^[A-Za-z0-9-]+$")) throw new InvalidDataException("Некорректный заголовок WebSocket.");
                    if (!headers.TryAdd(header.Name, SafeText(header.Value.GetString()!))) throw new InvalidDataException("Повторяется заголовок WebSocket.");
                }
            var host = Text(ws, "host");
            if (headers.TryGetValue("Host", out var headerHost))
            {
                if (host.Length > 0 && !host.Equals(headerHost, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Host в wsSettings и headers различаются.");
                host = headerHost;
            }
            var grpc = Property(stream, "grpcSettings");
            CheckFields(grpc, "serviceName", "multiMode");
            if (Bool(grpc, "multiMode")) throw new InvalidDataException("gRPC multiMode пока не поддерживается.");
            var tcpHeader = Property(Property(stream, "tcpSettings"), "header");
            if (Text(tcpHeader, "type", "none") != "none") throw new InvalidDataException("TCP-обфускация Xray пока не поддерживается.");
            AddTransport(node, network, Text(ws, "path", "/"), host, Text(grpc, "serviceName"), headers);
            var earlyData = Property(ws, "maxEarlyData");
            if (network == "ws" && earlyData.ValueKind != JsonValueKind.Undefined)
            {
                if (!earlyData.TryGetInt32(out var bytes) || bytes is < 0 or > 65536) throw new InvalidDataException("Некорректный размер WebSocket early data.");
                var opts = (YamlMappingNode)node.Children[new YamlScalarNode("ws-opts")];
                Put(opts, "max-early-data", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Put(opts, "early-data-header-name", SafeText(Text(ws, "earlyDataHeaderName", "Sec-WebSocket-Protocol")));
            }
            return Build(node, network, security, insecure);
        }
    }

    private static YamlMappingNode MakeNode(string server, int port, string id, string network, string security,
        string sni, bool insecure, string fingerprint, string alpn, string flow, string publicKey, string shortId)
    {
        server = SafeText(server);
        if (Uri.CheckHostName(server) == UriHostNameType.Unknown || port is < 1 or > 65535) throw new InvalidDataException("Некорректный адрес или порт сервера.");
        if (!Guid.TryParse(id, out var uuid)) throw new InvalidDataException("Некорректный UUID VLESS.");
        if (network is not ("tcp" or "raw" or "ws" or "grpc" or "httpupgrade")) throw new InvalidDataException("Поддерживаются VLESS TCP, WebSocket, gRPC и HTTPUpgrade. Для другого транспорта используй готовый YAML.");
        if (security is not ("none" or "tls" or "reality")) throw new InvalidDataException("Поддерживаются security=none, tls и reality.");
        if (flow is not ("" or "xtls-rprx-vision")) throw new InvalidDataException("Неподдерживаемый VLESS flow.");
        if (flow.Length > 0 && (security == "none" || network is not ("tcp" or "raw"))) throw new InvalidDataException("Vision требует TCP и TLS / Reality.");
        if (sni.Length > 0 && Uri.CheckHostName(SafeText(sni)) == UriHostNameType.Unknown) throw new InvalidDataException("Некорректное TLS-имя сервера (SNI).");
        var node = new YamlMappingNode();
        Put(node, "name", NodeName); Put(node, "type", "vless"); Put(node, "server", server);
        Put(node, "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)); Put(node, "uuid", uuid.ToString("D")); Put(node, "udp", "true");
        if (network is "ws" or "grpc" or "httpupgrade") Put(node, "network", network == "httpupgrade" ? "ws" : network);
        if (flow.Length > 0) Put(node, "flow", flow);
        Put(node, "tls", security == "none" ? "false" : "true");
        if (security != "none")
        {
            Put(node, "skip-cert-verify", insecure ? "true" : "false");
            if (sni.Length > 0) Put(node, "servername", sni);
            Put(node, "client-fingerprint", SafeText(fingerprint));
            if (alpn.Length > 0) node.Add("alpn", new YamlSequenceNode(alpn.Split(',').Select(value => new YamlScalarNode(SafeText(value.Trim())))));
        }
        if (security == "reality")
        {
            if (!Regex.IsMatch(publicKey, "^[A-Za-z0-9_-]{43}=?$") || !Regex.IsMatch(shortId, "^(?:[0-9A-Fa-f]{2}){0,8}$"))
                throw new InvalidDataException("Некорректные public key / short ID Reality.");
            if (insecure) throw new InvalidDataException("Отключение проверки сертификата неприменимо к Reality.");
            var reality = new YamlMappingNode(); Put(reality, "public-key", publicKey); Put(reality, "short-id", shortId); node.Add("reality-opts", reality);
        }
        return node;
    }

    private static void AddTransport(YamlMappingNode node, string network, string path, string host, string service, Dictionary<string, string>? headers)
    {
        if (network is "ws" or "httpupgrade")
        {
            path = SafeText(path);
            if (!path.StartsWith('/')) throw new InvalidDataException("WebSocket-путь должен начинаться с /.");
            if (host.Length > 0 && (host.IndexOfAny(new[] { '\\', '/', '?', '#', '@' }) >= 0 || host.Any(char.IsWhiteSpace) || !Uri.TryCreate("https://" + host, UriKind.Absolute, out var authority)
                || authority.AbsolutePath != "/" || authority.Query.Length > 0 || authority.Fragment.Length > 0 || authority.UserInfo.Length > 0))
                throw new InvalidDataException("Некорректный WebSocket Host. Нужен адрес без слешей, пробелов и обратной косой черты.");
            var options = new YamlMappingNode(); Put(options, "path", path);
            var values = new YamlMappingNode();
            foreach (var header in headers ?? new()) if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) Put(values, header.Key, header.Value);
            if (host.Length > 0) Put(values, "Host", host);
            if (values.Children.Count > 0) options.Add("headers", values);
            if (network == "httpupgrade") Put(options, "v2ray-http-upgrade", "true");
            node.Add("ws-opts", options);
        }
        if (network == "grpc")
        {
            var options = new YamlMappingNode(); Put(options, "grpc-service-name", SafeText(service)); node.Add("grpc-opts", options);
        }
    }

    private static ImportedProfile Build(YamlMappingNode node, string network, string security, bool insecure)
    {
        var yaml = new YamlStream(); yaml.Load(new StringReader(BundledResources.TemplateText));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        root.Children[new YamlScalarNode("proxies")] = new YamlSequenceNode(node);
        var group = (YamlMappingNode)((YamlSequenceNode)root.Children[new YamlScalarNode("proxy-groups")]).Children[0];
        group.Children[new YamlScalarNode("proxies")] = new YamlSequenceNode(new YamlScalarNode(NodeName));
        using var writer = new StringWriter(); yaml.Save(writer, assignAnchors: false);
        var text = writer.ToString();
        var document = ClashConfigDocument.Parse("import.yaml", text);
        if (document.Validate().Count > 0 || document.ValidateForRouting().Count > 0) throw new InvalidDataException("Не удалось сформировать профиль маршрутизации.");
        return new ImportedProfile(text, $"VLESS · {network.ToUpperInvariant()} · {security.ToUpperInvariant()}", insecure);
    }

    private static JsonElement Property(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value : default;
    private static void CheckFields(JsonElement obj, params string[] allowed)
    {
        if (obj.ValueKind == JsonValueKind.Undefined) return;
        if (obj.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Неверный формат настроек транспорта в JSON.");
        if (obj.EnumerateObject().Any(field => !allowed.Contains(field.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("В JSON есть неподдерживаемые параметры транспорта / TLS. Импорт отменён, чтобы не потерять настройки. Используй готовый YAML.");
    }
    private static void RejectDuplicateJsonKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("В JSON повторяется ключ. Удали дубликат перед импортом.");
                RejectDuplicateJsonKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicateJsonKeys(child);
    }
    private static string Text(JsonElement obj, string key, string fallback = "")
    {
        var value = Property(obj, key);
        if (value.ValueKind == JsonValueKind.Undefined) return fallback;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Некорректный тип поля подключения в JSON.");
        return value.GetString() ?? fallback;
    }
    private static bool Bool(JsonElement obj, string key)
    {
        var value = Property(obj, key);
        if (value.ValueKind == JsonValueKind.Undefined) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Ожидается логическое значение в JSON.");
        return value.GetBoolean();
    }
    private static bool Flag(string text) => text.ToLowerInvariant() switch { "0" or "false" => false, "1" or "true" => true, _ => throw new InvalidDataException("Некорректный флаг проверки сертификата.") };
    private static string SafeText(string text)
    {
        if (text.Length > 8192 || text.Any(char.IsControl)) throw new InvalidDataException("В параметрах подключения есть недопустимые символы.");
        return text;
    }
    private static void Put(YamlMappingNode mapping, string key, string value) => mapping.Children[new YamlScalarNode(key)] = new YamlScalarNode(value);
}
