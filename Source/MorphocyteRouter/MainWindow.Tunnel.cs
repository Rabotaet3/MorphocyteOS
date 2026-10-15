using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;

namespace MorphocyteRouter;

public partial class MainWindow
{
    public static readonly DependencyProperty FullTunnelActiveProperty = DependencyProperty.Register(
        nameof(FullTunnelActive), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool FullTunnelActive { get => (bool)GetValue(FullTunnelActiveProperty); set => SetValue(FullTunnelActiveProperty, value); }
    private int _speedTestPort;
    private CancellationTokenSource? _speedTestLifetime;
    private double? _downloadCapacity;
    private string _capacityServer = "";
    private bool _changingTunnel;

    private static int FreeLoopbackPort(int otherPort)
    {
        int port;
        do
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        } while (port == otherPort);
        return port;
    }

    private void RefreshTunnelControls()
    {
        if (!_changingTunnel) FullTunnelCheck.IsChecked = _settings.FullTunnel;
        FullTunnelActive = _settings.FullTunnel;
        FullTunnelCheck.IsEnabled = !_busy && !_closing && _document is { HasVpnConnection: true };
        SpeedTestButton.IsEnabled = !_busy && !_closing && _core.IsRunning && _diagnostics is not null;
        SpeedTestButton.Content = _speedTestLifetime is null ? "СКОРОСТЬ" : "ОСТАНОВИТЬ";
    }

    private async void FullTunnel_Click(object sender, RoutedEventArgs e)
    {
        var requested = FullTunnelCheck.IsChecked == true;
        if (_busy || _closing) { RefreshTunnelControls(); return; }
        _changingTunnel = true;
        try { await RunExclusiveAsync(() => ChangeTunnelModeAsync(requested)); }
        finally { _changingTunnel = false; RefreshTunnelControls(); }
    }

    private async Task ChangeTunnelModeAsync(bool enabled)
    {
        if (enabled == _settings.FullTunnel || _document is not { HasVpnConnection: true }) return;
        var previous = _settings.FullTunnel;
        var wasRunning = _core.IsRunning;
        var previousIntent = _connectionRequested;
        CancelConnectionIntent();
        if (wasRunning && !await StopCoreAsync())
        { _connectionRequested = previousIntent; throw new IOException("Не удалось подтвердить остановку ядра. Режим не изменён."); }
        _settings.FullTunnel = enabled;
        try
        {
            // A mode switch never commits unrelated pending rule edits.
            if (wasRunning) await StartCoreAsync(applyDraft: false);
            if (wasRunning && !_core.IsRunning) throw new IOException("Переключение режима отменено.");
            await SaveSettingsAsync();
            SetLog(enabled ? "TUN: VPN по умолчанию; исключения «Напрямую» и блокировки сохранены." : "TUN: восстановлена маршрутизация по правилам.");
        }
        catch
        {
            if (_core.HasTrackedProcess && !await StopCoreAsync()) throw new IOException("Остановка ядра не подтверждена. Проверь журнал перед сменой режима.");
            _settings.FullTunnel = previous;
            if (wasRunning && !_closing) await StartCoreAsync(applyDraft: false);
            throw;
        }
        finally { RefreshTunnelControls(); }
    }

    private void RefreshBandwidthReadout()
    {
        StabilityText.Text = VpnSpeedTest.FormatCapacity(_downloadCapacity);
        if (_downloadCapacity is null && _speedTestLifetime is null)
        { SpeedProgressText.Text = ""; SpeedProgressText.Visibility = Visibility.Hidden; }
        StabilityText.ToolTip = _downloadCapacity is > 0 && double.IsFinite(_downloadCapacity.Value)
            ? "Наибольшая измеренная скорость приёма по последнему тесту VPN: " + StabilityText.Text
            : "Нажми «Скорость», чтобы определить пропускную способность VPN.";
    }

    private async void SpeedTest_Click(object sender, RoutedEventArgs e)
    {
        if (_speedTestLifetime is { } active) { active.Cancel(); return; }
        await MeasureVpnSpeedAsync();
    }

    private async Task MeasureVpnSpeedAsync()
    {
        if (_busy || _closing || _speedTestLifetime is not null || !_core.IsRunning || _diagnostics is not { } diagnostics
            || _document?.VpnRoute is not { } route || _speedTestPort == 0) return;
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _diagnosticLifetime?.Token ?? _lifetime.Token);
        _speedTestLifetime = lifetime;
        var completed = false;
        var shownProgress = 0;
        void ShowProgress(int percent)
        {
            shownProgress = Math.Max(shownProgress, Math.Clamp(percent, 0, 100));
            SpeedProgressText.Text = shownProgress + " %";
            SpeedProgressText.Visibility = Visibility.Visible;
        }
        ShowProgress(0);
        LastIssueText.Text = "";
        RefreshTunnelControls();
        var transferStarted = false;
        try
        {
            var server = await diagnostics.ReadSelectionAsync(route, lifetime.Token);
            if (server is "DIRECT" or "REJECT")
            { LastIssueText.Text = "Для замера выбери VPN-подключение вместо «Напрямую» или блокировки."; SetLog(LastIssueText.Text); return; }
            if (_capacityServer.Length > 0 && _capacityServer != server)
            { _downloadCapacity = null; _capacityServer = ""; RefreshBandwidthReadout(); }
            SetLog("Замер скорости через VPN: тестовая загрузка Cloudflare, до 64 МБ / 15 секунд.");
            using var test = new VpnSpeedTest(_speedTestPort, diagnostics.Secret, socksProxy: CoreBackend.IsSingBox(_settings.CorePath));
            transferStarted = true;
            var progress = new Progress<int>(percent =>
            {
                if (!completed && ReferenceEquals(_speedTestLifetime, lifetime) && !lifetime.IsCancellationRequested && !_closing)
                    ShowProgress(1 + percent * 96 / 100);
            });
            var result = await test.MeasureAsync(lifetime.Token, progress);
            ShowProgress(98);
            var after = await diagnostics.ReadSelectionAsync(route, lifetime.Token);
            if (after != server)
            { _downloadCapacity = null; _capacityServer = ""; RefreshBandwidthReadout(); LastIssueText.Text = "Сервер изменился во время замера. Повтори проверку."; SetLog(LastIssueText.Text); return; }
            if (lifetime.IsCancellationRequested || !ReferenceEquals(diagnostics, _diagnostics) || !_core.IsRunning || _closing) return;
            _downloadCapacity = result.DownloadBytesPerSecond;
            _capacityServer = server;
            completed = true;
            ShowProgress(100);
            LastIssueText.Text = "";
            RefreshBandwidthReadout();
            SetLog("Максимальная скорость приёма по замеру через VPN: " + VpnSpeedTest.FormatCapacity(result.DownloadBytesPerSecond));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (ReferenceEquals(diagnostics, _diagnostics) && !_closing)
            { LastIssueText.Text = transferStarted ? VpnSpeedTest.FailureMessage(error) : "Не удалось получить выбранное подключение у ядра. Перезапусти VPN."; SetLog(LastIssueText.Text); }
        }
        finally
        {
            if (ReferenceEquals(_speedTestLifetime, lifetime)) _speedTestLifetime = null;
            if (!completed) { SpeedProgressText.Text = ""; SpeedProgressText.Visibility = Visibility.Hidden; }
            lifetime.Dispose();
            if (!_closing) RefreshTunnelControls();
        }
    }
}
