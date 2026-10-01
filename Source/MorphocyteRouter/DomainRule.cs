namespace MorphocyteRouter;

public sealed class DomainRule
{
    public string Kind { get; set; } = "DOMAIN-SUFFIX";
    public string Value { get; set; } = "";
    public string Route { get; set; } = "";
    public int SourceIndex { get; set; } = -1;
    public string Extra { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Folder { get; set; } = "";
    public string FolderLabel => string.IsNullOrWhiteSpace(Folder) ? "ОБЩИЕ ПРАВИЛА" : Folder;
    public DomainRule Copy() => new() { Kind = Kind, Value = Value, Route = Route, SourceIndex = SourceIndex, Extra = Extra, Enabled = Enabled, Folder = Folder };

    public string KindLabel => Kind switch
    {
        "DOMAIN" => "ТОЧНО",
        "DOMAIN-KEYWORD" => "КЛЮЧ",
        "PROCESS-NAME" => "ПРОЦЕСС",
        "PROCESS-NAME-REGEX" => "ПРОЦЕСС RX",
        _ => "ДОМЕН"
    };

    public string RouteLabel => Route.ToUpperInvariant() switch
    {
        "DIRECT" => "НАПРЯМУЮ",
        "REJECT" => "БЛОКИРОВКА",
        _ => Route
    };
}
