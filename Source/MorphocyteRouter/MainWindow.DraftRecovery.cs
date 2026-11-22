using System.Windows;
using System.Windows.Controls;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private sealed record DraftRuleChange(int Index, DomainRule Rule, string Description);

    private async void RecoverDraft_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(async () =>
        {
            if (_document is not { } document) return;
            var versions = _settings.GetArchivedDrafts(document.Path);
            if (versions.Count == 0) return;
            var current = _rules.Select(rule => rule.Copy()).ToList();
            const string title = "СРАВНИТЬ ЧЕРНОВИК";
            var dialog = DialogChrome.CreateWindow(this, title, 740, 510, 520, 370, ResizeMode.CanResize);
            var version = new ComboBox { Margin = new Thickness(0, 8, 0, 9) };
            foreach (var old in versions)
                version.Items.Add(old.ArchivedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "Сохранённый черновик");
            System.Windows.Automation.AutomationProperties.SetName(version, "Версия черновика");
            var explanation = new TextBlock
            {
                Text = "Выбери правила из старого черновика. Они будут добавлены к текущим правкам. Для обновления YAML нажми «Применить в YAML» после сравнения.",
                TextWrapping = TextWrapping.Wrap
            };
            explanation.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
            var rows = new StackPanel();
            var scroller = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new DockPanel { Margin = new Thickness(18, 12, 18, 12) };
            DockPanel.SetDock(explanation, Dock.Top); body.Children.Add(explanation);
            DockPanel.SetDock(version, Dock.Top); body.Children.Add(version);
            body.Children.Add(scroller);
            var cancel = DialogChrome.MakeButton("ЗАКРЫТЬ", false);
            var restore = DialogChrome.MakeButton("ПЕРЕНЕСТИ В ЧЕРНОВИК", true);
            restore.Margin = new Thickness(8, 0, 0, 0);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            footer.Children.Add(cancel); footer.Children.Add(restore);
            DraftRuleChange[]? selected = null;
            cancel.Click += (_, _) => dialog.Close();
            restore.Click += (_, _) =>
            {
                selected = rows.Children.OfType<CheckBox>().Where(item => item.IsChecked == true)
                    .Select(item => (DraftRuleChange)item.Tag).ToArray();
                dialog.Close();
            };
            version.SelectionChanged += (_, _) =>
            {
                rows.Children.Clear();
                restore.IsEnabled = false;
                if (version.SelectedIndex < 0) return;
                var changes = CompareDraftRules(versions[version.SelectedIndex].Rules, current, document.Routes);
                if (changes.Count == 0)
                {
                    var empty = new TextBlock { Text = "В этой версии нет отличающихся правил, которые можно перенести.", TextWrapping = TextWrapping.Wrap };
                    empty.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
                    rows.Children.Add(empty);
                }
                foreach (var change in changes)
                {
                    var text = new TextBlock { Text = change.Description, TextWrapping = TextWrapping.Wrap };
                    text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
                    var option = new CheckBox { Content = text, Tag = change, Margin = new Thickness(0, 4, 0, 7) };
                    option.Checked += (_, _) => restore.IsEnabled = true;
                    option.Unchecked += (_, _) => restore.IsEnabled = rows.Children.OfType<CheckBox>().Any(item => item.IsChecked == true);
                    rows.Children.Add(option);
                }
            };
            version.SelectedIndex = 0;
            dialog.Content = DialogChrome.BuildFrame(dialog, title, body, footer);
            DialogChrome.ShowModal(this, dialog);
            if (selected is not { Length: > 0 } || _closing) return;
            foreach (var change in selected)
            {
                var rule = change.Rule.Copy();
                rule.SourceIndex = change.Index < 0 ? -1 : current[change.Index].SourceIndex;
                if (change.Index < 0) current.Add(rule);
                else current[change.Index] = rule;
            }
            ReplaceRules(current);
            await RecordEditAsync();
            SetLog($"Перенесено правил в черновик: {selected.Length}. YAML можно обновить кнопкой «Применить в YAML».");
        });
    }

    private static IReadOnlyList<DraftRuleChange> CompareDraftRules(IReadOnlyList<DomainRule> archived,
        IReadOnlyList<DomainRule> current, IReadOnlyList<string> routes)
    {
        var live = current.Select((rule, index) => (rule, index)).ToLookup(item => EditIdentity(item.rule), StringComparer.OrdinalIgnoreCase);
        var changes = new List<DraftRuleChange>();
        foreach (var group in archived.GroupBy(EditIdentity, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Take(2).Count() != 1) continue;
            var rule = group.First();
            if (rule.Kind is not ("DOMAIN" or "DOMAIN-SUFFIX" or "DOMAIN-KEYWORD" or "PROCESS-NAME")) continue;
            if (rule.Route is not ("DIRECT" or "REJECT" or "REJECT-DROP") && !routes.Contains(rule.Route)) continue;
            var matches = live[group.Key].Take(2).ToArray();
            if (matches.Length > 1) continue;
            var match = matches.SingleOrDefault();
            if (match.rule is not null && EditValue(match.rule) == EditValue(rule)) continue;
            static string Describe(DomainRule value)
                => $"{value.RouteLabel} · {(value.Enabled ? "включено" : "выключено")} · {value.FolderLabel}{value.Extra}";
            var before = match.rule is null ? "Правила нет" : Describe(match.rule);
            changes.Add(new(match.rule is null ? -1 : match.index, rule,
                $"{rule.KindLabel} · {rule.Value}\nСейчас: {before}\nВ черновике: {Describe(rule)}"));
        }
        return changes;
    }
}
