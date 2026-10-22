using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private void ConnectionsGrid_SizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            ConnectionsGrid.Clip = new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(e.NewSize), 11, 11);
    }
    private CoreDiagnostics? _diagnostics;
    private string? _runtimeConfigPath;
    private CancellationTokenSource? _diagnosticLifetime;
    private DispatcherTimer? _diagnosticTimer;
    private bool _refreshingConnections, _checkingConnection;
    private IReadOnlyList<ConnectionActivity> _connections = Array.Empty<ConnectionActivity>();
    // Batch updates behind the stable view, then refresh once.
    private readonly List<ConnectionTableRow> _connectionRows = new();
    private ListCollectionView? _connectionsView;
    private string _selectedConnection = "";
    private readonly ConnectionHealth _health = new();
    private DateTime _nextHealthCheck = DateTime.MinValue;

    private void InitializeDiagnostics()
    {
        _connectionsView = new ListCollectionView(_connectionRows) { Filter = MatchesConnectionSearch };
        ConnectionsGrid.ItemsSource = _connectionsView;
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
            if (_capacityServer.Length > 0 && _capacityServer != snapshot.Selection)
            { _downloadCapacity = null; _capacityServer = ""; RefreshBandwidthReadout(); }
            _selectedConnection = snapshot.Selection;
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
        if (_connectionsView is not { } view) return;
        // Keep the chosen row still while its menu is open. Network polling
        // continues; only painting the newest table snapshot is deferred.
        if (_connectionRouteMenu is { IsOpen: true }) { _connectionTableRefreshPending = true; return; }
        _connectionTableRefreshPending = false;
        var scroll = FindVisualDescendant<ScrollViewer>(ConnectionsGrid);
        var vertical = scroll?.VerticalOffset ?? 0;
        var horizontal = scroll?.HorizontalOffset ?? 0;
        var index = (int)(vertical / ConnectionsGrid.RowHeight);
        var anchor = index >= 0 && index < view.Count ? view.GetItemAt(index) : null;
        var selected = ConnectionsGrid.SelectedItems.Cast<ConnectionTableRow>().ToArray();
        var current = ConnectionsGrid.CurrentCell;
        var byKey = _connectionRows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        // No collection notifications or view refresh until the snapshot is complete.
        {
            foreach (var activity in _connections)
            {
                var identity = activity.Id.Length > 0 ? "id:" + activity.Id
                    : "row:" + string.Join('\0', activity.Process, activity.Destination, activity.Network, activity.Rule, activity.Route);
                var occurrence = occurrences.GetValueOrDefault(identity);
                occurrences[identity] = occurrence + 1;
                var key = identity + "\0" + occurrence;
                retained.Add(key);
                if (byKey.TryGetValue(key, out var row)) row.Update(activity);
                else _connectionRows.Add(new(key, activity));
            }
            for (var i = _connectionRows.Count - 1; i >= 0; i--)
                if (!retained.Contains(_connectionRows[i].Key)) _connectionRows.RemoveAt(i);
        }
        // Retain the view and its sort descriptions, even when a visible field changes.
        view.Refresh();
        foreach (var row in selected)
            if (view.Contains(row) && !ConnectionsGrid.SelectedItems.Contains(row)) ConnectionsGrid.SelectedItems.Add(row);
        if (current.IsValid && view.Contains(current.Item)) ConnectionsGrid.CurrentCell = current;
        ConnectionsGrid.UpdateLayout();
        if (scroll is not null)
        {
            var anchorIndex = anchor is null ? -1 : view.IndexOf(anchor);
            var offset = vertical > 0 && anchorIndex >= 0
                ? anchorIndex * ConnectionsGrid.RowHeight + vertical % ConnectionsGrid.RowHeight : vertical;
            scroll.ScrollToVerticalOffset(offset);
            scroll.ScrollToHorizontalOffset(horizontal);
        }
        RefreshJournalRouteControls();
    }

    private bool MatchesConnectionSearch(object value)
    {
        var row = (ConnectionTableRow)value;
        var search = ConnectionSearchBox.Text.Trim();
        return search.Length == 0 || $"{row.Process} {row.Destination} {row.Network} {row.RuleLabel} {row.RawRule} {row.Route}"
            .Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private async void CheckConnection_Click(object sender, System.Windows.RoutedEventArgs e) => await CheckConnectionAsync(manual: true);

    private async Task CheckConnectionAsync(bool manual)
    {
        if (_checkingConnection || _speedTestLifetime is not null || !_core.IsRunning || _diagnostics is not { } diagnostics || _document?.VpnRoute is not { } route) return;
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
            if (_capacityServer.Length > 0 && _capacityServer != selection)
            { _downloadCapacity = null; _capacityServer = ""; RefreshBandwidthReadout(); }
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
        RefreshBandwidthReadout();
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
        _speedTestLifetime?.Cancel();
        _downloadCapacity = null;
        SpeedProgressText.Text = "";
        SpeedProgressText.Visibility = System.Windows.Visibility.Hidden;
        _capacityServer = "";
        _speedTestPort = 0;
        _diagnosticLifetime?.Cancel();
        _diagnosticLifetime?.Dispose();
        _diagnosticLifetime = null;
        _diagnostics?.Dispose();
        _diagnostics = null;
        _selectedConnection = "";
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
