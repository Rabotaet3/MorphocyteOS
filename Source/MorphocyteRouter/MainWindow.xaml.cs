using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MorphocyteRouter;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DomainRule> _rules = new();
    private readonly ICollectionView _rulesView;
    private readonly List<string> _folderOrder = new();
    private readonly Dictionary<string, bool> _folderExpansionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppSettings _settings;
    private readonly CoreProcessManager _core = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _uptimeTimer;
    private readonly DispatcherTimer _logRefreshTimer;
    private readonly Queue<string> _logLines = new();
    private readonly ConcurrentQueue<string> _pendingCoreOutput = new();
    private const int MaxDisplayedLogLines = 2500;
    private const int MaxCoreLinesPerRefresh = 1000;
    private readonly string _logPath;
    private ClashConfigDocument? _document;
    private ScrollViewer? _rulesScrollViewer;
    private ScrollViewer? _logScrollViewer;
    private DateTime _startedAt;
    private bool _dirty, _loading, _busy, _closing, _allowClose, _draftSaved;
    private int _dialogBackdropVersion;
    private bool _dialogBackdropVisible, _logDisplayDeferred;
    private static readonly SemaphoreSlim LogWriteGate = new(1, 1);
    private static string PersistentLogPath => Path.Combine(AppSettings.LocalDataDirectory, "router.log");

    public MainWindow() : this(null) { }

    internal bool LaunchAtSignIn => _settings.LaunchAtSignIn;
    internal bool AutoConnectOnStartup => _settings.AutoConnectOnStartup;

    public MainWindow(AppSettings? settings, string? logPath = null)
    {
        _desktopIntegrationEnabled = settings is null;
        _settings = settings ?? AppSettings.Load();
        _settings.Theme = ThemeManager.Normalize(_settings.Theme);
        ThemeManager.Apply(_settings.Theme);
        InitializeComponent();
        ThemeManager.Changed += RefreshThemeIcons;
        SourceInitialized += (_, _) => RefreshThemeIcons();
        DpiChanged += (_, _) => RefreshThemeIcons();
        RefreshThemeIcons();
        ApplyInterfaceScale(_settings.InterfaceScale);
        _rulesView = new ListCollectionView(_rules) { Filter = FilterUserRule };
        var folderGroups = new PropertyGroupDescription(nameof(DomainRule.FolderLabel))
        {
            CustomSort = new FolderOrderComparer(() => _folderOrder)
        };
        _rulesView.GroupDescriptions.Add(folderGroups);
        RulesList.ItemsSource = _rulesView;
        SelectMainPage(_settings.ShowEventLog);
        _logPath = logPath ?? PersistentLogPath;
        if (File.Exists(_logPath))
        {
            try
            {
                foreach (var line in File.ReadLines(_logPath).TakeLast(MaxDisplayedLogLines)) _logLines.Enqueue(line);
                if (_logLines.Count > 0) LogText.Text = string.Join(Environment.NewLine, _logLines);
            }
            catch { }
        }
        if (_logLines.Count == 0) LogText.Clear();

        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => UpdateUptime();
        _logRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _logRefreshTimer.Tick += (_, _) => FlushCoreOutput();
        _logRefreshTimer.Start();
        InitializeDiagnostics();
        InitializeAutomaticUpdates();
        InitializeConnectionRecovery();
        LogText.Loaded += LogText_Loaded;

        _core.Output += message => _pendingCoreOutput.Enqueue(message);
        _core.Exited += () => Post(() =>
        {
            if (_busy || _closing || _core.IsRunning) return;
            _uptimeTimer.Stop();
            SetStatus("ЯДРО ОСТАНОВЛЕНО", "Смотри журнал событий", "#FFD166");
            ResetDiagnostics();
            UpdateControls();
            RecoverUnexpectedCoreExit();
        });
        _core.StartupFailed += message => Post(() =>
        {
            _uptimeTimer.Stop();
            SetStatus("СБОЙ ЯДРА", "Смотри журнал событий", "#FF6B8A");
            ResetDiagnostics();
            LastIssueText.Text = "Ошибка запуска ядра. Подробности — в журнале.";
            SetLog(message);
            UpdateControls();
        });

        Loaded += async (_, _) =>
        {
            if (_desktopIntegrationEnabled) InitializeDesktopIntegration();
            await RunExclusiveAsync(async () =>
            {
                await BundledResources.PrepareDefaultsAsync(_settings, _lifetime.Token);
                if (File.Exists(_settings.ConfigPath)) await LoadConfigAsync(_settings.ConfigPath);
                else SetLog("Выбранный YAML не найден. Выбери профиль в настройках или импортируй подключение.");
                if (_startupMode && _settings.LaunchAtSignIn && _settings.AutoConnectOnStartup)
                    await StartCoreAsync(applyDraft: false);
            });
            if (_settings.RecoveryMessage is { } message) SetLog(message);
            UpdateControls();
            await ReadUpdateResultAsync();
            _ = CleanCompletedUpdateCachesAsync();
            if (_settings.AutoCheckUpdates) await CheckForUpdatesOnStartupAsync();
        };
        Closing += MainWindow_Closing;
        Closed += (_, _) => { _logRefreshTimer.Stop(); _automaticUpdateTimer?.Stop(); DisposeDesktopIntegration(); DisposeConnectionRecovery(); ResetDiagnostics(); };
        Deactivated += (_, _) => CancelRuleDrag();
        UpdateControls();
    }

    internal void ApplyInterfaceScale(double scale)
    {
        UiScaleManager.CurrentScale = AppSettings.NormalizeScale(scale);
        UiScaleManager.Apply(this, WindowContent, UiScaleManager.CurrentScale);
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.CaptionHeight = 58 * UiScaleManager.CurrentScale;
        UpdateFrameClip();
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        UpdateCheckResult result;
        try { result = await CheckUpdatesAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_closing) { return; }
        catch (Exception ex) { SetLog("Проверка обновлений не выполнена: " + ex.Message); return; }
        if (_closing) return;
        SetLog(result.Message);
    }

    internal void SetDialogBackdrop(bool visible)
    {
        var version = ++_dialogBackdropVersion;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        if (visible)
        {
            _dialogBackdropVisible = true;
            // Blur the entire visual layer (including the title bar and decorative texture),
            // while the rounded clip prevents the effect leaking outside the window silhouette.
            // Keep the blur kernel fixed: animating its radius rerenders the entire
            // main window while the transparent dialog is also being animated.
            var blur = new BlurEffect { Radius = 5.5, RenderingBias = RenderingBias.Performance };
            WindowContent.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
            WindowContent.Effect = blur;
            DialogShade.Visibility = Visibility.Visible;
            // Do not animate a second large layered HWND behind the entering dialog.
            // Compose the static backdrop once; only the dialog fades in.
            DialogShade.BeginAnimation(UIElement.OpacityProperty, null);
            DialogShade.Opacity = 0.75;
        }
        else
        {
            var shadeFade = new DoubleAnimation(DialogShade.Opacity, 0, duration) { EasingFunction = easing };
            shadeFade.Completed += (_, _) =>
            {
                if (version != _dialogBackdropVersion) return;
                DialogShade.Visibility = Visibility.Collapsed;
                WindowContent.Effect = null;
                WindowContent.CacheMode = null;
                _dialogBackdropVisible = false;
                UpdateUptime();
                RefreshDeferredLog();
                ScheduleAutoInstall();
            };
            DialogShade.BeginAnimation(UIElement.OpacityProperty, shadeFade);
        }
    }

    private void FrameContent_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFrameClip();

    private void UpdateFrameClip()
    {
        var bounds = new Rect(0, 0, FrameContent.ActualWidth, FrameContent.ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        // Clip the composed frame after child effects are rendered. In particular,
        // BlurEffect expands its pixels beyond the child's own clip; this parent
        // clip keeps both the blurred content and modal shade inside the rounded frame.
        const double frameRadius = 19.5;
        FrameContent.Clip = new RectangleGeometry(bounds, frameRadius, frameRadius);
    }

    // Every mutation and lifecycle operation enters this gate. Nested helpers do not reacquire it.
    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (_closing || !await _operations.WaitAsync(0)) return;
        _busy = true;
        UpdateControls();
        try { await action(); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            LastIssueText.Text = ex.Message;
            SetLog(ex.Message);
            if (!IsVisible && !_closing) RestoreFromTray();
            if (!_closing) UtilityDialogs.ShowNotice(this, "MorphocyteOS", ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateControls();
            _operations.Release();
        }
    }

    private void UpdateControls()
    {
        // Disabling the whole WPF workspace invokes Windows' system disabled ListView palette,
        // which flashes pale/white while the VPN is starting or stopping. Block input only.
        Workspace.IsEnabled = !_closing;
        Workspace.IsHitTestVisible = !_busy && !_closing;
        var ready = _document is { HasVpnConnection: true } && File.Exists(_settings.ConfigPath) && File.Exists(_settings.CorePath);
        PowerButton.IsEnabled = !_busy && !_closing && (ready || _core.HasTrackedProcess);
        AddRuleButton.IsEnabled = _document is not null && !_busy && !_closing;
        InstallUpdateButton.IsEnabled = !_busy && !_closing;
        RefreshUpdateOffer();
        ScheduleAutoInstall();
        EmptyRulesMessage.Opacity = 1;
        CheckConnectionButton.IsEnabled = _core.IsRunning && !_busy && !_closing && !_checkingConnection;
        RefreshConnectionsButton.IsEnabled = _core.IsRunning && !_closing;
        PowerButton.Content = _core.HasTrackedProcess ? "ОСТАНОВИТЬ VPN"
            : _recoveryLifetime is not null ? "ОТМЕНИТЬ ВОССТАНОВЛЕНИЕ" : "ЗАПУСТИТЬ VPN";
        RestartButton.IsEnabled = _core.IsRunning;
        ApplyButton.IsEnabled = _dirty && _document is not null;
        RefreshRuleHistoryControls();
        RefreshConnectionCard();
        RefreshTrayState();
        RefreshTunnelControls();
    }

    private async Task LoadConfigAsync(string path)
    {
        if (_dirty) await SaveDraftAsync();
        CancelConnectionIntent();
        var loaded = await Task.Run(() => ClashConfigDocument.Load(path)).WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token);
        var errors = loaded.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!await StopCoreAsync()) throw new IOException("Смена профиля отменена: остановка ядра не подтверждена.");

        _loading = true;
        try
        {
            _document = loaded;
            _settings.ConfigPath = Path.GetFullPath(path);
            var draft = _settings.GetDraft(path);
            var restoreDraft = draft is not null && (string.IsNullOrEmpty(draft.SourceHash) || draft.SourceHash == loaded.SourceHash);
            var state = _settings.GetRuleState(path);
            _folderOrder.Clear();
            _folderOrder.AddRange(_settings.GetFolderOrder(path));
            var rules = restoreDraft ? draft!.Rules
                : state is not null && state.SourceHash == loaded.SourceHash ? MergeRuleUiState(loaded.Rules, state.Rules)
                : loaded.Rules;
            ReplaceRules(rules);
            var preferredAction = _settings.PreferredRoute is "DIRECT" or "REJECT" ? _settings.PreferredRoute : "VPN";
            RouteCombo.SelectedItem = RouteCombo.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, preferredAction));
            UpdateRouteHint(preferredAction);
            _draftSaved = restoreDraft;
            SetDirty(restoreDraft || loaded.HadMarkdownFence);
            ResetRuleHistory();
            if (state is null || state.SourceHash == loaded.SourceHash)
                _settings.PutRuleState(path, loaded.SourceHash, _rules);
            await SaveSettingsAsync();
            SetStatus(loaded.HasVpnConnection ? "VPN ВЫКЛЮЧЕН" : "НУЖЕН ПРОФИЛЬ",
                loaded.HasVpnConnection ? "Профиль загружен" : "Импорт профиля или YAML в настройках", "#667A74");
            SetLog(restoreDraft ? "Восстановлен черновик этого профиля."
                : draft is not null ? "YAML изменён извне. Старый черновик сохранён, но не загружен."
                : $"Профиль загружен: {_rules.Count} редактируемых правил; {loaded.PreservedRuleCount} прочих сохранены в исходном порядке.");
        }
        finally { _loading = false; }
    }

    private void ReplaceRules(IEnumerable<DomainRule> rules)
    {
        var snapshot = rules.Select(rule => rule.Copy()).ToArray();
        _rules.Clear();
        foreach (var rule in snapshot) _rules.Add(rule);
        EnsureFolderOrder(snapshot);
        RefreshCount();
    }

    private static string RuleIdentity(DomainRule rule) => string.Join("\u001f", rule.Kind.ToUpperInvariant(),
        rule.Value.ToLowerInvariant(), rule.Route.ToUpperInvariant(), rule.Extra);

    private static List<DomainRule> MergeRuleUiState(IEnumerable<DomainRule> yamlRules, IEnumerable<DomainRule> savedRules)
    {
        var saved = savedRules.GroupBy(RuleIdentity).ToDictionary(group => group.Key, group => new Queue<DomainRule>(group));
        var merged = new List<DomainRule>();
        foreach (var yamlRule in yamlRules)
        {
            var copy = yamlRule.Copy();
            if (saved.TryGetValue(RuleIdentity(yamlRule), out var queue) && queue.Count > 0)
                copy.Folder = queue.Dequeue().Folder;
            merged.Add(copy);
        }
        foreach (var queue in saved.Values)
            foreach (var prior in queue.Where(rule => !rule.Enabled)) merged.Add(prior.Copy());
        return merged;
    }

    private async Task ApplyChangesAsync(bool restart = true)
    {
        if (_document is null) throw new InvalidOperationException("Сначала выбери YAML-конфигурацию.");
        if (!File.Exists(_settings.CorePath)) throw new FileNotFoundException("Выбери ядро для проверки конфигурации.");
        await SaveDraftAsync();
        var document = _document;
        var snapshot = _rules.Select(rule => rule.Copy()).ToArray();
        var candidate = await Task.Run(() => document.BuildText(snapshot));
        var temp = Path.Combine(Path.GetDirectoryName(document.Path)!, ".morphocyte-check-" + Guid.NewGuid().ToString("N") + ".yaml");
        var wasRunning = _core.IsRunning;
        string? backup = null;
        var committed = false;
        try
        {
            await File.WriteAllTextAsync(temp, candidate, new UTF8Encoding(false), _lifetime.Token);
            var check = await CoreProcessManager.ValidateAsync(_settings.CorePath, temp, _lifetime.Token);
            if (!check.Success) throw new InvalidDataException("Конфиг не применён. Проверка ядра: " + check.Message);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!await StopCoreAsync()) throw new IOException("Конфиг не изменён: остановка ядра не подтверждена.");
            _lifetime.Token.ThrowIfCancellationRequested();
            backup = await Task.Run(() => document.CommitText(candidate));
            committed = true;
            _document = await Task.Run(() => ClashConfigDocument.Load(document.Path));
            var merged = MergeRuleUiState(_document.Rules, snapshot);
            CaptureFolderExpansionStates();
            ReplaceRules(merged);
            _settings.PutRuleState(document.Path, _document.SourceHash, merged);
            SetDirty(false);
            _settings.RemoveDraft(document.Path);
            await SaveSettingsAsync();
            SetLog("Правила проверены и сохранены. Резервная копия: " + Path.GetFileName(backup));
            if (wasRunning && restart && !_closing) await StartCoreAsync();
            ResetRuleHistory();
        }
        catch
        {
            // If the candidate cannot start, restore the prior config only after confirming stop.
            if (committed && backup is not null && !_closing && await StopCoreAsync())
            {
                await Task.Run(() =>
                {
                    if (File.ReadAllText(document.Path) != candidate) throw new IOException("YAML изменён извне; автоматический откат остановлен.");
                    // The rejected rules remain in the draft; do not accumulate extra failed YAML copies.
                    AtomicFile.Write(document.Path, File.ReadAllText(backup));
                });
                _document = await Task.Run(() => ClashConfigDocument.Load(document.Path));
                ReplaceRules(snapshot);
                SetDirty(true);
                await SaveDraftAsync();
                SetLog("Восстановлен предыдущий YAML; изменения остались в черновике.");
            }
            if (wasRunning && !_closing && !_core.IsRunning)
            {
                try { await StartCoreAsync(applyDraft: false); }
                catch (Exception recoveryError) { SetLog("Не удалось восстановить запуск: " + recoveryError.Message); }
            }
            throw;
        }
        finally { if (File.Exists(temp)) await Task.Run(() => File.Delete(temp)); }
    }

    private async Task StartCoreAsync(bool applyDraft = true)
    {
        var startToken = _recoveringConnection && _recoveryLifetime is not null ? _recoveryLifetime.Token : _lifetime.Token;
        if (_closing) return;
        if (_core.IsRunning) return;
        if (!File.Exists(_settings.CorePath)) throw new FileNotFoundException("Выбери ядро в настройках.");
        if (!File.Exists(_settings.ConfigPath)) throw new FileNotFoundException("Выбери YAML-конфигурацию.");
        if (_dirty && applyDraft) await ApplyChangesAsync(restart: false);
        startToken.ThrowIfCancellationRequested();
        var actual = await Task.Run(() => ClashConfigDocument.Load(_settings.ConfigPath));
        if (_document?.SourceHash != actual.SourceHash)
            await LoadConfigAsync(_settings.ConfigPath);
        var routingErrors = _settings.FullTunnel ? actual.Validate() : actual.ValidateForRouting();
        if (routingErrors.Count > 0) throw new InvalidDataException(string.Join("\n", routingErrors));

        SetStatus("ПРОВЕРКА…", "Проверяю конфигурацию", "#FFD166");
        var check = await CoreProcessManager.ValidateAsync(_settings.CorePath, _settings.ConfigPath, startToken);
        if (!check.Success)
        {
            SetStatus("ОШИБКА YAML", "Ядро не запущено", "#FF6B8A");
            throw new InvalidDataException(check.Message);
        }
        startToken.ThrowIfCancellationRequested();
        if (!await ConfirmAndStopOtherMihomoAsync()) return;
        try
        {
            _ignoreRecoveryNetworkUntil = DateTime.UtcNow.AddSeconds(15);
            _diagnostics = new CoreDiagnostics();
            _speedTestPort = FreeLoopbackPort(_diagnostics.Port);
            var runtimeDirectory = Path.Combine(_settings.StorageDirectory, ".runtime");
            Directory.CreateDirectory(runtimeDirectory);
            _runtimeConfigPath = Path.Combine(runtimeDirectory, "active-" + Guid.NewGuid().ToString("N") + (CoreBackend.IsSingBox(_settings.CorePath) ? ".json" : ".yaml"));
            var selectedServer = _settings.ProfileSubscriptions.GetValueOrDefault(_settings.ConfigPath)?.SelectedServer;
            await File.WriteAllTextAsync(_runtimeConfigPath, CoreBackend.RuntimeText(_settings.CorePath, actual, _diagnostics, _settings.FullTunnel, _speedTestPort, selectedServer), new UTF8Encoding(false), startToken);
            var runtimeCheck = await CoreProcessManager.ValidateAsync(_settings.CorePath, _runtimeConfigPath, startToken);
            if (!runtimeCheck.Success) throw new InvalidDataException(runtimeCheck.Message);
            await _core.StartAsync(CoreProcessManager.CreateStartInfo(_settings.CorePath, _runtimeConfigPath,
                Path.GetDirectoryName(_settings.ConfigPath)), startToken);
            if (CoreBackend.IsSingBox(_settings.CorePath))
            {
                // A live process alone does not mean its TUN / API is ready.
                using var ready = CancellationTokenSource.CreateLinkedTokenSource(startToken);
                ready.CancelAfter(TimeSpan.FromSeconds(8));
                while (true)
                {
                    ready.Token.ThrowIfCancellationRequested();
                    try { await _diagnostics.ReadVersionAsync(ready.Token); break; }
                    catch (System.Net.Http.HttpRequestException) { await Task.Delay(150, ready.Token); }
                }
            }
            if (_settings.FullTunnel && actual.VpnRoute is { } route)
            {
                var selected = await _diagnostics.ReadSelectionAsync(route, startToken);
                if (selected is "DIRECT" or "REJECT")
                {
                    await StopCoreAsync();
                    throw new IOException("TUN не включён: в группе выбран прямой маршрут или блокировка. Выбери VPN-подключение в профиле.");
                }
            }
            _connectionRequested = true;
            _ignoreRecoveryNetworkUntil = DateTime.UtcNow.AddSeconds(15);
            _startedAt = DateTime.Now;
            _uptimeTimer.Start();
            SetStatus("ЯДРО РАБОТАЕТ", _settings.FullTunnel ? "TUN · весь трафик через VPN" : "Правила VPN загружены", "#73FFBF");
            LastIssueText.Text = "";
            ConnectionStateText.Text = "VPN ещё не проверен";
            StartTrafficMonitor();
            _ = RefreshConnectionSnapshotAsync();
        }
        catch
        {
            if ((_settings.FullTunnel || CoreBackend.IsSingBox(_settings.CorePath)) && _core.HasTrackedProcess) await StopCoreAsync();
            if (!_core.HasTrackedProcess) ResetDiagnostics();
            SetStatus("ОШИБКА ЗАПУСКА", _core.IsRunning ? "Ядро требует остановки" : "Ядро не запущено", "#FF6B8A");
            throw;
        }
    }

    private async Task<bool> ConfirmAndStopOtherMihomoAsync()
    {
        // Different TUN engines must not compete for the machine's default route.
        var otherEngine = Path.Combine(AppContext.BaseDirectory, CoreBackend.IsSingBox(_settings.CorePath) ? "mihomo.exe" : "sing-box.exe");
        var recognizedEngine = CoreBackend.IsSingBox(_settings.CorePath) || Path.GetFileNameWithoutExtension(_settings.CorePath).Equals("mihomo", StringComparison.OrdinalIgnoreCase);
        if (recognizedEngine && CoreProcessManager.FindOtherInstances(otherEngine).Count > 0)
        {
            UtilityDialogs.ShowNotice(this, "ДРУГОЕ VPN-ЯДРО РАБОТАЕТ", "Сначала останови VPN в другом приложении или экземпляре MorphocyteOS. Mihomo и sing-box не должны одновременно управлять TUN. Чужие процессы не остановлены.");
            return false;
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var instances = CoreProcessManager.FindOtherInstances(_settings.CorePath);
            if (instances.Count == 0) return true;
            if (_recoveringConnection) { SetLog("Восстановление остановлено: найдено другое ядро."); return false; }
            if (_startupMode) RestoreFromTray();

            var unverified = instances.Where(instance => string.IsNullOrWhiteSpace(instance.ExecutablePath)).ToArray();
            if (unverified.Length > 0)
            {
                var ids = string.Join(", ", unverified.Select(instance => instance.ProcessId));
                SetStatus("ПРОВЕРКА ЯДРА", "Найден процесс, чей путь нельзя проверить", "#FFD166");
                SetLog($"Ядро не запущено: путь процесса Mihomo PID {ids} недоступен; он не был остановлен.");
                UtilityDialogs.ShowNotice(this, "НЕ УДАЛОСЬ ПРОВЕРИТЬ ЯДРО",
                    $"Найден процесс с именем «{Path.GetFileName(_settings.CorePath)}», но Windows не разрешила проверить его путь (PID: {ids}).\n\nЧтобы не завершить посторонний процесс и не оставить TUN в неопределённом состоянии, новое ядро не запущено. Закрой этот процесс вручную или запусти программу с правами, позволяющими проверить его.");
                SetStatus("VPN ВЫКЛЮЧЕН", "Ядро не запускалось", "#667A74");
                return false;
            }

            var processList = string.Join(Environment.NewLine, instances.Select(instance =>
                $"PID {instance.ProcessId} — {instance.ExecutablePath}"));
            var accepted = UtilityDialogs.ShowConfirm(this, "УЖЕ ЗАПУЩЕНО VPN-ЯДРО",
                $"Перед запуском найдены другие процессы «{Path.GetFileName(_settings.CorePath)}» — в том числе копии из других папок:\n\n{processList}\n\nОни могут удерживать порты или TUN-интерфейс и оставлять старые правила. Остановить только перечисленные процессы и продолжить? Соединение на короткое время прервётся.",
                "ОСТАНОВИТЬ И ПРОДОЛЖИТЬ", "ОТМЕНА");
            if (!accepted)
            {
                SetStatus("VPN ВЫКЛЮЧЕН", "Запуск отменён; найденное ядро не тронуто", "#667A74");
                SetLog("Запуск отменён: найден другой процесс Mihomo.");
                return false;
            }

            await CoreProcessManager.StopOtherInstancesAsync(_settings.CorePath, instances, _lifetime.Token);
            SetLog("Остановка найденных копий Mihomo подтверждена; проверяю, что они завершились.");
        }

        var remaining = CoreProcessManager.FindOtherInstances(_settings.CorePath);
        if (remaining.Count == 0) return true;
        var remainingIds = string.Join(", ", remaining.Select(instance => instance.ProcessId));
        throw new IOException($"Дублирующие процессы Mihomo снова появились или не завершились (PID: {remainingIds}). Текущее ядро не запущено, чтобы не оставить конфликтующий TUN.");
    }

    private async Task<bool> StopCoreAsync()
    {
        if (!_core.HasTrackedProcess) return true;
        SetStatus("ОСТАНОВКА…", "Ожидаю завершения ядра", "#FFD166");
        var stopped = await _core.StopAsync();
        if (stopped)
        {
            if (_selectedConnection.Length > 0 && _settings.ProfileSubscriptions.TryGetValue(_settings.ConfigPath, out var source))
            {
                source.SelectedServer = _selectedConnection;
                try { await SaveSettingsAsync(); }
                catch { SetLog("Не удалось сохранить выбор сервера. Остановка VPN выполнена."); }
            }
            ResetDiagnostics();
            _uptimeTimer.Stop();
            SetStatus("VPN ВЫКЛЮЧЕН", "Процесс ядра завершён", "#667A74");
            SetLog("Завершение ядра подтверждено.");
        }
        else SetStatus("СБОЙ ОСТАНОВКИ", "Ядро отслеживается; повтори остановку", "#FF6B8A");
        return stopped;
    }

    private void SetStatus(string title, string subtitle, string color)
    {
        StatusText.Text = title;
        UptimeText.Text = subtitle;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
        UpdateControls();
    }
    private void UpdateUptime()
    {
        if (!_core.IsRunning || _busy || _closing || _dialogBackdropVisible) return;
        UptimeText.Text = $"Ядро работает {(DateTime.Now - _startedAt):hh\\:mm\\:ss}";
    }
    private void Post(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_allowClose) action(); }));
    }
    private void SetLog(string message)
    {
        var clean = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var entry = FormatLogEntry(clean);
        AppendLogEntries(new[] { entry });
        _ = PersistLogAsync(_logPath, entry);
    }

    private static string FormatLogEntry(string message) => $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message.Replace('\r', ' ').Replace('\n', ' ').Trim()}";

    private void FlushCoreOutput()
    {
        var batch = new List<string>();
        while (batch.Count < MaxCoreLinesPerRefresh && _pendingCoreOutput.TryDequeue(out var message))
            batch.Add(FormatLogEntry(message));
        if (batch.Count == 0) return;

        AppendLogEntries(batch);
        _ = PersistLogBatchAsync(_logPath, batch);
    }

    private void AppendLogEntries(IReadOnlyCollection<string> entries)
    {
        if (entries.Count == 0) return;
        var oldVerticalOffset = _logScrollViewer?.VerticalOffset ?? 0;
        var wasNearEnd = _logScrollViewer is null
            || _logScrollViewer.ScrollableHeight - _logScrollViewer.VerticalOffset < 28;
        var droppedOldLines = false;
        foreach (var entry in entries)
        {
            _logLines.Enqueue(entry);
            while (_logLines.Count > MaxDisplayedLogLines)
            {
                _logLines.Dequeue();
                droppedOldLines = true;
            }
        }

        // Keep collecting and persisting every entry while a dialog is visible,
        // but do not invalidate the blurred background bitmap with log text layout.
        if (_dialogBackdropVisible)
        {
            _logDisplayDeferred = true;
            return;
        }
        if (!JournalPage.IsVisible) return;
        if (droppedOldLines || string.IsNullOrEmpty(LogText.Text))
            LogText.Text = string.Join(Environment.NewLine, _logLines);
        else
            LogText.AppendText(Environment.NewLine + string.Join(Environment.NewLine, entries));

        if (wasNearEnd) LogText.ScrollToEnd();
        else if (_logScrollViewer is not null) _logScrollViewer.ScrollToVerticalOffset(oldVerticalOffset);
    }

    private void RefreshDeferredLog()
    {
        if (!_logDisplayDeferred) return;
        _logDisplayDeferred = false;
        if (!JournalPage.IsVisible || _closing) return;
        var oldOffset = _logScrollViewer?.VerticalOffset ?? 0;
        var wasNearEnd = _logScrollViewer is null
            || _logScrollViewer.ScrollableHeight - _logScrollViewer.VerticalOffset < 28;
        LogText.Text = string.Join(Environment.NewLine, _logLines);
        if (wasNearEnd) LogText.ScrollToEnd();
        else _logScrollViewer?.ScrollToVerticalOffset(oldOffset);
    }

    private static async Task PersistLogAsync(string logPath, string entry)
        => await PersistLogBatchAsync(logPath, new[] { entry });

    private static async Task PersistLogBatchAsync(string logPath, IReadOnlyCollection<string> entries)
    {
        await LogWriteGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 2_000_000)
                File.Move(logPath, logPath + ".previous", true);
            await File.AppendAllTextAsync(logPath, string.Join(Environment.NewLine, entries) + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
        finally { LogWriteGate.Release(); }
    }

    private void SetDirty(bool value)
    {
        _dirty = value;
        UpdateControls();
    }
    private Task SaveSettingsAsync() => Task.Run(() => _settings.Save());
    private async Task SaveDraftAsync()
    {
        if (_document is null) return;
        _settings.PutDraft(_document.Path, _document.SourceHash, _rules);
        _settings.PutFolderOrder(_document.Path, _folderOrder);
        _draftSaved = false;
        try { await SaveSettingsAsync(); _draftSaved = true; }
        finally { SetDirty(_dirty); }
    }
    private Task RecordEditAsync() => RecordEditCoreAsync(refreshView: true);
    private Task RecordEditWithoutViewRefreshAsync() => RecordEditCoreAsync(refreshView: false);
    private async Task RecordEditCoreAsync(bool refreshView)
    {
        _draftSaved = false;
        SetDirty(true);
        EnsureFolderOrder(_rules);
        TrackRuleEdit(_dirty);
        RefreshCount(refreshView);
        await SaveDraftAsync();
    }
    private async Task ReorderFolderGroupAsync(string source, string target, bool after)
    {
        if (_document is null || string.Equals(source, target, StringComparison.Ordinal)) return;
        EnsureFolderOrder(_rules);
        TrackRuleEdit(_dirty);
        var sourceIndex = _folderOrder.FindIndex(name => string.Equals(name, source, StringComparison.OrdinalIgnoreCase));
        var targetIndex = _folderOrder.FindIndex(name => string.Equals(name, target, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0 || targetIndex < 0) return;
        _folderOrder.RemoveAt(sourceIndex);
        targetIndex = _folderOrder.FindIndex(name => string.Equals(name, target, StringComparison.OrdinalIgnoreCase));
        _folderOrder.Insert(targetIndex + (after ? 1 : 0), source);
        RefreshRulesView();
        TrackRuleEdit(_dirty);
        _settings.PutFolderOrder(_document.Path, _folderOrder);
        await SaveSettingsAsync();
        SetLog($"Папка «{source}» перемещена {(after ? "после" : "перед")} «{target}».");
    }
    private void EnsureFolderOrder(IEnumerable<DomainRule> rules)
    {
        var active = rules.Select(rule => rule.FolderLabel).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_folderOrder.Count == 0)
        {
            _folderOrder.AddRange(active.Where(name => name != "ОБЩИЕ ПРАВИЛА").OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            if (active.Contains("ОБЩИЕ ПРАВИЛА", StringComparer.OrdinalIgnoreCase)) _folderOrder.Add("ОБЩИЕ ПРАВИЛА");
            return;
        }
        _folderOrder.RemoveAll(name => !active.Contains(name, StringComparer.OrdinalIgnoreCase));
        foreach (var name in active.Where(name => !_folderOrder.Contains(name, StringComparer.OrdinalIgnoreCase)
                     && name != "ОБЩИЕ ПРАВИЛА").OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var generalIndex = _folderOrder.FindIndex(value => value == "ОБЩИЕ ПРАВИЛА");
            _folderOrder.Insert(generalIndex < 0 ? _folderOrder.Count : generalIndex, name);
        }
        if (active.Contains("ОБЩИЕ ПРАВИЛА", StringComparer.OrdinalIgnoreCase)
            && !_folderOrder.Contains("ОБЩИЕ ПРАВИЛА", StringComparer.OrdinalIgnoreCase))
            _folderOrder.Add("ОБЩИЕ ПРАВИЛА");
    }
    private void RefreshCount(bool refreshView = true)
    {
        if (refreshView) RefreshRulesView();
    }
    private void RefreshRulesView()
    {
        _rulesView.Refresh();
    }

    private string FolderExpansionKey(string groupName) => $"{_document?.Path ?? ""}\u001f{groupName}";

    private void FolderExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || FindAncestor<GroupItem>(expander)?.Content is not CollectionViewGroup group) return;
        expander.Expanded -= FolderExpander_Expanded;
        expander.Collapsed -= FolderExpander_Collapsed;
        var key = FolderExpansionKey(group.Name?.ToString() ?? "");
        if (_folderExpansionStates.TryGetValue(key, out var expanded)) expander.IsExpanded = expanded;
        else _folderExpansionStates[key] = expander.IsExpanded;
        expander.Expanded += FolderExpander_Expanded;
        expander.Collapsed += FolderExpander_Collapsed;
    }

    private void FolderExpander_Expanded(object sender, RoutedEventArgs e) => SaveFolderExpansion(sender, true);
    private void FolderExpander_Collapsed(object sender, RoutedEventArgs e) => SaveFolderExpansion(sender, false);

    private void SaveFolderExpansion(object sender, bool expanded)
    {
        if (sender is Expander expander && FindAncestor<GroupItem>(expander)?.Content is CollectionViewGroup group)
            _folderExpansionStates[FolderExpansionKey(group.Name?.ToString() ?? "")] = expanded;
    }

    private void CaptureFolderExpansionStates()
    {
        foreach (var expander in FindVisualDescendants<Expander>(RulesList))
            SaveFolderExpansion(expander, expander.IsExpanded);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualDescendants<T>(child)) yield return nested;
        }
    }
    private bool MatchesSearch(DomainRule rule)
    {
        var query = SearchBox?.Text?.Trim();
        return string.IsNullOrWhiteSpace(query) || rule.Value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    private bool FilterUserRule(object item) => item is DomainRule rule && MatchesSearch(rule);

    private async void AddRule_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (_document is null) throw new InvalidOperationException("Сначала выбери профиль.");
        var route = ResolveNewRuleRoute();
        var kind = (RuleKindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "DOMAIN-SUFFIX";
        var value = ClashConfigDocument.NormalizeDomain(DomainInput.Text, kind);
        if (_rules.Any(rule => rule.Kind == kind && rule.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Такое правило уже есть в списке. Его маршрут показан справа.");
        _rules.Add(new DomainRule { Kind = kind, Value = value, Route = route });
        await RecordEditAsync();
        DomainInput.Clear();
        SetLog("Правило добавлено в черновик.");
    });
    private async void DeleteRule_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (sender is not Button { Tag: DomainRule rule }) return;
        _rules.Remove(rule);
        await RecordEditAsync();
        SetLog("Правило удалено из черновика. Другие совпадающие правила продолжают действовать.");
    });
    private async void RuleEnabled_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (sender is not CheckBox { DataContext: DomainRule rule }) return;
        var selected = RulesList.SelectedItems.OfType<DomainRule>().ToArray();
        var affected = selected.Contains(rule) ? selected : new[] { rule };
        var enabled = !affected.Any(member => member.Enabled);
        foreach (var member in affected) member.Enabled = enabled;
        await RecordEditWithoutViewRefreshAsync();
        RestoreRuleSelection(selected);
        UpdateVisibleFolderCheckBoxes(RulesList);
        SetLog(affected.Length > 1 ? $"{(enabled ? "Включено" : "Выключено")} правил: {affected.Length}."
            : enabled ? $"Правило включено: {rule.Value}" : $"Правило выключено: {rule.Value}");
    });

    private void RuleCheck_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep the selection while clicking its checkbox: the normal ListView mouse handler
        // would collapse the selection before the checkbox's Click event is raised.
        e.Handled = true;
        RuleEnabled_Click(sender, new RoutedEventArgs());
    }

    private void FolderEnabled_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.DataContext is CollectionViewGroup group)
            UpdateFolderCheckBox(checkBox, group);
    }

    private DomainRule[] FolderMembers(CollectionViewGroup group) => _rules.Where(rule =>
        rule.FolderLabel.Equals(group.Name?.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();

    private void UpdateFolderCheckBox(CheckBox checkBox, CollectionViewGroup group)
    {
        var rules = FolderMembers(group);
        checkBox.IsChecked = rules.Length > 0 && rules.All(rule => rule.Enabled) ? true
            : rules.All(rule => !rule.Enabled) ? false : null;
    }

    private void UpdateVisibleFolderCheckBoxes(DependencyObject parent)
    {
        if (parent is CheckBox checkBox && checkBox.DataContext is CollectionViewGroup group)
            UpdateFolderCheckBox(checkBox, group);
        else if (parent is CheckBox ruleCheckBox && ruleCheckBox.DataContext is DomainRule rule)
            ruleCheckBox.IsChecked = rule.Enabled;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            UpdateVisibleFolderCheckBoxes(VisualTreeHelper.GetChild(parent, index));
    }

    private async void FolderEnabled_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not CollectionViewGroup group) return;
        var rules = FolderMembers(group);
        if (rules.Length == 0) return;
        var enable = rules.Any(rule => !rule.Enabled);
        foreach (var rule in rules) rule.Enabled = enable;
        UpdateVisibleFolderCheckBoxes(RulesList);
        await RecordEditAsync();
        SetLog(enable ? $"Включены все правила группы «{group.Name}»." : $"Выключены все правила группы «{group.Name}».");
    });

    private void FolderToggle_Click(object sender, RoutedEventArgs e)
    {
        var expander = FindAncestor<Expander>(e.OriginalSource as DependencyObject);
        if (expander is not null) expander.IsExpanded = !expander.IsExpanded;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void LogText_Loaded(object sender, RoutedEventArgs e)
    {
        _logScrollViewer ??= FindVisualDescendant<ScrollViewer>(LogText);
        LogText.ScrollToEnd();
    }
    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (sender is not Button { Tag: CollectionViewGroup group }) return;
        var folderName = group.Name?.ToString();
        if (string.IsNullOrWhiteSpace(folderName) || folderName == "ОБЩИЕ ПРАВИЛА") return;
        var members = FolderMembers(group);
        foreach (var rule in members) rule.Folder = "";
        RefreshRulesView();
        await RecordEditAsync();
        SetLog($"Папка «{folderName}» удалена; {members.Length} правил(а) оставлено в общих правилах.");
    });
    private async void UndoDelete_Click(object sender, RoutedEventArgs e) => await UndoRuleEditAsync();
    private async void RouteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RouteCombo.SelectedItem is not ComboBoxItem { Tag: string route }) return;
        UpdateRouteHint(route);
        if (_loading || _settings is null) return;
        await RunExclusiveAsync(async () =>
        {
            _settings.PreferredRoute = route;
            await SaveSettingsAsync();
            SetLog("Маршрут выбран для новых правил. Назначения существующих правил сохранены.");
        });
    }
    private void UpdateRouteHint(string? route) => RouteHintText.Text = route?.ToUpperInvariant() switch
    {
        "DIRECT" => "Новое правило пойдёт напрямую, в обход VPN",
        "REJECT" => "Новое правило заблокирует совпавший трафик",
        { Length: > 0 } => "Новое правило пойдёт через выбранный VPN-маршрут",
        _ => "Выбери действие для добавляемых правил"
    };
    private void RuleKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DomainInputHint is null || DomainInput is null) return;
        var kind = (RuleKindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        DomainInputHint.Text = kind switch
        {
            "PROCESS-NAME" => "Имя процесса, например application.exe",
            "DOMAIN-KEYWORD" => "Часть домена, например google",
            "DOMAIN" => "Точный домен, например api.example.com",
            _ => "Сайт или домен, например example.com"
        };
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _rulesView?.Refresh();
    }
    private void DomainInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; AddRule_Click(sender, e); } }
    private async void ApplyButton_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(() => ApplyChangesAsync());
    private async void PowerButton_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (_core.HasTrackedProcess || _recoveryLifetime is not null) { CancelConnectionIntent(); await StopCoreAsync(); } else await StartCoreAsync();
    });
    private async void RestartButton_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (await StopCoreAsync()) await StartCoreAsync();
    });
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text); SetLog("Журнал скопирован в буфер обмена."); }
        catch (Exception ex) { SetLog("Не удалось скопировать журнал: " + ex.Message); }
    }
    private void FaqButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && !_closing) UtilityDialogs.ShowFaq(this);
    }
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        UtilityDialogs.ShowSettings(this, _settings.ConfigPath, _settings.CorePath, _settings.Theme,
            _core.HasTrackedProcess, _settings.InterfaceScale, _settings.AutoCheckUpdates, SaveSettingsSelectionAsync);
    }

    private async Task<SettingsDialogSelection?> SaveSettingsSelectionAsync(SettingsDialogSelection choice)
    {
        SettingsDialogSelection? applied = null;
        await RunExclusiveAsync(async () =>
        {
            string? transferredPath = null;
            if (choice.Transfer is { } transfer)
            {
                if (_core.HasTrackedProcess && !string.Equals(choice.CorePath, _settings.CorePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Перед сменой ядра останови VPN.");
                transferredPath = await ConfigurationTransfer.CreateProfileAsync(transfer, choice.CorePath, _settings.StorageDirectory, _lifetime.Token);
            }
            _settings.Theme = ThemeManager.Normalize(choice.Theme);
            _settings.InterfaceScale = AppSettings.NormalizeScale(choice.InterfaceScale);
            _settings.AutoCheckUpdates = choice.AutoCheckUpdates;
            if (choice.LaunchAtSignIn != _settings.LaunchAtSignIn)
                StartupRegistration.SetEnabled(choice.LaunchAtSignIn);
            _settings.LaunchAtSignIn = choice.LaunchAtSignIn;
            _settings.AutoConnectOnStartup = choice.AutoConnectOnStartup;
            _settings.AutoReconnect = choice.AutoReconnect;
            if (!_settings.AutoReconnect) _recoveryLifetime?.Cancel();
            ThemeManager.Apply(_settings.Theme);
            ApplyInterfaceScale(_settings.InterfaceScale);
            await SaveSettingsAsync();
            if (!string.Equals(choice.CorePath, _settings.CorePath, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(choice.CorePath))
            {
                if (_core.HasTrackedProcess) throw new InvalidOperationException("Перед сменой ядра останови VPN.");
                if (!File.Exists(choice.CorePath)) throw new FileNotFoundException("Выбранный файл ядра не найден.");
                if (CoreBackend.IsSingBox(choice.CorePath) && choice.Imported is null && choice.Transfer is null && File.Exists(choice.ConfigPath))
                {
                    var compatibility = await CoreProcessManager.ValidateAsync(choice.CorePath, choice.ConfigPath, _lifetime.Token);
                    if (!compatibility.Success) throw new InvalidDataException(compatibility.Message);
                }
                _settings.CorePath = Path.GetFullPath(choice.CorePath);
                await SaveSettingsAsync();
                SetLog("Выбрано ядро: " + CoreBackend.Name(_settings.CorePath) + ".");
            }
            if (choice.Transfer is { } package) await ImportConfigurationAsync(package, transferredPath!);
            else if (choice.Imported is { } imported) await ImportProfileAsync(imported);
            else if (!string.Equals(choice.ConfigPath, _settings.ConfigPath, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(choice.ConfigPath))
                await LoadConfigAsync(choice.ConfigPath);
            SetLog("Настройки сохранены.");
            applied = new SettingsDialogSelection(_settings.ConfigPath, _settings.CorePath, _settings.Theme,
                _settings.InterfaceScale, _settings.AutoCheckUpdates, LaunchAtSignIn: _settings.LaunchAtSignIn,
                AutoConnectOnStartup: _settings.AutoConnectOnStartup, AutoReconnect: _settings.AutoReconnect);
        });
        return applied;
    }

    private async Task ImportProfileAsync(ImportedProfile imported)
    {
        if (!File.Exists(_settings.CorePath)) _settings.CorePath = await BundledResources.EnsureCoreAsync(_lifetime.Token);
        var path = await ProfileStorage.CreateAsync(imported, _settings.CorePath, _settings.StorageDirectory, _lifetime.Token);
        _settings.PreferredRoute = "VPN";
        if (imported.SubscriptionUrl is { } url) _settings.ProfileSubscriptions[path] = SubscriptionSource.Create(url, DateTimeOffset.UtcNow);
        await LoadConfigAsync(path);
        SetLog("Создан новый локальный YAML. Остальные профили сохранены. Добавь сайты или приложения в правила VPN.");
    }

    private async void ChooseProcess_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        var dialog = new OpenFileDialog { Filter = "Приложения Windows (*.exe)|*.exe", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await RunExclusiveAsync(() => AddApplicationsAsync(dialog.FileNames.Select(Path.GetFileName).OfType<string>()));
    }
    private async void ChooseRunningProcess_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        var processNames = await RunningProcessDialog.ShowAsync(this);
        if (processNames.Count == 0) return;
        await RunExclusiveAsync(() => AddApplicationsAsync(processNames));
    }
    private async Task AddApplicationsAsync(IEnumerable<string> processNames)
    {
        if (_document is null)
            throw new InvalidOperationException("Сначала выбери профиль и маршрут.");
        var names = processNames.Select(name => ClashConfigDocument.NormalizeDomain(name, "PROCESS-NAME"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var route = ResolveNewRuleRoute();
        var added = names.Where(name => !_rules.Any(rule => rule.Kind == "PROCESS-NAME" &&
            rule.Value.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(name => new DomainRule { Kind = "PROCESS-NAME", Value = name, Route = route }).ToArray();
        foreach (var rule in added) _rules.Add(rule);
        if (added.Length == 0) { SetLog("Выбранные приложения уже есть в правилах."); return; }
        await RecordEditAsync();
        RuleKindCombo.SelectedIndex = 3;
        DomainInput.Clear();
        RestoreRuleSelection(added);
        SetLog($"Добавлено приложений: {added.Length}. Для применения нажми «ПРИМЕНИТЬ В YAML».");
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            if (_desktopIntegrationEnabled) TaskbarIconIntegration.ClearWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            return;
        }
        e.Cancel = true;
        if (_tray is not null && !_exitRequested && !_closing) { HideToTray(); return; }
        if (_closing) return;
        _closing = true;
        CancelConnectionIntent();
        _lifetime.Cancel();
        UpdateControls();
        await _operations.WaitAsync(); // Wait for a pending operation; cancellation prevents a late start.
        try
        {
            if (_dirty) await SaveDraftAsync();
            if (!await StopCoreAsync())
                throw new IOException("Окно оставлено открытым: ядро ещё не завершено. Повтори остановку.");
            _uptimeTimer.Stop();
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            _closing = false;
            _exitRequested = false;
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
            SetLog(ex.Message);
            RestoreFromTray();
            UtilityDialogs.ShowNotice(this, "Не удалось закрыть приложение", ex.Message);
        }
        finally { _operations.Release(); UpdateControls(); }
    }
}

internal sealed class FolderOrderComparer : System.Collections.IComparer
{
    private readonly Func<IReadOnlyList<string>> _getOrder;
    public FolderOrderComparer(Func<IReadOnlyList<string>> getOrder) => _getOrder = getOrder;

    public int Compare(object? x, object? y)
    {
        var left = (x as CollectionViewGroup)?.Name?.ToString() ?? x?.ToString() ?? "";
        var right = (y as CollectionViewGroup)?.Name?.ToString() ?? y?.ToString() ?? "";
        var order = _getOrder();
        var leftIndex = IndexOf(order, left);
        var rightIndex = IndexOf(order, right);
        if (leftIndex >= 0 || rightIndex >= 0)
        {
            if (leftIndex < 0) return 1;
            if (rightIndex < 0) return -1;
            return leftIndex.CompareTo(rightIndex);
        }
        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static int IndexOf(IReadOnlyList<string> order, string value)
    {
        for (var index = 0; index < order.Count; index++)
            if (string.Equals(order[index], value, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }
}
