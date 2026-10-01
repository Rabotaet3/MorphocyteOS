using System.Windows;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private async void LogVisibilityToggle_Click(object sender, RoutedEventArgs e) => await RunExclusiveAsync(async () =>
    {
        _settings.ShowEventLog = LogVisibilityToggle.IsChecked == true;
        ApplyLogVisibility();
        await SaveSettingsAsync();
    });

    private void ApplyLogVisibility()
    {
        var visible = LogVisibilityToggle.IsChecked == true;
        if (!visible && LogText.Visibility == Visibility.Visible && LogRow.ActualHeight >= 70)
            _expandedLogHeight = LogRow.ActualHeight;
        LogText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CopyLogButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LogSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LogHeader.Margin = new Thickness(2, 0, 2, visible ? 6 : 0);
        LogRow.MinHeight = visible ? 70 : 0;
        LogRow.Height = visible ? new GridLength(_expandedLogHeight) : GridLength.Auto;
        WorkspaceRow.Height = new GridLength(1, GridUnitType.Star);
        if (visible)
        {
            LogText.Text = string.Join(Environment.NewLine, _logLines);
            LogText.ScrollToEnd();
            SyncLogScrollbar();
        }
        else LogScrollTrack.Visibility = Visibility.Collapsed;
    }
}
