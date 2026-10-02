using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private (DomainRule Rule, int Index)? _lastDeleted;
    private ScrollViewer? _rulesScrollViewer;
    private ScrollViewer? _logScrollViewer;
    private bool _logScrollHooked, _logThumbDragging;
    private double _expandedLogHeight = 140;
    private double _logThumbGrabOffset;
    private DateTime _startedAt;
    private bool _dirty, _loading, _busy, _closing, _allowClose, _draftSaved;
    private int _dialogBackdropVersion;
    private bool _dialogBackdropVisible, _logDisplayDeferred;
    private static readonly SemaphoreSlim LogWriteGate = new(1, 1);
    private static string PersistentLogPath => Path.Combine(AppSettings.LocalDataDirectory, "router.log");

    public MainWindow() : this(null) { }
    internal string StorageDirectory => _settings.StorageDirectory;

    public MainWindow(AppSettings? settings, string? logPath = null)
    {
        _settings = settings ?? AppSettings.Load();
        _settings.Theme = ThemeManager.Normalize(_settings.Theme);
        ThemeManager.Apply(_settings.Theme);
        InitializeComponent();
        ApplyInterfaceScale(_settings.InterfaceScale);
        _rulesView = new ListCollectionView(_rules) { Filter = FilterUserRule };
        var folderGroups = new PropertyGroupDescription(nameof(DomainRule.FolderLabel))
        {
            CustomSort = new FolderOrderComparer(() => _folderOrder)
        };
        _rulesView.GroupDescriptions.Add(folderGroups);
        RulesList.ItemsSource = _rulesView;
        LogVisibilityToggle.IsChecked = _settings.ShowEventLog;
        ApplyLogVisibility();
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
        LogText.Loaded += LogText_Loaded;

        _core.Output += message => _pendingCoreOutput.Enqueue(message);
        _core.Exited += () => Post(() =>
        {
            if (_busy || _closing || _core.IsRunning) return;
            _uptimeTimer.Stop();
            SetStatus("ЯДРО ОСТАНОВЛЕНО", "Смотри журнал событий", "#FFD166");
            UpdateControls();
        });
        _core.StartupFailed += message => Post(() =>
        {
            _uptimeTimer.Stop();
            SetStatus("СБОЙ ЯДРА", "Смотри журнал событий", "#FF6B8A");
            SetLog(message);
            UpdateControls();
        });

        Loaded += async (_, _) =>
        {
            await RunExclusiveAsync(async () =>
            {
                await BundledResources.PrepareDefaultsAsync(_settings, _lifetime.Token);
                if (File.Exists(_settings.ConfigPath)) await LoadConfigAsync(_settings.ConfigPath);
                else SetLog("Выбранный YAML не найден. Выбери профиль в настройках или импортируй подключение.");
            });
            if (_settings.RecoveryMessage is { } message) SetLog(message);
            UpdateControls();
            await ReadUpdateResultAsync();
            _ = CleanCompletedUpdateCachesAsync();
            if (_settings.AutoCheckUpdates) await CheckForUpdatesOnStartupAsync();
        };
        Closing += MainWindow_Closing;
        Closed += (_, _) => _logRefreshTimer.Stop();
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
            SetLog(ex.Message);
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
        DownloadUpdateButton.IsEnabled = !_busy && !_closing;
        EmptyRulesMessage.Opacity = 1;
        LogVisibilityToggle.IsEnabled = !_busy && !_closing;
        PowerButton.Content = _core.HasTrackedProcess ? "ОСТАНОВИТЬ VPN" : "ЗАПУСТИТЬ VPN";
        RestartButton.IsEnabled = _core.IsRunning;
        ApplyButton.IsEnabled = _dirty && _document is not null;
        UndoButton.IsEnabled = _lastDeleted is not null && !_busy && !_closing;
    }

    private async Task LoadConfigAsync(string path)
    {
        if (_dirty) await SaveDraftAsync();
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
            var actionRoutes = loaded.Routes.Concat(new[] { "DIRECT", "REJECT" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            RouteCombo.ItemsSource = actionRoutes;
            var existing = _rules.Select(rule => rule.Route).FirstOrDefault(route => actionRoutes.Contains(route));
            RouteCombo.SelectedItem = actionRoutes.Contains(_settings.PreferredRoute) ? _settings.PreferredRoute : existing ?? loaded.Routes.FirstOrDefault() ?? "DIRECT";
            UpdateRouteHint(RouteCombo.SelectedItem?.ToString());
            EndpointText.Text = loaded.Endpoint;
            _lastDeleted = null;
            _draftSaved = restoreDraft;
            SetDirty(restoreDraft || loaded.HadMarkdownFence);
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
            _lastDeleted = null;
            SetDirty(false);
            _settings.RemoveDraft(document.Path);
            await SaveSettingsAsync();
            SetLog("Правила проверены и сохранены. Резервная копия: " + Path.GetFileName(backup));
            if (wasRunning && restart && !_closing) await StartCoreAsync();
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
        if (_closing) return;
        if (_core.IsRunning) return;
        if (!File.Exists(_settings.CorePath)) throw new FileNotFoundException("Выбери ядро Mihomo в настройках.");
        if (!File.Exists(_settings.ConfigPath)) throw new FileNotFoundException("Выбери YAML-конфигурацию.");
        if (_dirty && applyDraft) await ApplyChangesAsync(restart: false);
        _lifetime.Token.ThrowIfCancellationRequested();
        var actual = await Task.Run(() => ClashConfigDocument.Load(_settings.ConfigPath));
        if (_document?.SourceHash != actual.SourceHash)
            await LoadConfigAsync(_settings.ConfigPath);
        var routingErrors = actual.ValidateForRouting();
        if (routingErrors.Count > 0) throw new InvalidDataException(string.Join("\n", routingErrors));

        SetStatus("ПРОВЕРКА…", "Проверяю конфигурацию", "#FFD166");
        var check = await CoreProcessManager.ValidateAsync(_settings.CorePath, _settings.ConfigPath, _lifetime.Token);
        if (!check.Success)
        {
            SetStatus("ОШИБКА YAML", "Ядро не запущено", "#FF6B8A");
            throw new InvalidDataException(check.Message);
        }
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!await ConfirmAndStopOtherMihomoAsync()) return;
        try
        {
            await _core.StartAsync(CoreProcessManager.CreateStartInfo(_settings.CorePath, _settings.ConfigPath), _lifetime.Token);
            _startedAt = DateTime.Now;
            _uptimeTimer.Start();
            SetStatus("ЯДРО РАБОТАЕТ", "Правила VPN загружены", "#73FFBF");
        }
        catch
        {
            SetStatus("ОШИБКА ЗАПУСКА", _core.IsRunning ? "Ядро требует остановки" : "Ядро не запущено", "#FF6B8A");
            throw;
        }
    }

    private async Task<bool> ConfirmAndStopOtherMihomoAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var instances = CoreProcessManager.FindOtherInstances(_settings.CorePath);
            if (instances.Count == 0) return true;

            var unverified = instances.Where(instance => string.IsNullOrWhiteSpace(instance.ExecutablePath)).ToArray();
            if (unverified.Length > 0)
            {
                var ids = string.Join(", ", unverified.Select(instance => instance.ProcessId));
                SetStatus("ПРОВЕРКА ЯДРА", "Найден процесс, чей путь нельзя проверить", "#FFD166");
                SetLog($"Ядро не запущено: путь процесса Mihomo PID {ids} недоступен; он не был остановлен.");
                UtilityDialogs.ShowNotice(this, "НЕ УДАЛОСЬ ПРОВЕРИТЬ MIHOMO",
                    $"Найден процесс с именем «{Path.GetFileName(_settings.CorePath)}», но Windows не разрешила проверить его путь (PID: {ids}).\n\nЧтобы не завершить посторонний процесс и не оставить TUN в неопределённом состоянии, новое ядро не запущено. Закрой этот процесс вручную или запусти программу с правами, позволяющими проверить его.");
                SetStatus("VPN ВЫКЛЮЧЕН", "Ядро не запускалось", "#667A74");
                return false;
            }

            var processList = string.Join(Environment.NewLine, instances.Select(instance =>
                $"PID {instance.ProcessId} — {instance.ExecutablePath}"));
            var accepted = UtilityDialogs.ShowConfirm(this, "УЖЕ ЗАПУЩЕНО ЯДРО MIHOMO",
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
        if (LogText.Visibility != Visibility.Visible) return;
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
        if (LogText.Visibility != Visibility.Visible || _closing) return;
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
        RefreshCount(refreshView);
        await SaveDraftAsync();
    }
    private async Task ReorderFolderGroupAsync(string source, string target, bool after)
    {
        if (_document is null || string.Equals(source, target, StringComparison.Ordinal)) return;
        EnsureFolderOrder(_rules);
        var sourceIndex = _folderOrder.FindIndex(name => string.Equals(name, source, StringComparison.OrdinalIgnoreCase));
        var targetIndex = _folderOrder.FindIndex(name => string.Equals(name, target, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0 || targetIndex < 0) return;
        _folderOrder.RemoveAt(sourceIndex);
        targetIndex = _folderOrder.FindIndex(name => string.Equals(name, target, StringComparison.OrdinalIgnoreCase));
        _folderOrder.Insert(targetIndex + (after ? 1 : 0), source);
        RefreshRulesView();
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
        CountText.Text = $"{_rules.Count(rule => rule.Enabled)} / {_rules.Count}";
        CountText.ToolTip = "Включённые правила / всего. Ctrl+клик выбирает несколько правил, Shift+клик — диапазон.";
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
        if (_document is null || RouteCombo.SelectedItem is not string route) throw new InvalidOperationException("Сначала выбери профиль и маршрут.");
        var kind = (RuleKindCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "DOMAIN-SUFFIX";
        var value = ClashConfigDocument.NormalizeDomain(DomainInput.Text, kind);
        if (_rules.Any(rule => rule.Kind == kind && rule.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Такое правило уже есть в списке. Его маршрут показан справа.");
        _rules.Add(new DomainRule { Kind = kind, Value = value, Route = route });
        _lastDeleted = null;
        await RecordEditAsync();
        DomainInput.Clear();
        SetLog("Правило добавлено в черновик.");
    });
    private async void DeleteRule_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (sender is not Button { Tag: DomainRule rule }) return;
        var index = _rules.IndexOf(rule);
        _lastDeleted = (rule.Copy(), index);
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
        if (!_logScrollHooked)
        {
            LogText.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LogText_ScrollChanged));
            _logScrollHooked = true;
        }
        LogText.UpdateLayout();
        SyncLogScrollbar();
        LogText.ScrollToEnd();
    }

    private void LogText_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        SyncLogScrollbar();
    }

    private void SyncLogScrollbar()
    {
        if (LogVisibilityToggle.IsChecked != true) { LogScrollTrack.Visibility = Visibility.Collapsed; return; }
        if (_logScrollViewer is null) return;
        var maximum = Math.Max(0, _logScrollViewer.ScrollableHeight);
        if (maximum <= 0 || _logScrollViewer.ExtentHeight <= 0)
        {
            LogScrollTrack.Visibility = Visibility.Collapsed;
            return;
        }
        LogScrollTrack.Visibility = Visibility.Visible;
        LogScrollTrack.UpdateLayout();
        var trackHeight = LogScrollTrack.ActualHeight;
        if (trackHeight <= 6) { LogScrollTrack.Visibility = Visibility.Collapsed; return; }
        var idealThumb = trackHeight * _logScrollViewer.ViewportHeight / _logScrollViewer.ExtentHeight;
        var minimumThumb = Math.Min(trackHeight - 4, Math.Max(18, trackHeight * 0.18));
        LogScrollThumb.Height = Math.Clamp(idealThumb - 4, minimumThumb, trackHeight - 4);
        LogScrollThumb.UpdateLayout();
        var travel = Math.Max(0, trackHeight - LogScrollThumb.ActualHeight - LogScrollThumb.Margin.Top - LogScrollThumb.Margin.Bottom);
        var position = _logScrollViewer.VerticalOffset / maximum * travel;
        ((TranslateTransform)LogScrollThumb.RenderTransform).Y = position;
    }

    private void LogScrollTrack_SizeChanged(object sender, SizeChangedEventArgs e) => SyncLogScrollbar();

    private void LogScrollTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_logScrollViewer is null || _logScrollViewer.ScrollableHeight <= 0) return;
        var trackHeight = LogScrollTrack.ActualHeight;
        var travel = Math.Max(1, trackHeight - LogScrollThumb.ActualHeight - LogScrollThumb.Margin.Top - LogScrollThumb.Margin.Bottom);
        var desiredPosition = Math.Clamp(e.GetPosition(LogScrollTrack).Y - LogScrollThumb.ActualHeight / 2, 0, travel);
        SetLogScrollFromThumbPosition(desiredPosition, travel);
        e.Handled = true;
    }

    private void LogScrollThumb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_logScrollViewer is null) return;
        _logThumbDragging = true;
        _logThumbGrabOffset = e.GetPosition(LogScrollTrack).Y - ((TranslateTransform)LogScrollThumb.RenderTransform).Y;
        LogScrollThumb.CaptureMouse();
        e.Handled = true;
    }

    private void LogScrollThumb_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_logThumbDragging || _logScrollViewer is null) return;
        var travel = Math.Max(1, LogScrollTrack.ActualHeight - LogScrollThumb.ActualHeight);
        var desiredPosition = Math.Clamp(e.GetPosition(LogScrollTrack).Y - _logThumbGrabOffset, 0, travel);
        SetLogScrollFromThumbPosition(desiredPosition, travel);
    }

    private void LogScrollThumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _logThumbDragging = false;
        LogScrollThumb.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void LogScrollThumb_LostMouseCapture(object sender, MouseEventArgs e) => _logThumbDragging = false;

    private void LogScrollTrack_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_logScrollViewer is null) return;
        _logScrollViewer.ScrollToVerticalOffset(_logScrollViewer.VerticalOffset - e.Delta / 2d);
        e.Handled = true;
    }

    private void SetLogScrollFromThumbPosition(double position, double travel)
    {
        if (_logScrollViewer is null) return;
        ((TranslateTransform)LogScrollThumb.RenderTransform).Y = position;
        _logScrollViewer.ScrollToVerticalOffset(position / travel * _logScrollViewer.ScrollableHeight);
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
    private async void UndoDelete_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (_lastDeleted is not { } deleted) return;
        _rules.Insert(Math.Clamp(deleted.Index, 0, _rules.Count), deleted.Rule);
        _lastDeleted = null;
        await RecordEditAsync();
        SetLog("Удалённое правило восстановлено.");
    });
    private async void RouteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RouteCombo.SelectedItem is not string route) return;
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
        if (_core.HasTrackedProcess) await StopCoreAsync(); else await StartCoreAsync();
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
            _settings.Theme = ThemeManager.Normalize(choice.Theme);
            _settings.InterfaceScale = AppSettings.NormalizeScale(choice.InterfaceScale);
            _settings.AutoCheckUpdates = choice.AutoCheckUpdates;
            ThemeManager.Apply(_settings.Theme);
            ApplyInterfaceScale(_settings.InterfaceScale);
            await SaveSettingsAsync();
            if (!string.Equals(choice.CorePath, _settings.CorePath, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(choice.CorePath))
            {
                if (_core.HasTrackedProcess) throw new InvalidOperationException("Перед сменой ядра останови VPN.");
                if (!File.Exists(choice.CorePath)) throw new FileNotFoundException("Выбранный файл ядра не найден.");
                _settings.CorePath = Path.GetFullPath(choice.CorePath);
                await SaveSettingsAsync();
                SetLog("Путь к ядру Mihomo сохранён.");
            }
            if (choice.Imported is { } imported) await ImportProfileAsync(imported);
            else if (!string.Equals(choice.ConfigPath, _settings.ConfigPath, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(choice.ConfigPath))
                await LoadConfigAsync(choice.ConfigPath);
            SetLog("Настройки сохранены.");
            applied = new SettingsDialogSelection(_settings.ConfigPath, _settings.CorePath, _settings.Theme,
                _settings.InterfaceScale, _settings.AutoCheckUpdates);
        });
        return applied;
    }
    private async void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing) return;
        var imported = ProfileImportDialog.Show(this);
        if (imported is not null) await RunExclusiveAsync(() => ImportProfileAsync(imported));
    }
    private async Task ImportProfileAsync(ImportedProfile imported)
    {
        if (!File.Exists(_settings.CorePath)) _settings.CorePath = await BundledResources.EnsureCoreAsync(_lifetime.Token);
        var path = await ProfileStorage.CreateAsync(imported, _settings.CorePath, _settings.StorageDirectory, _lifetime.Token);
        _settings.PreferredRoute = "VPN";
        await LoadConfigAsync(path);
        SetLog("Создан новый локальный YAML. Остальные профили сохранены. Добавь сайты или приложения в правила VPN.");
    }
    private async void ChooseConfig_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "YAML (*.yaml;*.yml)|*.yaml;*.yml", CheckFileExists = true };
        if (File.Exists(_settings.ConfigPath)) dialog.InitialDirectory = Path.GetDirectoryName(_settings.ConfigPath);
        if (dialog.ShowDialog(this) == true) await LoadConfigAsync(dialog.FileName);
    });
    private async void ChooseCore_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        if (_core.HasTrackedProcess) throw new InvalidOperationException("Перед сменой ядра останови VPN.");
        var dialog = new OpenFileDialog { Filter = "Ядро Mihomo (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _settings.CorePath = dialog.FileName;
        await SaveSettingsAsync();
    });
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
        if (_document is null || RouteCombo.SelectedItem is not string route)
            throw new InvalidOperationException("Сначала выбери профиль и маршрут.");
        var names = processNames.Select(name => ClashConfigDocument.NormalizeDomain(name, "PROCESS-NAME"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var added = names.Where(name => !_rules.Any(rule => rule.Kind == "PROCESS-NAME" &&
            rule.Value.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(name => new DomainRule { Kind = "PROCESS-NAME", Value = name, Route = route }).ToArray();
        foreach (var rule in added) _rules.Add(rule);
        if (added.Length == 0) { SetLog("Выбранные приложения уже есть в правилах."); return; }
        _lastDeleted = null;
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
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
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
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
            SetLog(ex.Message);
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
