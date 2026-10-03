using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

public partial class MainWindow
{
    internal string CurrentProfilePath => _settings.ConfigPath;
    internal string CurrentCorePath => _settings.CorePath;
    internal bool HasSubscriptionSource => _settings.ProfileSubscriptions.ContainsKey(_settings.ConfigPath);

    internal async Task<bool> SetSubscriptionSourceAsync(string url)
    {
        var saved = false;
        await RunExclusiveAsync(async () =>
        {
            if (_document is null) throw new InvalidOperationException("Сначала выбери профиль.");
            var previous = _settings.ProfileSubscriptions.GetValueOrDefault(_document.Path);
            _settings.ProfileSubscriptions[_document.Path] = SubscriptionSource.Create(url);
            try { await SaveSettingsAsync(); saved = true; }
            catch
            {
                if (previous is null) _settings.ProfileSubscriptions.Remove(_document.Path);
                else _settings.ProfileSubscriptions[_document.Path] = previous;
                throw;
            }
            SetLog("Ссылка подписки сохранена для выбранного профиля.");
        });
        return saved;
    }

    internal void ShowSubscriptionSource(Window owner)
    {
        if (_busy || _closing || _document is null) return;
        var dialog = DialogChrome.CreateWindow(owner, "ПОДПИСКА", 570, 265, 450, 230, ResizeMode.CanResize);
        var input = new TextBox { Name = "SubscriptionUrlInput", MaxLength = 8192, Margin = new Thickness(22, 16, 22, 8), Padding = new Thickness(10) };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 22, 12), FontSize = 12 };
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        var body = new StackPanel(); body.Children.Add(input); body.Children.Add(feedback);
        var save = DialogChrome.MakeButton("СОХРАНИТЬ", true);
        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.Margin = new Thickness(0, 0, 9, 0); cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            try
            {
                _ = SubscriptionImporter.ValidateUrl(input.Text.Trim());
                save.IsEnabled = false;
                if (await SetSubscriptionSourceAsync(input.Text.Trim())) { if (dialog.IsVisible) dialog.Close(); }
                else { feedback.Text = "Ссылка не сохранена. Проверь журнал."; save.IsEnabled = true; }
            }
            catch { feedback.Text = "Нужна действительная HTTPS-ссылка на подписку."; save.IsEnabled = true; }
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(cancel); footer.Children.Add(save);
        dialog.Content = DialogChrome.BuildFrame(dialog, "ПОДПИСКА", body, footer);
        DialogChrome.ShowModal(owner, dialog);
    }

    internal async Task<bool> RefreshSubscriptionAsync(CancellationToken token, HttpClient? client = null)
    {
        var success = false;
        await RunExclusiveAsync(async () =>
        {
            try { success = await RefreshSubscriptionCoreAsync(token, client); }
            catch (OperationCanceledException) when (token.IsCancellationRequested || _closing)
            { if (!_closing) SetLog("Обновление подписки отменено."); }
        });
        return success;
    }

    private async Task<bool> RefreshSubscriptionCoreAsync(CancellationToken token, HttpClient? client)
    {
        if (_document is not { } original || !_settings.ProfileSubscriptions.TryGetValue(original.Path, out var source))
            throw new InvalidOperationException("Для профиля не сохранена ссылка подписки. Укажи её в настройках.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = lifetime.Token;
        var imported = await SubscriptionImporter.ImportAsync(source.ReadUrl(), token, client);
        if (imported.InsecureTls && !UtilityDialogs.ShowConfirm(this, "ПРОВЕРКА СЕРТИФИКАТА ОТКЛЮЧЕНА",
            "В обновлённой подписке отключена проверка TLS-сертификата. Сохранить эти настройки?", "СОХРАНИТЬ", "ОТМЕНА")) return false;
        var oldText = await File.ReadAllTextAsync(original.Path, token);
        if (ClashConfigDocument.Hash(oldText) != original.SourceHash)
            throw new IOException("Профиль изменён на диске. Открой его заново перед обновлением подписки.");
        if (imported.NeedsName)
        {
            var yaml = new YamlStream(); yaml.Load(new StringReader(oldText));
            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            var nodes = (YamlSequenceNode)root.Children[new YamlScalarNode("proxies")];
            var name = nodes.Children.Count == 1 && nodes.Children[0] is YamlMappingNode single
                && single.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode) && nameNode is YamlScalarNode label ? label.Value : null;
            imported = ProfileImporter.NameConnection(imported, name ?? "Подключение");
        }
        var selected = _selectedConnection.Length > 0 ? _selectedConnection : source.SelectedServer;
        if (_core.IsRunning && _diagnostics is { } diagnostics && original.VpnRoute is { } route)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try { selected = await diagnostics.ReadSelectionAsync(route, deadline.Token); }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        var merged = SubscriptionRefresh.Merge(original.Path, oldText, imported, selected);
        var candidate = ClashConfigDocument.Parse(original.Path, merged.Yaml);
        var rules = _rules.Select(rule => rule.Copy()).ToArray();
        var folders = _folderOrder.ToArray();
        var dirty = _dirty;
        var ruleState = _settings.GetRuleState(original.Path);
        var previousUpdatedAt = source.UpdatedAt; var previousSelection = source.SelectedServer;
        var undo = _undoRules.ToArray(); var redo = _redoRules.ToArray();
        var wantedBefore = _connectionRequested;
        var temp = Path.Combine(Path.GetDirectoryName(original.Path)!, ".subscription-" + Guid.NewGuid().ToString("N") + ".yaml");
        string? backup = null; var wasRunning = _core.IsRunning;
        try
        {
            await File.WriteAllTextAsync(temp, merged.Yaml, token);
            var check = await CoreProcessManager.ValidateAsync(_settings.CorePath, temp, token);
            if (!check.Success) throw new InvalidDataException("Ядро не приняло обновлённую подписку. Текущий профиль сохранён.");
            if (dirty)
            {
                await File.WriteAllTextAsync(temp, candidate.BuildText(rules), token);
                check = await CoreProcessManager.ValidateAsync(_settings.CorePath, temp, token);
                if (!check.Success) throw new InvalidDataException("Обновлённые серверы несовместимы с черновиком правил. Текущий профиль сохранён.");
            }
            token.ThrowIfCancellationRequested();
            if (merged.SelectionRemoved && !UtilityDialogs.ShowConfirm(this, "СЕРВЕР УДАЛЁН ИЗ ПОДПИСКИ",
                "Выбранного сервера больше нет. После обновления будет использован доступный сервер из той же группы.", "ОБНОВИТЬ", "ОТМЕНА")) return false;
            if (!await StopCoreAsync()) throw new IOException("Подписка не изменена: остановка ядра не подтверждена.");
            token.ThrowIfCancellationRequested();
            backup = original.CommitText(merged.Yaml);
            _document = candidate;
            if (dirty) _settings.PutDraft(original.Path, candidate.SourceHash, rules);
            else _settings.RemoveDraft(original.Path);
            if (ruleState is not null) _settings.PutRuleState(original.Path, candidate.SourceHash, ruleState.Rules);
            await LoadConfigAsync(original.Path);
            _undoRules.AddRange(undo); _redoRules.AddRange(redo); _historyState = CaptureRuleEditState(_dirty);
            token.ThrowIfCancellationRequested();
            if (wasRunning && !_closing)
            {
                await StartCoreAsync(applyDraft: false);
                if (!_core.IsRunning) throw new IOException("Не удалось возобновить VPN после обновления подписки.");
            }
            token.ThrowIfCancellationRequested();
            source.UpdatedAt = DateTimeOffset.UtcNow; source.SelectedServer = merged.SelectedServer;
            await SaveSettingsAsync();
            token.ThrowIfCancellationRequested();
            RefreshRuleHistoryControls();
            SetLog($"Подписка обновлена: добавлено серверов {merged.Added}, удалено {merged.Removed}. Правила и папки сохранены.");
            return true;
        }
        catch
        {
            if (backup is not null && await StopCoreAsync())
            {
                if (await File.ReadAllTextAsync(original.Path) != merged.Yaml)
                    throw new IOException("YAML изменён извне; откат подписки остановлен. Резервная копия сохранена.");
                AtomicFile.Write(original.Path, oldText);
                _document = ClashConfigDocument.Load(original.Path);
                _folderOrder.Clear(); _folderOrder.AddRange(folders); ReplaceRules(rules);
                SetDirty(dirty);
                if (dirty) _settings.PutDraft(original.Path, original.SourceHash, rules);
                else _settings.RemoveDraft(original.Path);
                if (ruleState is not null) _settings.PutRuleState(original.Path, original.SourceHash, ruleState.Rules);
                else _settings.ProfileRuleStates.Remove(original.Path);
                _settings.PutFolderOrder(original.Path, folders);
                source.UpdatedAt = previousUpdatedAt; source.SelectedServer = previousSelection;
                _undoRules.Clear(); _undoRules.AddRange(undo); _redoRules.Clear(); _redoRules.AddRange(redo);
                _historyState = CaptureRuleEditState(dirty);
                try { await SaveSettingsAsync(); }
                catch { SetLog("Прежний профиль восстановлен, но настройки не удалось записать. Проверь доступ к файлу настроек."); }
                SetLog("Предыдущая подписка восстановлена. Правила и черновик сохранены.");
            }
            if (wasRunning && !_closing && !_core.IsRunning)
            {
                _connectionRequested = wantedBefore;
                try { await StartCoreAsync(applyDraft: false); }
                catch { SetLog("Не удалось восстановить запуск VPN. Запусти его вручную."); }
            }
            throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
