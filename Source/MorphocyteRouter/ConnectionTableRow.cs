using System.ComponentModel;
using System.Net;

namespace MorphocyteRouter;

// UI rows keep their identity while the controller returns fresh snapshots.
internal sealed class ConnectionTableRow(string key, ConnectionActivity activity) : INotifyPropertyChanged
{
    private ConnectionActivity _activity = activity;
    private string? _addressDomain = ConnectionAddress.Domain(activity.Destination);
    internal string Key { get; } = key;
    public string Process => _activity.Process;
    public string Destination => _activity.Destination;
    public string Network => _activity.Network;
    public string Route => _activity.Route;
    public string? AddressDomain => _addressDomain;
    public bool CanChangeAddress => AddressDomain is not null;
    public string RouteHint => Route + (PendingRouteLabel.Length > 0 ? Environment.NewLine + PendingRouteLabel : "") + Environment.NewLine + (AddressDomain is { } domain
        ? $"Изменить маршрут только для {domain}, без поддоменов, для всех приложений. Выбор сохранится в черновик."
        : "Для этого соединения не определён домен. Изменение по IP здесь недоступно.");
    public string PendingRouteLabel { get; private set; } = "";
    public string RawRule => _activity.Rule;
    public string RuleLabel => ConnectionRuleLabel.Format(RawRule, Process);
    public string RuleHint => RuleLabel + Environment.NewLine + "Техническое правило: " + RawRule;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetPendingRoute(string label)
    {
        if (PendingRouteLabel == label) return;
        PendingRouteLabel = label;
        PropertyChanged?.Invoke(this, new(nameof(PendingRouteLabel)));
        PropertyChanged?.Invoke(this, new(nameof(RouteHint)));
    }

    internal void Update(ConnectionActivity activity)
    {
        var previous = _activity;
        _activity = activity;
        void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
        if (previous.Process != Process) Changed(nameof(Process));
        if (previous.Destination != Destination)
        {
            _addressDomain = ConnectionAddress.Domain(Destination);
            Changed(nameof(Destination)); Changed(nameof(AddressDomain)); Changed(nameof(CanChangeAddress)); Changed(nameof(RouteHint));
        }
        if (previous.Network != Network) Changed(nameof(Network));
        if (previous.Route != Route) { Changed(nameof(Route)); Changed(nameof(RouteHint)); }
        if (previous.Rule != RawRule || previous.Process != Process)
        { Changed(nameof(RawRule)); Changed(nameof(RuleLabel)); Changed(nameof(RuleHint)); }
    }
}

internal static class ConnectionAddress
{
    // Controller destinations are host:port, not URLs. Never infer a wider domain or an app rule.
    internal static string? Domain(string destination)
    {
        if (destination.Length == 0 || destination.Any(char.IsWhiteSpace)
            || destination.Any(char.IsControl) || destination.IndexOfAny(['/', '\\', '@', '?', '#', '[', ']']) >= 0) return null;
        var host = destination;
        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(host[(colon + 1)..], out var port) || port is < 1 or > 65535) return null;
            host = host[..colon];
        }
        if (host.Contains(':') || IPAddress.TryParse(host, out _)) return null;
        try { return ClashConfigDocument.NormalizeDomain(host, "DOMAIN"); }
        catch (ArgumentException) { return null; }
        catch (System.IO.InvalidDataException) { return null; }
    }
}

internal static class ConnectionRuleLabel
{
    internal static string Format(string raw, string process = "")
    {
        var text = raw.Trim();
        if (text.Length == 0) return "Правило не указано";
        var arrow = text.IndexOf("=>", StringComparison.Ordinal);
        if (arrow >= 0) text = text[..arrow].Trim();
        var separator = text.IndexOfAny(['=', ' ', '\t', ':']);
        var kind = (separator < 0 ? text : text[..separator]).Replace("_", "").Replace("-", "").ToLowerInvariant();
        var value = separator < 0 ? "" : text[(separator + 1)..].Trim();
        return kind switch
        {
            "match" or "final" => "По умолчанию",
            "domain" => "Домен: " + value,
            "domainsuffix" => "Домен и поддомены: " + value,
            "domainkeyword" => "Слово в домене: " + value,
            "domainregex" => "Домен по шаблону",
            "processname" => "Приложение: " + value,
            "processpath" => "Путь приложения: " + value,
            "processpathregex" => process.Length > 0 ? "Приложение: " + process : "Путь приложения по шаблону",
            "ipcidr" or "ipcidr6" => "IP-адрес / подсеть: " + value,
            "srcipcidr" or "sourceipcidr" => "Исходная подсеть: " + value,
            "geoip" => "IP-регион: " + value,
            "geosite" or "ruleset" => "Набор правил: " + value,
            "dstport" or "port" => "Порт назначения: " + value,
            "srcport" or "sourceport" => "Исходный порт: " + value,
            "network" or "protocol" => "Протокол: " + value.ToUpperInvariant(),
            "and" or "or" or "not" or "logical" => "Составное правило",
            _ => "Другое правило"
        };
    }
}
