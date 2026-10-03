using System.Windows;
using System.Windows.Controls;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private void RoutesTab_Click(object sender, RoutedEventArgs e) => ChangeMainPage(false);
    private void JournalTab_Click(object sender, RoutedEventArgs e) => ChangeMainPage(true);

    private async void ChangeMainPage(bool journal)
    {
        await RunExclusiveAsync(async () =>
        {
            SelectMainPage(journal);
            _settings.ShowEventLog = journal;
            await SaveSettingsAsync();
        });
        if (journal && JournalPage.IsVisible) await RefreshConnectionSnapshotAsync();
    }

    private void SelectMainPage(bool journal)
    {
        RoutesPage.Visibility = journal ? Visibility.Collapsed : Visibility.Visible;
        JournalPage.Visibility = journal ? Visibility.Visible : Visibility.Collapsed;
        RoutesTabButton.SetResourceReference(Control.BorderBrushProperty, journal ? "ThemeButtonBorder" : "ThemeAccent");
        JournalTabButton.SetResourceReference(Control.BorderBrushProperty, journal ? "ThemeAccent" : "ThemeButtonBorder");
        if (journal)
        {
            LogText.Text = string.Join(Environment.NewLine, _logLines);
            LogText.ScrollToEnd();
        }
    }
}
