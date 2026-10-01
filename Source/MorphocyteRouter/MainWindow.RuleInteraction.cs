using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private Point _dragStart, _dragAnchor;
    private DomainRule? _dragRule;
    private ListViewItem? _dragSourceItem;
    private FrameworkElement? _dragSourceElement;
    private string? _draggedFolderName;
    private DomainRule? _selectionAnchorRule;
    private DomainRule[] _draggedRules = Array.Empty<DomainRule>();
    private bool _ruleDragHandlerHooked, _dragActive, _pendingClickToggle;
    private Image? _dragCard;
    private Border? _dragCountBadge, _activeDropHeader, _folderInsertMarker;
    private Brush? _dropHeaderBackground, _dropHeaderBorder;
    private readonly List<(UIElement Item, double Opacity)> _dimmedDragItems = new();

    private void RulesList_Loaded(object sender, RoutedEventArgs e)
    {
        _rulesScrollViewer ??= FindVisualDescendant<ScrollViewer>(RulesList);
        if (_ruleDragHandlerHooked) return;
        RulesList.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(RulesList_PreviewMouseMove), true);
        RulesList.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(RulesList_PreviewMouseUp), true);
        _ruleDragHandlerHooked = true;
    }

    private void RulesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _closing) return;
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<CheckBox>(source) is not null || FindAncestor<Button>(source) is not null)
        {
            _dragRule = null;
            _dragSourceElement = null;
            return;
        }
        var folderSurface = FindNamedBorder(source, "FolderDropSurface");
        var folderGroup = FindAncestor<GroupItem>(source)?.Content as CollectionViewGroup;
        if (folderSurface is not null && folderGroup is not null
            && folderGroup.Name?.ToString() is { Length: > 0 } folderName)
        {
            _draggedFolderName = folderName;
            _draggedRules = folderGroup.Items.OfType<DomainRule>().ToArray();
            _dragRule = _draggedRules.FirstOrDefault();
            _dragSourceItem = null;
            _dragSourceElement = folderSurface;
            _pendingClickToggle = false;
            _dragStart = e.GetPosition(RuleListHost);
            _dragAnchor = e.GetPosition(folderSurface);
            e.Handled = true;
            return;
        }
        if (FindAncestor<ListViewItem>(source) is not { DataContext: DomainRule rule } item)
        {
            _dragRule = null;
            _dragSourceElement = null;
            return;
        }
        RulesList.Focus();
        var modifiers = Keyboard.Modifiers;
        _pendingClickToggle = modifiers == ModifierKeys.None && RulesList.SelectedItems.Contains(rule);
        SelectRuleForPointer(rule, modifiers);
        _dragRule = RulesList.SelectedItems.Contains(rule) ? rule : null;
        _dragSourceItem = item;
        _dragSourceElement = item;
        _draggedFolderName = null;
        _dragStart = e.GetPosition(RuleListHost);
        _dragAnchor = e.GetPosition(item);
        e.Handled = true;
    }

    private void SelectRuleForPointer(DomainRule rule, bool control) =>
        SelectRuleForPointer(rule, control ? ModifierKeys.Control : ModifierKeys.None);

    private void SelectRuleForPointer(DomainRule rule, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            var anchor = _selectionAnchorRule is not null && RulesList.Items.Contains(_selectionAnchorRule)
                ? _selectionAnchorRule : rule;
            var anchorIndex = RulesList.Items.IndexOf(anchor);
            var currentIndex = RulesList.Items.IndexOf(rule);
            if (anchorIndex < 0 || currentIndex < 0) return;
            if ((modifiers & ModifierKeys.Control) == 0) RulesList.SelectedItems.Clear();
            for (var index = Math.Min(anchorIndex, currentIndex); index <= Math.Max(anchorIndex, currentIndex); index++)
                if (RulesList.Items[index] is DomainRule rangeRule && !RulesList.SelectedItems.Contains(rangeRule))
                    RulesList.SelectedItems.Add(rangeRule);
        }
        else if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (RulesList.SelectedItems.Contains(rule)) RulesList.SelectedItems.Remove(rule);
            else RulesList.SelectedItems.Add(rule);
            _selectionAnchorRule = rule;
        }
        else
        {
            // Keep the group intact until mouse-up: a press on any selected row may
            // start a drag of the complete selection rather than a new single selection.
            if (!RulesList.SelectedItems.Contains(rule))
            {
                RulesList.SelectedItems.Clear();
                RulesList.SelectedItems.Add(rule);
            }
            _selectionAnchorRule = rule;
        }
    }

    private void RestoreRuleSelection(IEnumerable<DomainRule> selected)
    {
        RulesList.SelectedItems.Clear();
        foreach (var rule in selected)
            if (_rules.Contains(rule) && RulesList.Items.Contains(rule)) RulesList.SelectedItems.Add(rule);
    }

    private void RulesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragRule is null || _dragSourceElement is null) return;
        var point = e.GetPosition(RuleListHost);
        if (!_dragActive)
        {
            if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!BeginRuleDrag(_dragSourceElement)) return;
        }
        UpdateRuleDrag(point);
        e.Handled = true;
    }

    private bool BeginRuleDrag(FrameworkElement item)
    {
        if (!PrepareRuleDrag(item)) return false;
        if (Mouse.Capture(RulesList, CaptureMode.Element)) return true;
        CancelRuleDrag();
        return false;
    }

    private bool PrepareRuleDrag(FrameworkElement item)
    {
        if (_dragRule is null || item.ActualWidth < 1 || item.ActualHeight < 1) return false;
        if (string.IsNullOrEmpty(_draggedFolderName))
        {
            _draggedRules = RulesList.SelectedItems.OfType<DomainRule>().ToArray();
            if (!_draggedRules.Contains(_dragRule)) _draggedRules = new[] { _dragRule };
        }

        // Snapshot the actual row before dimming its placeholder. No separate popup or caption.
        var dpi = VisualTreeHelper.GetDpi(item);
        var size = new Size(item.ActualWidth, item.ActualHeight);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(item) { Stretch = Stretch.Fill }, null, new Rect(size));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(size.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        _dragCard = new Image
        {
            Source = bitmap, Width = size.Width, Height = size.Height, Opacity = 0.97,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 3, Opacity = 0.45 }
        };
        RuleDragOverlay.Children.Add(_dragCard);
        if (_draggedRules.Length > 1)
        {
            _dragCountBadge = new Border
            {
                Background = ThemeManager.GetBrush("ThemeAccent"), CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 3, 7, 3),
                Child = new TextBlock { Text = _draggedRules.Length.ToString(), Foreground = ThemeManager.GetBrush("ThemeButtonPrimaryText"), FontSize = 11, FontWeight = FontWeights.SemiBold }
            };
            RuleDragOverlay.Children.Add(_dragCountBadge);
        }
        if (_draggedFolderName is not null)
        {
            _dimmedDragItems.Add((item, item.Opacity));
            item.SetCurrentValue(OpacityProperty, 0.25);
        }
        foreach (var container in VisualDescendants<ListViewItem>(RulesList))
        {
            if (container.DataContext is not DomainRule member || !_draggedRules.Contains(member)) continue;
            _dimmedDragItems.Add((container, container.Opacity));
            container.SetCurrentValue(OpacityProperty, 0.25);
        }
        _dragActive = true;
        RuleDragOverlay.Visibility = Visibility.Visible;
        RulesList.Cursor = Cursors.SizeAll;
        return true;
    }

    private void UpdateRuleDrag(Point point)
    {
        if (!_dragActive || _dragCard is null) return;
        var left = Math.Clamp(point.X - _dragAnchor.X, 0, Math.Max(0, RuleListHost.ActualWidth - _dragCard.Width));
        var top = Math.Clamp(point.Y - _dragAnchor.Y, 0, Math.Max(0, RuleListHost.ActualHeight - _dragCard.Height));
        Canvas.SetLeft(_dragCard, left);
        Canvas.SetTop(_dragCard, top);
        if (_dragCountBadge is not null)
        {
            Canvas.SetLeft(_dragCountBadge, left + Math.Max(0, _dragCard.Width - 52));
            Canvas.SetTop(_dragCountBadge, top + 3);
        }
        var hit = RuleDropHit(point);
        var targetRule = FindAncestor<ListViewItem>(hit)?.DataContext as DomainRule;
        var targetGroup = FindAncestor<GroupItem>(hit);
        var targetFolderHeader = targetGroup is null ? null
            : VisualDescendants<Border>(targetGroup).FirstOrDefault(border => border.Name == "FolderDropSurface");
        var surface = _draggedFolderName is not null
            ? targetFolderHeader
            : targetRule is not null && !_draggedRules.Contains(targetRule)
                ? FindNamedBorder(hit, "RuleSurface") : targetRule is null ? FindNamedBorder(hit, "FolderDropSurface") : null;
        HighlightDropSurface(surface);
        if (_draggedFolderName is not null && targetGroup is not null && targetFolderHeader is not null)
        {
            _folderInsertMarker ??= new Border
            {
                Height = 3,
                Background = ThemeManager.GetBrush("ThemeAccent"),
                CornerRadius = new CornerRadius(2),
                Effect = new DropShadowEffect { Color = ((SolidColorBrush)ThemeManager.GetBrush("ThemeAccent")).Color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.8 },
                IsHitTestVisible = false
            };
            if (!RuleDragOverlay.Children.Contains(_folderInsertMarker)) RuleDragOverlay.Children.Add(_folderInsertMarker);
            _folderInsertMarker.Visibility = Visibility.Visible;
            var headerBounds = targetFolderHeader.TransformToAncestor(RuleListHost)
                .TransformBounds(new Rect(0, 0, targetFolderHeader.ActualWidth, targetFolderHeader.ActualHeight));
            var after = point.Y >= headerBounds.Top + headerBounds.Height / 2;
            Canvas.SetLeft(_folderInsertMarker, 6);
            Canvas.SetTop(_folderInsertMarker, Math.Clamp(after ? headerBounds.Bottom - 1 : headerBounds.Top - 2, 0, RuleListHost.ActualHeight - 3));
            _folderInsertMarker.Width = Math.Max(0, RuleListHost.ActualWidth - 12);
            Panel.SetZIndex(_folderInsertMarker, 20);
        }
        else if (_folderInsertMarker is not null)
            _folderInsertMarker.Visibility = Visibility.Collapsed;
    }

    private DependencyObject? RuleDropHit(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X >= RuleListHost.ActualWidth || point.Y >= RuleListHost.ActualHeight) return null;
        var hit = RulesList.InputHitTest(RuleListHost.TranslatePoint(point, RulesList)) as DependencyObject;
        return FindAncestor<ScrollBar>(hit) is null ? hit : null;
    }

    private async void RulesList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!_dragActive)
        {
            if (_pendingClickToggle && _dragRule is not null && RulesList.SelectedItems.Contains(_dragRule)
                && Keyboard.Modifiers == ModifierKeys.None)
                RulesList.SelectedItems.Remove(_dragRule);
            _pendingClickToggle = false;
            _dragRule = null;
            _dragSourceItem = null;
            _dragSourceElement = null;
            _draggedFolderName = null;
            return;
        }
        e.Handled = true;
        await FinishRuleDragAsync(e.GetPosition(RuleListHost));
    }

    private async Task FinishRuleDragAsync(Point point)
    {
        var hit = RuleDropHit(point);
        var targetRule = FindAncestor<ListViewItem>(hit)?.DataContext as DomainRule;
        var targetGroup = FindAncestor<GroupItem>(hit)?.Content as CollectionViewGroup;
        var moving = _draggedRules.ToArray();
        var draggedGroupName = _draggedFolderName;
        var targetGroupName = targetGroup?.Name?.ToString() ?? targetRule?.FolderLabel;
        var targetGroupContainer = FindAncestor<GroupItem>(hit);
        var afterTargetGroup = false;
        var targetFolderHeader = targetGroupContainer is null ? null
            : VisualDescendants<Border>(targetGroupContainer).FirstOrDefault(border => border.Name == "FolderDropSurface");
        if (targetFolderHeader is not null && targetFolderHeader.ActualHeight > 0)
        {
            var bounds = targetFolderHeader.TransformToAncestor(RuleListHost)
                .TransformBounds(new Rect(0, 0, targetFolderHeader.ActualWidth, targetFolderHeader.ActualHeight));
            afterTargetGroup = point.Y >= bounds.Top + bounds.Height / 2;
        }
        CancelRuleDrag();
        if (hit is null) return;
        if (draggedGroupName is not null && targetGroupName is not null)
            await RunExclusiveAsync(() => ReorderFolderGroupAsync(draggedGroupName, targetGroupName, afterTargetGroup));
        else
            await RunExclusiveAsync(() => MoveDraggedRulesAsync(moving, targetRule, targetGroup));
    }

    private void CancelRuleDrag()
    {
        _dragActive = false;
        _dragRule = null;
        _dragSourceItem = null;
        _dragSourceElement = null;
        _draggedFolderName = null;
        _pendingClickToggle = false;
        _draggedRules = Array.Empty<DomainRule>();
        if (ReferenceEquals(Mouse.Captured, RulesList)) Mouse.Capture(null);
        foreach (var (item, opacity) in _dimmedDragItems) item.SetCurrentValue(OpacityProperty, opacity);
        _dimmedDragItems.Clear();
        RuleDragOverlay.Visibility = Visibility.Collapsed;
        RuleDragOverlay.Children.Clear();
        _dragCard = null;
        _dragCountBadge = null;
        _folderInsertMarker = null;
        RulesList.ClearValue(CursorProperty);
        HighlightDropSurface(null);
    }

    private void RulesList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragActive && !ReferenceEquals(Mouse.Captured, RulesList)) CancelRuleDrag();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_dragActive) return;
        CancelRuleDrag();
        e.Handled = true;
    }

    private void RulesList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_dragActive) return;
        ScrollRulesDuringDrag(e.Delta);
        e.Handled = true;
    }

    private void ScrollRulesDuringDrag(int delta)
    {
        _rulesScrollViewer ??= FindVisualDescendant<ScrollViewer>(RulesList);
        if (_rulesScrollViewer is null) return;
        _rulesScrollViewer.ScrollToVerticalOffset(_rulesScrollViewer.VerticalOffset - delta / 2d);
        RulesList.UpdateLayout();
        UpdateRuleDrag(Mouse.GetPosition(RuleListHost));
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static Border? FindNamedBorder(DependencyObject? hit, string name)
    {
        while (hit is not null)
        {
            if (hit is Border border && border.Name == name) return border;
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        return null;
    }

    private void HighlightDropSurface(Border? surface)
    {
        if (ReferenceEquals(surface, _activeDropHeader)) return;
        if (_activeDropHeader is not null)
        {
            _activeDropHeader.SetCurrentValue(Border.BackgroundProperty, _dropHeaderBackground);
            _activeDropHeader.SetCurrentValue(Border.BorderBrushProperty, _dropHeaderBorder);
        }
        _activeDropHeader = surface;
        if (surface is null) return;
        _dropHeaderBackground = surface.Background;
        _dropHeaderBorder = surface.BorderBrush;
        surface.SetCurrentValue(Border.BackgroundProperty, ThemeManager.GetBrush("ThemeRuleSelectedHover"));
        surface.SetCurrentValue(Border.BorderBrushProperty, ThemeManager.GetBrush("ThemeAccent"));
    }

    private async Task MoveDraggedRulesAsync(DomainRule[] moving, DomainRule? targetRule, CollectionViewGroup? targetGroup, string? sourceFolder = null)
    {
        moving = moving.Where(_rules.Contains).Distinct().ToArray();
        if (moving.Length == 0 || (targetRule is not null && moving.Contains(targetRule))) return;
        string folder;
        if (targetRule is not null)
        {
            folder = targetRule.Folder;
            if (sourceFolder is not null && string.IsNullOrWhiteSpace(folder))
            {
                folder = sourceFolder;
                targetRule.Folder = folder;
            }
            else if (string.IsNullOrWhiteSpace(folder))
            {
                var caption = moving.Length == 1 ? moving[0].Value : $"{moving.Length} выбранных правил";
                folder = await FolderNameDialog.ShowAsync(this, caption, targetRule.Value) ?? "";
            }
            if (string.IsNullOrWhiteSpace(folder)) return;
            targetRule.Folder = folder;
        }
        else if (targetGroup is not null)
            folder = targetGroup.Name?.ToString() == "ОБЩИЕ ПРАВИЛА" ? "" : targetGroup.Name?.ToString() ?? "";
        else return;
        if (targetGroup is not null && sourceFolder is not null
            && string.Equals(sourceFolder, folder, StringComparison.Ordinal)) return;
        foreach (var rule in moving) rule.Folder = folder;
        await RecordEditAsync();
        RestoreRuleSelection(moving);
        SetLog($"Перемещено правил: {moving.Length}. Группа: {(string.IsNullOrWhiteSpace(folder) ? "ОБЩИЕ ПРАВИЛА" : folder)}.");
    }
}
