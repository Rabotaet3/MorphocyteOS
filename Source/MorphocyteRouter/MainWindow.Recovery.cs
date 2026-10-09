using Microsoft.Win32;
using System.Net.NetworkInformation;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private CancellationTokenSource? _recoveryLifetime;
    private CancellationTokenSource? _deferredNetworkLifetime;
    private readonly Queue<DateTime> _unexpectedCoreExits = new();
    private string _physicalNetworkStamp = ReadPhysicalNetworkStamp();
    private Func<CancellationToken, Task> _recoverySystemProbe = token => CoreDiagnostics.CheckSystemHttpsAsync(token);
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
            if (!e.IsAvailable) { _physicalNetworkStamp = "offline"; _recoveryLifetime?.Cancel(); _deferredNetworkLifetime?.Cancel(); return; }
            ScheduleNetworkRecovery();
        });
    }
    private void NetworkAddressChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (NetworkInterface.GetIsNetworkAvailable()) ScheduleNetworkRecovery();
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
        _deferredNetworkLifetime?.Cancel();
        _unexpectedCoreExits.Clear();
    }

    private static string ReadPhysicalNetworkStamp()
    {
        try
        {
            return string.Join("|", NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && !adapter.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                    && !adapter.Name.Contains("MorphocyteTUN", StringComparison.OrdinalIgnoreCase)
                    && !adapter.Name.Contains("mihomo", StringComparison.OrdinalIgnoreCase))
                .Select(adapter => {
                    var info = adapter.GetIPProperties();
                    return adapter.Id + ":" + string.Join(",", info.UnicastAddresses.Select(item => item.Address.ToString()).Order())
                        + ":" + string.Join(",", info.GatewayAddresses.Select(item => item.Address.ToString()).Order())
                        + ":" + string.Join(",", info.DnsAddresses.Select(item => item.ToString()).Order());
                }).Order());
        }
        catch (NetworkInformationException) { return "unavailable"; }
    }

    private void ScheduleNetworkRecovery(string? networkStamp = null)
    {
        var stamp = networkStamp ?? ReadPhysicalNetworkStamp();
        if (stamp == _physicalNetworkStamp) return; // The core's own TUN notifications are not a physical network change.
        _physicalNetworkStamp = stamp;
        if (!_settings.AutoReconnect || _closing || _exitRequested || (!_connectionRequested && !_core.HasTrackedProcess)) return;
        if (DateTime.UtcNow >= _ignoreRecoveryNetworkUntil && !_recoveringConnection) { QueueConnectionRecovery(); return; }
        // Defer startup/TUN route notifications instead of discarding real network changes.
        // Do not cancel an in-progress recovery because its own adapter emits notifications.
        _deferredNetworkLifetime?.Cancel();
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _deferredNetworkLifetime = lifetime;
        _ = RecoverAfterNetworkSettlesAsync(lifetime);
    }

    private async Task RecoverAfterNetworkSettlesAsync(CancellationTokenSource lifetime)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            do { await Task.Delay(500, lifetime.Token); }
            while (DateTime.UtcNow < deadline && (DateTime.UtcNow < _ignoreRecoveryNetworkUntil || _busy || _recoveringConnection));
            if (DateTime.UtcNow >= deadline) return;
            if (CanReconnect && NetworkInterface.GetIsNetworkAvailable()) QueueConnectionRecovery();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_deferredNetworkLifetime, lifetime)) _deferredNetworkLifetime = null;
            lifetime.Dispose();
        }
    }

    private void RecoverUnexpectedCoreExit()
    {
        if (!CanReconnect) return;
        var now = DateTime.UtcNow;
        while (_unexpectedCoreExits.TryPeek(out var previous) && now - previous > TimeSpan.FromMinutes(2)) _unexpectedCoreExits.Dequeue();
        if (_unexpectedCoreExits.Count >= 3)
        {
            CancelConnectionIntent();
            SetLog("Ядро несколько раз аварийно завершилось. Автовосстановление остановлено; проверь журнал.");
            return;
        }
        _unexpectedCoreExits.Enqueue(now);
        SetLog("Ядро неожиданно завершилось. Пробую восстановить работавшее подключение.");
        QueueConnectionRecovery();
    }

    private void QueueConnectionRecovery()
    {
        if (!CanReconnect) return;
        _speedTestLifetime?.Cancel();
        _downloadCapacity = null;
        _capacityServer = "";
        RefreshBandwidthReadout();
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
                probeDeadline.CancelAfter(TimeSpan.FromSeconds(25));
                var selected = await diagnostics.ReadSelectionAsync(route, probeDeadline.Token);
                if (selected is "DIRECT" or "REJECT")
                { SetLog("Восстановление остановлено: в группе VPN выбран прямой маршрут или блокировка."); return RecoveryAttemptResult.Stop; }
                var delay = await diagnostics.CheckProxyAsync(route, probeDeadline.Token);
                if (_settings.FullTunnel) await _recoverySystemProbe(probeDeadline.Token);
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
            using var verifyDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            verifyDeadline.CancelAfter(TimeSpan.FromSeconds(25));
            var verifiedDelay = await restored.CheckProxyAsync(restoredRoute, verifyDeadline.Token);
            if (_settings.FullTunnel) await _recoverySystemProbe(verifyDeadline.Token);
            if (!CanReconnect) return RecoveryAttemptResult.Stop;
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
