using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal static partial class ProfileImporter
{
    private static ImportedProfile FromHysteriaLink(string text)
    {
        if (text.Any(char.IsWhiteSpace) || Regex.IsMatch(text, "%[^0-9A-Fa-f]|%[0-9A-Fa-f](?![0-9A-Fa-f])|%$")
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.AbsolutePath is not ("" or "/")
            || uri.UserInfo.Length == 0 || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new InvalidDataException("Некорректная ссылка Hysteria2: нужны адрес и пароль.");
        var port = uri.Port == -1 ? 443 : uri.Port;
        if (port is < 1 or > 65535) throw new InvalidDataException("Некорректный порт Hysteria2.");
        if (XrayProfile.Requires(uri, XrayProfile.Query(uri))) return XrayProfile.Import(text);
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            if (key.ToLowerInvariant() is not ("sni" or "insecure" or "obfs" or "obfs-password" or "alpn"))
                throw new InvalidDataException("В Hysteria2 есть неподдерживаемый параметр. Закрепление сертификата, ECH и другие опции не удаляются молча.");
            if (!options.TryAdd(key, SafeText(Uri.UnescapeDataString(pair.ElementAtOrDefault(1) ?? ""))))
                throw new InvalidDataException("В ссылке Hysteria2 повторяется параметр.");
        }
        string Get(string key, string fallback = "") => options.GetValueOrDefault(key, fallback);
        var sni = Get("sni");
        if (sni.Length > 0 && Uri.CheckHostName(sni) == UriHostNameType.Unknown) throw new InvalidDataException("Некорректный SNI Hysteria2.");
        var obfs = Get("obfs");
        if (obfs is not ("" or "salamander") || (obfs.Length == 0) != (Get("obfs-password").Length == 0))
            throw new InvalidDataException("Неподдерживаемая или неполная обфускация Hysteria2.");
        var insecure = Flag(Get("insecure", "0"));
        var node = new YamlMappingNode();
        Put(node, "name", ConnectionNameFromLink(uri)); Put(node, "type", "hysteria2");
        Put(node, "server", uri.Host.Trim('[', ']')); Put(node, "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Put(node, "password", SafeText(Uri.UnescapeDataString(uri.UserInfo))); Put(node, "udp", "true");
        Put(node, "sni", sni); Put(node, "skip-cert-verify", insecure ? "true" : "false");
        if (obfs.Length > 0) { Put(node, "obfs", obfs); Put(node, "obfs-password", Get("obfs-password")); }
        if (options.ContainsKey("alpn"))
        {
            var values = Get("alpn").Split(',');
            if (values.Any(value => value.Length == 0)) throw new InvalidDataException("Некорректный ALPN Hysteria2.");
            node.Add("alpn", new YamlSequenceNode(values.Select(value => new YamlScalarNode(value))));
        }
        return BuildConnections(new[] { node }, "Hysteria2 · QUIC · TLS", insecure) with { NeedsName = !HasLinkName(uri) };
    }
}
