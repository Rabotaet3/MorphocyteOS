using System.Windows;
using System.Windows.Controls;

namespace MorphocyteRouter;

internal static class ConnectionDiagnosticDialog
{
    internal static void Show(MainWindow owner)
    {
        var dialog = DialogChrome.CreateWindow(owner, "ДИАГНОСТИКА ПОДКЛЮЧЕНИЯ", 690, 620, 580, 450, ResizeMode.CanResize);
        owner.SetDiagnosticWindow(dialog);
        var layout = new Grid { Margin = new Thickness(22, 16, 22, 16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var status = new TextBlock { Name = "DiagnosticStatusText", FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 14) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText"); layout.Children.Add(status);
        var cards = new StackPanel { Margin = new Thickness(0, 0, 5, 0) };
        var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var copy = DialogChrome.MakeButton("КОПИРОВАТЬ", false); copy.Name = "CopyDiagnosticButton";
        var repeat = DialogChrome.MakeButton("ПОВТОРИТЬ ПРОВЕРКУ", true); repeat.Name = "RepeatDiagnosticButton";
        var cancel = DialogChrome.MakeButton("ОТМЕНИТЬ ПРОВЕРКУ", false); cancel.Name = "CancelDiagnosticButton";
        foreach (var button in new[] { copy, repeat, cancel }) button.Margin = new Thickness(3);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(copy); footer.Children.Add(cancel); footer.Children.Add(repeat);
        var closed = false;
        void Update()
        {
            if (closed) return;
            status.Text = owner.DiagnosticStatus; cards.Children.Clear();
            copy.IsEnabled = owner.CanCopyDiagnostic; repeat.IsEnabled = !owner.DiagnosticRunning;
            cancel.Visibility = owner.DiagnosticRunning ? Visibility.Visible : Visibility.Collapsed;
            foreach (var step in owner.DiagnosticSteps)
            {
                var color = step.State switch { DiagnosticState.Success => "ThemeAccent", DiagnosticState.Failure => "ThemeDanger", DiagnosticState.Warning => "ThemeAccentSecondary", _ => "ThemeMuted" };
                var caption = new TextBlock { Text = step.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextWrapping = TextWrapping.Wrap };
                caption.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
                var badge = new TextBlock { Text = step.State switch { DiagnosticState.Success => "ГОТОВО", DiagnosticState.Warning => "ВНИМАНИЕ", DiagnosticState.Failure => "ОШИБКА", _ => "ИНФОРМАЦИЯ" }, FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                badge.SetResourceReference(TextBlock.ForegroundProperty, color);
                var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                heading.Children.Add(caption); Grid.SetColumn(badge, 1); heading.Children.Add(badge);
                var text = new TextBlock { Text = step.Result, FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
                text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
                var content = new StackPanel(); content.Children.Add(heading); content.Children.Add(text);
                var card = new Border { Child = content, Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "ThemeSurface"); card.SetResourceReference(Border.BorderBrushProperty, "ThemeSurfaceBorder"); cards.Children.Add(card);
            }
        }
        repeat.Click += async (_, _) => await owner.RunConnectionDiagnosticAsync();
        cancel.Click += (_, _) => owner.CancelConnectionDiagnostic();
        copy.Click += (_, _) => { if (owner.CopyConnectionDiagnostic()) status.Text = "Диагностика скопирована. Профиль и ключи не включены."; };
        owner.DiagnosticChanged += Update;
        dialog.Closed += (_, _) => { closed = true; owner.DiagnosticChanged -= Update; owner.CancelConnectionDiagnostic(); owner.SetDiagnosticWindow(null); };
        dialog.Content = DialogChrome.BuildFrame(dialog, "ДИАГНОСТИКА ПОДКЛЮЧЕНИЯ", layout, footer);
        dialog.Loaded += async (_, _) => await owner.RunConnectionDiagnosticAsync();
        Update();
        try { DialogChrome.ShowModal(owner, dialog); }
        finally { closed = true; owner.DiagnosticChanged -= Update; owner.CancelConnectionDiagnostic(); owner.SetDiagnosticWindow(null); }
    }
}
