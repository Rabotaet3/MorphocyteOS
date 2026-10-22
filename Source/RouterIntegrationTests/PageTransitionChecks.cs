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
            Check(!layer.IsHitTestVisible && !layer.Focusable && layer.HasAnimatedProperties,
                "decorative transition cannot intercept pointer or keyboard interaction");
            Check(host.RenderSize == originalSize && routes.Opacity == 1 && journal.Opacity == 1,
                "page transition does not move layout, sidebar, or the real page opacity");
            layer.Visibility = Visibility.Hidden;
            var incomingFrame = CapturePage();
            layer.Visibility = Visibility.Visible;
            await Task.Delay(60);
            var middleOpacity = layer.Opacity;
            var middleFrame = CapturePage();
            Check(middleOpacity is > 0 and < 1 && ChangedPixels(firstFrame, middleFrame) > .01
                && ChangedPixels(middleFrame, incomingFrame) > .01,
                "rendered middle frame is an actual intermediate blend, neither an instant cut nor a delayed jump");
            await Finish();
            Check(layer.Visibility == Visibility.Collapsed && image.Source is null && !layer.HasAnimatedProperties,
                "completed fade releases its bitmap and animation clock");
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
            Check(File.ReadAllText(path) == initial && !((CoreProcessManager)Field("_core")!).HasTrackedProcess,
                "decorative page transitions neither edit routing nor start a VPN");
            Invoke("AnimateMainPage", false);
            Set("_allowClose", true); window.Close(); await Task.Delay(260);
            Check(image.Source is null && !layer.HasAnimatedProperties, "closing during a transition releases its image and cannot trigger late navigation");
            return count;
        }
        finally
        {
            Set("_allowClose", true); window.Close(); UiScaleManager.CurrentScale = 1; ThemeManager.Apply(null);
        }
    }
}
