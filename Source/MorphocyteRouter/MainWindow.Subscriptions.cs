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
    internal string SubscriptionIdentityDirectory => _settings.StorageDirectory;
    internal bool HasSubscriptionSource => _settings.ProfileSubscriptions.ContainsKey(_settings.ConfigPath);

    internal async Task<(bool Applied, string Message)> ApplySubscriptionSourceAsync(string url,
        CancellationToken token, HttpClient? client = null, Window? promptOwner = null)
    {
        (bool Applied, string Message) result = (false, "Другая операция ещё выполняется. Повтори загрузку после её завершения.");
        await RunExclusiveAsync(async () =>
        {
            try
            {
                var proposed = SubscriptionSource.Create(url.Trim());
                var applied = await RefreshSubscriptionFromSourceCoreAsync(token, client, proposed, promptOwner ?? this);
                result = applied ? (true, $"Подписка применена. Серверов: {_document!.ConnectionCount}.")
                    : (false, "Замена подписки отменена. Прежний профиль сохранён.");
            }
            catch (OperationCanceledException) { result = (false, "Загрузка отменена. Подписка не применена."); }
            catch (InvalidDataException error) { result = (false, error.Message); SetLog(error.Message); }
            catch (Exception)
            {
                result = (false, "Не удалось применить подписку. Проверь доступ к профилю и выбранное ядро; подробности в журнале.");
                SetLog("Не удалось заменить подписку. Проверь выбранное ядро, профиль и доступ к файлам.");
            }
        });
        return result;
    }

    internal bool ShowSubscriptionSource(Window owner, HttpClient? client = null)
    {
        if (_busy || _closing || _document is null) return false;
        var applied = false;
        var dialog = DialogChrome.CreateWindow(owner, "ПОДПИСКА", 650, 390, 550, 340, ResizeMode.CanResize);
        using var lifetime = new CancellationTokenSource();
        dialog.Closed += (_, _) => lifetime.Cancel();
        var input = new TextBox { Name = "SubscriptionUrlInput", MaxLength = 8192, Margin = new Thickness(22, 16, 22, 8), Padding = new Thickness(10) };
        var feedback = new TextBlock { Name = "SubscriptionSourceFeedback", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 22, 12), FontSize = 12 };
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        var explanation = new TextBlock { Text = "Новая подписка заменит серверы в текущем профиле. Правила и папки сохранятся. Если конфигурация изменится, работающий VPN кратковременно переподключится.",
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(22, 14, 22, 0) };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        var body = new StackPanel(); body.Children.Add(explanation); body.Children.Add(input);
        body.Children.Add(feedback);
        var current = _settings.ProfileSubscriptions.GetValueOrDefault(_document.Path);
        try
        {
            input.Text = current?.ReadUrl() ?? "";
        }
        catch (InvalidDataException) { feedback.Text = "Сохранённую ссылку не удалось прочитать. Вставь её заново."; }
        var save = DialogChrome.MakeButton("ЗАГРУЗИТЬ И ПРИМЕНИТЬ", true); save.Name = "ApplySubscriptionSourceButton";
        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.Margin = new Thickness(0, 0, 9, 0); cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            try
            {
                _ = SubscriptionImporter.ValidateUrl(input.Text.Trim());
                save.IsEnabled = input.IsEnabled = false;
                feedback.Text = "Загружаю и проверяю серверы… Можно отменить.";
                var result = await ApplySubscriptionSourceAsync(input.Text.Trim(),
                    lifetime.Token, client, dialog);
                if (lifetime.IsCancellationRequested) return;
                if (result.Applied) { applied = true; dialog.Close(); }
                else feedback.Text = result.Message;
            }
            catch (InvalidDataException error) { if (!lifetime.IsCancellationRequested) feedback.Text = error.Message; }
            catch { if (!lifetime.IsCancellationRequested) feedback.Text = "Не удалось применить подписку. Прежний профиль не заменён."; }
            finally { if (!lifetime.IsCancellationRequested) save.IsEnabled = input.IsEnabled = true; }
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(cancel); footer.Children.Add(save);
        dialog.Content = DialogChrome.BuildFrame(dialog, "ПОДПИСКА", body, footer);
        DialogChrome.ShowModal(owner, dialog);
        return applied;
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

    private Task<bool> RefreshSubscriptionCoreAsync(CancellationToken token, HttpClient? client)
        => RefreshSubscriptionFromSourceCoreAsync(token, client, null, this);

    private async Task<bool> RefreshSubscriptionFromSourceCoreAsync(CancellationToken token, HttpClient? client,
        SubscriptionSource? proposed, Window promptOwner)
    {
        _updatingSubscription = true;
        RefreshSubscriptionBanner();
        try { return await ApplySubscriptionRefreshAsync(token, client, proposed, promptOwner); }
        finally
        {
            _updatingSubscription = false;
            if (!_closing) RefreshSubscriptionBanner();
        }
    }

    private async Task<bool> ApplySubscriptionRefreshAsync(CancellationToken token, HttpClient? client,
        SubscriptionSource? proposed, Window promptOwner)
    {
        if (_document is not { } original)
            throw new InvalidDataException("Сначала выбери профиль.");
        var previousSource = _settings.ProfileSubscriptions.GetValueOrDefault(original.Path);
        var source = proposed ?? previousSource;
        if (source is null)
            throw new InvalidOperationException("Для профиля не сохранена ссылка подписки. Укажи её в настройках.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = lifetime.Token;
        // An older header-only request must not replace the metadata delivered
        // by this complete refresh, even when it finishes downloading later.
        _subscriptionInfoGeneration++;
        var storage = _settings.StorageDirectory;
        var imported = await Task.Run(() => SubscriptionImporter.ImportAsync(source.ReadUrl(), token, client,
            hardwareId: SubscriptionDeviceIdentity.GetOrCreate(storage), allowPartial: true), token);
        if (!ProfileImportDialog.ConfirmCompatibility(promptOwner, imported)) return false;
        if (imported.InsecureTls && !UtilityDialogs.ShowConfirm(promptOwner, "ПРОВЕРКА СЕРТИФИКАТА ОТКЛЮЧЕНА",
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
        var selected = _selectedConnection.Length > 0 ? _selectedConnection : previousSource?.SelectedServer ?? source.SelectedServer;
        if (_core.IsRunning && _diagnostics is { } diagnostics && original.VpnRoute is { } route)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try { selected = await diagnostics.ReadSelectionAsync(route, deadline.Token); }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        var (merged, candidate, currentCount, updatedCount) = await Task.Run(() =>
        {
            var result = SubscriptionRefresh.Merge(original.Path, oldText, imported, selected);
            var document = result.Yaml == oldText ? original : ClashConfigDocument.Parse(original.Path, result.Yaml);
            return (result, document, VpnServerCatalog.Choices(original).Count, VpnServerCatalog.Choices(document).Count);
        }, token);
        var profileChanged = !ReferenceEquals(candidate, original);
        var rules = _rules.Select(rule => rule.Copy()).ToArray();
        var dirty = _dirty;
        var ruleState = _settings.GetRuleState(original.Path);
        var previousDraft = _settings.GetDraft(original.Path);
        var previousDraftSaved = _draftSaved;
        var previousUpdatedAt = source.UpdatedAt; var previousSelection = source.SelectedServer;
        var previousMetadata=source.Metadata;
        var previousPartialImport = source.AllowPartialImport;
        var previousHardwareId = source.SendHardwareId;
        var previousProfileSelection = _settings.ProfileSelections.GetValueOrDefault(original.Path);
        var wantedBefore = _connectionRequested;
        var temp = Path.Combine(Path.GetDirectoryName(original.Path)!, ".subscription-" + Guid.NewGuid().ToString("N") + ".yaml");
        var corePath = _settings.CorePath;
        var fullTunnel = _settings.FullTunnel;
        var proxyOnly = _settings.ProxyOnly;
        var wasRunning = _core.IsRunning;
        var metadataChanged = false;
        string? backup = null;
        try
        {
            await File.WriteAllTextAsync(temp, merged.Yaml, token);
            if (!CoreBackend.IsSingBox(corePath) && XrayBridgePlan.ContainsConnections(candidate))
                throw new InvalidDataException("Для серверов этой подписки выбери основное ядро sing-box в настройках и повтори загрузку.");
            var check = await Task.Run(() => CoreProcessManager.ValidateAsync(corePath, temp, token, fullTunnel, proxyOnly), token);
            if (!check.Success) throw new InvalidDataException("Ядро не приняло обновлённую подписку. Текущий профиль сохранён.");
            if (dirty)
            {
                var draftText = await Task.Run(() => candidate.BuildText(rules), token);
                await File.WriteAllTextAsync(temp, draftText, token);
                check = await Task.Run(() => CoreProcessManager.ValidateAsync(corePath, temp, token, fullTunnel, proxyOnly), token);
                if (!check.Success) throw new InvalidDataException("Обновлённые серверы несовместимы с черновиком правил. Текущий профиль сохранён.");
            }
            token.ThrowIfCancellationRequested();
            if (merged.Removed > 0 || updatedCount < currentCount || merged.SelectionRemoved)
            {
                var details = new List<string>();
                if (updatedCount < currentCount)
                    details.Add($"Количество серверов уменьшится с {currentCount} до {updatedCount}.");
                if (merged.Removed > 0)
                    details.Add($"Добавится серверов — {merged.Added}. Исчезнет — {merged.Removed}.");
                if (merged.SelectionRemoved)
                    details.Add("Выбранного сервера больше нет. После обновления будет использован доступный сервер из той же группы.");
                if (!UtilityDialogs.ShowConfirm(promptOwner, "ИЗМЕНИЛСЯ СПИСОК СЕРВЕРОВ", string.Join("\n\n", details), "ОБНОВИТЬ", "ОТМЕНА")) return false;
            }
            if (profileChanged)
            {
                if (!await StopCoreAsync()) throw new IOException("Подписка не изменена: остановка ядра не подтверждена.");
                token.ThrowIfCancellationRequested();
                backup = await Task.Run(() => original.CommitText(merged.Yaml), token);
                CancelConnectionIntent();
                _document = candidate;
                // The refresh only replaces the server sections. Keep the editor,
                // its applied rules and history attached to the same rule objects.
                if (dirty) _settings.PutDraft(original.Path, candidate.SourceHash, rules, archiveConflict: false);
                else _settings.RemoveDraft(original.Path);
                if (ruleState?.SourceHash == original.SourceHash)
                    _settings.PutRuleState(original.Path, candidate.SourceHash, ruleState.Rules);
                if (merged.SelectedServer.Length > 0) _settings.ProfileSelections[original.Path] = merged.SelectedServer;
                else _settings.ProfileSelections.Remove(original.Path);
            }
            else if (await File.ReadAllTextAsync(original.Path, token) != oldText)
                throw new IOException("Профиль изменён на диске. Открой его заново перед обновлением подписки.");
            token.ThrowIfCancellationRequested();
            if (profileChanged && wasRunning && !_closing)
            {
                await StartCoreAsync(applyDraft: false);
                if (!_core.IsRunning) throw new IOException("Не удалось возобновить VPN после обновления подписки.");
            }
            token.ThrowIfCancellationRequested();
            metadataChanged = true;
            source.UpdatedAt = DateTimeOffset.UtcNow; source.SelectedServer = merged.SelectedServer;
            source.AllowPartialImport = true;
            source.SendHardwareId = true;
            source.Metadata=imported.Metadata;
            _settings.ProfileSubscriptions[original.Path] = source;
            await SaveSettingsAsync();
            if (profileChanged) _draftSaved = dirty;
            token.ThrowIfCancellationRequested();
            _selectedConnection = merged.SelectedServer;
            RefreshConnectionCard();
            SetLog(profileChanged
                ? $"Подписка обновлена: добавлено серверов {merged.Added}, удалено {merged.Removed}. Правила и папки сохранены."
                : "Сведения о подписке обновлены. Список серверов не изменился.");
            return true;
        }
        catch
        {
            if (backup is not null || metadataChanged)
            {
                if (backup is not null)
                {
                    if (!await StopCoreAsync()) throw;
                    if (await File.ReadAllTextAsync(original.Path) != merged.Yaml)
                        throw new IOException("YAML изменён извне; откат подписки остановлен. Резервная копия сохранена.");
                    await Task.Run(() => AtomicFile.Write(original.Path, oldText));
                    _document = original;
                    if (previousDraft is null) _settings.RemoveDraft(original.Path);
                    else _settings.ProfileDrafts[original.Path] = previousDraft;
                    _draftSaved = previousDraftSaved;
                    if (ruleState is null) _settings.ProfileRuleStates.Remove(original.Path);
                    else _settings.ProfileRuleStates[original.Path] = ruleState;
                    if (previousProfileSelection is null) _settings.ProfileSelections.Remove(original.Path);
                    else _settings.ProfileSelections[original.Path] = previousProfileSelection;
                }
                source.UpdatedAt = previousUpdatedAt; source.SelectedServer = previousSelection;
                source.AllowPartialImport = previousPartialImport;
                source.SendHardwareId = previousHardwareId;
                source.Metadata=previousMetadata;
                if (previousSource is null) _settings.ProfileSubscriptions.Remove(original.Path);
                else _settings.ProfileSubscriptions[original.Path] = previousSource;
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
        finally { await Task.Run(() => { if (File.Exists(temp)) File.Delete(temp); }); }
    }
}
