using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

// The saved/edited profile remains Clash YAML. Only a private runtime JSON is generated.
// Unknown proxy options/rules fail closed instead of silently losing security or routing.
internal static class SingBoxConfig
{
    private static Dictionary<string, object> Obj(params (string Key, object Value)[] fields) => fields.ToDictionary(field => field.Key, field => field.Value);
    private static YamlNode? Get(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    private static string Text(YamlMappingNode node, string key, string fallback = "") => (Get(node, key) as YamlScalarNode)?.Value ?? fallback;
    private static bool Flag(YamlMappingNode node, string key) => Get(node, key) is null ? false
        : bool.TryParse(Text(node, key), out var value) ? value : throw Unsupported("формат переключателя подключения");
    private static string[] List(YamlMappingNode node, string key) => Get(node, key) is YamlSequenceNode sequence
        ? sequence.Children.Select(item => item is YamlScalarNode { Value: { } value } ? value : throw Unsupported("список параметров")).ToArray() : [];
    private static InvalidDataException Unsupported(string feature) => new("sing-box: пока не поддерживается " + feature + ". Профиль не изменён; можно вернуться на Mihomo.");
    private static void Only(YamlMappingNode node, params string[] allowed)
    {
        foreach (var key in node.Children.Keys)
        {
            if (key is not YamlScalarNode { Value: { } name }) throw Unsupported("формат названия параметра подключения");
            if (!allowed.Contains(name))
                throw Unsupported("параметр «" + (Regex.IsMatch(name, "^[a-z][a-z0-9-]{0,48}$") ? name : "неизвестный") + "»");
        }
    }
    private static int Number(YamlMappingNode node, string key, int fallback = 0) => int.TryParse(Text(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    internal static string Build(ClashConfigDocument document, int controllerPort, string secret, bool fullTunnel,
        int speedPort = 0, string? selectedServer = null, bool includeTun = true)
    {
        // Reuse the tested full-VPN exception selection / pruning, without adding Mihomo listeners.
        var yaml = new YamlStream(); yaml.Load(new StringReader(document.BuildRuntimeText(controllerPort, secret, fullTunnel)));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        if (Get(root, "proxy-providers") is YamlMappingNode { Children.Count: > 0 }) throw Unsupported("proxy-providers");
        if (Get(root, "listeners") is YamlSequenceNode { Children.Count: > 0 }) throw Unsupported("пользовательские listeners");
        if (Get(root, "sub-rules") is YamlMappingNode { Children.Count: > 0 }) throw Unsupported("sub-rules");
        if (Text(root, "interface-name").Length > 0 || Get(root, "routing-mark") is not null) throw Unsupported("привязка исходящего интерфейса");
        var vpn = document.VpnRoute ?? "DIRECT"; // Empty templates are valid; startup separately requires a VPN connection.
        // Full-TUN rules already flatten deterministic exception aliases. sing-box
        // has no reject outbound: omit only those now-unreferenced blocking aliases.
        var exceptions = ClashConfigDocument.ExceptionRoutes(root);
        bool FlattenedBlock(YamlMappingNode node) => fullTunnel && exceptions.GetValueOrDefault(Text(node, "name")) is "REJECT" or "REJECT-DROP";
        var outbounds = new List<Dictionary<string, object>> { Obj(("type", "direct"), ("tag", "DIRECT")) };
        var names = new HashSet<string>(StringComparer.Ordinal) { "DIRECT", "REJECT", "REJECT-DROP" };
        if (Get(root, "proxies") is YamlSequenceNode proxies)
            foreach (var proxy in proxies.Children)
            {
                if (proxy is not YamlMappingNode node) throw Unsupported("формат proxies");
                if (FlattenedBlock(node)) continue;
                var name = Text(node, "name");
                if (name.Length == 0 || !names.Add(name)) throw Unsupported("повторяющееся/зарезервированное имя подключения");
                outbounds.Add(Proxy(node));
            }
        if (Get(root, "proxy-groups") is YamlSequenceNode groups)
            foreach (var group in groups.Children)
            {
                if (group is not YamlMappingNode node) throw Unsupported("формат proxy-groups");
                if (FlattenedBlock(node)) continue;
                Only(node, "name", "type", "proxies", "url", "interval", "tolerance", "lazy", "hidden", "icon");
                var name = Text(node, "name"); var type = Text(node, "type");
                if (name.Length == 0 || !names.Add(name)) throw Unsupported("повторяющееся имя группы");
                if (type is not ("select" or "url-test")) throw Unsupported("тип VPN-группы");
                var members = List(node, "proxies");
                if (members.Length == 0 || members.Any(member => member is "REJECT" or "REJECT-DROP")) throw Unsupported("пустая группа или блокировка внутри группы");
                var converted = Obj(("type", type == "select" ? "selector" : "urltest"), ("tag", name), ("outbounds", members));
                if (type == "select") converted["default"] = selectedServer is not null && members.Contains(selectedServer) ? selectedServer : members[0];
                else
                {
                    converted["url"] = Text(node, "url", "https://www.gstatic.com/generate_204");
                    converted["interval"] = Math.Max(10, Number(node, "interval", 300)) + "s";
                    converted["tolerance"] = Math.Max(0, Number(node, "tolerance", 50));
                }
                outbounds.Add(converted);
            }
        foreach (var group in outbounds.Where(item => item.ContainsKey("outbounds")))
            if (((string[])group["outbounds"]).Any(member => !names.Contains(member))) throw Unsupported("ссылка группы на неизвестное подключение");
        var internalPrefix = "morphocyte-" + secret[..12] + "-";
        string Internal(string name)
        {
            var tag = internalPrefix + name;
            if (names.Contains(tag)) throw Unsupported("конфликт внутренних имён");
            return tag;
        }
        var tunTag = Internal("tun"); var speedTag = Internal("speed");
        var bootstrap = Internal("bootstrap");
        var rules = new List<Dictionary<string, object>>();
        if (speedPort != 0)
        {
            if (speedPort is < 1 or > 65535 || speedPort == controllerPort) throw new ArgumentException("Некорректный порт замера.");
            rules.Add(Obj(("inbound", new[] { speedTag }), ("action", "route"), ("outbound", vpn)));
        }
        // Resolve DNS before applying connection rules; sniff supports browsers using encrypted DNS.
        rules.Add(Obj(("action", "sniff")));
        rules.Add(Obj(("protocol", "dns"), ("action", "hijack-dns")));
        // Drop packets to the adapter's own addresses to avoid a loop through a direct outbound.
        rules.Add(Obj(("ip_cidr", new[] { "172.19.0.1/32", "fdfe:dcba:9876::1/128" }), ("action", "reject"), ("method", "drop")));
        var domainRoutes = new List<Dictionary<string, object>>();
        if (Get(root, "rules") is not YamlSequenceNode yamlRules) throw Unsupported("формат правил");
        var final = "DIRECT";
        var matched = false;
        foreach (var value in yamlRules.Children)
        {
            if (matched) throw Unsupported("правила после MATCH");
            var parts = value.ToString().Split(',').Select(part => part.Trim()).ToArray();
            var kind = parts[0].ToUpperInvariant();
            var noResolve = parts[^1].Equals("no-resolve", StringComparison.OrdinalIgnoreCase);
            if (noResolve) parts = parts[..^1];
            var match = kind == "MATCH";
            if (parts.Length != (match ? 2 : 3)) throw Unsupported("составное правило или модификатор правил");
            var target = parts[^1];
            if (!names.Contains(target)) throw Unsupported("неизвестный маршрут правила");
            var block = target is "REJECT" or "REJECT-DROP";
            var rule = block ? Obj(("action", "reject"), ("method", target == "REJECT-DROP" ? "drop" : "default")) : Obj(("action", "route"), ("outbound", target));
            if (match)
            {
                matched = true;
                if (block) rules.Add(rule); else final = target;
                continue;
            }
            var key = kind switch {
                "DOMAIN" => "domain", "DOMAIN-SUFFIX" => "domain_suffix", "DOMAIN-KEYWORD" => "domain_keyword",
                "PROCESS-NAME" or "PROCESS-PATH" => "process_path_regex", "IP-CIDR" or "IP-CIDR6" => "ip_cidr",
                "SRC-IP-CIDR" => "source_ip_cidr", "DST-PORT" => "port", "SRC-PORT" => "source_port", "NETWORK" => "network",
                _ => throw Unsupported("тип правила " + Regex.Replace(kind, "[^A-Z-]", ""))
            };
            if (key is "port" or "source_port")
            {
                if (!ushort.TryParse(parts[1], out var port) || port == 0) throw Unsupported("диапазон портов");
                rule[key] = new[] { (int)port };
            }
            else if (kind == "PROCESS-NAME")
                // sing-box's exact-name map is case-sensitive, unlike Windows filenames.
                // Match the whole basename, never a substring of a different executable.
                rule[key] = new[] { @"(?i)(?:^|[\\/])" + Regex.Escape(parts[1]) + "$" };
            else if (kind == "PROCESS-PATH")
                rule[key] = new[] { "(?i)^" + string.Join(@"[\\/]", parts[1].Split(['\\', '/']).Select(Regex.Escape)) + "$" };
            else rule[key] = new[] { kind == "NETWORK" ? parts[1].ToLowerInvariant() : parts[1] };
            // Clash resolves a domain before ordinary destination IP rules, but not
            // for no-resolve rules. sing-box does not do this implicitly for FakeIP.
            if (kind is "IP-CIDR" or "IP-CIDR6" && !noResolve)
                rules.Add(Obj(("action", "resolve"), ("timeout", "5s")));
            rules.Add(rule);
            if (key is "domain" or "domain_suffix" or "domain_keyword")
                domainRoutes.Add(Obj((key, new[] { parts[1] }), ("outbound", exceptions.GetValueOrDefault(target, target))));
        }
        var mixedPort = Number(root, "mixed-port");
        var bootstrapPort = DnsPort(controllerPort, speedPort, mixedPort);
        var directPort = DnsPort(controllerPort, speedPort, mixedPort, bootstrapPort);
        var dns = SingBoxDns.Build(root, vpn, tunTag, Internal, domainRoutes, bootstrapPort, directPort);
        // Resolver-only loopback listeners let domain_resolver use the same native
        // DNS races as normal queries. Numeric bootstrap upstreams break recursion.
        rules.Insert(speedPort == 0 ? 0 : 1, Obj(("inbound", new[] { Internal("bootstrap-in"), Internal("direct-dns-in") }), ("action", "hijack-dns")));
        foreach (var outbound in outbounds.Where(item => (string)item["type"] == "direct"))
            outbound["domain_resolver"] = Internal("direct-resolver");
        var inbounds = new List<Dictionary<string, object>>();
        if (includeTun)
        {
            var tun = Get(root, "tun") as YamlMappingNode ?? new YamlMappingNode();
            var stack = Text(tun, "stack", "gvisor");
            if (stack == "mixed") stack = "gvisor"; // Existing Windows default.
            if (stack is not ("system" or "gvisor")) throw Unsupported("стек TUN");
            var mtu = Get(tun, "mtu") is null ? 1280 : Number(tun, "mtu");
            if (mtu is < 1280 or > 65535) throw Unsupported("MTU TUN (1280–65535)");
            inbounds.Add(Obj(("type", "tun"), ("tag", tunTag), ("interface_name", "MorphocyteTUN"),
                ("address", new[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" }), ("mtu", mtu), ("stack", stack),
                ("auto_route", true), ("strict_route", true), ("dns_mode", "hijack")));
        }
        inbounds.Add(Obj(("type", "direct"), ("tag", Internal("bootstrap-in")), ("listen", "127.0.0.1"), ("listen_port", bootstrapPort)));
        inbounds.Add(Obj(("type", "direct"), ("tag", Internal("direct-dns-in")), ("listen", "127.0.0.1"), ("listen_port", directPort)));
        // All listeners are loopback-only; the speed-test listener also requires authentication.
        if (mixedPort > 0 && mixedPort != controllerPort && mixedPort != speedPort)
            inbounds.Add(Obj(("type", "mixed"), ("tag", Internal("mixed")), ("listen", "127.0.0.1"), ("listen_port", mixedPort)));
        if (speedPort != 0)
            inbounds.Add(Obj(("type", "socks"), ("tag", speedTag), ("listen", "127.0.0.1"), ("listen_port", speedPort),
                ("users", new[] { Obj(("username", "morphocyte"), ("password", secret)) })));
        var config = Obj(("log", Obj(("level", "info"), ("timestamp", true))),
            ("inbounds", inbounds), ("outbounds", outbounds),
            ("route", Obj(("auto_detect_interface", true), ("default_domain_resolver", bootstrap), ("rules", rules), ("final", final))),
            ("dns", dns),
            ("experimental", Obj(("cache_file", Obj(("enabled", true), ("store_fakeip", true),
                ("path", Path.Combine(Path.GetDirectoryName(document.Path)!, ".morphocyte-singbox-" +
                    ClashConfigDocument.Hash(document.Path.ToUpperInvariant())[..16] + ".db")))),
                ("clash_api", Obj(("external_controller", "127.0.0.1:" + controllerPort), ("secret", secret),
                ("default_mode", "Rule"), ("access_control_allow_origin", new[] { "http://127.0.0.1" }))))));
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static int DnsPort(params int[] excluded)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            if (excluded.Contains(port)) continue;
            try { using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); return port; }
            catch (SocketException) { }
        }
        throw new IOException("Не удалось выделить локальный порт DNS.");
    }

    private static Dictionary<string, object> Proxy(YamlMappingNode node)
    {
        var type = Text(node, "type");
        if (type is not ("vless" or "socks5" or "http" or "ss" or "trojan" or "vmess" or "direct")) throw Unsupported("тип подключения");
        var allowed = new List<string> { "name", "type", "server", "port", "udp" };
        if (type is "vless" or "vmess" or "trojan" or "http") allowed.AddRange(["tls", "servername", "sni", "skip-cert-verify", "client-fingerprint", "alpn"]);
        if (type is "vless" or "vmess" or "trojan") allowed.AddRange(["network", "ws-opts", "grpc-opts"]);
        if (type is "vless" or "vmess") allowed.Add("uuid");
        if (type == "vless") allowed.AddRange(["flow", "reality-opts", "packet-encoding"]);
        if (type == "vmess") allowed.AddRange(["alterId", "security", "cipher"]);
        if (type == "ss") allowed.Add("cipher");
        if (type is "socks5" or "http") allowed.Add("username");
        if (type is "socks5" or "http" or "ss" or "trojan") allowed.Add("password");
        Only(node, type == "direct" ? ["name", "type"] : allowed.ToArray());
        var output = Obj(("type", type switch { "socks5" => "socks", "ss" => "shadowsocks", _ => type }), ("tag", Text(node, "name")));
        if (type == "direct") return output;
        var port = Number(node, "port");
        if (port is < 1 or > 65535 || Text(node, "server").Length == 0) throw Unsupported("адрес/порт подключения");
        output["server"] = Text(node, "server"); output["server_port"] = port;
        if (type is "vless" or "vmess") output["uuid"] = Text(node, "uuid");
        if (type == "vless" && Text(node, "flow").Length > 0) output["flow"] = Text(node, "flow");
        if (type == "vless" && Get(node, "packet-encoding") is not null)
        {
            var encoding = Text(node, "packet-encoding");
            if (encoding is not ("xudp" or "packetaddr" or "" or "none")) throw Unsupported("значение packet-encoding");
            output["packet_encoding"] = encoding == "none" ? "" : encoding;
        }
        if (type == "vmess") { output["security"] = Text(node, "security", Text(node, "cipher", "auto")); output["alter_id"] = Number(node, "alterId"); }
        if (type == "ss") output["method"] = Text(node, "cipher");
        if (type is "socks5" or "http" && Text(node, "username").Length > 0) output["username"] = Text(node, "username");
        if (type is "socks5" or "http" or "ss" or "trojan" && Text(node, "password").Length > 0) output["password"] = Text(node, "password");
        if (type == "socks5") output["version"] = "5";
        if (Get(node, "udp") is not null && !Flag(node, "udp") && type != "http") output["network"] = "tcp";
        if (Flag(node, "tls") || type == "trojan")
        {
            var tls = Obj(("enabled", true), ("insecure", Flag(node, "skip-cert-verify")));
            var sni = Text(node, "servername", Text(node, "sni"));
            if (sni.Length == 0 && Text(node, "network") == "ws" && Get(node, "ws-opts") is YamlMappingNode wsTls
                && Get(wsTls, "headers") is YamlMappingNode wsHeaders)
                sni = wsHeaders.Children.FirstOrDefault(pair => pair.Key.ToString().Equals("Host", StringComparison.OrdinalIgnoreCase)).Value?.ToString().Split(',')[0].Trim() ?? "";
            if (sni.Length > 0) tls["server_name"] = sni;
            if (List(node, "alpn") is { Length: > 0 } alpn) tls["alpn"] = alpn;
            if (Text(node, "client-fingerprint") is { Length: > 0 } fingerprint) tls["utls"] = Obj(("enabled", true), ("fingerprint", fingerprint));
            if (Get(node, "reality-opts") is YamlMappingNode reality)
            {
                Only(reality, "public-key", "short-id");
                tls["reality"] = Obj(("enabled", true), ("public_key", Text(reality, "public-key")), ("short_id", Text(reality, "short-id")));
            }
            output["tls"] = tls;
        }
        var network = Text(node, "network", "tcp");
        if (network is "ws" or "grpc")
        {
            var transport = Obj(("type", network));
            if (network == "grpc")
            {
                if (Get(node, "grpc-opts") is YamlMappingNode grpc) { Only(grpc, "grpc-service-name"); transport["service_name"] = Text(grpc, "grpc-service-name"); }
            }
            else if (Get(node, "ws-opts") is YamlMappingNode ws)
            {
                Only(ws, "path", "headers", "max-early-data", "early-data-header-name", "v2ray-http-upgrade");
                transport["path"] = Text(ws, "path", "/");
                var headers = Get(ws, "headers") is YamlMappingNode values ? values.Children.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.ToString()) : new Dictionary<string, string>();
                if (Flag(ws, "v2ray-http-upgrade"))
                {
                    transport["type"] = "httpupgrade";
                    if (headers.Remove("Host", out var host)) transport["host"] = host;
                    if (Number(ws, "max-early-data") > 0) throw Unsupported("early-data для HTTPUpgrade");
                }
                else if (Number(ws, "max-early-data") > 0)
                {
                    transport["max_early_data"] = Number(ws, "max-early-data");
                    transport["early_data_header_name"] = Text(ws, "early-data-header-name", "Sec-WebSocket-Protocol");
                }
                if (!Flag(ws, "v2ray-http-upgrade")) NormalizeWebSocketEarlyData(transport, ws);
                if (headers.Count > 0) transport["headers"] = headers;
            }
            output["transport"] = transport;
        }
        else if (network is not ("tcp" or "raw")) throw Unsupported("транспорт подключения");
        return output;
    }

    private static void NormalizeWebSocketEarlyData(Dictionary<string, object> transport, YamlMappingNode ws)
    {
        var path = (string)transport["path"];
        var question = path.IndexOf('?');
        if (question < 0) return;
        var segments = path[(question + 1)..].Split('&');
        var early = segments.Where(part => part.StartsWith("ed=", StringComparison.Ordinal)).ToArray();
        var header = segments.Where(part => part.StartsWith("eh=", StringComparison.Ordinal)).ToArray();
        if (early.Length == 0) return;
        if (early.Length != 1 || header.Length > 1 || !int.TryParse(early[0][3..], out var bytes) || bytes is < 0 or > 65536)
            throw Unsupported("early-data в пути WebSocket");
        if (Get(ws, "max-early-data") is not null && Number(ws, "max-early-data") != bytes)
            throw Unsupported("противоречащий early-data WebSocket");
        var name = header.Length == 0 ? Text(ws, "early-data-header-name", "Sec-WebSocket-Protocol") : Uri.UnescapeDataString(header[0][3..]);
        if (name.Length == 0 || !Regex.IsMatch(name, "^[A-Za-z0-9!#$%&'*+.^_`|~-]+$")) throw Unsupported("заголовок early-data WebSocket");
        var remaining = segments.Where(part => !part.StartsWith("ed=", StringComparison.Ordinal) && !part.StartsWith("eh=", StringComparison.Ordinal)).ToArray();
        transport["path"] = path[..question] + (remaining.Length == 0 ? "" : "?" + string.Join('&', remaining));
        if (bytes > 0) { transport["max_early_data"] = bytes; transport["early_data_header_name"] = name; }
    }
}
