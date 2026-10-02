using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private UpdateCheckResult? _availableUpdate;
    private readonly SemaphoreSlim _updateChecks = new(1, 1);
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
        DownloadUpdateButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadUpdateButton.Content = "Загрузить актуальную версию: " + _availableUpdate?.LatestVersion;
        UpdateStateChanged?.Invoke(_availableUpdate);
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e) => await InstallAvailableUpdateAsync(this);

    internal async Task InstallAvailableUpdateAsync(Window dialogOwner)
    {
        await RunExclusiveAsync(async () =>
        {
            if (_availableUpdate is null) return;
            var prepared = await UpdateProgressDialog.ShowAsync(dialogOwner, _availableUpdate, UpdateCacheDirectory, _lifetime.Token);
            if (prepared is null) return;
            var handedToInstaller = false;
            try
            {
                if (_closing) return;
                var accepted = UtilityDialogs.ShowConfirm(dialogOwner, "ОБНОВЛЕНИЕ ГОТОВО",
                    $"Версия {prepared.Manifest.Version} загружена и проверена. Для установки приложение закроется и откроется снова. Активный VPN будет остановлен; после обновления его нужно запустить вручную.\n\nЧерновики, личные профили и пользовательское ядро сохранятся.",
                    "ОБНОВИТЬ И ПЕРЕЗАПУСТИТЬ", "ПОЗЖЕ");
                if (!accepted) return;
                _lifetime.Token.ThrowIfCancellationRequested();
                if (_dirty) await SaveDraftAsync();
                await SaveSettingsAsync();
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
            if (!outcome.Success) UtilityDialogs.ShowNotice(this, "ОБНОВЛЕНИЕ НЕ УСТАНОВЛЕНО", outcome.Message + "\n\nПапка восстановления: " + operation);
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
