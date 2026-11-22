using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunPageTransitionChecks(string scratch, string fakeExe)
    {
        var root = Path.Combine(scratch, "page-transitions"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "profile.yaml");
        var initial = "proxies:\n  - {name: Test, type: socks5, server: 127.0.0.1, port: 9999}\nrules:\n  - DOMAIN,example.org,Test\n  - MATCH,DIRECT\n";
        File.WriteAllText(path, initial);
        var settingsPath = Path.Combine(root, "settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.ConfigPath = path; settings.CorePath = fakeExe; settings.AutoCheckUpdates = false; settings.Theme = "Аврора";
        var window = new MainWindow(settings, Path.Combine(root, "test.log")) { ShowInTaskbar = false };
        object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
        object? Invoke(string name, params object[] values) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, values);
        var count = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
        async Task Idle()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations")!; await gate.WaitAsync(); gate.Release(); window.UpdateLayout();
        }
        async Task Finish()
        {
            await Task.Delay(260); await Idle();
        }
        BitmapSource CapturePage()
        {
            window.UpdateLayout();
            var frame = (FrameworkElement)Field("FrameContent")!;
            var host = (FrameworkElement)Field("MainPageHost")!;
            var bounds = host.TransformToAncestor(frame).TransformBounds(new Rect(host.RenderSize));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth), (int)Math.Ceiling(frame.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(frame);
            return new CroppedBitmap(bitmap, new Int32Rect((int)bounds.Left, (int)bounds.Top,
                Math.Min((int)Math.Ceiling(bounds.Width), bitmap.PixelWidth - (int)bounds.Left),
                Math.Min((int)Math.Ceiling(bounds.Height), bitmap.PixelHeight - (int)bounds.Top)));
        }
        void SaveFrame(BitmapSource frame, string name)
        {
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(frame));
            using var output = File.Create(Path.Combine(root, name + ".png")); png.Save(output);
        }
        double ChangedPixels(BitmapSource a, BitmapSource b)
        {
            if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return 1;
            var stride = a.PixelWidth * 4; var left = new byte[stride * a.PixelHeight]; var right = new byte[left.Length];
            a.CopyPixels(left, stride, 0); b.CopyPixels(right, stride, 0);
            var different = 0;
            for (var index = 0; index < left.Length; index += 4)
                if (Math.Abs(left[index] - right[index]) > 12 || Math.Abs(left[index + 1] - right[index + 1]) > 12
                    || Math.Abs(left[index + 2] - right[index + 2]) > 12 || Math.Abs(left[index + 3] - right[index + 3]) > 12) different++;
            return different / (double)(a.PixelWidth * a.PixelHeight);
        }
        try
        {
            window.Show(); await Idle();
            ((DispatcherTimer)Field("_diagnosticTimer")!).Stop(); ((DispatcherTimer)Field("_logRefreshTimer")!).Stop();
            var power = (Button)Field("PowerButton")!;
            var offlineCheck = (Button)Field("CheckConnectionButton")!;
            var releaseBusy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var busy = (Task)Invoke("RunExclusiveAsync", (Func<Task>)(() => releaseBusy.Task))!;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Render);
                Check(!power.IsEnabled && ((Border)power.Template.FindName("B", power)).Opacity == 1,
                    "a brief workspace lock never dims the available start button while VPN is stopped");
                Check(!offlineCheck.IsEnabled && ((Border)offlineCheck.Template.FindName("B", offlineCheck)).Opacity < 1,
                    "actions unavailable with a stopped VPN retain their disabled appearance");
            }
            finally { releaseBusy.TrySetResult(); await busy; }
            Set("_pageAnimationsEnabled", new Func<bool>(() => true));
            var routes = (FrameworkElement)Field("RoutesPage")!;
            var journal = (FrameworkElement)Field("JournalPage")!;
            var layer = (Canvas)Field("PageTransitionLayer")!;
            var image = (Image)Field("PageTransitionImage")!;
            var host = (FrameworkElement)Field("MainPageHost")!;
            var log = (TextBox)Field("LogText")!;
            var table = (DataGrid)Field("ConnectionsGrid")!;
            var view = (ListCollectionView)table.ItemsSource;
            view.SortDescriptions.Add(new SortDescription("Destination", ListSortDirection.Descending));
            table.Columns[0].Width = new DataGridLength(211);
            log.Text = "Retained journal text"; log.Select(2, 5);
            var logSelection = log.SelectedText;
            Check(layer.Visibility == Visibility.Collapsed && image.Source is null, "startup does not animate or retain a transition snapshot");
            var originalSize = host.RenderSize;
            var before = CapturePage(); SaveFrame(before, "routes-before");
            ((Button)Field("JournalTabButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var sourceFrame = (BitmapSource?)image.Source;
            var firstFrame = CapturePage();
            var initialDifference = ChangedPixels(before, firstFrame);
            Console.WriteLine($"VISUAL first-frame difference: {initialDifference:P2}; frames: {root}");
            Check(initialDifference < .02, "first animation frame visually matches the outgoing page instead of revealing the new page through transparent gaps");
            Check(journal.IsVisible && !routes.IsVisible && layer.IsVisible && image.Source is not null,
                "real journal-tab click starts a smooth overlay while immediately selecting just the journal page");
            Check(!layer.IsHitTestVisible && !layer.Focusable && layer.Clip is RectangleGeometry { HasAnimatedProperties: true },
                "decorative transition cannot intercept pointer or keyboard interaction");
            Check(host.RenderSize == originalSize && routes.Opacity == 1 && journal.Opacity == 1 && layer.Opacity == 1,
                "the incoming page is fully rendered underneath the opaque transition without resizing the layout");
            layer.Visibility = Visibility.Hidden;
            var incomingFrame = CapturePage();
            journal.Opacity = 0;
            var emptyFrame = CapturePage();
            journal.Opacity = 1;
            layer.Visibility = Visibility.Visible;
            await Task.Delay(70);
            var remaining = ((RectangleGeometry)layer.Clip).Rect;
            var middleFrame = CapturePage();
            Check(remaining.Left > 0 && remaining.Width > 0 && remaining.Width < originalSize.Width
                && ChangedPixels(firstFrame, middleFrame) > .01
                && ChangedPixels(middleFrame, incomingFrame) > .01,
                "the moving edge transitions between two fully rendered pages");
            var leastContent = ChangedPixels(middleFrame, emptyFrame);
            var deadline = DateTime.UtcNow.AddMilliseconds(210);
            while (layer.IsVisible && DateTime.UtcNow < deadline)
            {
                await Task.Delay(12);
                leastContent = Math.Min(leastContent, ChangedPixels(CapturePage(), emptyFrame));
            }
            Check(leastContent > .01 && journal.Opacity == 1,
                "frame samples across the handoff never show the empty background");
            await Finish();
            Check(layer.Visibility == Visibility.Collapsed && image.Source is null && layer.Clip is null,
                "completed transition releases its bitmap and animation clock");
            var finalFrame = CapturePage();
            Check(ChangedPixels(finalFrame, incomingFrame) < .02, "final rendered frame matches the real incoming page without a completion flash");
            SaveFrame(firstFrame, "transition-first"); SaveFrame(middleFrame, "transition-middle"); SaveFrame(finalFrame, "journal-after");
            if (sourceFrame is not null) SaveFrame(sourceFrame, "transition-source");
            Check(AppSettings.Load(settingsPath).ShowEventLog, "animated journal-tab click still persists the selected page");
            Invoke("AnimateMainPage", false); await Task.Delay(45);
            var interruptedBefore = CapturePage();
            Invoke("AnimateMainPage", true);
            var interruptedAfter = CapturePage();
            Check(ChangedPixels(interruptedBefore, interruptedAfter) < .02,
                "reversing a partially rendered transition continues from its current composition without a jump");
            await Finish();
            SaveFrame(interruptedBefore, "interrupted-before"); SaveFrame(interruptedAfter, "interrupted-after");
            Invoke("AnimateMainPage", false);
            Invoke("AnimateMainPage", true);
            Invoke("AnimateMainPage", false);
            Check(routes.IsVisible && !journal.IsVisible && layer.IsVisible, "rapid reversals immediately keep the last requested page active");
            await Finish();
            Check(routes.IsVisible && !journal.IsVisible && image.Source is null,
                "old animation completions cannot restore a stale page or snapshot after rapid reversals");
            Check(ReferenceEquals(view, table.ItemsSource) && view.SortDescriptions.Single().PropertyName == "Destination"
                && table.Columns[0].Width.Value == 211 && log.Text == "Retained journal text" && log.SelectedText == logSelection,
                "switching pages preserves table view, sort, resized columns, journal text and selection");
            Invoke("AnimateMainPage", true);
            Invoke("SelectMainPage", false);
            await Finish();
            Check(routes.IsVisible && !journal.IsVisible && image.Source is null && layer.Visibility == Visibility.Collapsed,
                "immediate settings/profile navigation cancels any pending transition safely");
            Set("_pageAnimationsEnabled", new Func<bool>(() => false));
            Invoke("AnimateMainPage", true);
            Check(journal.IsVisible && !routes.IsVisible && image.Source is null && !layer.IsVisible,
                "reduced-motion preference skips snapshot allocation and animation");
            Set("_pageAnimationsEnabled", new Func<bool>(() => true));
            Invoke("AnimateMainPage", false);
            window.Width += 40; await Idle();
            Check(routes.IsVisible && !layer.IsVisible && image.Source is null,
                "resizing the window clears a transition rather than stretching stale content");
            window.ApplyInterfaceScale(1.25); await Idle();
            var scaledBefore = CapturePage();
            Invoke("AnimateMainPage", true);
            var scaledFirst = CapturePage();
            var scaledDifference = ChangedPixels(scaledBefore, scaledFirst);
            Console.WriteLine($"VISUAL scaled first-frame difference: {scaledDifference:P2}");
            SaveFrame(scaledBefore, "scaled-before"); SaveFrame(scaledFirst, "scaled-first");
            Check(scaledDifference < .02, "scaled transition preserves the outgoing content position without a duplicated scale transform");
            Check(image.Source is System.Windows.Media.Imaging.BitmapSource frame
                && Math.Abs(frame.DpiX - VisualTreeHelper.GetDpi(host).PixelsPerInchX * 1.25) < .1,
                "transition snapshot accounts for interface scale and monitor DPI");
            await Finish(); window.ApplyInterfaceScale(1); await Idle();
            foreach (var theme in new[] { "Аврора", "Светлая морфоцитная" })
                foreach (var startJournal in new[] { false, true })
                {
                    ThemeManager.Apply(theme); window.ApplyInterfaceScale(1.5);
                    Invoke("SelectMainPage", startJournal); await Idle();
                    var themedBefore = CapturePage(); Invoke("AnimateMainPage", !startJournal); var themedFirst = CapturePage();
                    var difference = ChangedPixels(themedBefore, themedFirst);
                    Console.WriteLine($"VISUAL {theme}, reverse={startJournal}, scale=150%: {difference:P2}");
                    if (difference >= .02)
                    {
                        SaveFrame(themedBefore, "large-before"); SaveFrame(themedFirst, "large-first");
                        Console.WriteLine($"VISUAL large geometry: image={image.RenderSize}; offset={Canvas.GetLeft(image)};{Canvas.GetTop(image)}; dpi={(image.Source as BitmapSource)?.DpiX}; scale={UiScaleManager.CurrentScale}");
                    }
                    Check(difference < .02, "first-frame position and background remain stable in both themes/directions at 150% scale");
                    await Finish();
                }
            window.ApplyInterfaceScale(1); ThemeManager.Apply("Аврора"); await Idle();
            foreach(var target in new[]{"SubscriptionPage","RoutesPage","JournalPage","RoutesPage","SubscriptionPage","JournalPage","SubscriptionPage"})
            {
                var tab=target switch { "SubscriptionPage"=>"SubscriptionTabButton","JournalPage"=>"JournalTabButton",_=>"RoutesTabButton" };
                var incoming=(FrameworkElement)Field(target)!;
                ((Button)Field(tab)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(incoming.IsVisible&&incoming.Opacity==1&&layer.IsVisible&&layer.Opacity==1,$"{tab} keeps the new page visible behind the outgoing image");
                await Task.Delay(30);
                Check(incoming.Opacity==1&&layer.Opacity==1&&layer.Clip is RectangleGeometry,$"{tab} reveals a moving portion of the new page at full opacity");
                var finishBy=DateTime.UtcNow.AddMilliseconds(240);
                while(layer.IsVisible&&DateTime.UtcNow<finishBy) await Task.Delay(5);
                Check(!layer.IsVisible&&image.Source is null&&incoming.IsVisible,$"{tab} removes the outgoing snapshot as soon as the reveal finishes");
                await Finish();
                Check(incoming.Opacity==1&&!incoming.HasAnimatedProperties,$"{tab} finishes with a sharp page and no retained animation clock");
            }
            Check(File.ReadAllText(path) == initial && !((CoreProcessManager)Field("_core")!).HasTrackedProcess,
                "decorative page transitions neither edit routing nor start a VPN");
            Invoke("AnimateMainPage", false);
            Set("_allowClose", true); window.Close(); await Task.Delay(260);
            Check(image.Source is null && layer.Clip is null, "closing during a transition releases its image and cannot trigger late navigation");
            return count;
        }
        finally
        {
            Set("_allowClose", true); window.Close(); UiScaleManager.CurrentScale = 1; ThemeManager.Apply(null);
        }
    }
}
