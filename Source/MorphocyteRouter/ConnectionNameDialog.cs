using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MorphocyteRouter;

internal static class ConnectionNameDialog
{
    internal static string? Show(Window owner)
    {
        var dialog = DialogChrome.CreateWindow(owner, "НАЗВАНИЕ ПОДКЛЮЧЕНИЯ", 500, 310, 480, 300);
        string? result = null;
        var description = new TextBlock
        {
            Text = "В подключении нет названия. Как его назвать?",
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
        var input = new TextBox
        {
            Name = "ConnectionNameInput", Height = 40, FontSize = 14, Padding = new Thickness(11, 8, 11, 7),
            ToolTip = "Например: Нидерланды или Мой VPN", MaxLength = 256, Margin = new Thickness(0, 0, 0, 10)
        };
        input.SetResourceReference(Control.ForegroundProperty, "ThemeText");
        input.SetResourceReference(Control.BackgroundProperty, "ThemeInputBackground");
        input.SetResourceReference(Control.BorderBrushProperty, "ThemeInputBorder");
        input.SetResourceReference(TextBox.CaretBrushProperty, "ThemeAccent");
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 30 };
        status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeAccent");
        var body = new StackPanel { Margin = new Thickness(22, 16, 22, 12) };
        body.Children.Add(description); body.Children.Add(input); body.Children.Add(status);
        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => dialog.Close();
        var accept = DialogChrome.MakeButton("ПРОДОЛЖИТЬ", true);
        accept.IsDefault = true;
        accept.Click += (_, _) =>
        {
            try { result = ProfileImporter.ValidateConnectionName(input.Text); dialog.Close(); }
            catch (InvalidDataException ex) { status.Text = ex.Message; input.Focus(); }
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(cancel); footer.Children.Add(accept);
        dialog.Content = DialogChrome.BuildFrame(dialog, "НАЗВАНИЕ ПОДКЛЮЧЕНИЯ", body, footer);
        dialog.Loaded += (_, _) => input.Focus();
        DialogChrome.ShowModal(owner, dialog);
        return result;
    }
}
