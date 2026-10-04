using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunThemePolishChecks(string scratch, string fakeExe)
    {
        var root = Path.Combine(scratch, "theme-polish"); Directory.CreateDirectory(root);
        var config = Path.Combine(root, "profile.yaml");
        File.WriteAllText(config, "proxies:\n  - {name: Server, type: socks5, server: 127.0.0.1, port: 9999}\nproxy-groups:\n  - {name: VPN, type: select, proxies: [Server]}\nrules:\n  - MATCH,DIRECT\n");
        var settings = AppSettings.Load(Path.Combine(root, "settings.json"));
        settings.ConfigPath = config; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false; settings.Theme = "Аметист";
        var window = new MainWindow(settings, Path.Combine(root, "test.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release();
            var grid = (DataGrid)Field("ConnectionsGrid");
            ((Button)Field("JournalTabButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var rows = new[] { new ConnectionActivity("Browser.exe", "example.org:443", "TCP", "DomainSuffix example.org", "Server ← VPN"),
                new ConnectionActivity("Notes.exe", "sync.example.org:443", "TCP", "Match", "DIRECT"),
                new ConnectionActivity("Player.exe", "media.example.org:443", "UDP", "ProcessName Player.exe", "Server ← VPN") };
            grid.ItemsSource = rows;
            typeof(MainWindow).GetMethod("InitializeDesktopIntegration", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var menu = (ContextMenu)Field("_trayMenu");
            Check(menu.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(new object[] { "Открыть MorphocyteOS", "Запустить VPN", "Настройки", "Журнал", "Выход" }), "tray has themed WPF actions including Settings");
            Check(menu.Style == window.FindResource("TrayMenuStyle") && menu.Items.OfType<MenuItem>().All(item => item.Template is not null), "tray uses application templates rather than Windows stock menu");
            foreach (var theme in ThemeManager.ThemeNames)
            {
                ThemeManager.Apply(theme); window.UpdateLayout(); grid.SelectedItem = rows[0]; grid.Focus();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var logo = (BitmapSource)Application.Current.Resources["ThemeLogo"];
                Check(logo.IsFrozen && logo.PixelWidth == 512 && ReferenceEquals(window.Icon, logo), "high-resolution frozen window artwork follows theme: " + theme);
                var headers = Descendants<DataGridColumnHeader>(grid).Where(header => header.Column is not null).ToArray();
                Check(headers.Length == 5 && headers.All(header => header.HorizontalContentAlignment == HorizontalAlignment.Center), "all connection headings are centered: " + theme);
                var selected = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                Check(Descendants<ScrollViewer>(grid).First().ScrollableWidth < 1, "row decoration does not cause a needless horizontal scrollbar: " + theme);
                Check(selected.IsSelected && Equals(selected.Background, ThemeManager.GetBrush("ThemeSurfaceSelected")) && Equals(selected.BorderBrush, ThemeManager.GetBrush("ThemeAccent")), "selected connection remains in theme palette: " + theme);
                Check(Descendants<DataGridCell>(selected).All(cell => Equals(cell.Foreground, ThemeManager.GetBrush("ThemeText")) && Equals(cell.Background, Brushes.Transparent)), "cells never use stock white/blue selection: " + theme);
                ((Button)Field("RefreshConnectionsButton")).Focus(); window.UpdateLayout();
                Check(Equals(selected.Background, ThemeManager.GetBrush("ThemeSurfaceSelected")), "inactive selection preserves theme: " + theme);
                if (theme is "Аметист" or "Светлая морфоцитная" or "Океан")
                    CaptureInteraction(window, Path.Combine(root, "journal-" + Array.IndexOf(ThemeManager.ThemeNames, theme) + ".png"));
            }
            ThemeManager.Apply("Аметист");
            var cached = Application.Current.Resources["ThemeLogo"];
            ThemeManager.Apply("Океан"); Check(!ReferenceEquals(cached, Application.Current.Resources["ThemeLogo"]), "different themes have different artwork");
            ThemeManager.Apply("Аметист"); Check(ReferenceEquals(cached, Application.Current.Resources["ThemeLogo"]), "repeated theme changes reuse generated artwork");
            var pixels = new byte[512 * 512 * 4]; ((BitmapSource)cached).CopyPixels(pixels, 512 * 4, 0);
            Check(Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 3] > 200 && pixels[index * 4] > pixels[index * 4 + 1] + 40 && pixels[index * 4 + 2] > pixels[index * 4 + 1] + 20), "Amethyst artwork contains violet highlights rather than mint");
            var bytes = IconArtwork.EncodeIcon((BitmapSource)cached);
            Check(BitConverter.ToUInt16(bytes, 4) == 10 && IconArtwork.Sizes.Contains(20) && IconArtwork.Sizes.Contains(256), "ICO contains ten appropriate DPI sizes, not one enlarged small icon");
            var scaled = new FormatConvertedBitmap(IconArtwork.Scale((BitmapSource)cached, 20), PixelFormats.Bgra32, null, 0);
            var smallPixels = new byte[20 * 20 * 4]; scaled.CopyPixels(smallPixels, 80, 0);
            Check(Enumerable.Range(0, 400).Any(index => smallPixels[index * 4 + 3] is > 0 and < 255), "small icons keep antialiased transparent edges");
            menu.Placement = PlacementMode.AbsolutePoint; menu.HorizontalOffset = 200; menu.VerticalOffset = 160; menu.IsOpen = true;
            await Task.Delay(160); menu.UpdateLayout();
            var border = Descendants<Border>(menu).First();
            Check(border.CornerRadius.TopLeft == 15 && Equals(border.Background, ThemeManager.GetBrush("ThemeTrayBackground")), "tray popup has a rounded themed gradient surface");
            Check(border.Background is LinearGradientBrush, "tray background uses the full main-theme gradient rather than a near-black popup color");
            CaptureInteraction(menu, Path.Combine(root, "tray-amethyst.png")); menu.IsOpen = false;
            window.HideToTray(); menu.IsOpen = true;
            await Task.Delay(130);
            Check(!window.IsVisible && menu.IsVisible, "tray menu opens while the main window is hidden");
            menu.IsOpen = false;
            var settingsSeen = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) =>
            {
                var dialog = window.OwnedWindows.OfType<Window>().FirstOrDefault(value => value.Title == "НАСТРОЙКИ");
                if (dialog is null) return;
                settingsSeen = true; timer.Stop(); dialog.Close();
            };
            timer.Start();
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Настройки")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            timer.Stop(); Check(settingsSeen, "tray Settings opens the normal settings dialog");
            Check(window.IsVisible, "tray Settings restores the hidden main window before opening its dialog");
            Check(File.ReadAllText(config).Contains("MATCH,DIRECT") && !((CoreProcessManager)Field("_core")).HasTrackedProcess, "visual checks do not alter routing or start VPN");
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close(); ThemeManager.Apply("Тёмная морфоцитная");
        }
        return count;
    }
}
