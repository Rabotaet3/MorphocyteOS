using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MorphocyteRouter;

internal sealed class VpnServerListView : Grid, IDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly Func<string, Task<bool>> _select;
    private Func<string, CancellationToken, Task<int>>? _probe;
    private readonly List<VpnServerItem> _items;
    private readonly Dictionary<string, VpnServerItem> _itemsByName;
    private readonly ListCollectionView _view;
    private readonly Action _updateSearchState;
    private readonly Button _pingButton;
    private readonly TextBox _search;
    private readonly ScrollViewer _scroll;
    private readonly ListBox _rows;
    private readonly ServerListMotion _scrollMotion;
    private Task _checking = Task.CompletedTask;
    private CancellationTokenSource? _batch;
    private string _current;
    private bool _selecting, _disposed;

    internal event Func<Task>? ChecksFinished;
    internal (string Query, double Offset) ViewState => (_search.Text, _scroll.VerticalOffset);

    internal VpnServerListView(IReadOnlyList<string> servers, string current,
        Func<string, CancellationToken, Task<int>>? probe, Func<string, Task<bool>> select,
        IReadOnlyDictionary<string, VpnServerPresentation>? presentations = null, CancellationToken token = default)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _select = select;
        _probe = probe;
        _current = current;
        _items = MakeItems(servers, presentations).ToList();
        _itemsByName = _items.ToDictionary(item => item.Name, StringComparer.Ordinal);
        _itemsByName.GetValueOrDefault(current)?.SetSelected(true);

        _search = new TextBox { Name = "VpnServerSearch", Padding = new Thickness(11, 9, 11, 9) };
        AutomationProperties.SetName(_search, "Поиск по названию или стране");
        var searchHost = new Grid();
        var hint = new TextBlock { Text = "Поиск по названию или стране", Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        searchHost.Children.Add(_search); searchHost.Children.Add(hint);
        var ping = _pingButton = DialogChrome.MakeButton("PING", false);
        ping.Name = "VpnServerPing"; ping.Height = 36; ping.MinWidth = 112; ping.Margin = new Thickness(10, 0, 0, 0);
        ping.IsEnabled = probe is not null && _items.Count > 0;
        ping.ToolTip = "Проверить задержку HTTPS через каждый сервер.";
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(searchHost); SetColumn(ping, 1); toolbar.Children.Add(ping);

        var template = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(VpnServerRow)) };
        var query = _search.Text.Trim();
        var view = _view = new ListCollectionView(_items)
        {
            Filter = value =>
            {
                var item = (VpnServerItem)value;
                return item.Presentation.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.Presentation.CountryCode?.Equals(query, StringComparison.OrdinalIgnoreCase) == true;
            }
        };
        _rows = new ListBox { Name = "VpnServerRows", ItemTemplate = template, ItemsSource = view, IsSynchronizedWithCurrentItem = false };
        _rows.SetResourceReference(StyleProperty, "VpnServerListStyle");
        _rows.AddHandler(Button.ClickEvent, new RoutedEventHandler(Row_Click));
        _rows.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Row_KeyDown));
        _rows.ApplyTemplate();
        _scroll = (ScrollViewer)_rows.Template.FindName("VpnServerScroll", _rows);
        _scrollMotion = new ServerListMotion(_scroll,
            () => RealizedRows(_rows).Select(row => row.ActualHeight + row.Margin.Bottom).FirstOrDefault(height => height > 0, 50),
            () =>
            {
                foreach (var row in RealizedRows(_rows)) ToolTipService.SetIsEnabled(row, !_scrollMotion!.IsAnimating);
            });
        _rows.AddHandler(ToolTipOpeningEvent, new ToolTipEventHandler((_, args) =>
        {
            if (_scrollMotion.IsAnimating) args.Handled = true;
        }));
        var empty = new TextBlock { Name = "VpnServerEmpty", TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Margin = new Thickness(16), IsHitTestVisible = false };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        var viewportContent = new Grid();
        viewportContent.Children.Add(_rows); viewportContent.Children.Add(empty);
        var viewport = new Border { Name = "VpnServerViewport", CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1), Padding = new Thickness(3), Child = viewportContent };
        viewport.SetResourceReference(Border.BorderBrushProperty, "ThemeSurfaceBorder");
        viewport.SetResourceReference(Border.BackgroundProperty, "ThemeSurface");
        var problem = new TextBlock { Name = "VpnServerCount", FontSize = 12, Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        problem.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        Children.Add(toolbar); SetRow(problem, 1); Children.Add(problem); SetRow(viewport, 2); Children.Add(viewport);

        void ShowSearchState()
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = _items.Count == 0 ? "В профиле нет доступных серверов. Добавь или обнови подписку."
                : "Ничего не найдено. Попробуй другое название или страну.";
            AutomationProperties.SetHelpText(_rows, $"Найдено: {view.Count} из {_items.Count}");
        }
        _updateSearchState = ShowSearchState;
        _search.TextChanged += (_, _) =>
        {
            _scrollMotion.Cancel();
            var nextQuery = _search.Text.Trim();
            if (!string.Equals(query, nextQuery, StringComparison.OrdinalIgnoreCase))
            {
                query = nextQuery;
                view.Refresh();
                _scroll.ScrollToTop();
            }
            ShowSearchState();
        };
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || view.Count != 1) return;
            e.Handled = true;
            await ChooseAsync((VpnServerItem)view.GetItemAt(0));
        };
        ping.Click += async (_, _) =>
        {
            if (_batch is { } running) { running.Cancel(); return; }
            if (_probe is not { } pendingProbe || _disposed) return;
            _checking = CheckAllAsync(_items, ping, problem, pendingProbe);
            await _checking;
        };
        ShowSearchState();
    }

    private static IEnumerable<VpnServerItem> MakeItems(IReadOnlyList<string> servers,
        IReadOnlyDictionary<string, VpnServerPresentation>? presentations) => servers.Select(name =>
            new VpnServerItem(name, presentations?.GetValueOrDefault(name) ?? VpnServerPresentation.Describe(name, null),
                presentations is not null));

    internal void ReplaceServers(IReadOnlyList<string> names, string selected,
        IReadOnlyDictionary<string, VpnServerPresentation> presentations, Func<string, CancellationToken, Task<int>> probe)
    {
        var replacement = MakeItems(names, presentations).ToArray();
        _probe = probe;
        _pingButton.IsEnabled = replacement.Length > 0;
        if (_items.Select(item => (item.Name, item.Presentation)).SequenceEqual(
            replacement.Select(item => (item.Name, item.Presentation))))
        {
            SetCurrent(selected);
            foreach (var item in _items) item.SetLatency(pending: false);
            return;
        }

        // Keep the virtualizing viewport, focus and search control attached to
        // the window. Publish a single refresh of the bound rows.
        _scrollMotion.Cancel();
        var offset = _scroll.VerticalOffset;
        var anchor = RealizedRows(_rows).Where(row => row.IsVisible && row.DataContext is VpnServerItem)
            .Select(row => (Row: row, Top: row.TranslatePoint(new Point(), _scroll).Y))
            .Where(entry => entry.Top + entry.Row.ActualHeight > _scroll.Padding.Top)
            .OrderBy(entry => entry.Top).FirstOrDefault();
        var anchorName = (anchor.Row?.DataContext as VpnServerItem)?.Name;
        _items.Clear();
        _items.AddRange(replacement);
        _itemsByName.Clear();
        foreach (var item in replacement) _itemsByName.Add(item.Name, item);
        _current = selected;
        _itemsByName.GetValueOrDefault(selected)?.SetSelected(true);
        _view.Refresh();
        if (anchorName is not null && _itemsByName.TryGetValue(anchorName, out var visible) && _view.Contains(visible))
        {
            _rows.ScrollIntoView(visible);
            _rows.UpdateLayout();
            if (_rows.ItemContainerGenerator.ContainerFromItem(visible) is { } container
                && RealizedRows(container).FirstOrDefault() is { } row)
            {
                var delta = row.TranslatePoint(new Point(), _scroll).Y - anchor.Top;
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + delta);
                _rows.UpdateLayout();
            }
            else _scroll.ScrollToVerticalOffset(offset);
        }
        else _scroll.ScrollToVerticalOffset(offset);
        _updateSearchState();
    }

    private static IEnumerable<VpnServerRow> RealizedRows(DependencyObject element)
    {
        if (element is VpnServerRow row) { yield return row; yield break; }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            foreach (var child in RealizedRows(VisualTreeHelper.GetChild(element, index))) yield return child;
    }

    private async void Row_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is VpnServerRow { DataContext: VpnServerItem item }) await ChooseAsync(item);
    }

    private void Row_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || Keyboard.FocusedElement is not VpnServerRow { DataContext: VpnServerItem item }) return;
        var index = _rows.Items.IndexOf(item) + (e.Key == Key.Up ? -1 : 1);
        if (index < 0 || index >= _rows.Items.Count) return;
        _scrollMotion.Cancel();
        _rows.ScrollIntoView(_rows.Items[index]);
        _rows.UpdateLayout();
        if (_rows.ItemContainerGenerator.ContainerFromIndex(index) is { } next) RealizedRows(next).FirstOrDefault()?.Focus();
        e.Handled = true;
    }

    private async Task ChooseAsync(VpnServerItem item)
    {
        if (_selecting || _disposed) return;
        _selecting = true;
        try { if (await _select(item.Name) && !_disposed) SetCurrent(item.Name); }
        finally { _selecting = false; }
    }

    internal void SetCurrent(string name)
    {
        if (_current == name || _disposed) return;
        _itemsByName.GetValueOrDefault(_current)?.SetSelected(false);
        _itemsByName.GetValueOrDefault(name)?.SetSelected(true);
        _current = name;
    }

    private async Task CheckAllAsync(List<VpnServerItem> items, Button ping, TextBlock problem, Func<string, CancellationToken, Task<int>> probe)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _batch = run;
        var completed = 0;
        var pending = new HashSet<VpnServerItem>(items);
        problem.Visibility = Visibility.Collapsed;
        ping.Content = $"СТОП · 0/{items.Count}";
        ping.ToolTip = "Остановить проверку. Полученные результаты сохранятся в этом окне.";
        foreach (var item in items) item.SetLatency(pending: true);
        using var concurrency = new SemaphoreSlim(4);
        try
        {
            await Task.WhenAll(items.Select(async item =>
            {
                await concurrency.WaitAsync(run.Token);
                try
                {
                    run.Token.ThrowIfCancellationRequested();
                    int? delay = null;
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(11));
                        var measured = await probe(item.Name, deadline.Token);
                        if (measured is >= 0 and <= 120000) delay = measured;
                    }
                    catch (ServerPingSetupException)
                    {
                        problem.Text = "Не удалось подготовить проверку серверов. Повтори попытку.";
                        problem.Visibility = Visibility.Visible;
                        run.Cancel();
                    }
                    catch (Exception) when (!run.IsCancellationRequested) { }
                    run.Token.ThrowIfCancellationRequested();
                    if (_disposed) return;
                    pending.Remove(item);
                    item.SetLatency(pending: false, measured: true, delay);
                    ping.Content = $"СТОП · {++completed}/{items.Count}";
                }
                finally { concurrency.Release(); }
            }));
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        finally
        {
            try
            {
                if (ChecksFinished is { } finished)
                    foreach (Func<Task> cleanup in finished.GetInvocationList()) await cleanup();
            }
            catch
            {
                problem.Text = "Не удалось завершить проверку серверов. Повтори остановку перед запуском VPN.";
                problem.Visibility = Visibility.Visible;
            }
            finally
            {
                _batch = null;
                if (!_disposed)
                {
                    foreach (var item in pending) item.SetLatency(pending: false);
                    ping.Content = "PING";
                    ping.ToolTip = "Повторить проверку задержки HTTPS через каждый сервер.";
                }
            }
        }
    }

    internal async Task StopChecksAsync()
    {
        _batch?.Cancel();
        await _checking;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scrollMotion.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
