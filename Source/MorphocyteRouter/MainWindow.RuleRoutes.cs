using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private static string RouteLabel(string route) => route switch { "DIRECT" => "Напрямую", "REJECT" => "Блокировать", _ => route };
    private string ResolveNewRuleRoute() => ResolveRouteAction((RouteCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "VPN");
    private string ResolveRouteAction(string action) => action == "VPN"
        ? _document?.VpnRoute ?? throw new InvalidOperationException("В профиле нет VPN-маршрута. Сначала импортируй подключение.")
        : action is "DIRECT" or "REJECT" ? action : throw new ArgumentException("Неизвестное действие правила.");

    private void RuleRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || sender is not Button { Tag: DomainRule rule } button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom,
            VerticalOffset = 5, Style = (Style)FindResource("RouteMenuStyle") };
        menu.Opened += (_, _) => menu.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        foreach (var action in new[] { "VPN", "DIRECT", "REJECT" })
        {
            var item = new MenuItem { Header = RouteLabel(action), IsCheckable = true, Style = (Style)FindResource("RouteMenuItemStyle"),
                IsChecked = action == "VPN" ? rule.Route is not ("DIRECT" or "REJECT") : rule.Route == action,
                IsEnabled = action != "VPN" || _document?.VpnRoute is not null };
            item.Click += async (_, _) => await ChangeRuleRouteAsync(rule, action);
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    internal async Task ChangeRuleRouteAsync(DomainRule rule, string action)
    {
        await RunExclusiveAsync(async () =>
        {
            if (!_rules.Contains(rule)) return;
            var route = ResolveRouteAction(action);
            var selected = RulesList.SelectedItems.OfType<DomainRule>().ToArray();
            var affected = selected.Contains(rule) ? selected : new[] { rule };
            if (affected.All(member => member.Route == route)) return;
            foreach (var member in affected) member.Route = route;
            await RecordEditAsync();
            RestoreRuleSelection(selected);
            SetLog($"Маршрут изменён в черновике: {RouteLabel(action)}; правил: {affected.Length}. Нажми «Применить в YAML».");
        });
    }
}
