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
        void CheckSidebar(string context)
        {
            var panel = (Border)Field("SidebarPanel");
            var fit = (Viewbox)Field("SidebarMetricsFit");
            if (Descendants<ScrollViewer>(panel).Any())
                issues.Add($"{context}: sidebar must not contain a scrollable viewport");
            if (fit.Stretch != Stretch.Uniform || fit.StretchDirection != StretchDirection.DownOnly)
                issues.Add($"{context}: sidebar must fit without upscaling or distorting controls");
            foreach (var name in new[] { "StatusDot", "StatusText", "UptimeText", "PowerButton", "RestartButton",
                "ConnectionCard", "CheckConnectionButton", "SpeedTestButton", "LastIssueText", "SpeedProgressText", "FullTunnelCheck", "SidebarTools" })
            {
                var element = (FrameworkElement)Field(name);
                if (!element.IsVisible) continue;
                var bounds = element.TransformToAncestor(panel).TransformBounds(new Rect(element.RenderSize));
                if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Left < panel.Padding.Left
                    || bounds.Top < panel.Padding.Top || bounds.Right > panel.ActualWidth - panel.Padding.Right + 1
                    || bounds.Bottom > panel.ActualHeight - panel.Padding.Bottom + 1)
                    issues.Add($"{context}: sidebar control {name} is clipped: {bounds}");
            }
            var dot = (FrameworkElement)Field("StatusDot");
            var holder = (FrameworkElement)dot.Parent;
            var dotBounds = dot.TransformToAncestor(holder).TransformBounds(new Rect(dot.RenderSize));
            if (dotBounds.Left < 4.5 || dotBounds.Top < 4.5 || holder.ActualWidth - dotBounds.Right < 4.5
                || holder.ActualHeight - dotBounds.Bottom < 4.5)
                issues.Add($"{context}: status indicator has no room for its glow");
            var card = (FrameworkElement)Field("ConnectionCard");
            var tunnel = (FrameworkElement)((FrameworkElement)Field("FullTunnelCheck")).Parent;
            var cardBottom = card.TranslatePoint(new Point(0, card.ActualHeight), panel).Y;
            var tunnelTop = tunnel.TranslatePoint(new Point(), panel).Y;
            if (cardBottom > tunnelTop + .5)
                issues.Add($"{context}: metrics overlap the fixed TUN control");
            foreach (var delta in new[] { -120, 120 })
                card.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta)
                    { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent });
            card.BringIntoView();
            window.UpdateLayout();
            if (Math.Abs(card.TranslatePoint(new Point(0, card.ActualHeight), panel).Y - cardBottom) > .1)
                issues.Add($"{context}: wheel input or BringIntoView moved the sidebar contents");
            count++;
        }
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
            // Simulate the connected readout without starting or touching a real VPN.
            ((TextBlock)Field("StatusText")).Text = "ЯДРО РАБОТАЕТ";
            ((System.Windows.Shapes.Ellipse)Field("StatusDot")).Fill = new SolidColorBrush(Color.FromRgb(115, 255, 191));
            ((TextBlock)Field("UptimeText")).Text = "Ядро работает 00:17:40";
            ((TextBlock)Field("DownloadSpeedText")).Text = "1,6 КБ/с";
            ((TextBlock)Field("UploadSpeedText")).Text = "1,6 КБ/с";
            ((TextBlock)Field("PingText")).Text = "117 мс";
            ((TextBlock)Field("StabilityText")).Text = "87,3 Мбит/с";
            ((TextBlock)Field("ConnectionStateText")).Text = "Проверено 20:31:28";
            ((TextBlock)Field("SpeedProgressText")).Text = "100 %";
            ((TextBlock)Field("SpeedProgressText")).Visibility = Visibility.Visible;
            ((Button)Field("PowerButton")).Content = "ОСТАНОВИТЬ VPN";
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
                    ((TextBlock)Field("LastIssueText")).Text = minimum
                        ? "Длинный текст ошибки подключения: проверьте доступность выбранного VPN-сервера. Полное описание доступно в подсказке."
                        : "";
                    foreach (var journal in new[] { false, true })
                    {
                        typeof(MainWindow).GetMethod("SelectMainPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { journal });
                        var context = $"{scale * 100:0}-{(minimum ? "minimum" : "normal")}-{(journal ? "journal" : "routes")}";
                        Scan(window, context);
                        CheckSidebar(context);
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
            window.ApplyInterfaceScale(1);
            window.Width = 1120; window.Height = 760;
            ((FrameworkElement)Field("UpdateBanner")).Visibility = Visibility.Collapsed;
            ((TextBlock)Field("LastIssueText")).Text = "";
            foreach (var theme in new[] { "Аврора", "Тёмная морфоцитная", "Светлая морфоцитная" })
            {
                ThemeManager.Apply(theme);
                window.UpdateLayout();
                CheckSidebar("sidebar-" + theme);
                CaptureInteraction((FrameworkElement)Field("SidebarPanel"), Path.Combine(root, "sidebar-" + theme + ".png"));
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
