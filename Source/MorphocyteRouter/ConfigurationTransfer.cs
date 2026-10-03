using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal sealed class ConfigurationPackage
{
    public string Format { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string ApplicationVersion { get; set; } = "";
    public string Yaml { get; set; } = "";
    public bool HasDraft { get; set; }
    public List<TransferRule> Rules { get; set; } = null!;
    public List<string> FolderOrder { get; set; } = null!;
    public TransferOptions Options { get; set; } = null!;
    public List<TransferFile> Files { get; set; } = null!;
}

internal sealed class TransferOptions
{
    public string Theme { get; set; } = "Тёмная морфоцитная";
    public double InterfaceScale { get; set; } = 1;
    public bool AutoCheckUpdates { get; set; }
    public bool LaunchAtSignIn { get; set; }
    public bool AutoConnectOnStartup { get; set; }
    public string PreferredRoute { get; set; } = "VPN";
    public bool ShowEventLog { get; set; }
}

internal sealed class TransferRule
{
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string Route { get; set; } = "";
    public string Extra { get; set; } = "";
    public bool Enabled { get; set; }
    public string Folder { get; set; } = "";
    public int SourceIndex { get; set; } = -1;
    internal DomainRule ToRule() => new() { Kind = Kind, Value = Value, Route = Route, Extra = Extra, Enabled = Enabled, Folder = Folder, SourceIndex = SourceIndex };
    internal static TransferRule FromRule(DomainRule rule) => new() { Kind = rule.Kind, Value = rule.Value, Route = rule.Route,
        Extra = rule.Extra, Enabled = rule.Enabled, Folder = rule.Folder, SourceIndex = rule.SourceIndex };
}

internal sealed class TransferFile
{
    public string Path { get; set; } = "";
    public byte[] Content { get; set; } = null!;
}

internal static class ConfigurationTransfer
{
    internal const int MaxPackageBytes = 64 * 1024 * 1024;
    private const int MaxResourceBytes = 32 * 1024 * 1024;
    private const string Format = "MorphocyteOS.Configuration";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 32 };

    internal static ConfigurationPackage Capture(string path, string yamlText, IEnumerable<DomainRule> rules,
        IEnumerable<string> folders, TransferOptions options, bool hasDraft)
    {
        var document = ClashConfigDocument.Parse(path, yamlText);
        if (document.HadMarkdownFence) yamlText = document.BuildText(document.Rules);
        var yaml = ParseYaml(yamlText);
        var package = new ConfigurationPackage { Format = Format, SchemaVersion = 1, ApplicationVersion = BuildInfo.Version,
            Yaml = yamlText, HasDraft = hasDraft, Rules = rules.Select(TransferRule.FromRule).ToList(),
            FolderOrder = folders.ToList(), Options = options, Files = new() };
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Providers(yaml))
        {
            if (!provider.Children.TryGetValue(new YamlScalarNode("path"), out var node)) continue;
            if (node is not YamlScalarNode { Value: { Length: > 0 } source }) throw new InvalidDataException("Некорректный путь файла провайдера.");
            var sourcePath = System.IO.Path.GetFullPath(source, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            if (!sources.TryGetValue(sourcePath, out var portable))
            {
                var extension = System.IO.Path.GetExtension(sourcePath);
                if (!Regex.IsMatch(extension, @"^\.[A-Za-z0-9]{1,10}\z")) extension = ".dat";
                portable = $"assets/resource-{sources.Count + 1:000}{extension}";
                sources.Add(sourcePath, portable);
                if (File.Exists(sourcePath)) package.Files.Add(new TransferFile { Path = portable, Content = ReadBounded(sourcePath, MaxResourceBytes) });
                else if (!IsRemote(provider)) throw new FileNotFoundException("Не найден локальный файл провайдера: " + System.IO.Path.GetFileName(sourcePath));
            }
            provider.Children[new YamlScalarNode("path")] = new YamlScalarNode(portable);
        }
        if (sources.Count > 0)
        {
            using var writer = new StringWriter();
            yaml.Save(writer, assignAnchors: false);
            package.Yaml = writer.ToString();
        }
        Validate(package);
        return package;
    }

    internal static string Serialize(ConfigurationPackage package)
    {
        Validate(package);
        var text = JsonSerializer.Serialize(package, JsonOptions);
        if (Encoding.UTF8.GetByteCount(text) > MaxPackageBytes) throw new InvalidDataException("Файл переноса слишком большой.");
        return text;
    }

    internal static ConfigurationPackage Deserialize(byte[] bytes)
    {
        if (bytes.Length > MaxPackageBytes) throw new InvalidDataException("Файл переноса слишком большой.");
        try
        {
            var package = JsonSerializer.Deserialize<ConfigurationPackage>(bytes, JsonOptions) ?? throw new InvalidDataException("Пустой файл переноса.");
            Validate(package);
            return package;
        }
        catch (JsonException) { throw new InvalidDataException("Некорректный файл переноса MorphocyteOS."); }
    }

    internal static ConfigurationPackage Read(string path) => Deserialize(ReadBounded(path, MaxPackageBytes));

    internal static byte[] ReadBounded(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException("Файл переноса или его ресурс слишком большой.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Файл изменился во время чтения.");
        return bytes;
    }

    internal static void Validate(ConfigurationPackage package)
    {
        if (package.Format != Format || package.SchemaVersion != 1) throw new InvalidDataException("Неизвестный формат или версия файла переноса.");
        if (string.IsNullOrWhiteSpace(package.Yaml) || package.Yaml.Length > 4 * 1024 * 1024
            || package.Rules is null || package.Rules.Count > 10000 || package.FolderOrder is null || package.FolderOrder.Count > 10000
            || package.Options is null || package.Files is null || package.Files.Count > 128)
            throw new InvalidDataException("Некорректный состав файла переноса.");
        var options = package.Options;
        if (!double.IsFinite(options.InterfaceScale) || options.InterfaceScale is < .8 or > 1.5
            || options.Theme is null || options.Theme.Length > 128 || options.PreferredRoute is not ("VPN" or "DIRECT" or "REJECT"))
            throw new InvalidDataException("Некорректные параметры интерфейса в файле переноса.");
        var document = ClashConfigDocument.Parse(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "transfer-profile.yaml"), package.Yaml);
        if (document.Validate().Count != 0) throw new InvalidDataException("В файле переноса нет VPN-маршрутов.");
        foreach (var rule in package.Rules)
        {
            if (rule is null || rule.Kind is not ("DOMAIN" or "DOMAIN-SUFFIX" or "DOMAIN-KEYWORD" or "PROCESS-NAME")
                || !ValidField(rule.Value) || !ValidField(rule.Route) || rule.Extra is null || rule.Extra.Length > 1024
                || rule.Extra.Any(char.IsControl) || rule.Extra.Length > 0 && !rule.Extra.StartsWith(',')
                || !ValidFolder(rule.Folder) || rule.SourceIndex is < -1 or > 10000000
                || rule.Route is not ("DIRECT" or "REJECT") && !document.Routes.Contains(rule.Route))
                throw new InvalidDataException("Некорректное правило в файле переноса.");
        }
        if (package.FolderOrder.Any(name => !ValidFolder(name) || string.IsNullOrWhiteSpace(name)))
            throw new InvalidDataException("Некорректные папки в файле переноса.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var file in package.Files)
        {
            if (file is null || file.Content is null || file.Path is null || !Regex.IsMatch(file.Path, @"^assets/resource-[0-9]{3}\.[A-Za-z0-9]{1,10}\z")
                || !files.Add(file.Path) || (size += file.Content.LongLength) > MaxResourceBytes)
                throw new InvalidDataException("Некорректные ресурсы в файле переноса.");
        }
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Providers(ParseYaml(package.Yaml)))
        {
            if (!provider.Children.TryGetValue(new YamlScalarNode("path"), out var node)) continue;
            if (node is not YamlScalarNode { Value: { } path } || !Regex.IsMatch(path, @"^assets/resource-[0-9]{3}\.[A-Za-z0-9]{1,10}\z"))
                throw new InvalidDataException("Файл провайдера не входит в переносимый профиль.");
            referenced.Add(path);
            if (!files.Contains(path) && !IsRemote(provider)) throw new InvalidDataException("В файле переноса отсутствует локальный провайдер.");
        }
        if (files.Any(path => !referenced.Contains(path))) throw new InvalidDataException("В файле переноса есть посторонние ресурсы.");
        var candidate = document.BuildText(package.Rules.Select(rule => rule.ToRule()));
        if (!package.HasDraft && candidate != document.BuildText(document.Rules))
            throw new InvalidDataException("Правила не совпадают с сохранённой конфигурацией. Не указан черновик.");
    }

    internal static async Task<string> CreateProfileAsync(ConfigurationPackage package, string corePath, string storageDirectory, CancellationToken token)
    {
        Validate(package);
        var profilesRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(storageDirectory, "Profiles"));
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(profilesRoot, "transfer-" + Guid.NewGuid().ToString("N")));
        if (System.IO.Path.GetDirectoryName(directory) != profilesRoot || Directory.Exists(directory)) throw new IOException("Не удалось создать отдельный профиль.");
        Directory.CreateDirectory(directory);
        var success = false;
        try
        {
            var path = System.IO.Path.Combine(directory, "profile.yaml");
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                foreach (var file in package.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(directory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Недопустимый путь ресурса.");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stream.Write(file.Content);
                    stream.Flush(true);
                }
                AtomicFile.Write(path, package.Yaml);
            }, token);
            var check = await CoreProcessManager.ValidateAsync(corePath, path, token);
            if (!check.Success) throw new InvalidDataException("Ядро не приняло конфигурацию из файла переноса. Текущий профиль не изменён.");
            var candidate = System.IO.Path.Combine(directory, ".draft-check.yaml");
            AtomicFile.Write(candidate, ClashConfigDocument.Parse(path, package.Yaml).BuildText(package.Rules.Select(rule => rule.ToRule())));
            check = await CoreProcessManager.ValidateAsync(corePath, candidate, token);
            File.Delete(candidate);
            if (!check.Success) throw new InvalidDataException("Ядро не приняло правила из файла переноса. Текущий профиль не изменён.");
            token.ThrowIfCancellationRequested();
            success = true;
            return path;
        }
        finally
        {
            // This unique directory was created here and contains only this import.
            if (!success) { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private static bool ValidField(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && !value.Contains(',') && !value.Any(char.IsControl);
    private static bool ValidFolder(string? value) => value is not null && value.Length <= 256 && !value.Any(char.IsControl);
    private static YamlStream ParseYaml(string text)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode) throw new InvalidDataException("Некорректная конфигурация в файле переноса.");
        return yaml;
    }
    private static IEnumerable<YamlMappingNode> Providers(YamlStream yaml)
    {
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        foreach (var name in new[] { "proxy-providers", "rule-providers" })
            if (root.Children.TryGetValue(new YamlScalarNode(name), out var value) && value is YamlMappingNode providers)
                foreach (var provider in providers.Children.Values.OfType<YamlMappingNode>()) yield return provider;
    }
    private static bool IsRemote(YamlMappingNode provider) => provider.Children.TryGetValue(new YamlScalarNode("type"), out var type)
        && type is YamlScalarNode { Value: "http" } && provider.Children.TryGetValue(new YamlScalarNode("url"), out var url)
        && url is YamlScalarNode { Value: { } value } && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
