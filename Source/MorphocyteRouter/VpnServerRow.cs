using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MorphocyteRouter;

internal sealed class VpnServerItem(string name, VpnServerPresentation presentation, bool showFlag) : INotifyPropertyChanged
{
    internal string Name { get; } = name;
    internal VpnServerPresentation Presentation { get; } = presentation;
    internal bool ShowFlag { get; } = showFlag;
    internal bool Selected { get; private set; }
    internal string LatencyText { get; private set; } = "—";
    internal string LatencyHint { get; private set; } = "Нажми PING для проверки.";
    internal bool HasDelay { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetSelected(bool selected)
    {
        if (Selected == selected) return;
        Selected = selected;
        PropertyChanged?.Invoke(this, new(nameof(Selected)));
    }

    internal void SetLatency(bool pending, bool measured = false, int? delay = null)
    {
        HasDelay = delay.HasValue;
        LatencyText = pending ? "…" : delay is { } ms ? $"{ms} мс" : measured ? "Недоступен" : "—";
        LatencyHint = pending ? "Проверяется…" : HasDelay ? "Задержка HTTPS через этот сервер."
            : measured ? "Сервер не ответил на проверочные HTTPS-запросы." : "Нажми PING для проверки.";
        PropertyChanged?.Invoke(this, new(nameof(LatencyText)));
    }
}

public sealed class VpnServerRow : Button
{
    private readonly ColumnDefinition _flagColumn = new() { Width = new GridLength(36) };
    private readonly ContentControl _flag = new() { Name = "VpnServerCountry", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { Name = "VpnServerTitle", FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { Name = "VpnServerDetails", FontSize = 10, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _latency = new()
    {
        Name = "VpnServerLatency", MinWidth = 76, FontSize = 12, Margin = new Thickness(10, 0, 0, 0),
        TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
    };

    public VpnServerRow()
    {
        SetResourceReference(StyleProperty, "FlatButton");
        Tag = "";
        MinHeight = 46;
        Padding = new Thickness(11, 6, 11, 6);
        Margin = new Thickness(0, 0, 5, 4);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        SetResourceReference(TemplateProperty, "VpnServerRowTemplate");
        _details.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        var content = new Grid();
        content.ColumnDefinitions.Add(_flagColumn);
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(_title); labels.Children.Add(_details);
        content.Children.Add(_flag); Grid.SetColumn(labels, 1); content.Children.Add(labels);
        Grid.SetColumn(_latency, 2); content.Children.Add(_latency);
        Content = content;
        DataContextChanged += ModelChanged;
    }

    private void ModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is VpnServerItem oldItem) PropertyChangedEventManager.RemoveHandler(oldItem, RenderChange, "");
        if (e.NewValue is not VpnServerItem item) return;
        PropertyChangedEventManager.AddHandler(item, RenderChange, "");
        var presentation = item.Presentation;
        Tag = item.Name;
        ToolTip = item.Name;
        AutomationProperties.SetName(this, presentation.Title);
        _flagColumn.Width = new GridLength(item.ShowFlag ? 36 : 0);
        _flag.Visibility = item.ShowFlag ? Visibility.Visible : Visibility.Collapsed;
        _flag.Content = item.ShowFlag ? CountryBadge.Create(presentation.CountryCode) : null;
        _title.Text = presentation.Title;
        _details.Text = presentation.Details;
        _details.Visibility = presentation.Details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderChange(item, new PropertyChangedEventArgs(nameof(VpnServerItem.Selected)));
        RenderChange(item, new PropertyChangedEventArgs(nameof(VpnServerItem.LatencyText)));
    }

    private void RenderChange(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VpnServerItem item) return;
        if (e.PropertyName == nameof(VpnServerItem.Selected))
        {
            SetResourceReference(BorderBrushProperty, item.Selected ? "ThemeAccent" : "ThemeButtonBorder");
            SetResourceReference(BackgroundProperty, item.Selected ? "ThemeSurfaceSelected" : "ThemeButton");
            AutomationProperties.SetItemStatus(this, item.Selected ? "Выбран" : "");
        }
        else
        {
            _latency.Text = item.LatencyText;
            _latency.ToolTip = item.LatencyHint;
            _latency.SetResourceReference(TextBlock.ForegroundProperty, item.HasDelay ? "ThemeAccent" : "ThemeMuted");
        }
    }
}
