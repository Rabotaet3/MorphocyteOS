using System.IO;
using System.Net;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal static class SingBoxDns
{
    private static Dictionary<string, object> Obj(params (string Key, object Value)[] fields) => fields.ToDictionary(field => field.Key, field => field.Value);
    private static YamlNode? Get(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    private static string Text(YamlMappingNode node, string key) => (Get(node, key) as YamlScalarNode)?.Value ?? "";
    private static InvalidDataException Unsupported(string key) => new("sing-box: пока не поддерживается параметр «dns." + key + "». Профиль не изменён; можно вернуться на Mihomo.");
    private static string[] Endpoints(YamlNode? value, string key, params string[] defaults)
    {
        var entries = value switch {
            null => [], YamlScalarNode { Value: null or "" } => [],
            YamlScalarNode { Value: { } scalar } => new[] { scalar },
            YamlSequenceNode list => list.Children.Select(item => item is YamlScalarNode { Value: { } entry } ? entry : throw Unsupported(key)).ToArray(),
            _ => throw Unsupported(key)
        };
        if (entries.Length > 8) throw Unsupported(key);
        return entries.Length == 0 ? defaults : entries.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static Dictionary<string, object> Build(YamlMappingNode root, string vpn, string tunTag,
        Func<string, string> tag, IReadOnlyList<Dictionary<string, object>> domainRoutes, int bootstrapPort, int directPort,
        string? routedDetour = null)
    {
        var dns = Get(root, "dns") as YamlMappingNode ?? new YamlMappingNode();
        // These settings cannot safely be approximated by a plain resolver race.
        foreach (var key in new[] { "proxy-server-nameserver-policy", "fake-ip-filter-mode" })
            if (Get(dns, key) is not null) throw Unsupported(key);
        if (Get(dns, "fallback-filter") is YamlMappingNode filter
            && filter.Children.Any(pair => pair.Key.ToString() != "geoip" || pair.Value.ToString() != "false")) throw Unsupported("fallback-filter");
        var servers = new List<Dictionary<string, object>>();
        var rules = new List<Dictionary<string, object>>();
        bool Enabled(string key, bool fallback) => Get(dns, key) is null ? fallback
            : bool.TryParse(Text(dns, key), out var flag) ? flag : throw Unsupported(key);
        var useHosts = Enabled("use-hosts", true);
        var useSystemHosts = Enabled("use-system-hosts", true);
        var predefined = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (useHosts && Get(root, "hosts") is { } hostsNode)
        {
            if (hostsNode is not YamlMappingNode hosts || hosts.Children.Count > 10000) throw Unsupported("hosts");
            foreach (var entry in hosts.Children)
            {
                var domain = entry.Key.ToString().TrimEnd('.');
                if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Contains('*')) throw Unsupported("hosts: шаблон или псевдоним");
                var values = entry.Value is YamlSequenceNode addresses ? addresses.Children.ToArray() : new[] { entry.Value };
                if (values.Length is 0 or > 64 || values.Any(value => value is not YamlScalarNode || !IPAddress.TryParse(value.ToString(), out _)))
                    throw Unsupported("hosts: адрес");
                if (!predefined.TryAdd(domain.ToLowerInvariant(), values.Select(value => IPAddress.Parse(value.ToString()).ToString()).ToArray()))
                    throw Unsupported("hosts: повторяющееся имя");
            }
        }
        if (Text(dns, "ipv6") == "false") rules.Add(Obj(("query_type", new[] { "AAAA" }), ("action", "predefined"), ("rcode", "NOERROR")));
        if (useSystemHosts || predefined.Count > 0)
        {
            var hostsTag = tag("hosts");
            servers.Add(Obj(("type", "hosts"), ("tag", hostsTag), ("predefined", predefined)));
            var match = useSystemHosts ? Obj(("preferred_by", new[] { hostsTag })) : Obj(("domain", predefined.Keys.ToArray()));
            rules.Add(new Dictionary<string, object>(match) { ["query_type"] = new[] { "A", "AAAA" }, ["action"] = "route", ["server"] = hostsTag });
        }
        var bootstrap = tag("bootstrap");
        var directResolver = tag("direct-resolver");
        var bootstrapEndpoints = Endpoints(Get(dns, "proxy-server-nameserver") ?? Get(dns, "default-nameserver"), "proxy-server-nameserver", "1.1.1.1", "8.8.8.8");
        // Numeric bootstrap endpoints cannot recursively depend on the VPN server's hostname.
        var bootstrapTargets = bootstrapEndpoints.Select((endpoint, index) => {
            var serverTag = tag("bootstrap-upstream-" + index);
            servers.Add(Server(endpoint, serverTag, null, bootstrap, true)); return serverTag;
        }).ToArray();
        // Only DNS packets from these loopback listeners can enter the resolver
        // races. No proxy credentials or arbitrary forwarding are exposed.
        servers.Add(Obj(("type", "udp"), ("tag", bootstrap), ("server", "127.0.0.1"), ("server_port", bootstrapPort), ("inet4_bind_address", "127.0.0.1")));
        servers.Add(Obj(("type", "udp"), ("tag", directResolver), ("server", "127.0.0.1"), ("server_port", directPort), ("inet4_bind_address", "127.0.0.1")));

        string[] AddServers(string key, string[] endpoints, string? detour)
        {
            return endpoints.Select((endpoint, index) => {
                var serverTag = tag(key + "-" + index);
                servers.Add(Server(endpoint, serverTag, detour, bootstrap, false)); return serverTag;
            }).ToArray();
        }
        var resolutionIndex = 0;
        void Resolve(Dictionary<string, object> match, string[] targets)
        {
            if (targets.Length == 0) throw Unsupported("nameserver-policy");
            if (targets.Length == 1)
            {
                rules.Add(new Dictionary<string, object>(match) { ["action"] = "route", ["server"] = targets[0], ["timeout"] = "5s" }); return;
            }
            // sing-box 1.14 evaluates upstreams concurrently. Failed/SERVFAIL answers
            // cannot win; the first valid response wins without waiting for a dead server.
            var evaluations = targets.Select((target, index) => (Target: target, Response: tag("response-" + resolutionIndex + "-" + index))).ToArray();
            resolutionIndex++;
            foreach (var evaluation in evaluations)
                rules.Add(new Dictionary<string, object>(match) { ["action"] = "evaluate", ["server"] = evaluation.Target, ["tag"] = evaluation.Response, ["timeout"] = "5s" });
            foreach (var evaluation in evaluations)
                foreach (var code in new[] { "NOERROR", "NXDOMAIN" })
                    rules.Add(new Dictionary<string, object>(match) { ["match_response"] = evaluation.Response, ["response_rcode"] = code, ["action"] = "respond", ["race"] = true });
            rules.Add(new Dictionary<string, object>(match) { ["action"] = "predefined", ["rcode"] = "SERVFAIL" });
        }
        var remoteEndpoints = Endpoints(Get(dns, "nameserver"), "nameserver", "https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query");
        if (remoteEndpoints is ["https://1.1.1.1/dns-query"] or ["https://cloudflare-dns.com/dns-query"])
            remoteEndpoints = [.. remoteEndpoints, "https://8.8.8.8/dns-query"];
        remoteEndpoints = remoteEndpoints.Concat(Endpoints(Get(dns, "fallback"), "fallback")).Distinct().ToArray();
        var remote = AddServers("remote", remoteEndpoints, routedDetour ?? vpn);
        var direct = AddServers("direct", Endpoints(Get(dns, "direct-nameserver"), "direct-nameserver", bootstrapEndpoints), null);
        Resolve(Obj(("inbound", new[] { tag("bootstrap-in") })), bootstrapTargets);
        Resolve(Obj(("inbound", new[] { tag("direct-dns-in") })), direct);
        if (Get(dns, "nameserver-policy") is YamlMappingNode policies)
        {
            var index = 0;
            foreach (var policy in policies.Children)
            {
                var pattern = policy.Key.ToString();
                var suffix = pattern.StartsWith("+.", StringComparison.Ordinal) || pattern.StartsWith("*.", StringComparison.Ordinal);
                var domain = suffix ? pattern[2..] : pattern;
                if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Contains(':') || domain.Contains('*')) throw Unsupported("nameserver-policy");
                Resolve(Obj((suffix ? "domain_suffix" : "domain", new[] { domain })), AddServers("policy-" + index++, Endpoints(policy.Value, "nameserver-policy"), routedDetour ?? vpn));
            }
        }
        Resolve(Obj(("domain_suffix", new[] { ".lan", ".local" })), direct);
        var fake = tag("fakeip");
        servers.Add(Obj(("type", "fakeip"), ("tag", fake), ("inet4_range", "198.18.0.0/16"), ("inet6_range", "fc00::/18")));
        foreach (var pattern in Endpoints(Get(dns, "fake-ip-filter"), "fake-ip-filter"))
        {
            if (pattern is "*.lan" or "*.local") continue;
            var suffix = pattern.StartsWith("*.", StringComparison.Ordinal) || pattern.StartsWith("+.", StringComparison.Ordinal);
            var domain = suffix ? pattern[2..] : pattern;
            if (Uri.CheckHostName(domain) != UriHostNameType.Dns || domain.Contains('*')) throw Unsupported("fake-ip-filter");
            Resolve(Obj((suffix ? "domain_suffix" : "domain", new[] { domain })), remote);
        }
        rules.Add(Obj(("inbound", new[] { tunTag }), ("query_type", new[] { "A", "AAAA" }), ("action", "route"), ("server", fake)));
        // DNS emitted by svchost cannot reliably identify the originating browser.
        // Keep normal TUN queries as FakeIP; the actual connection rule then picks
        // VPN (server-side resolution) or DIRECT (the direct resolver race).
        // Non-TUN lookups follow the same ordered domain rules as connection routing.
        foreach (var route in domainRoutes)
        {
            var match = route.Where(field => field.Key != "outbound").ToDictionary(field => field.Key, field => field.Value);
            var target = (string)route["outbound"];
            // Connection blocking belongs to the ordered connection rules. An
            // earlier IP/process exception may still need this domain resolved.
            Resolve(match, target == "DIRECT" ? direct : remote);
        }
        Resolve(new(), remote);
        return Obj(("servers", servers), ("rules", rules), ("final", remote[0]), ("reverse_mapping", true), ("timeout", "5s"));
    }

    private static Dictionary<string, object> Server(string endpoint, string tag, string? detour, string bootstrap, bool numericOnly)
    {
        var value = endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "udp://" + endpoint;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("udp" or "tcp" or "tls" or "https")
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0 || uri.Host.Length == 0
            || numericOnly && !IPAddress.TryParse(uri.Host, out _)) throw Unsupported("адрес сервера");
        var server = Obj(("type", uri.Scheme), ("tag", tag), ("server", uri.Host));
        var port = uri.IsDefaultPort ? uri.Scheme switch { "https" => 443, "tls" => 853, _ => 53 } : uri.Port;
        if (port is < 1 or > 65535) port = uri.Scheme switch { "https" => 443, "tls" => 853, _ => 53 };
        server["server_port"] = port;
        if (uri.Scheme == "https") server["path"] = uri.AbsolutePath == "/" ? "/dns-query" : uri.AbsolutePath;
        if (detour is not null) server["detour"] = detour;
        if (!IPAddress.TryParse(uri.Host, out _)) server["domain_resolver"] = bootstrap;
        return server;
    }
}
