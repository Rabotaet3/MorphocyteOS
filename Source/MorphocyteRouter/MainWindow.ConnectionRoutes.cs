using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private ContextMenu? _connectionRouteMenu;
    private bool _connectionTableRefreshPending;

    private void ConnectionRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || _document is null || sender is not Button { Tag: ConnectionTableRow row } button
            || row.AddressDomain is not { } domain) return;
        if (_connectionRouteMenu is { } previous) previous.IsOpen = false;
        // DataGrid refresh recreates cell buttons. Anchor to the stable grid,
        // never to a recyclable cell that WPF may place at screen (0, 0).
        var anchor = button.TransformToAncestor(ConnectionsGrid).TransformBounds(new Rect(button.RenderSize));
        var menu = new ContextMenu { PlacementTarget = ConnectionsGrid, PlacementRectangle = anchor, Placement = PlacementMode.Bottom,
            VerticalOffset = 5, MinWidth = 260, Style = (Style)FindResource("RouteMenuStyle") };
        menu.Opened += (_, _) => menu.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        var scope = new StackPanel { MaxWidth = 330 };
        TextBlock ScopeLine(string text, double size, string brush, FontWeight weight)
        {
            var line = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2) };
            line.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return line;
        }
        scope.Children.Add(ScopeLine("МАРШРУТ ДЛЯ АДРЕСА", 9, "ThemeMuted", FontWeights.SemiBold));
        scope.Children.Add(ScopeLine(domain, 13, "ThemeText", FontWeights.SemiBold));
        scope.Children.Add(ScopeLine("Только этот домен, без поддоменов", 10, "ThemeMuted", FontWeights.Normal));
        menu.Items.Add(new MenuItem { Header = scope, IsEnabled = false,
            Style = (Style)FindResource("RouteMenuHeaderStyle") });
        var pending = FindAddressOverride(_rules, domain);
        var currentRoute = pending?.Route ?? row.Route.Split(" ← ", StringSplitOptions.None)[0];
        foreach (var action in new[] { "VPN", "DIRECT", "REJECT" })
        {
            var item = new MenuItem { Header = RouteLabel(action), IsCheckable = true,
                Style = (Style)FindResource("RouteMenuItemStyle"),
                IsChecked = action == "VPN" ? currentRoute.Length > 0 && currentRoute is not ("DIRECT" or "REJECT" or "REJECT-DROP")
                    : action == "REJECT" ? currentRoute is "REJECT" or "REJECT-DROP" : currentRoute == action,
                IsEnabled = action != "VPN" || _document.HasVpnConnection && _document.VpnRoute is not null };
            // Capture the address, not a live row that may change on the next controller snapshot.
            item.Click += async (_, _) => await ChangeConnectionRouteAsync(domain, action);
            menu.Items.Add(item);
        }
        _connectionRouteMenu = menu;
        void TargetUnloaded(object? _, RoutedEventArgs __) => menu.IsOpen = false;
        void GridVisibilityChanged(object? _, DependencyPropertyChangedEventArgs __)
        {
            if (!ConnectionsGrid.IsVisible) menu.IsOpen = false;
        }
        void TableScrolled(object _, ScrollChangedEventArgs args)
        {
            if (args.VerticalChange != 0 || args.HorizontalChange != 0) menu.IsOpen = false;
        }
        button.Unloaded += TargetUnloaded;
        ConnectionsGrid.IsVisibleChanged += GridVisibilityChanged;
        ConnectionsGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(TableScrolled));
        menu.Closed += (_, _) =>
        {
            button.Unloaded -= TargetUnloaded;
            ConnectionsGrid.IsVisibleChanged -= GridVisibilityChanged;
            ConnectionsGrid.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(TableScrolled));
            if (ReferenceEquals(button.ContextMenu, menu)) button.ContextMenu = null;
            if (!ReferenceEquals(_connectionRouteMenu, menu)) return;
            _connectionRouteMenu = null;
            if (!_connectionTableRefreshPending || _closing) return;
            // Run after the menu click has captured its address/action. Coalesce
            // all snapshots received during the choice into the latest one.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_closing) FilterConnections();
            }));
        };
        button.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    internal async Task ChangeConnectionRouteAsync(string address, string action)
    {
        await RunExclusiveAsync(async () =>
        {
            if (_document is null || ConnectionAddress.Domain(address) is not { } domain) return;
            var route = ResolveRouteAction(action);
            var rule = _rules.FirstOrDefault(value => value.Kind == "DOMAIN" && value.Enabled
                && value.Value.Equals(domain, StringComparison.OrdinalIgnoreCase))
                ?? _rules.FirstOrDefault(value => value.Kind == "DOMAIN" && value.Value.Equals(domain, StringComparison.OrdinalIgnoreCase));
            if (rule is { Enabled: true, AddressOverride: true } && rule.Route == route) return;
            var previous = CaptureRuleEditState(_dirty);
            var previousDraft = _settings.GetDraft(_document.Path);
            var hadOrder = _settings.ProfileFolderOrders.TryGetValue(_document.Path, out var previousOrder);
            var previousSaved = _draftSaved;
            var undo = _undoRules.ToArray(); var redo = _redoRules.ToArray(); var history = _historyState;
            try
            {
                rule ??= new DomainRule { Kind = "DOMAIN", Value = domain };
                rule.Route = route; rule.Enabled = true; rule.AddressOverride = true;
                _rules.Remove(rule); _rules.Insert(0, rule);
                await RecordEditAsync();
            }
            catch
            {
                _folderOrder.Clear(); _folderOrder.AddRange(previous.Folders);
                ReplaceRules(previous.Rules);
                if (previousDraft is null) _settings.RemoveDraft(_document.Path);
                else _settings.ProfileDrafts[_document.Path] = previousDraft;
                if (hadOrder) _settings.ProfileFolderOrders[_document.Path] = previousOrder!;
                else _settings.ProfileFolderOrders.Remove(_document.Path);
                _undoRules.Clear(); _undoRules.AddRange(undo); _redoRules.Clear(); _redoRules.AddRange(redo); _historyState = history;
                _draftSaved = previousSaved; SetDirty(previous.Dirty);
                throw;
            }
            SetLog($"Маршрут {domain}: {RouteLabel(action)} — сохранён в черновике, без поддоменов. Для подключения изменений нажми «Применить» в журнале.");
        });
    }

    private static DomainRule? FindAddressOverride(IEnumerable<DomainRule> rules, string domain) => rules.FirstOrDefault(rule =>
        rule.Enabled && rule.AddressOverride && rule.Kind == "DOMAIN" && rule.Value.Equals(domain, StringComparison.OrdinalIgnoreCase));

    private void RefreshJournalRouteControls()
    {
        if (JournalDraftPanel is null) return;
        JournalDraftPanel.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
        JournalApplyButton.IsEnabled = _dirty && _document is not null && !_busy && !_closing;
        JournalUndoButton.IsEnabled = _undoRules.Count > 0 && !_busy && !_closing;
        static Dictionary<string, DomainRule> AddressMap(IEnumerable<DomainRule> rules) => rules
            .Where(rule => rule.Enabled && rule.AddressOverride && rule.Kind == "DOMAIN")
            .GroupBy(rule => rule.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var pendingRules = AddressMap(_rules);
        var appliedRules = AddressMap(_appliedRules);
        foreach (var row in _connectionRows)
        {
            var pending = row.AddressDomain is { } domain ? pendingRules.GetValueOrDefault(domain) : null;
            var applied = pending is null ? null : appliedRules.GetValueOrDefault(pending.Value);
            row.SetPendingRoute(_dirty && pending is not null && (applied is null || applied.Route != pending.Route)
                ? "Черновик: " + (pending.IsVpnRoute ? "VPN" : RouteLabel(pending.Route)) : "");
        }
    }

    private async void JournalUndo_Click(object sender, RoutedEventArgs e) => await UndoRuleEditAsync();
}
