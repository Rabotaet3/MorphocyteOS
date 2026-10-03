
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private UpdateCheckResult? _availableUpdate;
    private readonly SemaphoreSlim _updateChecks = new(1, 1);
    private DispatcherTimer? _automaticUpdateTimer;
    private bool _autoInstallQueued, _autoInstallRunning, _automaticCheckRunning;
    private string? _deferredUpdateVersion, _attemptedAutoUpdateVersion;
    private string? _automaticUpdateFailure;
    internal string LastUpdateMessage { get; private set; } = "";
    internal UpdateCheckResult? AvailableUpdate => _availableUpdate;
    internal event Action<UpdateCheckResult?>? UpdateStateChanged;
    private string UpdateCacheDirectory => Path.Combine(_settings.StorageDirectory, ".update-cache");

    internal async Task<UpdateCheckResult> CheckUpdatesAsync(CancellationToken token)
    {
        await _updateChecks.WaitAsync(token);
        try
        {
            var result = await ReleaseUpdateService.CheckAsync(token);
            if (!_closing) SetAvailableUpdate(result);
            return result;
        }
        finally { _updateChecks.Release(); }
    }

    private void SetAvailableUpdate(UpdateCheckResult? result)
    {
        _availableUpdate = result is { UpdateAvailable: true, Asset: not null } ? result : null;
        LastUpdateMessage = result?.Message ?? "";
        RefreshUpdateOffer();
        UpdateStateChanged?.Invoke(_availableUpdate);
        ScheduleAutoInstall();
    }

    private void RefreshUpdateOffer()
    {
        UpdateBanner.Visibility = _availableUpdate is not null && _availableUpdate.LatestVersion != _deferredUpdateVersion
            ? Visibility.Visible : Visibility.Collapsed;
        if (_availableUpdate is null || _autoInstallRunning) return;
        if (_automaticUpdateFailure is not null && _attemptedAutoUpdateVersion == _availableUpdate.LatestVersion)
        {
            UpdateOfferText.Text = _automaticUpdateFailure;
            return;
        }
        UpdateOfferText.Text = $"Доступна версия {_availableUpdate.LatestVersion}. " + (_settings.AutoCheckUpdates
            ? _core.HasTrackedProcess ? "Установится после остановки VPN." : "Установится автоматически, когда приложение свободно."
            : "Можно установить сейчас или через настройки.");
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _deferredUpdateVersion = _availableUpdate?.LatestVersion;
        RefreshUpdateOffer();
    }

    internal bool CanInstallAutomatically => _settings.AutoCheckUpdates && _availableUpdate is { } update
        && update.LatestVersion != _deferredUpdateVersion && update.LatestVersion != _attemptedAutoUpdateVersion
        && update.LatestVersion != _settings.AutoUpdateBlockedVersion
        && !_core.HasTrackedProcess && !_busy && !_closing && !_dialogBackdropVisible && !_autoInstallRunning && _recoveryLifetime is null;

    private void InitializeAutomaticUpdates()
    {
        _automaticUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _automaticUpdateTimer.Tick += async (_, _) =>
        {
            if (!_settings.AutoCheckUpdates || _busy || _closing || _automaticCheckRunning) return;
            _automaticCheckRunning = true;
            try { await CheckForUpdatesOnStartupAsync(); }
            finally { _automaticCheckRunning = false; }
        };
        _automaticUpdateTimer.Start();
    }

    private void ScheduleAutoInstall()
    {
        if (_autoInstallQueued || !CanInstallAutomatically) return;
        _autoInstallQueued = true;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            _autoInstallQueued = false;
            if (!CanInstallAutomatically) return;
            _attemptedAutoUpdateVersion = _availableUpdate?.LatestVersion;
            _autoInstallRunning = true;
            try { await InstallAvailableUpdateAsync(this, automatic: true); }
            finally { _autoInstallRunning = false; }
        }));
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e) => await InstallAvailableUpdateAsync(this);

    internal async Task InstallAvailableUpdateAsync(Window dialogOwner, bool automatic = false)
    {
        await RunExclusiveAsync(async () =>
        {
            try
            {
            if (automatic && _core.HasTrackedProcess) return;
            if (_availableUpdate is null)
            {
                var result = await CheckUpdatesAsync(_lifetime.Token);
                if (_availableUpdate is null)
                {
                    SetLog(result.Message);
                    UtilityDialogs.ShowNotice(dialogOwner, "ОБНОВЛЕНИЕ", result.Message);
                    return;
                }
            }
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, UpdatePackage.ManifestName)))
                throw new IOException("В этом комплекте нет release-manifest.json. Для первого обновления распакуй полный архив релиза в отдельную папку. Личные настройки сохранятся.");
            var prepared = automatic
                ? await UpdateDownloader.PrepareAsync(_availableUpdate, UpdateCacheDirectory,
                    new Progress<UpdateProgress>(progress => UpdateOfferText.Text = progress.Message), _lifetime.Token)
                : await UpdateProgressDialog.ShowAsync(dialogOwner, _availableUpdate, UpdateCacheDirectory, _lifetime.Token);
            if (prepared is null) return;
            var handedToInstaller = false;
            try
            {
                if (_closing) return;
                _lifetime.Token.ThrowIfCancellationRequested();
                if (_dirty) await SaveDraftAsync();
                // Persist before handing off: a failed install/rollback must not
                // start the same automatic update again after the old app restarts.
                if (_settings.AutoCheckUpdates) _settings.AutoUpdateBlockedVersion = prepared.Manifest.Version;
                await SaveSettingsAsync();
                CancelConnectionIntent();
                if (!await StopCoreAsync()) throw new IOException("Обновление отменено: остановка ядра не подтверждена.");
                var protectedFiles = new List<string> { _settings.ConfigPath };
                var bundled = Path.Combine(AppContext.BaseDirectory, BundledResources.CoreFileName);
                if (!string.Equals(_settings.CorePath, bundled, StringComparison.OrdinalIgnoreCase) ||
                    (File.Exists(bundled) && !UpdatePackage.Hash(bundled).Equals(BundledResources.CoreHash, StringComparison.OrdinalIgnoreCase)))
                    protectedFiles.Add(_settings.CorePath);
                var executable = Environment.ProcessPath ?? throw new IOException("Не удалось определить файл приложения.");
                if (!Path.GetDirectoryName(executable)!.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Для обновления запусти приложение через EXE, а не через dotnet.");
                _ = await Task.Run(() => UpdateInstaller.Start(prepared, AppContext.BaseDirectory, Path.GetFileName(executable), protectedFiles));
                handedToInstaller = true;
                _closing = true; _allowClose = true; _lifetime.Cancel(); _uptimeTimer.Stop(); _logRefreshTimer.Stop();
                foreach (Window window in OwnedWindows.Cast<Window>().ToArray()) window.Close();
                Close();
            }
            finally
            {
                if (!handedToInstaller)
                    try { UpdateDownloader.DeleteOperation(UpdateCacheDirectory, prepared.Directory); }
                    catch { SetLog("Временные файлы обновления пока заняты; установленные файлы не заменены."); }
            }
            }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception ex) when (automatic)
            {
                LastUpdateMessage = "Не удалось обновить автоматически. Повтори установку в настройках.";
                _automaticUpdateFailure = LastUpdateMessage;
                UpdateOfferText.Text = LastUpdateMessage;
                SetLog("Автообновление: " + ex.Message);
                UpdateStateChanged?.Invoke(_availableUpdate);
            }
        });
    }

    private async Task ReadUpdateResultAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var position = Array.IndexOf(args, "--update-result");
        if (position < 0 || position + 1 >= args.Length) return;
        try
        {
            var result = Path.GetFullPath(args[position + 1]);
            var operation = Path.GetDirectoryName(result)!;
            if (!string.Equals(Path.GetDirectoryName(operation), Path.GetFullPath(UpdateCacheDirectory), StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(result) != "result.json" || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(operation), @"\A[a-f0-9]{32}\z") ||
                !File.Exists(result) || new FileInfo(result).Length > 32768) return;
            var outcome = JsonSerializer.Deserialize<UpdateOutcome>(await File.ReadAllTextAsync(result));
            if (outcome is null) return;
            SetLog(outcome.Message);
            LastUpdateMessage = outcome.Message;
            if (outcome.Success && _settings.AutoUpdateBlockedVersion == BuildInfo.Version)
            {
                _settings.AutoUpdateBlockedVersion = "";
                await SaveSettingsAsync();
            }
            if (!outcome.Success) { RestoreFromTray(); UtilityDialogs.ShowNotice(this, "ОБНОВЛЕНИЕ НЕ УСТАНОВЛЕНО", outcome.Message + "\n\nПапка восстановления: " + operation); }
        }
        catch { SetLog("Не удалось прочитать результат обновления."); }
    }

    private async Task CleanCompletedUpdateCachesAsync()
    {
        try
        {
        await Task.Run(async () =>
        {
            if (!Directory.Exists(UpdateCacheDirectory)) return;
            _ = UpdatePackage.ResolveFile(UpdateCacheDirectory, UpdatePackage.ManifestName);
            foreach (var operation in Directory.EnumerateDirectories(UpdateCacheDirectory))
            {
                try
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(operation), @"\A[a-f0-9]{32}\z")) continue;
                    if ((!string.IsNullOrEmpty(_settings.ConfigPath) && Path.GetFullPath(_settings.ConfigPath).StartsWith(operation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(_settings.CorePath) && Path.GetFullPath(_settings.CorePath).StartsWith(operation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                    var result = Path.Combine(operation, "result.json");
                    if (!File.Exists(result) || new FileInfo(result).Length > 32768 ||
                        JsonSerializer.Deserialize<UpdateOutcome>(await File.ReadAllTextAsync(result))?.Success != true) continue;
                    // The helper may still be returning when the restarted app first loads.
                    await Task.Delay(1000);
                    UpdateDownloader.DeleteOperation(UpdateCacheDirectory, operation);
                }
                catch { /* A live helper lock is retried at the next app start. Failed rollback is never pruned. */ }
            }
        });
        }
        catch { /* Cache cleanup must not interrupt startup or remove unverified files. */ }
    }
}
