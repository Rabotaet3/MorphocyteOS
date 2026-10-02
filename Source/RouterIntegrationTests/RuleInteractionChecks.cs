using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunRuleInteractionChecks(string scratch, string fakeExe)
    {
        var settingsPath = Path.Combine(scratch, "interaction-settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.AutoCheckUpdates = false;
        settings.CorePath = Path.GetFullPath(fakeExe);
        settings.ShowEventLog = true;
        var configPath = Path.Combine(scratch, "interaction.yaml");
        File.WriteAllText(configPath, "proxies:\n  - name: VPN\n    type: socks5\n    server: 127.0.0.1\n    port: 9999\ntun:\n  enable: true\nrules:\n"
            + string.Join("\n", Enumerable.Range(1, 32).Select(i => $"  - DOMAIN-SUFFIX,site{i}.example,VPN")) + "\n  - MATCH,DIRECT\n");
        var window = new MainWindow(settings, Path.Combine(scratch, "interaction.log")) { Width = 1120, Height = 760, ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void SetField(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length
                && method.GetParameters().Select((parameter, index) => args[index] is null
                    || parameter.ParameterType.IsInstanceOfType(args[index])).All(matches => matches))
            .Invoke(window, args);
        async Task Idle()
        {
            var gate = (SemaphoreSlim)Field("_operations");
            await gate.WaitAsync();
            gate.Release();
            window.UpdateLayout();
        }
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            count += RunRuleBadgeAlignmentChecks(window, scratch);
            Check(window.AllowsTransparency && window.Background is SolidColorBrush { Color.A: 0 },
                "the native window background is transparent outside the rounded WPF frame");
            var rulesListAppearance = (ListView)Field("RulesList");
            Check(rulesListAppearance.Background is SolidColorBrush { Color.A: 0 }
                && Descendants<Border>(window).Any(border => border.CornerRadius.TopLeft == 15),
                "the rule list reveals its rounded parent surface instead of painting a square dark rectangle");
            var sidebar = Descendants<Border>(window).Single(border => border.CornerRadius.TopLeft == 18 && border.Padding.Left == 20);
            Check(sidebar.Effect is null, "sidebar frame no longer applies a blurred drop shadow to its contents");
            var powerButton = (Button)Field("PowerButton");
            powerButton.ApplyTemplate();
            var powerSurface = powerButton.Template.FindName("B", powerButton) as Border;
            Check(powerSurface is { Margin.Left: >= 3, Margin.Right: >= 3, Margin.Top: >= 3, Margin.Bottom: >= 3 }
                && powerSurface.RenderTransform is ScaleTransform,
                "main buttons keep their hover-scale animation inside reserved paint bounds");
            await (Task)Invoke("LoadConfigAsync", configPath)!;
            async Task SmokeModal(string title, string method, object?[] arguments, string screenshot)
            {
                var utilityDialogs = typeof(MainWindow).Assembly.GetType("MorphocyteRouter.UtilityDialogs")!;
                var show = utilityDialogs.GetMethod(method, BindingFlags.Static | BindingFlags.Public)!;
                var seen = false;
                var blurred = false;
                var roundedClip = false;
                var themedFrame = false;
                var openingAnimated = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate =>
                        ReferenceEquals(candidate.Owner, window) && candidate.Title == title);
                    if (dialog is null) return;
                    timer.Stop();
                    seen = true;
                    var windowContent = (FrameworkElement)Field("WindowContent");
                    var frameContent = (FrameworkElement)Field("FrameContent");
                    blurred = windowContent.Effect is BlurEffect;
                    roundedClip = frameContent.Clip is RectangleGeometry { RadiusX: >= 19, RadiusY: >= 19 };
                    themedFrame = dialog.Content is Border frame && frame.CornerRadius.TopLeft >= 14
                        && frame.BorderThickness.Left > 0 && frame.Background is not null;
                    openingAnimated = dialog.Content is FrameworkElement animatedContent
                        && animatedContent.RenderTransform is ScaleTransform openingScale
                        && (openingScale.ScaleX < 1 || animatedContent.Opacity < 1);
                    if (title == "НАСТРОЙКИ")
                    {
                        var saveButton = Descendants<Button>(dialog).Single(button => Equals(button.Content, "СОХРАНИТЬ"));
                        saveButton.ApplyTemplate();
                        var saveSurface = saveButton.Template.FindName("B", saveButton) as Border;
                        Check(saveSurface is { Margin.Left: >= 3, Margin.Right: >= 3, Margin.Top: >= 3, Margin.Bottom: >= 3 }
                            && saveSurface.RenderTransform is ScaleTransform,
                            "settings buttons keep the hover texture animation within their reserved bounds");
                        var picker = Descendants<ComboBox>(dialog).Single(combo => combo.Items.Cast<object>().Contains("Аврора"));
                        Check(picker.Items.Count == 9 && picker.Items.Cast<string>().Contains("Светлая морфоцитная")
                            && picker.Items.Cast<string>().Contains("Аврора") && picker.Items.Cast<string>().Contains("Космический градиент"),
                            "settings expose nine dark, light and gradient themes");
                        Check(Descendants<ComboBox>(dialog).Any(combo => combo.Items.Cast<object>().Contains("125%")),
                            "settings expose interface scaling including font scaling");
                        Check(picker.Template.FindName("PART_Popup", picker) is Popup { PopupAnimation: PopupAnimation.Fade },
                            "theme, scale and route selectors use a fade-in dropdown without extending their layout bounds");
                        CaptureInteraction(dialog, Path.Combine(scratch, "appearance-" + screenshot));
                        var updatesTab = Descendants<Button>(dialog).Single(button => Equals(button.Content, "Обновления"));
                        updatesTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        dialog.UpdateLayout();
                        Check(Descendants<CheckBox>(dialog).Single().IsVisible
                            && Descendants<Button>(dialog).Single(button => Equals(button.Content, "Проверить обновления")).IsVisible,
                            "settings expose automatic and manual release checks");
                        Check(Descendants<ScrollViewer>(dialog).Any(page => page.Visibility == Visibility.Visible
                            && page.RenderTransform is TranslateTransform), "switching settings pages keeps its slide transition within an inset page viewport");
                    }
                    if (title == "ЧАСТЫЕ ВОПРОСЫ") CaptureInteraction(window, Path.Combine(scratch, "rounded-blurred-window.png"));
                    CaptureInteraction(dialog, Path.Combine(scratch, screenshot));
                    dialog.Close();
                };
                timer.Start();
                _ = show.Invoke(null, arguments);
                timer.Stop();
                await Task.Delay(240);
                Check(seen && blurred && roundedClip && themedFrame && openingAnimated && ((FrameworkElement)Field("WindowContent")).Effect is null,
                    $"{title} opens with the themed frame and rounded, clipped blur, then restores the main window");
            }
            await SmokeModal("ЧАСТЫЕ ВОПРОСЫ", "ShowFaq", new object?[] { window }, "faq-1.17.0-test.png");
            await SmokeModal("НАСТРОЙКИ", "ShowSettings", new object?[] { window, configPath, fakeExe, "Тёмная морфоцитная", false, 1d, false, null }, "settings-release.png");
            await SmokeModal("ПРОВЕРКА ОКНА", "ShowNotice", new object?[] { window, "ПРОВЕРКА ОКНА", "Короткое тестовое сообщение.", "ПОНЯТНО" }, "notice-dialog.png");
            var themeManager = typeof(MainWindow).Assembly.GetType("MorphocyteRouter.ThemeManager")!;
            var themeNames = (string[])themeManager.GetField("ThemeNames", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var applyTheme = themeManager.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
            var sampledAccents = new HashSet<Color>();
            foreach (var theme in themeNames)
            {
                applyTheme.Invoke(null, new object?[] { theme });
                window.UpdateLayout();
                var accent = (SolidColorBrush)Application.Current.Resources["ThemeAccent"];
                Check(accent.Color != Colors.Transparent && Application.Current.Resources["ThemeBackgroundGradient"] is LinearGradientBrush,
                    $"theme '{theme}' applies a complete palette and background gradient");
                sampledAccents.Add(accent.Color);
                CaptureInteraction(window, Path.Combine(scratch, "theme-" + themeNames.ToList().IndexOf(theme) + ".png"));
                if (theme is "Светлая морфоцитная" or "Космический градиент")
                    await SmokeModal("НАСТРОЙКИ", "ShowSettings", new object?[] { window, configPath, fakeExe, theme, false, 1d, false, null },
                        "settings-theme-" + themeNames.ToList().IndexOf(theme) + ".png");
            }
            Check(sampledAccents.Count == 9, "all nine themes have distinct accent palettes");
            applyTheme.Invoke(null, new object?[] { "Тёмная морфоцитная" });

            var folderDialogType = typeof(MainWindow).Assembly.GetType("MorphocyteRouter.FolderNameDialog")!;
            var showFolderDialog = folderDialogType.GetMethod("ShowAsync", BindingFlags.Static | BindingFlags.Public)!;
            var folderDialogSeen = false;
            var folderDialogBlurred = false;
            var folderDialogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            folderDialogTimer.Tick += (_, _) =>
            {
                var folderDialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.Owner, window) && candidate.Title == "Новая папка");
                if (folderDialog is null) return;
                folderDialogTimer.Stop();
                folderDialogSeen = folderDialog.Content is Border frame && frame.CornerRadius.TopLeft >= 14;
                folderDialogBlurred = ((FrameworkElement)Field("WindowContent")).Effect is BlurEffect;
                CaptureInteraction(folderDialog, Path.Combine(scratch, "folder-dialog.png"));
                folderDialog.Close();
            };
            folderDialogTimer.Start();
            var folderDialogTask = (Task<string?>)showFolderDialog.Invoke(null, new object[] { window, "Первое правило", "Второе правило" })!;
            await folderDialogTask;
            await Task.Delay(240);
            Check(folderDialogSeen && folderDialogBlurred && ((FrameworkElement)Field("WindowContent")).Effect is null,
                "folder creation uses the shared dialog chrome and restores the backdrop when closed");

            var settingsClick = typeof(MainWindow).GetMethod("SettingsButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Idle();
            Check(!(bool)Field("_busy") && !(bool)Field("_closing"), "settings stay available after closing the shared modals");
            var selectedThemeInSettings = false;
            var savedSettingsDialogStayedOpen = false;
            var initialStatusFont = RenderedFontSize((TextBlock)Field("StatusText"), window);
            var initialPowerHeight = RenderedHeight((FrameworkElement)Field("PowerButton"), window);
            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            settingsTimer.Tick += (_, _) =>
            {
                var settingsDialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.Owner, window) && candidate.Title == "НАСТРОЙКИ");
                if (settingsDialog is null) return;
                settingsTimer.Stop();
                var picker = Descendants<ComboBox>(settingsDialog).Single(combo => combo.Items.Cast<object>().Contains("Аврора"));
                picker.SelectedItem = "Аврора";
                selectedThemeInSettings = Equals(picker.SelectedItem, "Аврора");
                var scalePicker = Descendants<ComboBox>(settingsDialog).Single(combo => combo.Items.Cast<object>().Contains("125%"));
                scalePicker.SelectedItem = "125%";
                Descendants<Button>(settingsDialog).Single(button => Equals(button.Content, "Обновления"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                settingsDialog.UpdateLayout();
                Descendants<CheckBox>(settingsDialog).Single().IsChecked = true;
                var save = Descendants<Button>(settingsDialog).Single(button => Equals(button.Content, "СОХРАНИТЬ"));
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                savedSettingsDialogStayedOpen = settingsDialog.IsVisible;
                var closeAfterSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                closeAfterSave.Tick += (_, _) =>
                {
                    if ((bool)Field("_busy")) return;
                    closeAfterSave.Stop();
                    if (settingsDialog.IsVisible) settingsDialog.Close();
                };
                closeAfterSave.Start();
            };
            settingsTimer.Start();
            settingsClick.Invoke(window, new object[] { new Button(), new RoutedEventArgs() });
            await Idle();
            Check(savedSettingsDialogStayedOpen, "saving settings applies them without closing the settings window");
            Check(selectedThemeInSettings && settings.Theme == "Аврора" && AppSettings.Load(settingsPath).Theme == "Аврора",
                $"saving a theme in settings applies it and persists it across launches (picked={selectedThemeInSettings}, current={settings.Theme}, persisted={AppSettings.Load(settingsPath).Theme})");
            Check(settings.InterfaceScale == 1.25 && AppSettings.Load(settingsPath).InterfaceScale == 1.25
                && settings.AutoCheckUpdates && AppSettings.Load(settingsPath).AutoCheckUpdates,
                "settings persist interface scale and automatic release-check preference");
            window.UpdateLayout();
            Check(Math.Abs(RenderedFontSize((TextBlock)Field("StatusText"), window) / initialStatusFont - 1.25) < .02
                && Math.Abs(RenderedHeight((FrameworkElement)Field("PowerButton"), window) / initialPowerHeight - 1.25) < .02,
                "125-percent scaling changes rendered main-window fonts and controls together");
            var scaledDialogSeen = false;
            var scaledSettingsDialogStayedOpen = false;
            // Let the 170 ms entrance scale finish before measuring the stable
            // user-selected interface scale on controls inside the dialog.
            var restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            restoreTimer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.Owner, window) && candidate.Title == "НАСТРОЙКИ");
                if (dialog is null) return;
                restoreTimer.Stop();
                var save = Descendants<Button>(dialog).Single(button => Equals(button.Content, "СОХРАНИТЬ"));
                var localScale = RenderedHeight(save, dialog) / save.ActualHeight;
                scaledDialogSeen = Math.Abs(localScale - 1.25) < .02;
                Descendants<ComboBox>(dialog).Single(combo => combo.Items.Cast<object>().Contains("Аврора")).SelectedItem = "Тёмная морфоцитная";
                Descendants<ComboBox>(dialog).Single(combo => combo.Items.Cast<object>().Contains("125%")).SelectedItem = "100%";
                Descendants<Button>(dialog).Single(button => Equals(button.Content, "Обновления"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialog.UpdateLayout();
                Descendants<CheckBox>(dialog).Single().IsChecked = false;
                CaptureInteraction(dialog, Path.Combine(scratch, "settings-scaled-release.png"));
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                scaledSettingsDialogStayedOpen = dialog.IsVisible;
                var closeAfterSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                closeAfterSave.Tick += (_, _) =>
                {
                    if ((bool)Field("_busy")) return;
                    closeAfterSave.Stop();
                    if (dialog.IsVisible) dialog.Close();
                };
                closeAfterSave.Start();
            };
            restoreTimer.Start();
            settingsClick.Invoke(window, new object[] { new Button(), new RoutedEventArgs() });
            await Idle();
            Check(scaledSettingsDialogStayedOpen, "saving changed appearance settings leaves the dialog available for further adjustments");
            Check(scaledDialogSeen && settings.InterfaceScale == 1 && !settings.AutoCheckUpdates
                && Math.Abs(RenderedFontSize((TextBlock)Field("StatusText"), window) - initialStatusFont) < .2,
                "new dialogs inherit app scaling and restoring 100 percent restores main-window font size");
            var rules = (ObservableCollection<DomainRule>)Field("_rules");
            rules[^2].Folder = "Медиа";
            rules[^1].Folder = "Медиа";
            rules[0].Folder = "Папка A";
            rules[1].Folder = "Папка B";
            await (Task)Invoke("RecordEditAsync")!;
            window.UpdateLayout();
            var groupsView = (ListCollectionView)Field("_rulesView");
            var list = (ListView)Field("RulesList");
            var host = (FrameworkElement)Field("RuleListHost");
            var overlay = (Canvas)Field("RuleDragOverlay");
            var folderBGroup = groupsView.Groups!.OfType<CollectionViewGroup>().Single(group => Equals(group.Name, "Папка B"));
            var folderBHeader = Descendants<Border>(list).Single(border => border.Name == "FolderDropSurface"
                && border.DataContext is CollectionViewGroup group && Equals(group.Name, "Папка B"));
            var folderAHeader = Descendants<Border>(list).Single(border => border.Name == "FolderDropSurface"
                && border.DataContext is CollectionViewGroup group && Equals(group.Name, "Папка A"));
            SetField("_draggedFolderName", "Папка B");
            SetField("_draggedRules", folderBGroup.Items.OfType<DomainRule>().ToArray());
            SetField("_dragRule", folderBGroup.Items.OfType<DomainRule>().First());
            Check((bool)Invoke("PrepareRuleDrag", folderBHeader)!, "folder drag prepares a card of the folder header");
            var beforeFolderA = folderAHeader.TranslatePoint(new Point(120, 1), host);
            await (Task)Invoke("FinishRuleDragAsync", beforeFolderA)!;
            await Idle();
            var groupOrder = groupsView.Groups!.OfType<CollectionViewGroup>().Select(group => group.Name?.ToString()).ToArray();
            Check(Array.IndexOf(groupOrder, "Папка B") < Array.IndexOf(groupOrder, "Папка A"),
                "dropping a folder above another changes the actual WPF group sequence");
            await (Task)Invoke("ReorderFolderGroupAsync", "ОБЩИЕ ПРАВИЛА", "Папка A", true)!;
            groupOrder = groupsView.Groups!.OfType<CollectionViewGroup>().Select(group => group.Name?.ToString()).ToArray();
            Check(groupOrder[^1] == "ОБЩИЕ ПРАВИЛА", "the general-rules group can be moved to the final position");
            Check(AppSettings.Load(settingsPath).GetFolderOrder(configPath).SequenceEqual(groupOrder),
                "manual folder order is persisted per profile");
            var first = rules[0]; var second = rules[1];
            Invoke("SelectRuleForPointer", first, false);
            Invoke("SelectRuleForPointer", second, true);
            Check(list.SelectedItems.Count == 2, "Ctrl selection retains two independent rules");
            var firstItem = Descendants<ListViewItem>(list).Single(item => ReferenceEquals(item.DataContext, first));
            var checkbox = Descendants<CheckBox>(firstItem).Single();
            checkbox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            await Idle();
            Check(!first.Enabled && !second.Enabled && rules[2].Enabled && list.SelectedItems.Count == 2,
                "clicking a selected checkbox disables the complete selection and keeps other rules enabled");
            Check(AppSettings.Load(settingsPath).GetDraft(configPath)!.Rules.Take(2).All(rule => !rule.Enabled), "bulk disabled state is persisted");
            firstItem = Descendants<ListViewItem>(list).Single(item => ReferenceEquals(item.DataContext, first));
            checkbox = Descendants<CheckBox>(firstItem).Single();
            checkbox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            await Idle();
            Check(first.Enabled && second.Enabled && list.SelectedItems.Count == 2, "the same selected checkbox restores the selection's enabled state");

            first.Enabled = false;
            Invoke("RuleEnabled_Click", new CheckBox { DataContext = first }, new RoutedEventArgs());
            await Idle();
            Check(!first.Enabled && !second.Enabled, "a mixed selection is disabled even when its already-disabled checkbox is clicked");
            Invoke("RuleEnabled_Click", new CheckBox { DataContext = first }, new RoutedEventArgs());
            await Idle();

            firstItem = Descendants<ListViewItem>(list).Single(item => ReferenceEquals(item.DataContext, first));
            SetField("_dragRule", first);
            SetField("_dragSourceItem", firstItem);
            SetField("_dragAnchor", new Point(120, 20));
            // Synthetic routed events have no native MouseDevice.ActiveSource in this runner.
            // Exercise drag state/rendering separately from the OS mouse-capture request.
            Check((bool)Invoke("PrepareRuleDrag", firstItem)!, "drag prepares a snapshot of the actual rule row");
            Invoke("UpdateRuleDrag", new Point(210, 180));
            window.UpdateLayout();
            var card = overlay.Children.OfType<Image>().Single();
            Check(card.Source is BitmapSource && Math.Abs(card.ActualWidth - firstItem.ActualWidth) < 1 && overlay.IsVisible,
                "drag visual keeps the actual row's dimensions inside the list");
            CaptureInteraction(window, Path.Combine(scratch, "drag-1.13.0-test.png"));
            Invoke("UpdateRuleDrag", new Point(-500, 5000));
            Check(Canvas.GetLeft(card) >= 0 && Canvas.GetTop(card) + card.Height <= host.ActualHeight + 0.1, "dragged card is clamped to the rules viewport");
            var scroll = (ScrollViewer)Field("_rulesScrollViewer");
            var before = scroll.VerticalOffset;
            list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Check(scroll.VerticalOffset > before && (bool)Field("_dragActive"),
                "routed mouse wheel scrolls the list without ending the drag");
            scroll.ScrollToHome();
            window.UpdateLayout();
            var folder = Descendants<Border>(list).Single(border => border.Name == "FolderDropSurface"
                && border.DataContext is CollectionViewGroup group && Equals(group.Name, "Медиа"));
            var dropPoint = folder.TranslatePoint(new Point(130, folder.ActualHeight / 2), host);
            Check(dropPoint.Y >= 0 && dropPoint.Y < host.ActualHeight, "folder is reachable after scrolling during the drag");
            await (Task)Invoke("FinishRuleDragAsync", dropPoint)!;
            await Idle();
            Check(first.Folder == "Медиа" && second.Folder == "Медиа" && list.SelectedItems.Count == 2 && !overlay.IsVisible,
                "dropping on the folder moves the selected rules together and removes the drag visual");
            Check(AppSettings.Load(settingsPath).GetDraft(configPath)!.Rules.Take(2).All(rule => rule.Folder == "Медиа"), "multi-rule move persists in the draft");

            scroll.ScrollToHome();
            window.UpdateLayout();
            folder = Descendants<Border>(list).Single(border => border.Name == "FolderDropSurface"
                && border.DataContext is CollectionViewGroup group && Equals(group.Name, "Медиа"));
            var folderClose = Descendants<Button>(folder).Single(button => button.Tag is CollectionViewGroup);
            var rowClose = Descendants<Button>(list).First(button => button.Tag is DomainRule member && member.Folder == "Медиа");
            var folderX = folderClose.TranslatePoint(new Point(folderClose.ActualWidth / 2, 0), list).X;
            var rowX = rowClose.TranslatePoint(new Point(rowClose.ActualWidth / 2, 0), list).X;
            Check(Math.Abs(folderX - rowX) < 1, "folder and rule delete icons share the same horizontal alignment");
            scroll.ScrollToHome();
            window.UpdateLayout();
            CaptureInteraction(window, Path.Combine(scratch, "folders-1.13.0-test.png"));

            var logToggle = (ToggleButton)Field("LogVisibilityToggle");
            var logText = (TextBox)Field("LogText");
            var pendingCoreOutput = (ConcurrentQueue<string>)Field("_pendingCoreOutput");
            for (var i = 0; i < 500; i++) pendingCoreOutput.Enqueue($"batched-core-line-{i:D3}");
            Check(!logText.Text.Contains("batched-core-line-499", StringComparison.Ordinal),
                "core log output waits for the batch refresh instead of rebuilding the text box per line");
            Invoke("FlushCoreOutput");
            Check(logText.Text.Contains("batched-core-line-499", StringComparison.Ordinal)
                && logText.Text.Split(Environment.NewLine).Length <= 2500,
                "batched core output is displayed in a bounded recent-log buffer");
            var workspace = (FrameworkElement)Field("Workspace");
            var oldWorkspaceHeight = workspace.ActualHeight;
            logToggle.IsChecked = false;
            logToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Idle();
            Check(logText.Visibility == Visibility.Collapsed && logToggle.IsVisible && workspace.ActualHeight > oldWorkspaceHeight,
                "log switch hides the log and gives its space back to the rule list");
            Check(!AppSettings.Load(settingsPath).ShowEventLog, "log visibility preference survives settings reload");
            CaptureInteraction(window, Path.Combine(scratch, "log-hidden-1.13.0-test.png"));
            logToggle.IsChecked = true;
            logToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Idle();
            Check(logText.IsVisible && logText.Text.Length > 0, "enabling the log restores its existing text");

            var mediaExpander = Descendants<Expander>(list).Single(expander =>
                ((GroupItem)FindAncestor(expander, typeof(GroupItem))!).Content is CollectionViewGroup group && Equals(group.Name, "Медиа"));
            mediaExpander.IsExpanded = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await (Task)Invoke("ApplyChangesAsync", true)!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            mediaExpander = Descendants<Expander>(list).Single(expander =>
                ((GroupItem)FindAncestor(expander, typeof(GroupItem))!).Content is CollectionViewGroup group && Equals(group.Name, "Медиа"));
            Check(!mediaExpander.IsExpanded, "applying YAML preserves each folder's collapsed state");
            var completeMediaFolder = rules.Where(rule => rule.Folder == "Медиа").ToArray();
            var search = (TextBox)Field("SearchBox");
            search.Text = first.Value;
            window.UpdateLayout();
            var filteredMedia = groupsView.Groups!.OfType<CollectionViewGroup>().Single(group => Equals(group.Name, "Медиа"));
            Check(filteredMedia.Items.Count < completeMediaFolder.Length, "search hides some folder rules for the bulk-action test");
            Invoke("FolderEnabled_Click", new CheckBox { DataContext = filteredMedia }, new RoutedEventArgs());
            await Idle();
            Check(completeMediaFolder.All(rule => !rule.Enabled), "folder toggle disables members hidden by search too");
            filteredMedia = groupsView.Groups!.OfType<CollectionViewGroup>().Single(group => Equals(group.Name, "Медиа"));
            Invoke("FolderEnabled_Click", new CheckBox { DataContext = filteredMedia }, new RoutedEventArgs());
            await Idle();
            Check(completeMediaFolder.All(rule => rule.Enabled), "folder toggle restores hidden members too");
            filteredMedia = groupsView.Groups!.OfType<CollectionViewGroup>().Single(group => Equals(group.Name, "Медиа"));
            Invoke("RemoveFolder_Click", new Button { Tag = filteredMedia }, new RoutedEventArgs());
            await Idle();
            Check(completeMediaFolder.All(rule => rules.Contains(rule) && rule.Folder == ""),
                "deleting a searched folder moves every member to general rules without losing hidden members");
            search.Text = "";
            window.UpdateLayout();
            Console.WriteLine("INTERACTION PREVIEWS: " + scratch);
            return count;
        }
        finally
        {
            Invoke("CancelRuleDrag");
            SetField("_allowClose", true);
            window.Close();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static DependencyObject? FindAncestor(DependencyObject current, Type type)
    {
        while (current is not null)
        {
            if (type.IsInstanceOfType(current)) return current;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void CaptureInteraction(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static double RenderedHeight(FrameworkElement element, Visual ancestor)
    {
        var transform = element.TransformToAncestor(ancestor);
        var top = transform.Transform(new Point(0, 0));
        var bottom = transform.Transform(new Point(0, element.ActualHeight));
        return Math.Abs(bottom.Y - top.Y);
    }

    private static double RenderedFontSize(TextBlock text, Visual ancestor)
    {
        var transform = text.TransformToAncestor(ancestor);
        var top = transform.Transform(new Point(0, 0));
        var bottom = transform.Transform(new Point(0, text.FontSize));
        return Math.Abs(bottom.Y - top.Y);
    }
}
