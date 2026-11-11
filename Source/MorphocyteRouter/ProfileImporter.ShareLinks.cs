using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal static partial class ProfileImporter
{
    private static readonly UTF8Encoding ShareLinkUtf8 = new(false, true);

    private static ImportedProfile? TryImportAdditionalLink(string text)
    {
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0) return null;
        var scheme = text[..separator].ToLowerInvariant();
        if (scheme == "happ")
            throw new InvalidDataException("Для ссылки Happ нужна открытая HTTPS-ссылка или экспорт конфигурации от провайдера.");
        if (scheme is not ("vmess" or "trojan" or "ss" or "socks" or "socks5")) return null;
        if (text.Any(char.IsWhiteSpace) || Regex.IsMatch(text, "%(?![0-9A-Fa-f]{2})"))
            throw new InvalidDataException("В ссылке есть пробелы или некорректное URL-кодирование.");

        var parts = text[(separator + 3)..].Split('#', 2);
        var label = Uri.UnescapeDataString(parts.ElementAtOrDefault(1) ?? "").Trim();
        if (scheme == "vmess") return FromVmessShareLink(parts[0], label);
        if (scheme == "trojan") return FromTrojanShareLink(text, label);
        return FromShadowsocksOrSocksLink(parts[0], label, scheme == "ss");
    }

    private static ImportedProfile FromVmessShareLink(string payload, string label)
    {
        try
        {
            using var json = JsonDocument.Parse(DecodeShareLink(payload), new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            RejectDuplicateJsonKeys(root);
            CheckFields(root, "v", "ps", "add", "port", "id", "aid", "scy", "net", "type", "host", "path", "tls", "sni", "alpn", "fp", "insecure");
            string ReadNumber(string key, string fallback) => Property(root, key).ValueKind == JsonValueKind.Number
                ? Property(root, key).GetRawText() : Text(root, key, fallback);
            var server = Text(root, "add").Trim('[', ']');
            if (ReadNumber("v", "2") != "2" || Uri.CheckHostName(server) == UriHostNameType.Unknown
                || !ushort.TryParse(ReadNumber("port", ""), out var port) || port == 0
                || !Guid.TryParse(Text(root, "id"), out var id) || !ushort.TryParse(ReadNumber("aid", "0"), out var alterId))
                throw new InvalidDataException("Некорректные адрес, порт или идентификатор в ссылке VMess.");
            var security = Text(root, "scy", "auto");
            if (security.Length == 0) security = "auto";
            if (security is not ("auto" or "none" or "zero" or "aes-128-gcm" or "aes-128-ctr" or "chacha20-poly1305"))
                throw new InvalidDataException("Неподдерживаемый режим шифрования VMess.");
            var transport = Text(root, "net", "tcp");
            var mode = Text(root, "type");
            if (mode is not ("" or "none") && !(transport == "grpc" && mode == "gun"))
                throw new InvalidDataException("Неподдерживаемый режим транспорта VMess.");
            var tls = Text(root, "tls");
            if (tls is not ("" or "none" or "tls")) throw new InvalidDataException("Неподдерживаемый режим TLS в ссылке VMess.");
            var insecure = Property(root, "insecure").ValueKind is JsonValueKind.True or JsonValueKind.False
                ? Bool(root, "insecure") : Flag(ReadNumber("insecure", "0"));
            var node = NewShareNode("vmess", server, port);
            Put(node, "uuid", id.ToString("D"));
            Put(node, "alterId", alterId.ToString(CultureInfo.InvariantCulture));
            Put(node, "cipher", security);
            SetShareTls(node, tls == "tls", insecure, Text(root, "sni"), Text(root, "alpn"), Text(root, "fp"));
            var path = Text(root, "path");
            SetShareTransport(node, transport, path.Length == 0 ? "/" : path, Text(root, "host"), path);
            return FinishShareLink(node, label.Length == 0 ? Text(root, "ps") : label, "VMess", insecure);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Некорректный Base64 JSON в ссылке VMess.");
        }
    }

    private static ImportedProfile FromTrojanShareLink(string text, string label)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown
            || uri.Port is < 1 or > 65535 || uri.AbsolutePath is not ("" or "/") || uri.UserInfo.Length == 0)
            throw new InvalidDataException("Для Trojan нужны корректные адрес, порт и пароль.");
        var query = XrayProfile.Query(uri);
        if (query.Keys.Any(key => !new[] { "security", "sni", "alpn", "fp", "insecure", "allowInsecure",
                "type", "host", "path", "serviceName", "mode", "headerType" }.Contains(key, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("В ссылке Trojan есть неподдерживаемый параметр.");
        string Get(string name, string fallback = "") => query.GetValueOrDefault(name, fallback);
        var transport = Get("type", "tcp");
        if (Get("security", "tls") != "tls" || Get("headerType") is not ("" or "none")
            || Get("mode") is not ("" or "gun") || query.ContainsKey("mode") && transport != "grpc")
            throw new InvalidDataException("Неподдерживаемый режим TLS или транспорта в ссылке Trojan.");
        var insecure = Flag(Get("insecure", "0")) | Flag(Get("allowInsecure", "0"));
        var node = NewShareNode("trojan", uri.Host.Trim('[', ']'), uri.Port);
        Put(node, "password", SafeText(Uri.UnescapeDataString(uri.UserInfo)));
        SetShareTls(node, true, insecure, Get("sni"), Get("alpn"), Get("fp"));
        SetShareTransport(node, transport, Get("path", "/"), Get("host"), Get("serviceName"));
        return FinishShareLink(node, label, "Trojan", insecure);
    }

    private static ImportedProfile FromShadowsocksOrSocksLink(string payload, string label, bool shadowsocks)
    {
        if (payload.Contains('?'))
            throw new InvalidDataException("Ссылки Shadowsocks и SOCKS5 с дополнительными параметрами или плагинами пока не поддерживаются.");
        var wholeBase64 = !payload.Contains('@') && (shadowsocks || !payload.Contains(':'));
        var authority = wholeBase64 ? DecodeShareLink(payload) : payload;
        var separator = authority.LastIndexOf('@');
        var hostAndPort = authority[(separator + 1)..];
        if (!Uri.TryCreate("https://" + hostAndPort, UriKind.Absolute, out var endpoint) || endpoint.AbsolutePath != "/"
            || endpoint.HostNameType == UriHostNameType.Unknown || endpoint.UserInfo.Length > 0
            || endpoint.Fragment.Length > 0 || endpoint.Query.Length > 0
            || !ushort.TryParse(hostAndPort.TrimEnd('/').Split(':').Last(), out var port) || port == 0 || port != endpoint.Port)
            throw new InvalidDataException("В ссылке должны быть корректные адрес и порт сервера.");
        var node = NewShareNode(shadowsocks ? "ss" : "socks5", endpoint.Host.Trim('[', ']'), port);
        if (separator >= 0 || shadowsocks)
        {
            if (separator <= 0) throw new InvalidDataException("В ссылке нет данных подключения.");
            var credentials = authority[..separator];
            var encoded = !wholeBase64 && !credentials.Contains(':');
            var pair = (encoded ? DecodeShareLink(credentials) : credentials).Split(':', 2);
            if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0)
                throw new InvalidDataException("Некорректные данные подключения в ссылке.");
            string Decode(string value) => SafeText(wholeBase64 || encoded ? value : Uri.UnescapeDataString(value));
            var methodOrUser = Decode(pair[0]);
            if (shadowsocks && methodOrUser is not ("aes-128-gcm" or "aes-192-gcm" or "aes-256-gcm"
                or "chacha20-ietf-poly1305" or "xchacha20-ietf-poly1305"
                or "2022-blake3-aes-128-gcm" or "2022-blake3-aes-256-gcm" or "2022-blake3-chacha20-poly1305"
                or "aes-128-ctr" or "aes-192-ctr" or "aes-256-ctr" or "aes-128-cfb" or "aes-192-cfb" or "aes-256-cfb"
                or "rc4-md5" or "chacha20-ietf" or "xchacha20" or "none"))
                throw new InvalidDataException("Неподдерживаемый шифр Shadowsocks.");
            Put(node, shadowsocks ? "cipher" : "username", methodOrUser);
            Put(node, "password", Decode(pair[1]));
        }
        return FinishShareLink(node, label, shadowsocks ? "Shadowsocks" : "SOCKS5", false);
    }

    private static string DecodeShareLink(string value)
    {
        try
        {
            var base64 = Uri.UnescapeDataString(value).Replace('-', '+').Replace('_', '/');
            if (base64.Any(char.IsWhiteSpace)) throw new FormatException();
            return ShareLinkUtf8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("Некорректные Base64-данные в ссылке подключения.");
        }
    }

    private static YamlMappingNode NewShareNode(string type, string server, int port)
    {
        var node = new YamlMappingNode();
        Put(node, "type", type);
        Put(node, "server", server);
        Put(node, "port", port.ToString(CultureInfo.InvariantCulture));
        Put(node, "udp", "true");
        return node;
    }

    private static void SetShareTls(YamlMappingNode node, bool tls, bool insecure, string sni, string alpn, string fingerprint)
    {
        if (!tls)
        {
            if (insecure || sni.Length > 0 || alpn.Length > 0 || fingerprint.Length > 0)
                throw new InvalidDataException("Параметры TLS требуют включённого TLS в ссылке.");
            return;
        }
        Put(node, "tls", "true");
        if (insecure) Put(node, "skip-cert-verify", "true");
        if (sni.Length > 0) Put(node, "servername", SafeText(sni));
        if (fingerprint.Length > 0) Put(node, "client-fingerprint", SafeText(fingerprint));
        if (alpn.Length > 0) node.Add("alpn", new YamlSequenceNode(alpn.Split(',').Select(value => new YamlScalarNode(SafeText(value.Trim())))));
    }

    private static void SetShareTransport(YamlMappingNode node, string transport, string path, string host, string service)
    {
        if (transport is "tcp" or "raw") return;
        if (transport is not ("ws" or "grpc" or "httpupgrade"))
            throw new InvalidDataException("Этот транспорт в ссылке пока не поддерживается.");
        Put(node, "network", transport == "httpupgrade" ? "ws" : transport);
        AddTransport(node, transport, path, host, service, null);
    }

    private static ImportedProfile FinishShareLink(YamlMappingNode node, string label, string protocol, bool insecure)
    {
        var needsName = string.IsNullOrWhiteSpace(label);
        Put(node, "name", needsName ? NodeName : ValidateConnectionName(label, renameReserved: true));
        return BuildConnections([node], protocol, insecure) with { NeedsName = needsName };
    }
}
