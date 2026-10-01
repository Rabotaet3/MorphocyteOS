using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Microsoft.Win32;

namespace MorphocyteRouter;

internal static class RunningProcessDialog
{
    private sealed record RunningApp(string Executable, string Title, int Id);

    public static Task<string?> ShowAsync(Window owner)
    {
        var apps = GetVisibleApps();
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? selected = null;
        var dialog = DialogChrome.CreateWindow(owner, "Запущенные приложения", 650, 570, 560, 440);

        var search = new TextBox
        {
            Height = 38,
            Padding = new Thickness(11, 8, 11, 7),
            BorderThickness = new Thickness(1),
            ToolTip = "Фильтр по названию приложения или .exe"
        };
        search.SetResourceReference(Control.ForegroundProperty, "ThemeText");
        search.SetResourceReference(Control.BackgroundProperty, "ThemeInputBackground");
        search.SetResourceReference(Control.BorderBrushProperty, "ThemeInputBorder");
        search.SetResourceReference(TextBox.CaretBrushProperty, "ThemeAccent");

        var list = new ListBox
        {
            ItemsSource = apps,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12),
            SelectionMode = SelectionMode.Single
        };
        list.SetResourceReference(Control.BackgroundProperty, "ThemeRuleListBackground");
        list.SetResourceReference(Control.ForegroundProperty, "ThemeText");
        list.SetResourceReference(Control.BorderBrushProperty, "ThemeRuleListBorder");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        const string listResourcesXaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <DataTemplate x:Key="AppRowTemplate">
                <Grid Margin="7,1">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="72"/></Grid.ColumnDefinitions>
                  <StackPanel VerticalAlignment="Center">
                    <TextBlock Text="{Binding Executable}" Foreground="{DynamicResource ThemeText}" FontSize="12" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                    <TextBlock Text="{Binding Title}" Foreground="{DynamicResource ThemeMuted}" FontSize="10" Margin="0,2,0,0" TextTrimming="CharacterEllipsis"/>
                  </StackPanel>
                  <TextBlock Grid.Column="1" Text="{Binding Id}" Foreground="{DynamicResource ThemeDim}" FontSize="10" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                </Grid>
              </DataTemplate>
              <Style x:Key="AppRowContainer" TargetType="ListBoxItem">
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                <Setter Property="Padding" Value="0"/><Setter Property="Margin" Value="4,2"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                      <Border x:Name="Row" Background="{DynamicResource ThemeRuleBackground}" BorderBrush="{DynamicResource ThemeRuleBorder}" BorderThickness="1" CornerRadius="8" Padding="7" MinHeight="46">
                        <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Center"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource ThemeRuleHover}"/><Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource ThemeRuleHoverBorder}"/></Trigger>
                        <Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource ThemeRuleSelected}"/><Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource ThemeRuleSelectedBorder}"/></Trigger>
                        <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsSelected" Value="True"/><Condition Property="IsMouseOver" Value="True"/></MultiTrigger.Conditions><Setter TargetName="Row" Property="Background" Value="{DynamicResource ThemeRuleSelectedHover}"/><Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource ThemeAccent}"/></MultiTrigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </ResourceDictionary>
            """;
        var rowResources = (ResourceDictionary)XamlReader.Parse(listResourcesXaml);
        list.ItemTemplate = (DataTemplate)rowResources["AppRowTemplate"];
        list.ItemContainerStyle = (Style)rowResources["AppRowContainer"];
        list.Resources.MergedDictionaries.Add(rowResources);
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(apps);
        view.Filter = item => item is RunningApp app && (string.IsNullOrWhiteSpace(search.Text)
            || app.Executable.Contains(search.Text, StringComparison.OrdinalIgnoreCase)
            || app.Title.Contains(search.Text, StringComparison.OrdinalIgnoreCase));
        var emptyState = new TextBlock
        {
            Text = "Открытых приложений не найдено. Можно выбрать .exe вручную.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(30),
            IsHitTestVisible = false
        };
        emptyState.SetResourceReference(TextBlock.ForegroundProperty, "ThemeMuted");
        void UpdateEmptyState() => emptyState.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        search.TextChanged += (_, _) => { view.Refresh(); UpdateEmptyState(); };
        UpdateEmptyState();

        var chooseFile = DialogChrome.MakeButton("ВЫБРАТЬ ФАЙЛ", false);
        chooseFile.MinWidth = 132;
        chooseFile.Margin = new Thickness(0, 0, 12, 0);
        var choose = DialogChrome.MakeButton("ВЫБРАТЬ", true);
        choose.MinWidth = 120;
        choose.IsDefault = true;
        var cancel = DialogChrome.MakeButton("ОТМЕНА", false);
        cancel.MinWidth = 100;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        void Accept()
        {
            if (list.SelectedItem is not RunningApp app) return;
            selected = app.Executable;
            dialog.Close();
        }
        cancel.Click += (_, _) => dialog.Close();
        choose.Click += (_, _) => Accept();
        list.MouseDoubleClick += (_, _) => Accept();
        chooseFile.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "Выбрать приложение", Filter = "Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*", CheckFileExists = true };
            if (picker.ShowDialog(dialog) == true)
            {
                selected = Path.GetFileName(picker.FileName);
                dialog.Close();
            }
            else if (dialog.IsVisible)
            {
                dialog.Activate();
                search.Focus();
            }
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel);
        actions.Children.Add(choose);
        var footer = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chooseFile.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(chooseFile);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var body = new DockPanel { Margin = new Thickness(20, 5, 20, 18) };
        var listHeader = new Grid { Height = 34, Margin = new Thickness(0, 11, 0, 0) };
        listHeader.SetResourceReference(Panel.BackgroundProperty, "ThemeSurface");
        listHeader.ColumnDefinitions.Add(new ColumnDefinition());
        listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        var appHeader = new TextBlock { Text = "ПРИЛОЖЕНИЕ / .EXE", FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        appHeader.SetResourceReference(TextBlock.ForegroundProperty, "ThemeAccent");
        listHeader.Children.Add(appHeader);
        var pidHeader = new TextBlock { Text = "PID", FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        pidHeader.SetResourceReference(TextBlock.ForegroundProperty, "ThemeAccent");
        Grid.SetColumn(pidHeader, 1);
        listHeader.Children.Add(pidHeader);
        var listPanel = new DockPanel { Margin = new Thickness(0, 11, 0, 12) };
        DockPanel.SetDock(listHeader, Dock.Top);
        listPanel.Children.Add(listHeader);
        var listHost = new Grid();
        listHost.Children.Add(list);
        listHost.Children.Add(emptyState);
        listPanel.Children.Add(listHost);
        search.Margin = new Thickness(0, 4, 0, 0);
        DockPanel.SetDock(search, Dock.Top);
        body.Children.Add(search);
        body.Children.Add(listPanel);

        dialog.Content = DialogChrome.BuildFrame(dialog, "ВЫБОР ЗАПУЩЕННОГО ПРИЛОЖЕНИЯ", body, footer);
        dialog.Loaded += (_, _) => search.Focus();
        dialog.Closed += (_, _) => completion.TrySetResult(selected);
        DialogChrome.ShowModal(owner, dialog);
        return completion.Task;
    }

    private static List<RunningApp> GetVisibleApps()
    {
        var results = new List<RunningApp>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    var title = process.MainWindowTitle.Trim();
                    var name = process.ProcessName.Trim();
                    if (title.Length == 0 || name.Length == 0) continue;
                    results.Add(new RunningApp(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe", title, process.Id));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Процесс мог завершиться или быть защищённым во время чтения снимка.
                }
            }
        }
        return results.OrderBy(app => app.Executable, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
