using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunVisualLayoutChecks(string scratch, string fakeExe)
    {
        var root = Path.Combine(scratch, "visual-layout");
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "profile.yaml");
        File.WriteAllText(config, "proxies:\n  - name: VPN\n    type: socks5\n    server: 127.0.0.1\n    port: 9999\nrules:\n  - DOMAIN-SUFFIX,example.org,VPN\n  - MATCH,DIRECT\n");
        var settings = AppSettings.Load(Path.Combine(root, "settings.json"));
        settings.AutoCheckUpdates = false;
        settings.ConfigPath = config;
        settings.CorePath = Path.GetFullPath(fakeExe);
        settings.Theme = "Океан";
        var window = new MainWindow(settings, Path.Combine(root, "test.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var issues = new HashSet<string>();
        var count = 0;
        void Scan(Window candidate, string context)
        {
            candidate.UpdateLayout();
            var buttons = Descendants<Button>(candidate).Where(button => button.IsVisible && button.ActualWidth > 0).ToArray();
            foreach (var button in buttons)
            {
                var surface = button.Template.FindName("B", button) as Border ?? button.Template.FindName("ToolSurface", button) as Border;
                var presenter = Descendants<ContentPresenter>(button).FirstOrDefault(value => ReferenceEquals(value.TemplatedParent, button));
                if (surface is null || presenter is null) continue;
                var available = new Size(Math.Max(0, surface.ActualWidth - surface.BorderThickness.Left - surface.BorderThickness.Right - presenter.Margin.Left - presenter.Margin.Right),
                    Math.Max(0, surface.ActualHeight - surface.BorderThickness.Top - surface.BorderThickness.Bottom - presenter.Margin.Top - presenter.Margin.Bottom));
                var wraps = Descendants<TextBlock>(presenter).Any(text => text.TextWrapping != TextWrapping.NoWrap);
                var content = VisualTreeHelper.GetChildrenCount(presenter) > 0 ? VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement : null;
                if (content is null) continue;
                content.Measure(new Size(wraps ? available.Width : double.PositiveInfinity, double.PositiveInfinity));
                var desired = content.DesiredSize;
                content.InvalidateMeasure();
                if (desired.Width > available.Width + 1 || desired.Height > available.Height + 1)
                    issues.Add($"{context}: {button.Name} '{button.Content}' needs {desired}, has {available}");
            }
            candidate.UpdateLayout();
            count++;
        }
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations");
            await gate.WaitAsync(); gate.Release();
            foreach (var scale in new[] { .8, 1d, 1.25, 1.5 })
            {
                window.ApplyInterfaceScale(scale);
                foreach (var minimum in new[] { false, true })
                {
                    window.Width = minimum ? window.MinWidth : Math.Max(window.MinWidth, 1120 * scale);
                    window.Height = minimum ? window.MinHeight : Math.Max(window.MinHeight, 760 * scale);
                    ((FrameworkElement)Field("UpdateBanner")).Visibility = minimum ? Visibility.Visible : Visibility.Collapsed;
                    ((TextBlock)Field("UpdateOfferText")).Text = "Доступно обновление MorphocyteOS. Установить новую версию?";
                    ((TextBlock)Field("ChangesSummaryText")).Text = "Добавлено: 120 · Удалено: 30 · Изменено: 50";
                    ((FrameworkElement)Field("ChangesSummaryText")).Visibility = minimum ? Visibility.Visible : Visibility.Collapsed;
                    foreach (var journal in new[] { false, true })
                    {
                        typeof(MainWindow).GetMethod("SelectMainPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { journal });
                        var context = $"{scale * 100:0}-{(minimum ? "minimum" : "normal")}-{(journal ? "journal" : "routes")}";
                        Scan(window, context);
                        if (!journal)
                        {
                            var content = (FrameworkElement)Field("WindowContent");
                            foreach (var name in new[] { "RedoRulesButton", "UndoButton", "ApplyButton", "SidebarTools" })
                            {
                                var button = (FrameworkElement)Field(name);
                                var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                                if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > content.ActualWidth + 1 || bounds.Bottom > content.ActualHeight + 1)
                                    issues.Add($"{context}: {name} extends beyond the clipped window: {bounds}");
                            }
                        }
                        CaptureInteraction(window, Path.Combine(root, context + ".png"));
                    }
                }
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                Exception? modalError = null;
                timer.Tick += (_, _) =>
                {
                    var dialog = window.OwnedWindows.OfType<Window>().FirstOrDefault(value => value.Title == "НАСТРОЙКИ");
                    if (dialog is null) return;
                    timer.Stop();
                    try
                    {
                        dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                        var scroll = Descendants<ScrollViewer>(dialog).Single(value => value.Name == "SettingsScroll");
                        foreach (var section in new[] { "Внешний вид", "VPN-профиль", "Обновления", "Запуск" })
                        {
                            Descendants<Button>(dialog).Single(button => Equals(button.Content, section)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Scan(dialog, $"settings-{scale * 100:0}-{section}");
                            CaptureInteraction(dialog, Path.Combine(root, $"settings-{scale * 100:0}-{section}.png"));
                        }
                    }
                    catch (Exception ex) { modalError = ex; }
                    finally { dialog.Close(); }
                };
                timer.Start();
                UtilityDialogs.ShowSettings(window, config, fakeExe, "Океан", false, scale, false, null);
                if (modalError is not null) throw modalError;
            }
            foreach (var text in new[] { "ЗАПУСТИТЬ VPN", "ОСТАНОВИТЬ VPN", "ОТМЕНИТЬ ВОССТАНОВЛЕНИЕ" })
            {
                ((Button)Field("PowerButton")).Content = text;
                Scan(window, "power-state-" + text);
            }
            Console.WriteLine("VISUAL PREVIEWS: " + root);
            foreach (var issue in issues) Console.WriteLine("LAYOUT ISSUE: " + issue);
            if (issues.Count > 0) throw new Exception($"Found {issues.Count} clipped button layouts.");
            Console.WriteLine($"PASS: {count} button layouts retain complete text and icons at 80–150% scale, including minimum windows and all settings sections.");
            return count;
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
            UiScaleManager.CurrentScale = 1;
            ThemeManager.Apply(null);
        }
    }
}
