using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MorphocyteRouter;

internal static class FolderNameDialog
{
    public static Task<string?> ShowAsync(Window owner, string firstRule, string secondRule)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? result = null;
        var input = new TextBox
        {
            Height = 40,
            FontSize = 14,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 8, 11, 7),
            Text = "Новая папка",
            Margin = new Thickness(0, 13, 0, 14)
        };
        input.SetResourceReference(Control.ForegroundProperty, "ThemeText");
        input.SetResourceReference(Control.BackgroundProperty, "ThemeInputBackground");
        input.SetResourceReference(Control.BorderBrushProperty, "ThemeInputBorder");
        input.SetResourceReference(TextBox.CaretBrushProperty, "ThemeAccent");

        var dialog = DialogChrome.CreateWindow(owner, "Новая папка", 470, 270, 470, 270);
        var accept = DialogChrome.MakeButton("СОЗДАТЬ ПАПКУ", true);
        accept.MinWidth = 150;
        accept.IsDefault = true;
        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => dialog.Close();
        accept.Click += (_, _) =>
        {
            var name = input.Text.Trim();
            if (name.Length is 0 or > 40 || name.Any(char.IsControl) || name.Contains('/') || name.Contains('\\')
                || name.Equals("ОБЩИЕ ПРАВИЛА", StringComparison.OrdinalIgnoreCase))
            {
                input.SetResourceReference(Control.BorderBrushProperty, "ThemeDanger");
                input.Focus();
                return;
            }
            result = name;
            dialog.Close();
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };

        var content = new StackPanel { Margin = new Thickness(22, 14, 22, 16) };
        var description = new TextBlock
        {
            Text = $"Объединить правила «{firstRule}» и «{secondRule}» в папку.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 2)
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        content.Children.Add(description);
        content.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        dialog.Content = DialogChrome.BuildFrame(dialog, "НОВАЯ ПАПКА ПРАВИЛ", content, actions);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.Closed += (_, _) => completion.TrySetResult(result);
        DialogChrome.ShowModeless(owner, dialog);
        return completion.Task;
    }
}
