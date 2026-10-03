using Microsoft.Win32;
using System.Net.NetworkInformation;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private CancellationTokenSource? _recoveryLifetime;
    private bool _connectionRequested, _recoveringConnection, _recoveryEventsRegistered;
    private DateTime _ignoreRecoveryNetworkUntil;
    internal bool AutoReconnect => _settings.AutoReconnect;
    internal bool CanReconnect => _settings.AutoReconnect && _connectionRequested && !_closing && !_exitRequested;

    private void InitializeConnectionRecovery()
    {
        if (!_desktopIntegrationEnabled) return;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        _recoveryEventsRegistered = true;
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Post(() =>
        {
            if (!e.IsAvailable) { _recoveryLifetime?.Cancel(); return; }
            if (DateTime.UtcNow >= _ignoreRecoveryNetworkUntil) QueueConnectionRecovery();
        });
    }
    private void NetworkAddressChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (DateTime.UtcNow >= _ignoreRecoveryNetworkUntil && NetworkInterface.GetIsNetworkAvailable()) QueueConnectionRecovery();
    });
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e) => Post(() =>
    {
        if (e.Mode == PowerModes.Suspend) _recoveryLifetime?.Cancel();
        else if (e.Mode == PowerModes.Resume) QueueConnectionRecovery();
    });

    private void CancelConnectionIntent()
    {
        _connectionRequested = false;
        _recoveryLifetime?.Cancel();
    }

    private void QueueConnectionRecovery()
    {
        if (!CanReconnect) return;
        _recoveryLifetime?.Cancel();
        _recoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        UpdateControls();
        _ = RestoreConnectionAsync(_recoveryLifetime);
    }

    private async Task RestoreConnectionAsync(CancellationTokenSource lifetime)
    {
        try
        {
            // Address changes often arrive in bursts while Windows renews its routes.
            await Task.Delay(2500, lifetime.Token);
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (_busy || _dialogBackdropVisible)
            {
                if (!CanReconnect || DateTime.UtcNow >= deadline) return;
                await Task.Delay(500, lifetime.Token);
            }
            if (!CanReconnect || !NetworkInterface.GetIsNetworkAvailable()) return;
            SetLog("Проверяю VPN после сна или смены сети.");
            var connected = await ConnectionRecovery.RunAsync(TryRestoreConnectionAsync, () => CanReconnect, lifetime.Token);
            if (!connected && CanReconnect && !lifetime.IsCancellationRequested)
                SetLog("Автоматическое восстановление завершено: VPN не ответил. Запусти подключение вручную или проверь журнал.");
        }
        catch (OperationCanceledException) { }
        catch { if (!_closing) SetLog("Не удалось восстановить VPN автоматически. Проверь журнал и подключение."); }
        finally
        {
            if (ReferenceEquals(_recoveryLifetime, lifetime)) _recoveryLifetime = null;
            lifetime.Dispose();
            if (!_closing) UpdateControls();
        }
    }

    private async Task<RecoveryAttemptResult> TryRestoreConnectionAsync(CancellationToken token)
    {
        if (!CanReconnect || !NetworkInterface.GetIsNetworkAvailable()) return RecoveryAttemptResult.Stop;
        if (_busy || _dialogBackdropVisible) return RecoveryAttemptResult.Retry;
        if (_core.IsRunning && _diagnostics is { } diagnostics && _document?.VpnRoute is { } route)
        {
            try
            {
                using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                probeDeadline.CancelAfter(TimeSpan.FromSeconds(12));
                var selected = await diagnostics.ReadSelectionAsync(route, probeDeadline.Token);
                if (selected is "DIRECT" or "REJECT")
                { SetLog("Восстановление остановлено: в группе VPN выбран прямой маршрут или блокировка."); return RecoveryAttemptResult.Stop; }
                var delay = await diagnostics.CheckProxyAsync(route, probeDeadline.Token);
                if (!CanReconnect) return RecoveryAttemptResult.Stop;
                _health.Record(delay); RefreshHealthReadout();
                SetLog("VPN отвечает после восстановления сети.");
                return RecoveryAttemptResult.Connected;
            }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        if (!CanReconnect || !await _operations.WaitAsync(0, token)) return RecoveryAttemptResult.Retry;
        _busy = true; _recoveringConnection = true;
        try
        {
            UpdateControls();
            // Background recovery never asks to kill another application's core.
            if (CoreProcessManager.FindOtherInstances(_settings.CorePath).Any(instance => instance.ProcessId != _core.TrackedProcessId))
            { SetLog("Восстановление остановлено: найдено другое ядро. Проверь процессы или запусти VPN вручную."); return RecoveryAttemptResult.Stop; }
            _ignoreRecoveryNetworkUntil = DateTime.UtcNow.AddSeconds(15);
            if (!await StopCoreAsync()) return RecoveryAttemptResult.Stop;
            token.ThrowIfCancellationRequested();
            if (!CanReconnect) return RecoveryAttemptResult.Stop;
            await StartCoreAsync(applyDraft: false);
            if (!_core.IsRunning) return RecoveryAttemptResult.Stop;
            _ignoreRecoveryNetworkUntil = DateTime.UtcNow.AddSeconds(15);
            if (_diagnostics is not { } restored || _document?.VpnRoute is not { } restoredRoute) return RecoveryAttemptResult.Stop;
            var verifiedDelay = await restored.CheckProxyAsync(restoredRoute, token);
            _health.Record(verifiedDelay); RefreshHealthReadout();
            SetLog("VPN восстановлен и проверен после смены сети.");
            return RecoveryAttemptResult.Connected;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { SetLog("Попытка восстановления VPN не удалась."); return RecoveryAttemptResult.Retry; }
        finally
        {
            _recoveringConnection = false; _busy = false; _operations.Release();
            UpdateControls();
        }
    }

    private void DisposeConnectionRecovery()
    {
        CancelConnectionIntent();
        if (!_recoveryEventsRegistered) return;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        _recoveryEventsRegistered = false;
    }
}
