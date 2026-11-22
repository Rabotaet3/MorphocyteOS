using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private const int ExtendedWindowStyle = -20;
    private const int LayeredStyle = 0x00080000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int NativeWindowStyle(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    private static bool HasOpaqueNativeSurface(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && IsWindowVisible(handle) && window.IsVisible && !window.AllowsTransparency
            && window.Opacity == 1 && window.Background is SolidColorBrush { Color.A: 255 }
            && (NativeWindowStyle(handle, ExtendedWindowStyle) & LayeredStyle) == 0;
    }

    private static bool HasPaintedInterior(Window window)
    {
        const int width = 110;
        const int height = 72;
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0) return false;
        var bitmap = new RenderTargetBitmap(width, height, 96 * width / window.ActualWidth,
            96 * height / window.ActualHeight, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        // Low-resolution WPF rasterization can round an opaque pixel down by a few alpha levels.
        for (var y = 9; y < height - 9; y++)
            for (var x = 13; x < width - 13; x++)
                if (pixels[y * stride + x * 4 + 3] < 248) return false;
        return true;
    }

    private static async Task<int> RunRenderStabilityChecks(string scratch, string fakeExe)
    {
        var count = 0;
        void Check(bool ok, string description)
        {
            if (!ok) throw new InvalidOperationException("FAIL: " + description);
            count++;
            Console.WriteLine("PASS: " + description);
        }
        async Task StressScroll(Window window, ScrollViewer scroll, string label)
        {
            window.UpdateLayout();
            Check(scroll.ScrollableHeight > 0, label + " has scrollable content");
            scroll.ScrollToTop();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var maxOffset = 0d;
            var nativeStable = true;
            var framesPainted = true;
            for (var pulse = 0; pulse < 48; pulse++)
            {
                var delta = pulse is < 16 or >= 32 ? -120 : 120;
                for (var packet = 0; packet < 4; packet++)
                {
                    var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    scroll.RaiseEvent(preview);
                    if (!preview.Handled)
                        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                        { RoutedEvent = Mouse.MouseWheelEvent });
                }
                await Task.Delay(13);
                maxOffset = Math.Max(maxOffset, scroll.VerticalOffset);
                nativeStable &= HasOpaqueNativeSurface(window);
                if (pulse % 8 == 0) framesPainted &= HasPaintedInterior(window);
            }
            await Task.Delay(225);
            if (!nativeStable || !framesPainted || maxOffset <= 5)
                Console.WriteLine($"{label}: native={nativeStable}; painted={framesPainted}; maxOffset={maxOffset:0.0}");
            Check(nativeStable && framesPainted && HasOpaqueNativeSurface(window) && HasPaintedInterior(window) && maxOffset > 5,
                label + " remains painted during 192 rapid wheel events and direction changes");
        }

        var profile = Path.Combine(scratch, "visual-profile.yaml");
        var names = Enumerable.Range(1, 220).Select(index => $"Server {index:D3} Germany Bridge").ToArray();
        var proxies = names.Select((name, index) =>
            $"  - {{name: '{name}', type: socks5, server: 127.0.0.1, port: {13001 + index}}}");
        var serverGroup = "  - {name: VPN, type: select, proxies: [" + string.Join(", ", names.Select(name => $"'{name}'")) + "]}";
        var rules = Enumerable.Range(1, 150).Select(index => $"  - DOMAIN,host-{index:D3}.example,DIRECT");
        File.WriteAllLines(profile, new[] { "proxies:" }.Concat(proxies).Append("proxy-groups:").Append(serverGroup)
            .Append("rules:").Concat(rules).Append("  - MATCH,DIRECT"));
        var settings = AppSettings.Load(Path.Combine(scratch, "settings.json"));
        settings.ConfigPath = profile;
        settings.CorePath = fakeExe;
        settings.AutoCheckUpdates = false;
        settings.Theme = "Тёмная морфоцитная";
        var window = new MainWindow(settings, Path.Combine(scratch, "visual.log")) { ShowInTaskbar = false };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        async Task Idle()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = Field<SemaphoreSlim>("_operations");
            await gate.WaitAsync(); gate.Release();
            window.UpdateLayout();
        }
        async Task Tab(string field)
        {
            Field<Button>(field).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
            await Task.Delay(215);
        }
        async Task Modal(string title, Action open, Func<Window, Task> test)
        {
            Exception? error = null;
            var seen = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
            timer.Tick += async (_, _) =>
            {
                var dialog = window.OwnedWindows.OfType<Window>().FirstOrDefault(candidate => candidate.IsVisible && candidate.Title == title);
                if (dialog is null) return;
                timer.Stop();
                seen = true;
                try
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    dialog.UpdateLayout();
                    var nativeStable = HasOpaqueNativeSurface(dialog);
                    var painted = HasPaintedInterior(dialog);
                    if (!nativeStable || !painted)
                        Console.WriteLine($"{title}: native={nativeStable}; painted={painted}; width={dialog.ActualWidth}; height={dialog.ActualHeight}; background={dialog.Background}");
                    Check(nativeStable && painted, title + " opens with a painted native surface");
                    var background = Field<FrameworkElement>("WindowContent");
                    Check(background.Effect is null && background.CacheMode is null, title + " uses the shared shade without recreating the owner surface");
                    await test(dialog);
                }
                catch (Exception ex) { error = ex; }
                finally { dialog.Close(); }
            };
            timer.Start();
            try { open(); }
            finally { timer.Stop(); }
            if (error is not null) throw error;
            Check(seen, title + " rendered and closed normally");
            await Task.Delay(190);
        }

        try
        {
            window.Show();
            await Idle();
            Check(HasOpaqueNativeSurface(window) && HasPaintedInterior(window), "main window starts fully painted without a layered surface");
            await StressScroll(window, Descendants<ScrollViewer>(Field<ListView>("RulesList")).First(), "routing rules");
            await Tab("SubscriptionTabButton");
            foreach (var (theme, scale) in new[] { ("Тёмная морфоцитная", 1d), ("Светлая морфоцитная", 1.5) })
            {
                ThemeManager.Apply(theme); window.ApplyInterfaceScale(scale);
                await Idle();
                var list = (VpnServerListView)Field<ContentControl>("SubscriptionServersHost").Content;
                await StressScroll(window, Descendants<ScrollViewer>(list).Single(scroll => scroll.Name == "VpnServerScroll"),
                    $"220 subscription servers / {theme} / {scale:P0}");
            }
            ThemeManager.Apply(settings.Theme);
            window.ApplyInterfaceScale(1);
            await Tab("JournalTabButton");
            var rows = Enumerable.Range(1, 180).Select(index => new ConnectionActivity(
                $"App{index}.exe", $"host-{index}.example:443", "TCP", "Match", "DIRECT", "connection-" + index)).ToArray();
            typeof(MainWindow).GetField("_connections", flags)!.SetValue(window, rows);
            Call("FilterConnections");
            await Idle();
            await StressScroll(window, Descendants<ScrollViewer>(Field<DataGrid>("ConnectionsGrid")).First(), "connection journal");
            var logSwitch = Field<CheckBox>("CoreLogToggle");
            logSwitch.IsChecked = true;
            logSwitch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
            Call("AppendLogEntries", (object)Enumerable.Range(1, 350).Select(index => $"Log line {index}").ToArray());
            var log = Field<TextBox>("LogText");
            await StressScroll(window, Descendants<ScrollViewer>(log).First(), "text journal");
            var detached = (Window)Call("DetachCoreLog")!;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                detached.UpdateLayout();
                await StressScroll(detached, Descendants<ScrollViewer>(log).First(), "detached text journal");
            }
            finally { detached.Close(); }

            await Modal("НАСТРОЙКИ", () => UtilityDialogs.ShowSettings(window, profile, fakeExe, settings.Theme, false, 1, false, onSave: null),
                async dialog =>
                {
                    var picker = Descendants<ComboBox>(dialog).Single(combo => combo.Items.Cast<object>().Contains("Аврора"));
                    var pop = (Popup)picker.Template.FindName("PART_Popup", picker);
                    picker.IsDropDownOpen = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var native = (HwndSource?)PresentationSource.FromVisual(pop.Child);
                    Check(pop.IsOpen && native is not null && (NativeWindowStyle(native.Handle, ExtendedWindowStyle) & LayeredStyle) == 0,
                        "settings dropdown paints in an opaque popup");
                    picker.IsDropDownOpen = false;
                    await StressScroll(dialog, Descendants<ScrollViewer>(dialog).Single(scroll => scroll.Name == "SettingsScroll"), "settings");
                });
            await Modal("ЧаВо?", () => UtilityDialogs.ShowFaq(window), dialog =>
                StressScroll(dialog, Descendants<ScrollViewer>(dialog).First(), "help"));
            Check(HasOpaqueNativeSurface(window) && !Field<CoreProcessManager>("_core").HasTrackedProcess,
                "scrolling and modal changes leave the application visible with no VPN process started");
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", flags)!.SetValue(window, true);
            window.Close();
            UiScaleManager.CurrentScale = 1;
            ThemeManager.Apply(null);
        }
        return count;
    }
}
