using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MorphocyteRouter;

internal static class DialogChrome
{
    internal static Window CreateWindow(Window? owner, string title, double width, double height,
        double? minWidth = null, double? minHeight = null, ResizeMode resizeMode = ResizeMode.NoResize) => new()
    {
        Title = title,
        Owner = owner,
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        Width = width,
        Height = height,
        MinWidth = minWidth ?? width,
        MinHeight = minHeight ?? height,
        ResizeMode = resizeMode,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        Foreground = ThemeManager.GetBrush("ThemeText"),
        ShowInTaskbar = false
    };

    internal static Border BuildFrame(Window dialog, string title, UIElement content, UIElement? footer = null)
    {
        var close = MakeButton("×", false);
        close.MinWidth = 0;
        close.Width = 34;
        close.Height = 32;
        close.Padding = new Thickness(0);
        close.FontSize = 17;
        close.Click += (_, _) => dialog.Close();

        var headerGrid = new Grid { Margin = new Thickness(20, 12, 12, 9), MinHeight = 34 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caption = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "ThemeAccent");
        headerGrid.Children.Add(caption);
        Grid.SetColumn(close, 1);
        headerGrid.Children.Add(close);
        headerGrid.MouseLeftButtonDown += (_, e) =>
        {
            if (FindButtonAncestor(e.OriginalSource as DependencyObject) is null && e.ButtonState == MouseButtonState.Pressed)
                dialog.DragMove();
        };

        var header = new DockPanel();
        DockPanel.SetDock(headerGrid, Dock.Top);
        header.Children.Add(headerGrid);
        var accentLine = new Border { Height = 1, Margin = new Thickness(16, 0, 16, 0) };
        accentLine.SetResourceReference(Border.BackgroundProperty, "ThemeAccentGradient");
        DockPanel.SetDock(accentLine, Dock.Bottom);
        header.Children.Add(accentLine);

        var shell = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        shell.Children.Add(header);
        if (footer is not null)
        {
            var footerBorder = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 11, 20, 15),
                Child = footer
            };
            footerBorder.SetResourceReference(Border.BorderBrushProperty, "ThemePanelBorder");
            DockPanel.SetDock(footerBorder, Dock.Bottom);
            shell.Children.Add(footerBorder);
        }
        shell.Children.Add(content);

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            SnapsToDevicePixels = true,
            Child = new Border
            {
                Margin = new Thickness(1),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Child = shell
            }
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "ThemeAccentGradient");
        frame.SetResourceReference(Border.BackgroundProperty, "ThemePanelGradient");
        ((Border)frame.Child).SetResourceReference(Border.BorderBrushProperty, "ThemePanelBorder");
        ((Border)frame.Child).SetResourceReference(Border.BackgroundProperty, "ThemePanelSolid");
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); }
        };
        UiScaleManager.Apply(dialog, frame, UiScaleManager.CurrentScale);
        return frame;
    }

    internal static Button MakeButton(string caption, bool primary)
    {
        const string template = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="Button">
              <Border x:Name="Surface" CornerRadius="9" Background="{TemplateBinding Background}"
                      BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                      Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Surface" Property="Background" Value="{DynamicResource HoverBackground}"/>
                  <Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource ThemeAccent}"/>
                  <Setter TargetName="Surface" Property="Opacity" Value="0.94"/>
                </Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True">
                  <Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource ThemeAccent}"/>
                </Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.78"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """;
        var button = new Button
        {
            Content = caption,
            Height = 38,
            MinWidth = primary ? 112 : 94,
            Padding = new Thickness(13, 5, 13, 5),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            BorderThickness = new Thickness(1),
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                template.Replace("HoverBackground", primary ? "ThemeAccentGradient" : "ThemeButtonHover", StringComparison.Ordinal))
        };
        button.SetResourceReference(Control.ForegroundProperty, primary ? "ThemeButtonPrimaryText" : "ThemeText");
        button.SetResourceReference(Control.BackgroundProperty, primary ? "ThemeAccentGradient" : "ThemeButton");
        button.SetResourceReference(Control.BorderBrushProperty, primary ? "ThemeAccent" : "ThemeButtonBorder");
        return button;
    }

    internal static void ShowModal(Window? owner, Window dialog)
    {
        SetBackdrop(owner, true);
        dialog.Closed += (_, _) => SetBackdrop(owner, false);
        dialog.ShowDialog();
    }

    internal static void ShowModeless(Window owner, Window dialog)
    {
        SetBackdrop(owner, true);
        dialog.Closed += (_, _) => SetBackdrop(owner, false);
        dialog.Show();
        dialog.Activate();
    }

    private static Button? FindButtonAncestor(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is Button button) return button;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void SetBackdrop(Window? owner, bool visible)
    {
        if (owner is MainWindow main) main.SetDialogBackdrop(visible);
    }
}
