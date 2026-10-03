using System.IO;
using System.Text;

namespace MorphocyteRouter;

public partial class MainWindow
{
    internal Task<ConfigurationPackage> CaptureConfigurationAsync(SettingsDialogSelection selection)
    {
        var options = new TransferOptions { Theme = selection.Theme, InterfaceScale = selection.InterfaceScale,
            AutoCheckUpdates = selection.AutoCheckUpdates, LaunchAtSignIn = selection.LaunchAtSignIn,
            AutoConnectOnStartup = selection.AutoConnectOnStartup, AutoReconnect = selection.AutoReconnect, ShowEventLog = _settings.ShowEventLog,
            PreferredRoute = _settings.PreferredRoute is "DIRECT" or "REJECT" ? _settings.PreferredRoute : "VPN" };
        if (selection.Transfer is { } pending)
            return Task.Run(() =>
            {
                var copy = ConfigurationTransfer.Deserialize(Encoding.UTF8.GetBytes(ConfigurationTransfer.Serialize(pending)));
                options.PreferredRoute = pending.Options.PreferredRoute;
                options.ShowEventLog = pending.Options.ShowEventLog;
                copy.Options = options;
                return copy;
            }, _lifetime.Token);
        if (selection.Imported is { } imported)
            return Task.Run(() =>
            {
                var path = Path.Combine(_settings.StorageDirectory, "profile.yaml");
                var document = ClashConfigDocument.Parse(path, imported.Yaml);
                var package = ConfigurationTransfer.Capture(path, imported.Yaml, document.Rules, Array.Empty<string>(), options, false);
                package.SubscriptionUrl = imported.SubscriptionUrl;
                return package;
            }, _lifetime.Token);
        if (string.IsNullOrWhiteSpace(selection.ConfigPath)) throw new InvalidOperationException("Сначала выбери профиль VPN.");
        var configPath = Path.GetFullPath(selection.ConfigPath);
        var current = _document is { } active && string.Equals(active.Path, configPath, StringComparison.OrdinalIgnoreCase);
        var sourceHash = current ? _document!.SourceHash : null;
        var snapshot = current ? _rules.Select(rule => rule.Copy()).ToList() : null;
        var folders = current ? _folderOrder.ToList() : _settings.GetFolderOrder(configPath).ToList();
        var draft = _settings.GetDraft(configPath);
        var state = _settings.GetRuleState(configPath);
        var isDirty = _dirty;
        var subscriptionUrl = _settings.ProfileSubscriptions.GetValueOrDefault(configPath)?.ReadUrl();
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(ConfigurationTransfer.ReadBounded(configPath, 4 * 1024 * 1024));
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var yaml = reader.ReadToEnd();
            var document = ClashConfigDocument.Parse(configPath, yaml);
            if (sourceHash is not null && sourceHash != document.SourceHash)
                throw new IOException("Профиль изменён на диске. Открой его заново перед экспортом.");
            var restoreDraft = draft is not null && (draft.SourceHash.Length == 0 || draft.SourceHash == document.SourceHash);
            var rules = snapshot ?? (restoreDraft ? draft!.Rules : state is not null && state.SourceHash == document.SourceHash
                ? MergeRuleUiState(document.Rules, state.Rules) : document.Rules.ToList());
            var package = ConfigurationTransfer.Capture(configPath, yaml, rules, folders, options, current ? isDirty : restoreDraft);
            package.SubscriptionUrl = subscriptionUrl;
            return package;
        }, _lifetime.Token);
    }

    private async Task ImportConfigurationAsync(ConfigurationPackage package, string path)
    {
        var document = ClashConfigDocument.Load(path);
        var rules = package.Rules.Select(rule => rule.ToRule()).ToList();
        _settings.PutFolderOrder(path, package.FolderOrder);
        if (package.HasDraft) _settings.PutDraft(path, document.SourceHash, rules);
        else _settings.PutRuleState(path, document.SourceHash, rules);
        _settings.PreferredRoute = package.Options.PreferredRoute;
        if (package.SubscriptionUrl is { } url) _settings.ProfileSubscriptions[path] = SubscriptionSource.Create(url);
        await LoadConfigAsync(path);
        SelectMainPage(package.Options.ShowEventLog);
        SetLog("Настройки и профиль импортированы. " + (package.HasDraft ? "Правила восстановлены как черновик." : "Сохранённая конфигурация восстановлена."));
    }
}
