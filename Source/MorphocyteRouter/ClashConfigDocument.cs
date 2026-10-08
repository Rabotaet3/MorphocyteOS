using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

public sealed class ClashConfigDocument
{
    private const int MaxYamlBackups = 3;
    private readonly string _originalText;
    private readonly string _yamlText;
    private readonly YamlMappingNode _root;
    private readonly List<string> _allRules;
    private readonly int _rulesValueStart;
    private readonly int _rulesValueEnd;
    public string Path { get; }
    public bool HadMarkdownFence { get; }
    public string SourceHash { get; }
    public IReadOnlyList<DomainRule> Rules { get; }
    public IReadOnlyList<string> Routes { get; }

    public string? VpnRoute { get; }
    public int ConnectionCount { get; }

    public bool HasVpnConnection => Get(_root, "proxies") is YamlSequenceNode proxies && proxies.Children.OfType<YamlMappingNode>()
        .Any(proxy => Scalar(proxy, "type") is { } type && type is not ("direct" or "reject"))
        || Get(_root, "proxy-providers") is YamlMappingNode providers && providers.Children.Count > 0;
    public int PreservedRuleCount => _allRules.Count - Rules.Count;
    public string Endpoint { get; } = "не определён";

    private ClashConfigDocument(string path, string originalText)
    {
        Path = System.IO.Path.GetFullPath(path);
        _originalText = originalText;
        SourceHash = Hash(originalText);
        _yamlText = originalText;
        var trimmed = originalText.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = trimmed.IndexOf('\n');
            if (newline < 0 || !trimmed.EndsWith("```", StringComparison.Ordinal))
                throw new InvalidDataException("Незавершённая Markdown-обёртка YAML.");
            _yamlText = trimmed[(newline + 1)..^3].TrimEnd() + Environment.NewLine;
            HadMarkdownFence = true;
        }
        var yaml = new YamlStream();
        yaml.Load(new StringReader(_yamlText));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("Ожидается один YAML-документ с настройками.");
        _root = root;
        ConnectionCount = (Get(root, "proxies") as YamlSequenceNode)?.Children.Count ?? 0;
        if (Get(root, "rules") is not YamlSequenceNode sequence)
            throw new InvalidDataException("Секция rules должна быть списком правил.");
        _allRules = sequence.Children.Select(node => node is YamlScalarNode { Value: not null } scalar
            ? scalar.Value : throw new InvalidDataException("Каждое правило rules должно быть строкой.")).ToList();
        Rules = _allRules.Select(ParseRule).Where(rule => rule is not null).Cast<DomainRule>().ToArray();

        // Replace only the rules value; retain unrelated source text and credentials verbatim.
        var entry = root.Children.First(pair => pair.Key is YamlScalarNode { Value: "rules" });
        _rulesValueStart = checked((int)entry.Key.End.Index);
        var nextKey = root.Children.Keys.Where(key => key.Start.Index > entry.Key.Start.Index)
            .OrderBy(key => key.Start.Index).FirstOrDefault();
        _rulesValueEnd = nextKey is null ? _yamlText.Length : LineStart(_yamlText, checked((int)nextKey.Start.Index));

        var routes = new List<string>();
        foreach (var section in new[] { "proxies", "proxy-groups" })
        {
            if (Get(root, section) is not YamlSequenceNode list) continue;
            foreach (var item in list.Children.OfType<YamlMappingNode>())
            {
                var name = Scalar(item, "name");
                if (!string.IsNullOrWhiteSpace(name) && name is not "DIRECT" and not "REJECT" && !routes.Contains(name)) routes.Add(name);
                if (section == "proxies" && Endpoint == "не определён" && Scalar(item, "server") is { } server)
                {
                    Endpoint = $"{server}:{Scalar(item, "port")}";
                }
            }
        }
        Routes = routes;
        var groups = (Get(root, "proxy-groups") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>()
            .Select(group => Scalar(group, "name")).Where(name => !string.IsNullOrWhiteSpace(name) && name is not ("DIRECT" or "REJECT")).ToArray();
        VpnRoute = groups?.FirstOrDefault(name => name == "VPN") ?? groups?.FirstOrDefault() ?? routes.FirstOrDefault();
    }

    internal string BuildRuntimeText(int controllerPort, string secret, bool fullTunnel = false, int speedTestPort = 0)
    {
        if (controllerPort is < 1 or > 65535 || !System.Text.RegularExpressions.Regex.IsMatch(secret, "^[A-F0-9]{64}$"))
            throw new ArgumentException("Недопустимые параметры локальной диагностики.");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(_yamlText));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        foreach (var key in new[] { "external-controller", "external-controller-tls", "external-controller-unix", "external-controller-pipe", "secret", "external-ui", "external-ui-url", "external-ui-name", "external-controller-cors" })
            root.Children.Remove(new YamlScalarNode(key));
        root.Add("external-controller", $"127.0.0.1:{controllerPort}");
        root.Add("secret", secret);
        if (fullTunnel)
        {
            if (VpnRoute is null || !HasVpnConnection) throw new InvalidDataException("Нет VPN-подключения для TUN.");
            root.Children[new YamlScalarNode("mode")] = new YamlScalarNode("rule");
            // Explicit DIRECT / blocking exceptions remain active. A profile's old
            // catch-all is replaced, so everything else uses the VPN by default.
            var exceptions = _allRules.Where(rule =>
            {
                var value = rule.Trim();
                if (value.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase)) return false;
                if (value.EndsWith(",no-resolve", StringComparison.OrdinalIgnoreCase)) value = value[..^11].TrimEnd();
                var comma = value.LastIndexOf(',');
                return comma >= 0 && value[(comma + 1)..].Trim().ToUpperInvariant() is "DIRECT" or "REJECT" or "REJECT-DROP";
            }).Select(rule => (YamlNode)new YamlScalarNode(rule)).ToList();
            exceptions.Add(new YamlScalarNode("MATCH," + VpnRoute));
            root.Children[new YamlScalarNode("rules")] = new YamlSequenceNode(exceptions);
            // Cached selector choices cannot leave the full-tunnel path on DIRECT.
            var bypass = new HashSet<string>(StringComparer.Ordinal) { "DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE" };
            if (Get(root, "proxies") is YamlSequenceNode proxies)
                foreach (var proxy in proxies.Children.OfType<YamlMappingNode>())
                    if (Scalar(proxy, "type") is "direct" or "reject" && Scalar(proxy, "name") is { } name) bypass.Add(name);
            if (Get(root, "proxy-groups") is YamlSequenceNode groups)
            {
                var namedGroups = groups.Children.OfType<YamlMappingNode>().Where(group => Scalar(group, "name") is not null)
                    .ToDictionary(group => Scalar(group, "name")!, StringComparer.Ordinal);
                bool HasDynamicMembers(YamlMappingNode group) => Get(group, "use") is YamlSequenceNode { Children.Count: > 0 }
                    || new[] { "include-all", "include-all-proxies", "include-all-providers" }.Any(key => Scalar(group, key) == "true");
                // Mark direct-only nested groups before pruning the reachable VPN branch.
                bool changed;
                do
                {
                    changed = false;
                    foreach (var (name, group) in namedGroups)
                        if (!HasDynamicMembers(group) && Get(group, "proxies") is YamlSequenceNode members
                            && members.Children.All(member => bypass.Contains(member.ToString()))) changed |= bypass.Add(name);
                } while (changed);
                if (bypass.Contains(VpnRoute)) throw new InvalidDataException("TUN: VPN-группа содержит только прямые маршруты или блокировку.");
                var pending = new Queue<string>(); pending.Enqueue(VpnRoute);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (pending.TryDequeue(out var name))
                {
                    if (!visited.Add(name) || !namedGroups.TryGetValue(name, out var group) || Get(group, "proxies") is not YamlSequenceNode members) continue;
                    foreach (var member in members.Children.OfType<YamlScalarNode>().ToArray())
                        if (bypass.Contains(member.Value ?? "")) members.Children.Remove(member);
                        else if (member.Value is { } next) pending.Enqueue(next);
                }
            }
            else if (bypass.Contains(VpnRoute)) throw new InvalidDataException("TUN: выбран прямой маршрут или блокировка.");
            root.Children[new YamlScalarNode("ipv6")] = new YamlScalarNode("true");
            var tun = Get(root, "tun") as YamlMappingNode ?? new YamlMappingNode();
            root.Children[new YamlScalarNode("tun")] = tun;
            foreach (var key in new[] { "enable", "auto-route", "auto-detect-interface", "strict-route" })
                tun.Children[new YamlScalarNode(key)] = new YamlScalarNode("true");
            // A profile's split-route/interface exclusions must not bypass full-tunnel mode.
            foreach (var key in new[] { "route-address", "route-address-set", "route-exclude-address", "route-exclude-address-set",
                "inet4-route-address", "inet6-route-address", "inet4-route-exclude-address", "inet6-route-exclude-address",
                "include-interface", "exclude-interface" }) tun.Children.Remove(new YamlScalarNode(key));
            tun.Children[new YamlScalarNode("dns-hijack")] = new YamlSequenceNode("any:53", "tcp://any:53");
            if (Get(tun, "inet6-address") is null)
                tun.Add("inet6-address", new YamlSequenceNode("fdfe:dcba:9876::1/126"));
            var dns = Get(root, "dns") as YamlMappingNode ?? new YamlMappingNode();
            root.Children[new YamlScalarNode("dns")] = dns;
            foreach (var key in new[] { "enable", "ipv6", "respect-rules" })
                dns.Children[new YamlScalarNode(key)] = new YamlScalarNode("true");
            // Resolving the VPN server itself needs an independent bootstrap resolver.
            if (Get(dns, "proxy-server-nameserver") is null) dns.Add("proxy-server-nameserver", new YamlSequenceNode("1.1.1.1", "8.8.8.8"));
            dns.Children[new YamlScalarNode("nameserver")] = new YamlSequenceNode("https://1.1.1.1/dns-query");
            dns.Children.Remove(new YamlScalarNode("fallback"));
            dns.Children.Remove(new YamlScalarNode("fallback-filter"));
            dns.Children.Remove(new YamlScalarNode("nameserver-policy"));
        }
        if (speedTestPort != 0)
        {
            if (speedTestPort is < 1 or > 65535 || speedTestPort == controllerPort || VpnRoute is null)
                throw new ArgumentException("Недопустимый порт замера скорости.");
            var listeners = Get(root, "listeners") as YamlSequenceNode ?? new YamlSequenceNode();
            root.Children[new YamlScalarNode("listeners")] = listeners;
            listeners.Add(new YamlMappingNode {
                { "name", "morphocyte-speed-" + secret[..12] }, { "type", "http" },
                { "listen", "127.0.0.1" }, { "port", speedTestPort.ToString(CultureInfo.InvariantCulture) },
                { "proxy", VpnRoute },
                { "users", new YamlSequenceNode(new YamlMappingNode { { "username", "morphocyte" }, { "password", secret } }) }
            });
        }
        using var writer = new StringWriter();
        yaml.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    public static ClashConfigDocument Load(string path) => new(path, File.ReadAllText(path));
    public static ClashConfigDocument Parse(string path, string text) => new(path, text);
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static int LineStart(string text, int index) => text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
    private static YamlNode? Get(YamlMappingNode mapping, string key) => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    private static string? Scalar(YamlMappingNode mapping, string key) => (Get(mapping, key) as YamlScalarNode)?.Value;

    private static DomainRule? ParseRule(string text, int index)
    {
        var fields = text.Split(',');
        if (fields.Length < 3) return null;
        var kind = fields[0].Trim().ToUpperInvariant();
        if (kind is not ("DOMAIN" or "DOMAIN-SUFFIX" or "DOMAIN-KEYWORD" or "PROCESS-NAME")) return null;
        return new DomainRule { Kind = kind, Value = fields[1].Trim(), Route = fields[2].Trim(),
            SourceIndex = index, Extra = fields.Length > 3 ? "," + string.Join(',', fields.Skip(3)) : "" };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Routes.Count == 0) errors.Add("Не найдено VPN-маршрутов (proxies / proxy-groups).");
        return errors;
    }

    public IReadOnlyList<string> ValidateForRouting()
    {
        var errors = new List<string>();
        if (!HasVpnConnection) errors.Add("В шаблоне ещё нет VPN-подключения. Импортируй свою VLESS-ссылку / JSON или выбери готовый YAML.");
        var mode = Scalar(_root, "mode");
        if (!string.IsNullOrWhiteSpace(mode) && !mode.Equals("rule", StringComparison.OrdinalIgnoreCase))
            errors.Add("Для маршрутизации по правилам в YAML нужен mode: rule. Текущий режим: " + mode + ".");
        if (Get(_root, "tun") is not YamlMappingNode tun || !string.Equals(Scalar(tun, "enable"), "true", StringComparison.OrdinalIgnoreCase))
            errors.Add("Для перенаправления трафика приложений в YAML нужно включить TUN: tun → enable: true.");
        return errors;
    }

    public string BuildText(IEnumerable<DomainRule> rules)
    {
        var edited = rules.Where(rule => rule.Enabled).Select(rule => rule.Copy()).ToList();
        foreach (var rule in edited)
        {
            if (rule.Kind is not ("DOMAIN" or "DOMAIN-SUFFIX" or "DOMAIN-KEYWORD" or "PROCESS-NAME"))
                throw new InvalidDataException("Неподдерживаемый тип редактируемого правила: " + rule.Kind);
            ValidateField(rule.Value);
            ValidateField(rule.Route);
            if (rule.Extra.Any(char.IsControl)) throw new InvalidDataException("Недопустимые символы в правиле.");
        }
        var matched = new Dictionary<int, DomainRule>();
        var additions = new List<DomainRule>();
        foreach (var rule in edited)
        {
            var original = Rules.FirstOrDefault(old => old.SourceIndex == rule.SourceIndex && old.Kind == rule.Kind && old.Value == rule.Value && !matched.ContainsKey(old.SourceIndex))
                ?? Rules.FirstOrDefault(old => old.Kind == rule.Kind && old.Value == rule.Value && !matched.ContainsKey(old.SourceIndex));
            if (original is null) additions.Add(rule);
            else matched.Add(original.SourceIndex, rule);
        }
        var managedIndices = Rules.Select(rule => rule.SourceIndex).ToHashSet();
        // Domain exceptions must be evaluated before broad PROCESS-NAME rules.
        // This makes the UI scenario "application through VPN, selected sites DIRECT" safe.
        var priorityAdditions = additions.Where(rule => rule.Route.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
            || rule.Route.Equals("REJECT", StringComparison.OrdinalIgnoreCase)).ToList();
        var regularAdditions = additions.Except(priorityAdditions).ToList();
        var output = new List<string>();
        var insertedPriority = false;
        var insertedRegular = false;
        for (var i = 0; i < _allRules.Count; i++)
        {
            var raw = _allRules[i].TrimStart();
            if (!insertedPriority && (raw.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("PROCESS-NAME-REGEX,", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase)))
            {
                output.AddRange(priorityAdditions.Select(FormatRule));
                insertedPriority = true;
            }
            if (!insertedRegular && raw.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase))
            {
                output.AddRange(regularAdditions.Select(FormatRule));
                insertedRegular = true;
            }
            if (!managedIndices.Contains(i)) output.Add(_allRules[i]);
            else if (matched.TryGetValue(i, out var rule)) output.Add(FormatRule(rule));
        }
        if (!insertedPriority) output.AddRange(priorityAdditions.Select(FormatRule));
        if (!insertedRegular) output.AddRange(regularAdditions.Select(FormatRule));
        var body = output.Count == 0 ? " []\n" : "\n" + string.Join("\n", output.Select(rule => "  - '" + rule.Replace("'", "''") + "'")) + "\n";
        var result = _yamlText[.._rulesValueStart] + ":" + body + _yamlText[_rulesValueEnd..];
        _ = Parse(Path, result);
        return result;
    }

    private static string FormatRule(DomainRule rule) => $"{rule.Kind},{rule.Value},{rule.Route}{rule.Extra}";
    private static void ValidateField(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains(',') || value.Any(char.IsControl))
            throw new ArgumentException("Поле правила пусто или содержит запятую / управляющий символ.");
    }

    public string CommitText(string text)
    {
        _ = Parse(Path, text);
        if (File.ReadAllText(Path) != _originalText)
            throw new IOException("YAML изменён другой программой. Открой профиль заново перед применением.");
        var backup = Path + $".backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        AtomicFile.Write(Path, text, backup);
        PruneYamlBackups(Path, backup);
        return backup;
    }

    private static void PruneYamlBackups(string path, string currentBackup)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            var pattern = System.IO.Path.GetFileName(path) + ".backup-*";
            // File.Replace can preserve the source's old LastWriteTime on its backup.
            // Sort the timestamp-bearing names instead and always reserve one slot
            // for the exact backup returned to the caller for a possible rollback.
            var stale = Directory.EnumerateFiles(directory, pattern)
                .Where(file => !string.Equals(file, currentBackup, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => System.IO.Path.GetFileName(file), StringComparer.Ordinal)
                .Skip(MaxYamlBackups - 1)
                .ToArray();
            foreach (var file in stale)
                DeleteBackupWithRetry(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteBackupWithRetry(string file)
    {
        // Brief sharing locks from indexers/antivirus are common directly after
        // File.Replace. This executes in the commit worker, not the UI dispatcher.
        var delays = new[] { 30, 60, 120 };
        for (var attempt = 0; ; attempt++)
        {
            try { File.Delete(file); return; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < delays.Length)
            {
                Thread.Sleep(delays[attempt]);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    public static string NormalizeDomain(string input, string kind)
    {
        if (input.Any(char.IsControl)) throw new ArgumentException("Ввод должен содержать одну строку без управляющих символов.");
        var value = input.Trim();
        ValidateField(value);
        if (kind == "PROCESS-NAME")
        {
            if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || value.Length <= 4 || value.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '#' }) >= 0)
                throw new ArgumentException("Укажи имя файла процесса, например application.exe. Можно выбрать EXE кнопкой рядом с полем.");
            return value;
        }
        if (kind == "DOMAIN-KEYWORD")
        {
            if (value.Length < 2 || value.Any(char.IsWhiteSpace) || value.IndexOfAny(new[] { '/', ':', '#', '?', '*' }) >= 0)
                throw new ArgumentException("«По слову» ожидает часть домена, например google. Для полной ссылки выбери «Весь домен».");
            return value.ToLowerInvariant();
        }
        if (kind is not ("DOMAIN" or "DOMAIN-SUFFIX")) throw new ArgumentException("Неизвестный тип правила.");
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Не удалось распознать адрес сайта.");
        var host = new IdnMapping().GetAscii(uri.Host.Trim('.').ToLowerInvariant());
        if (host.Length < 3 || Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains(' '))
            throw new ArgumentException("Укажи доменное имя сайта, например example.com.");
        return host;
    }

}
