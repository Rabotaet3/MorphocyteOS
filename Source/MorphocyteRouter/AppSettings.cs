using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace MorphocyteRouter;

public sealed class ProfileDraft
{
    public string SourceHash { get; set; } = "";
    public List<DomainRule> Rules { get; set; } = new();
}

public sealed class ProfileRuleState
{
    public string SourceHash { get; set; } = "";
    public List<DomainRule> Rules { get; set; } = new();
}

public sealed partial class SubscriptionSource
{
    public string ProtectedUrl { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    public string SelectedServer { get; set; } = "";
}

public sealed class AppSettings
{
    public string ConfigPath { get; set; } = "";
    public string CorePath { get; set; } = "";
    public string PreferredRoute { get; set; } = "";
    public string Theme { get; set; } = "Тёмная морфоцитная";
    public double InterfaceScale { get; set; } = 1;
    public bool AutoCheckUpdates { get; set; } = true;
    public string AutoUpdateBlockedVersion { get; set; } = "";
    public bool ShowEventLog { get; set; }
    public bool LaunchAtSignIn { get; set; }
    public bool AutoConnectOnStartup { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public Dictionary<string, SubscriptionSource> ProfileSubscriptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Legacy fields migrate 1.5.x drafts on load.
    public bool HasRuleDraft { get; set; }
    public string DraftConfigPath { get; set; } = "";
    public List<DomainRule> DraftRules { get; set; } = new();
    public Dictionary<string, ProfileDraft> ProfileDrafts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProfileRuleState> ProfileRuleStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> ProfileFolderOrders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public string? RecoveryMessage { get; private set; }
    private string? _storagePath;
    internal string StorageDirectory => Path.GetDirectoryName(_storagePath ?? DefaultPath)!;
    private bool _recovered;
    internal static string LocalDataDirectory => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MorphocyteOS", "Application");
    private static string DefaultPath => Path.Combine(LocalDataDirectory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        AppSettings? settings = null;
        string? recovery = null;
        var recovered = false;
        try { if (File.Exists(path)) settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Пустые настройки."); }
        catch (Exception)
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path + ".bak")) ?? throw new InvalidDataException();
                recovery = "Настройки восстановлены из резервной копии.";
                recovered = true;
            }
            catch
            {
                // A damaged settings file must not prevent a user from opening the app.
                // Preserve both files before the next successful settings write replaces them.
                PreserveDamagedFile(path);
                PreserveDamagedFile(path + ".bak");
                settings = new AppSettings();
                recovery = "Настройки повреждены или недоступны. Загружены стандартные значения; исходные файлы сохранены для восстановления: " + path;
                recovered = File.Exists(path);
            }
        }
        settings ??= new AppSettings();
        settings._storagePath = path;
        settings._recovered = recovered;
        settings.RecoveryMessage = recovery;
        settings.ConfigPath = settings.ConfigPath?.Trim() ?? "";
        settings.CorePath = settings.CorePath?.Trim() ?? "";
        settings.PreferredRoute = settings.PreferredRoute?.Trim() ?? "";
        settings.Theme = string.IsNullOrWhiteSpace(settings.Theme) ? "Тёмная морфоцитная" : settings.Theme.Trim();
        settings.InterfaceScale = NormalizeScale(settings.InterfaceScale);
        settings.ProfileDrafts = NormalizeProfiles(settings.ProfileDrafts, draft =>
        {
            draft.SourceHash ??= "";
            draft.Rules = NormalizeRules(draft.Rules);
        });
        settings.ProfileRuleStates = NormalizeProfiles(settings.ProfileRuleStates, state =>
        {
            state.SourceHash ??= "";
            state.Rules = NormalizeRules(state.Rules);
        });
        settings.ProfileSubscriptions = NormalizeProfiles(settings.ProfileSubscriptions, source => { source.ProtectedUrl ??= ""; source.SelectedServer ??= ""; });
        settings.ProfileFolderOrders = NormalizeProfiles(settings.ProfileFolderOrders, _ => { });
        foreach (var key in settings.ProfileFolderOrders.Keys.ToArray())
            settings.ProfileFolderOrders[key] = settings.ProfileFolderOrders[key]
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (settings.HasRuleDraft && !string.IsNullOrWhiteSpace(settings.DraftConfigPath))
        {
            try { settings.ProfileDrafts.TryAdd(Path.GetFullPath(settings.DraftConfigPath), new ProfileDraft { Rules = NormalizeRules(settings.DraftRules) }); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            settings.HasRuleDraft = false;
            settings.DraftConfigPath = "";
            settings.DraftRules = new();
        }
        return settings;
    }

    public static double NormalizeScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, 0.8, 1.5) : 1;

    private static Dictionary<string, T> NormalizeProfiles<T>(Dictionary<string, T>? profiles, Action<T> normalize) where T : class
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in profiles ?? new())
        {
            if (pair.Value is null || string.IsNullOrWhiteSpace(pair.Key)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(pair.Key); }
            catch (ArgumentException) { continue; }
            catch (NotSupportedException) { continue; }
            normalize(pair.Value);
            result[fullPath] = pair.Value;
        }
        return result;
    }

    private static List<DomainRule> NormalizeRules(List<DomainRule>? rules)
    {
        var result = new List<DomainRule>();
        foreach (var rule in rules ?? new())
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Kind) || string.IsNullOrWhiteSpace(rule.Value)
                || string.IsNullOrWhiteSpace(rule.Route)) continue;
            rule.Kind = rule.Kind.Trim().ToUpperInvariant();
            rule.Extra ??= "";
            rule.Folder ??= "";
            result.Add(rule);
        }
        return result;
    }

    private static void PreserveDamagedFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public ProfileDraft? GetDraft(string path) => ProfileDrafts.GetValueOrDefault(Path.GetFullPath(path));
    public void PutDraft(string path, string hash, IEnumerable<DomainRule> rules) =>
        ProfileDrafts[Path.GetFullPath(path)] = new ProfileDraft { SourceHash = hash, Rules = rules.Select(rule => rule.Copy()).ToList() };
    public void RemoveDraft(string path) => ProfileDrafts.Remove(Path.GetFullPath(path));
    public ProfileRuleState? GetRuleState(string path) => ProfileRuleStates.GetValueOrDefault(Path.GetFullPath(path));
    public void PutRuleState(string path, string hash, IEnumerable<DomainRule> rules) =>
        ProfileRuleStates[Path.GetFullPath(path)] = new ProfileRuleState { SourceHash = hash, Rules = rules.Select(rule => rule.Copy()).ToList() };
    public List<string> GetFolderOrder(string path) => ProfileFolderOrders.GetValueOrDefault(Path.GetFullPath(path)) ?? new List<string>();
    public void PutFolderOrder(string path, IEnumerable<string> folders) =>
        ProfileFolderOrders[Path.GetFullPath(path)] = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void Save()
    {
        InterfaceScale = NormalizeScale(InterfaceScale);
        var path = _storagePath ?? DefaultPath;
        var backup = _recovered ? path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") : path + ".bak";
        AtomicFile.Write(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), backup);
        _recovered = false;
    }
}
