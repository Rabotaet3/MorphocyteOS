using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private CoreDiagnostics? _diagnostics;
    private string? _runtimeConfigPath;
    private CancellationTokenSource? _diagnosticLifetime;
    private DispatcherTimer? _diagnosticTimer;
    private bool _refreshingConnections, _checkingConnection;
    private IReadOnlyList<ConnectionActivity> _connections = Array.Empty<ConnectionActivity>();
    private string _selectedConnection = "", _runningCoreVersion = "";
    private readonly ConnectionHealth _health = new();
    private DateTime _nextHealthCheck = DateTime.MinValue;

    private void InitializeDiagnostics()
    {
        _diagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _diagnosticTimer.Tick += async (_, _) =>
        {
            if (!IsVisible || _busy || _dialogBackdropVisible || _closing) return;
            if (JournalPage.IsVisible) await RefreshConnectionSnapshotAsync();
            if (DateTime.UtcNow >= _nextHealthCheck) await CheckConnectionAsync(manual: false);
        };
        _diagnosticTimer.Start();
    }

    private void RefreshConnectionCard()
    {
        EndpointText.Text = _document is null || !_document.HasVpnConnection ? "Нет подключения" : _selectedConnection.Length > 0
            ? _selectedConnection : _document.ConnectionCount == 1 ? _document.Endpoint : "VPN-подключение";
        EndpointText.ToolTip = EndpointText.Text;
    }

    private async void RefreshConnections_Click(object sender, System.Windows.RoutedEventArgs e) => await RefreshConnectionSnapshotAsync();

    private async Task RefreshConnectionSnapshotAsync()
    {
        if (_diagnostics is not { } diagnostics || !_core.IsRunning || _closing || _refreshingConnections) return;
        _diagnosticLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _diagnosticLifetime.Token;
        _refreshingConnections = true;
        try
        {
            var snapshot = await diagnostics.ReadAsync(_document?.VpnRoute, token);
            if (_closing || token.IsCancellationRequested || !ReferenceEquals(diagnostics, _diagnostics)) return;
            _connections = snapshot.Connections;
            _selectedConnection = snapshot.Selection;
            _runningCoreVersion = snapshot.Version;
            FilterConnections();
            RefreshConnectionCard();
            ConnectionsStatusText.Text = $"Активных соединений: {_connections.Count}. Обновлено {DateTime.Now:HH:mm:ss}. Короткие запросы могут не попасть в список.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (ReferenceEquals(diagnostics, _diagnostics) && !_closing)
                ConnectionsStatusText.Text = "Диагностика ядра пока недоступна. Нажми «Обновить» или проверь журнал.";
        }
        finally { _refreshingConnections = false; }
    }

    private void ConnectionSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterConnections();

    private void FilterConnections()
    {
        if (ConnectionsGrid is null) return;
        var search = ConnectionSearchBox.Text.Trim();
        ConnectionsGrid.ItemsSource = _connections.Where(row => search.Length == 0 ||
            $"{row.Process} {row.Destination} {row.Network} {row.Rule} {row.Route}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private async void CheckConnection_Click(object sender, System.Windows.RoutedEventArgs e) => await CheckConnectionAsync(manual: true);

    private async Task CheckConnectionAsync(bool manual)
    {
        if (_checkingConnection || !_core.IsRunning || _diagnostics is not { } diagnostics || _document?.VpnRoute is not { } route) return;
        _diagnosticLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _diagnosticLifetime.Token;
        _checkingConnection = true;
        _nextHealthCheck = DateTime.UtcNow.AddSeconds(20);
        UpdateControls();
        ConnectionStateText.Text = "Проверяю доступность…";
        try
        {
            var selection = await diagnostics.ReadSelectionAsync(route, token);
            if (selection is "DIRECT" or "REJECT")
                throw new IOException("В группе VPN выбран прямой маршрут или блокировка. Выбери VPN-подключение в профиле.");
            var delay = await diagnostics.CheckProxyAsync(route, token);
            if (!ReferenceEquals(diagnostics, _diagnostics) || token.IsCancellationRequested) return;
            _selectedConnection = selection;
            _health.Record(delay);
            RefreshHealthReadout();
            RefreshConnectionCard();
            ConnectionStateText.Text = $"Проверено {DateTime.Now:HH:mm:ss}";
            LastIssueText.Text = "";
            if (manual) SetLog("Проверка VPN: HTTPS-запрос выполнен успешно.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(diagnostics, _diagnostics) && !_closing)
            {
                _health.Record(null);
                RefreshHealthReadout();
                ConnectionStateText.Text = "Нет ответа · " + DateTime.Now.ToString("HH:mm:ss");
                LastIssueText.Text = manual ? ex is IOException ? ex.Message : "Проверка не завершилась. Проверь сервер и журнал." : "";
                if (manual || _health.Count == 1) SetLog("Проверка VPN: HTTPS-запрос не прошёл.");
            }
        }
        finally { _checkingConnection = false; UpdateControls(); }
    }

    private void RefreshHealthReadout()
    {
        PingText.Text = _health.LastDelay is { } delay ? $"{delay} мс" : "—";
        StabilityText.Text = _health.Count == 0 ? "—" : $"{_health.Availability:0}%";
        StabilityText.ToolTip = $"Успешных HTTPS-проверок: {_health.SuccessCount} из {_health.Count}. Учитываются последние 20; это не измерение потерь сетевых пакетов.";
    }

    private void StartTrafficMonitor()
    {
        if (_diagnostics is not { } diagnostics) return;
        _diagnosticLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = MonitorTrafficAsync(diagnostics, _diagnosticLifetime.Token);
    }

    private async Task MonitorTrafficAsync(CoreDiagnostics diagnostics, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && ReferenceEquals(diagnostics, _diagnostics) && _core.IsRunning)
            {
                if (!IsVisible) { await Task.Delay(1000, token); continue; }
                try
                {
                    var traffic = await diagnostics.ReadTrafficAsync(token);
                    if (token.IsCancellationRequested || !ReferenceEquals(diagnostics, _diagnostics)) return;
                    DownloadSpeedText.Text = ConnectionHealth.FormatRate(traffic.Download);
                    UploadSpeedText.Text = ConnectionHealth.FormatRate(traffic.Upload);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch
                {
                    if (!ReferenceEquals(diagnostics, _diagnostics)) return;
                    DownloadSpeedText.Text = UploadSpeedText.Text = "—";
                    await Task.Delay(2000, token);
                }
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ResetDiagnostics()
    {
        _manualDiagnosticLifetime?.Cancel();
        _diagnosticLifetime?.Cancel();
        _diagnosticLifetime?.Dispose();
        _diagnosticLifetime = null;
        _diagnostics?.Dispose();
        _diagnostics = null;
        _selectedConnection = "";
        _runningCoreVersion = "";
        _connections = Array.Empty<ConnectionActivity>();
        _health.Clear();
        _nextHealthCheck = DateTime.MinValue;
        DownloadSpeedText.Text = UploadSpeedText.Text = "—";
        RefreshHealthReadout();
        FilterConnections();
        ConnectionStateText.Text = "VPN выключен";
        ConnectionsStatusText.Text = "Соединения появятся после запуска VPN.";
        if (_runtimeConfigPath is { } path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _runtimeConfigPath = null;
        }
    }
}
