using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

// Only connection share links are carried across. Provider routing, listeners,
// file-based certificates and process settings never enter our Xray runtime.
internal static class XrayProfile
{
    internal const string NodeType = "xray";
    private static InvalidDataException Invalid() => new("Некорректные или неподдерживаемые параметры подключения Xray. Настройки не упрощены.");
    internal static bool Requires(Uri uri, IReadOnlyDictionary<string, string> options) =>
        options.ContainsKey("fm") || options.ContainsKey("pcs") || options.ContainsKey("extra")
        || options.GetValueOrDefault("type", "").ToLowerInvariant() is "xhttp" or "splithttp";

    internal static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (!result.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair.ElementAtOrDefault(1) ?? ""))) throw Invalid();
        }
        return result;
    }

    internal static ImportedProfile Import(string link)
    {
        var (uri, outbound) = Parse(link);
        var node = new YamlMappingNode
        {
            { "name", ProfileImporter.ConnectionNameFromLink(uri) }, { "type", NodeType },
            { "server", uri.Host.Trim('[', ']') }, { "port", (uri.Port == -1 ? 443 : uri.Port).ToString(CultureInfo.InvariantCulture) },
            { "xray-link", link }
        };
        return ProfileImporter.BuildConnections([node], "Xray · " + (uri.Scheme == "vless" ? "VLESS" : "Hysteria2"), false)
            with { NeedsName = !ProfileImporter.HasLinkName(uri) };
    }

    internal static JsonObject FromNode(YamlMappingNode node)
    {
        if(node.Children.ContainsKey(new YamlScalarNode("xray-outbound"))) return XrayJsonConnection.FromNode(node);
        string Text(string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value ?? "" : "";
        if (node.Children.Keys.Any(key => key.ToString() is not ("name" or "type" or "server" or "port" or "xray-link"))) throw Invalid();
        var (uri, outbound) = Parse(Text("xray-link"));
        if (Text("type") != NodeType || Text("server") != uri.Host.Trim('[', ']')
            || Text("port") != (uri.Port == -1 ? 443 : uri.Port).ToString(CultureInfo.InvariantCulture)) throw Invalid();
        return outbound;
    }

    internal static (Uri Uri, JsonObject Outbound) Parse(string link)
    {
        if (link.Length > ProfileImporter.MaxInputLength || link.Any(char.IsWhiteSpace)
            || Regex.IsMatch(link, "%[^0-9A-Fa-f]|%[0-9A-Fa-f](?![0-9A-Fa-f])|%$")
            || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.AbsolutePath is not ("" or "/")
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) throw Invalid();
        var hy = uri.Scheme is "hy2" or "hysteria2";
        if (!hy && uri.Scheme != "vless") throw Invalid();
        var port = hy && uri.Port == -1 ? 443 : uri.Port;
        if (port is < 1 or > 65535) throw Invalid();
        var q = Query(uri);
        var allowed = hy ? new[] { "sni", "insecure", "alpn", "obfs", "obfs-password", "fm", "pcs" }
            : new[] { "encryption", "security", "sni", "allowInsecure", "insecure", "type", "host", "path", "flow", "fp", "alpn", "pbk", "sid", "serviceName", "mode", "headerType", "fm", "pcs", "extra" };
        if (q.Keys.Any(key => !allowed.Contains(key, StringComparer.OrdinalIgnoreCase))) throw Invalid();
        string Get(string key, string fallback = "") => q.GetValueOrDefault(key, fallback);
        // Current Xray removed allowInsecure. Never silently turn an insecure
        // request into a different certificate-validation policy, or drop a pin.
        foreach (var flag in new[] { "insecure", "allowInsecure" })
            if (q.ContainsKey(flag) && Get(flag) is not ("0" or "false")) throw Invalid();
        var network = hy ? "hysteria" : Get("type", "tcp").ToLowerInvariant();
        if (network is not ("tcp" or "raw" or "grpc" or "ws" or "httpupgrade" or "xhttp" or "splithttp" or "hysteria")) throw Invalid();
        var security = hy ? "tls" : Get("security", "none").ToLowerInvariant();
        if (security is not ("none" or "tls" or "reality") || (!hy && Get("encryption", "none") != "none")) throw Invalid();
        var id = Uri.UnescapeDataString(uri.UserInfo);
        if (id.Length == 0 || id.Any(char.IsControl) || (!hy && !Guid.TryParse(id, out _))) throw Invalid();
        var stream = new JsonObject { ["network"] = network, ["security"] = security };
        var outbound = new JsonObject { ["protocol"] = hy ? "hysteria" : "vless", ["streamSettings"] = stream };
        if (hy)
        {
            outbound["settings"] = new JsonObject { ["version"] = 2, ["address"] = uri.Host.Trim('[', ']'), ["port"] = port };
            stream["hysteriaSettings"] = new JsonObject { ["version"] = 2, ["auth"] = id };
        }
        else
        {
            var flow = Get("flow");
            if (flow is not ("" or "xtls-rprx-vision") || flow.Length > 0 && (network is not ("tcp" or "raw") || security == "none")) throw Invalid();
            outbound["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject {
                ["address"] = uri.Host.Trim('[', ']'), ["port"] = port,
                ["users"] = new JsonArray(new JsonObject { ["id"] = id, ["encryption"] = "none", ["flow"] = flow }) }) };
        }
        if (security != "none")
        {
            var sni = Get("sni", uri.Host.Trim('[', ']'));
            if (Uri.CheckHostName(sni) == UriHostNameType.Unknown) throw Invalid();
            var tls = new JsonObject { ["serverName"] = sni };
            if (Get("fp").Length > 0 || !hy) tls["fingerprint"] = Get("fp", "chrome");
            if (Get("alpn").Length > 0) tls["alpn"] = new JsonArray(Get("alpn").Split(',').Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            if (security == "tls")
            {
                if (q.ContainsKey("pcs"))
                {
                    if (!Regex.IsMatch(Get("pcs"), "^[A-Fa-f0-9]{64}$")) throw Invalid();
                    tls["pinnedPeerCertSha256"] = Get("pcs");
                }
                stream["tlsSettings"] = tls;
            }
            else
            {
                if (q.ContainsKey("pcs") || Get("alpn").Length > 0 || !Regex.IsMatch(Get("pbk"), "^[A-Za-z0-9_-]{43}=?$") || !Regex.IsMatch(Get("sid"), "^(?:[A-Fa-f0-9]{2}){0,8}$")) throw Invalid();
                tls.Remove("alpn");
                tls["publicKey"] = Get("pbk"); tls["shortId"] = Get("sid");
                stream["realitySettings"] = tls;
            }
        }
        else if (q.ContainsKey("pcs")) throw Invalid();
        if (q.ContainsKey("fm"))
        {
            var fm = JsonOptions(Get("fm"));
            if (fm.Any(p => p.Key is not ("tcp" or "udp" or "quicParams"))) throw Invalid();
            stream["finalmask"] = fm;
        }
        if (hy)
        {
            var obfs = Get("obfs");
            if (obfs is not ("" or "salamander") || (obfs.Length == 0) != (Get("obfs-password").Length == 0)) throw Invalid();
            if (obfs.Length > 0)
            {
                var fm = stream["finalmask"] as JsonObject ?? new JsonObject();
                var masks = fm["udp"] as JsonArray ?? new JsonArray();
                masks.Add(new JsonObject { ["type"] = "salamander", ["settings"] = new JsonObject { ["password"] = Get("obfs-password") } });
                if (fm["udp"] is null) fm["udp"] = masks;
                if (stream["finalmask"] is null) stream["finalmask"] = fm;
            }
        }
        else if (network is "xhttp" or "splithttp")
        {
            if (Get("mode", "auto") is not ("auto" or "packet-up" or "stream-up" or "stream-one")) throw Invalid();
            var settings = new JsonObject { ["host"] = Get("host"), ["path"] = Get("path", "/"), ["mode"] = Get("mode", "auto") };
            if (q.ContainsKey("extra")) settings["extra"] = JsonOptions(Get("extra"));
            stream["xhttpSettings"] = settings;
        }
        else
        {
            if (q.ContainsKey("extra")) throw Invalid();
            if (network == "grpc")
            {
                if (Get("mode", "gun") is not ("gun" or "multi")) throw Invalid();
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = Get("serviceName"), ["multiMode"] = Get("mode") == "multi" };
            }
            else if (q.ContainsKey("mode")) throw Invalid();
            if (network is "ws" or "httpupgrade")
                stream[network == "ws" ? "wsSettings" : "httpupgradeSettings"] = new JsonObject { ["host"] = Get("host"), ["path"] = Get("path", "/") };
        }
        if (Get("headerType", "none") is not ("" or "none")) throw Invalid();
        return (uri, outbound);
    }

    private static JsonObject JsonOptions(string text)
    {
        try
        {
            // Reserve space for streamSettings, outbound and subscription
            // wrappers so a valid link also survives JSON storage and reload.
            var options = new JsonDocumentOptions { MaxDepth = ProfileImporter.MaxJsonDepth - 8 };
            using var parsed = JsonDocument.Parse(text, options);
            XrayFullProfile.ValidateTree(parsed.RootElement);
            void Validate(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in element.EnumerateObject())
                    {
                        // Inline CA certificates are connection data. Local
                        // certificate/key files remain unsupported.
                        if (!keys.Add(field.Name) || field.Name.ToLowerInvariant() is "certificatefile" or "keyfile" or "masterkeylog" or "sockopt" or "dialerproxy" or "proxysettings") throw Invalid();
                        Validate(field.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) Validate(value);
            }
            Validate(parsed.RootElement);
            return JsonNode.Parse(text, documentOptions: options) as JsonObject ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
    }
}
