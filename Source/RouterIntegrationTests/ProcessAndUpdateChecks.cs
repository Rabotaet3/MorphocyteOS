using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunProcessAndUpdateChecks(string scratch, string fakeExe)
    {
        var config = Path.Combine(scratch, "process-selection.yaml");
        const string yaml = "proxies:\n  - name: VPN\n    type: socks5\n    server: 127.0.0.1\n    port: 9999\ntun:\n  enable: true\nrules:\n  - DOMAIN-SUFFIX,example.com,VPN\n  - MATCH,DIRECT\n";
        File.WriteAllText(config, yaml);
        var settings = AppSettings.Load(Path.Combine(scratch, "process-selection-settings.json"));
        settings.ConfigPath = config; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false;
        var window = new MainWindow(settings, Path.Combine(scratch, "process-selection.log")) { ShowInTaskbar = false };
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            window.Show();
            var gate = (SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var rules = (ObservableCollection<DomainRule>)Field("_rules");
            await (Task)Method("AddApplicationsAsync").Invoke(window, new object[] { new[] { "FirstApp.exe", "SecondApp.exe", "firstapp.EXE" } })!;
            Check(rules.Count == 3 && rules.Count(rule => rule.Kind == "PROCESS-NAME") == 2, "multiple selected applications enter the draft immediately with case-insensitive deduplication");
            Check(File.ReadAllText(config) == yaml && settings.GetDraft(config)?.Rules.Count == 3 && (bool)Field("_dirty"), "choosing applications persists draft but never applies YAML");
            await (Task)Method("AddApplicationsAsync").Invoke(window, new object[] { new[] { "FirstApp.exe" } })!;
            Check(rules.Count == 3, "selecting an existing application does not duplicate its rule");
            Check((Field("_core") as CoreProcessManager)?.IsRunning == false, "application selection does not start or restart the core");
            var assembly = typeof(MainWindow).Assembly;
            var pickerType = assembly.GetType("MorphocyteRouter.RunningProcessDialog")!;
            var appType = pickerType.GetNestedType("RunningApp", BindingFlags.NonPublic)!;
            var pickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            var extended = false;
            pickerTimer.Tick += (_, _) =>
            {
                var picker = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.Title == "Запущенные приложения");
                if (picker is null) return;
                pickerTimer.Stop();
                var list = Descendants<ListBox>(picker).Single(); extended = list.SelectionMode == SelectionMode.Extended;
                var data = Array.CreateInstance(appType, 3);
                for (var i = 0; i < 3; i++) data.SetValue(Activator.CreateInstance(appType, "Selected" + i + ".exe", "Test fixture " + i, 100 + i), i);
                list.ItemsSource = data; list.SelectedItems.Add(data.GetValue(0)); list.SelectedItems.Add(data.GetValue(2));
                CaptureInteraction(picker, Path.Combine(scratch, "process-multiselect.png"));
                Descendants<Button>(picker).Single(button => Equals(button.Content, "ВЫБРАТЬ")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            pickerTimer.Start();
            var picked = await (Task<IReadOnlyList<string>>)pickerType.GetMethod("ShowAsync")!.Invoke(null, new object[] { window })!;
            Check(extended && picked.SequenceEqual(new[] { "Selected0.exe", "Selected2.exe" }), "process picker supports Explorer-style multiple selection and returns all selected executables");
            var resultType = assembly.GetType("MorphocyteRouter.UpdateCheckResult")!;
            var assetType = assembly.GetType("MorphocyteRouter.UpdateAsset")!;
            var asset = Activator.CreateInstance(assetType, "MorphocyteOS-9.0.0-win-x64.zip", "https://github.com/sample-org/sample-app/releases/download/v9.0.0/MorphocyteOS-9.0.0-win-x64.zip", new string('a', 64), 1024L);
            var newer = Activator.CreateInstance(resultType, true, true, "Доступна версия 9.0.0.", "https://github.com/sample-org/sample-app/releases/tag/v9.0.0", "9.0.0", asset, "sample-org/sample-app");
            Method("SetAvailableUpdate").Invoke(window, new[] { newer });
            var download = (Button)Field("InstallUpdateButton");
            Check(((FrameworkElement)Field("UpdateBanner")).Visibility == Visibility.Visible && ((TextBlock)Field("UpdateOfferText")).Text.Contains("9.0.0"), "new verified release shows a conditional notification above the workspace");
            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            var settingsUpdated = false;
            settingsTimer.Tick += async (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.Title == "НАСТРОЙКИ");
                if (dialog is null) return;
                settingsTimer.Stop();
                var dialogCount = Application.Current.Windows.Count;
                var settingsScroll = Descendants<ScrollViewer>(dialog).Single(scroll => scroll.Name == "SettingsScroll");
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "Обновления")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(250);
                dialog.UpdateLayout();
                Check(settingsScroll.VerticalOffset > 0 && Application.Current.Windows.Count == dialogCount
                    && Descendants<CheckBox>(dialog).Count() == 4,
                    "section buttons scroll the shared settings page without opening or hiding separate pages");
                Check(!Descendants<TextBlock>(dialog).Any(text => text.Text.Contains("30 минут") || text.Text.StartsWith("Цвета меняются")
                    || text.Text.StartsWith("Меняет весь интерфейс") || text.Text.StartsWith("Приложение запускается в трее"))
                    && Descendants<CheckBox>(dialog).Any(option => Descendants<TextBlock>(option).Any(text => text.Text == "Автоматическое обновление")),
                    "settings keep the short automatic-update label without instructional paragraphs");
                settingsUpdated = Descendants<Button>(dialog).Any(button => Equals(button.Content, "ОБНОВИТЬ ДО 9.0.0") && button.IsVisible)
                    && !Descendants<TextBlock>(dialog).Any(text => text.Text.Contains("предложит открыть страницу загрузки"));
                CaptureInteraction(dialog, Path.Combine(scratch, "settings-download-update.png"));
                Method("SetAvailableUpdate").Invoke(window, new object?[] { null });
                settingsUpdated &= Descendants<Button>(dialog).Any(button => Equals(button.Content, "ОБНОВИТЬ ПРИЛОЖЕНИЕ") && button.IsVisible)
                    && !Descendants<TextBlock>(dialog).Any(text => text.Text == "Доступна версия 9.0.0.");
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "VPN-профиль")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog.UpdateLayout();
                settingsUpdated &= Descendants<TextBlock>(dialog).Any(text => text.Text == "Установленное ядро:");
                Check(settingsScroll.VerticalOffset > 0 && settingsScroll.VerticalOffset < settingsScroll.ScrollableHeight,
                    "profile shortcut positions the shared scrollbar between appearance and startup");
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "Запуск")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(250);
                dialog.UpdateLayout();
                var startupOptions = Descendants<CheckBox>(dialog).Where(option => Descendants<TextBlock>(option)
                    .Any(text => text.Text.StartsWith("Запускать приложение") || text.Text.StartsWith("Подключать VPN"))).ToArray();
                Check(startupOptions.Length == 2 && startupOptions.All(option =>
                    Descendants<TextBlock>(option).Any(label => label.IsVisible && label.ActualWidth > 20 && label.ActualHeight > 10 && label.Text.Length > 10)),
                    "startup option captions are rendered inside checkbox templates rather than merely stored as Content");
                var launch = startupOptions[0]; var connect = startupOptions[1];
                Check(!connect.IsEnabled && launch.Focusable, "auto-connect is disabled until Windows startup is enabled and the labeled option accepts keyboard focus");
                typeof(System.Windows.Controls.Primitives.ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(launch, null);
                Check(launch.IsChecked == true && connect.IsEnabled, "clicking the labeled startup option enables its dependent VPN setting");
                settingsScroll.ScrollToTop(); dialog.UpdateLayout();
                Check(settingsScroll.VerticalOffset == 0 && launch.IsChecked == true && connect.IsEnabled,
                    "manual scrolling returns to appearance without losing changes made in another section");
                ThemeManager.Apply("Аметист");
                foreach (var scale in new[] { 1d, 1.5d })
                {
                    UiScaleManager.Apply(dialog, (FrameworkElement)dialog.Content, scale);
                    dialog.UpdateLayout();
                    Descendants<Button>(dialog).Single(button => Equals(button.Content, "Запуск")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialog.UpdateLayout();
                    Check(startupOptions.All(option => Descendants<TextBlock>(option).Any(label => label.IsVisible && label.ActualWidth > 20 && label.TextWrapping == TextWrapping.Wrap)),
                        $"startup captions remain visible and can wrap at {scale * 100:0}% scale");
                    var screenshot = Path.Combine(scratch, $"startup-settings-{scale * 100:0}.png");
                    CaptureInteraction(dialog, screenshot);
                    var picture = new System.Windows.Media.Imaging.BitmapImage(new Uri(screenshot));
                    Check(picture.PixelWidth >= Math.Floor(dialog.ActualWidth) && picture.PixelHeight >= Math.Floor(dialog.ActualHeight),
                        $"startup screenshot includes the entire window at {scale * 100:0}% rather than cropping the scaled content");
                    Descendants<Button>(dialog).Single(button => Equals(button.Content, "Обновления")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    dialog.UpdateLayout();
                    var automatic = Descendants<CheckBox>(dialog).Single(option => Descendants<TextBlock>(option).Any(text => text.Text == "Автоматическое обновление"));
                    var viewport = Descendants<ScrollContentPresenter>(settingsScroll).First();
                    var optionBounds = automatic.TransformToAncestor(viewport).TransformBounds(new Rect(automatic.RenderSize));
                    Check(optionBounds.Top >= 0 && optionBounds.Bottom <= viewport.ActualHeight,
                        $"update shortcut brings its labeled option into the viewport at {scale * 100:0}% scale");
                    CaptureInteraction(dialog, Path.Combine(scratch, $"scrolling-settings-updates-{scale * 100:0}.png"));
                }
                UiScaleManager.Apply(dialog, (FrameworkElement)dialog.Content, 1);
                ThemeManager.Apply(settings.Theme);
                dialog.Close();
            };
            settingsTimer.Start();
            Method("SettingsButton_Click").Invoke(window, new object[] { new Button(), new RoutedEventArgs() });
            Check(settingsUpdated, "settings share update state and expose update actions and selected core in their sections");
            Check(((FrameworkElement)Field("UpdateBanner")).Visibility == Visibility.Collapsed &&
                typeof(MainWindow).GetField("CountText", BindingFlags.Instance | BindingFlags.NonPublic) is null,
                "main screen omits the route counter and update notification when there is no newer release");
            Check(!Descendants<TextBlock>(window).Any(text => text.Text.StartsWith("Направляй сайты и приложения"))
                && !Descendants<Image>(window).Any(image => image.Width == 52), "removed subtitle and extra sidebar logo do not appear");
            await Task.Delay(250);
            CaptureInteraction(window, Path.Combine(scratch, "compact-main-window.png"));
            Method("SetAvailableUpdate").Invoke(window, new[] { newer });
            window.Width = 980; window.UpdateLayout();
            await Task.Delay(150);
            CaptureInteraction(window, Path.Combine(scratch, "compact-update-available.png"));
            window.Height = 660;
            ((TextBlock)Field("LastIssueText")).Text = "Проверочный длинный текст ошибки подключения для проверки высоты карточки и доступности кнопок.";
            window.UpdateLayout();
            var sidebar = (ScrollViewer)Field("SidebarScrollViewer");
            sidebar.ScrollToBottom(); window.UpdateLayout();
            var card = (FrameworkElement)Field("ConnectionCard");
            var tools = (FrameworkElement)Field("SidebarTools");
            Check(sidebar.ScrollableHeight > 0 && card.TranslatePoint(new Point(0, card.ActualHeight), window).Y <= tools.TranslatePoint(new Point(0, 0), window).Y,
                "sidebar remains scrollable without overlapping metrics and settings at the minimum size with a notification and error");
            CaptureInteraction(window, Path.Combine(scratch, "minimum-window.png"));
            Method("SetAvailableUpdate").Invoke(window, new object?[] { null });
            await (Task)Method("ApplyChangesAsync").Invoke(window, new object[] { false })!;
            Check(ClashConfigDocument.Load(config).Rules.Count == 3 && settings.GetDraft(config) is null, "only explicit Apply writes selected applications to YAML and clears draft");
            return count;
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            window.Close();
        }
    }
}
