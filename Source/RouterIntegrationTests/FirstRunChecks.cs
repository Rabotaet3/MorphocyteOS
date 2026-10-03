using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunFreshLaunchChecks(string scratch)
    {
        var settingsPath = Path.Combine(scratch, "first-launch-settings.json");
        var settings = AppSettings.Load(settingsPath);
        if (!settings.AutoCheckUpdates) throw new Exception("Fresh installations must enable the startup update check.");
        settings.AutoCheckUpdates = false; // Keep this UI test offline; networking has an injected test suite.
        var window = new MainWindow(settings, Path.Combine(scratch, "first-launch.log"))
            { Width = 1120, Height = 760, ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var count = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            count++;
            Console.WriteLine("PASS: " + message);
        }
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations");
            await gate.WaitAsync(); gate.Release();
            window.UpdateLayout();
            Check(File.Exists(settings.ConfigPath) && File.Exists(settings.CorePath) && settings.ConfigPath.StartsWith(scratch),
                "fresh launch locates the side-by-side core and prepares the neutral template in isolated local storage");
            Check(((ObservableCollection<DomainRule>)Field("_rules")).Count == 0 && Field("_document") is ClashConfigDocument { HasVpnConnection: false },
                "fresh template contains neither credentials nor a real VPN connection or author's rules");
            Check(!((CoreProcessManager)Field("_core")).IsRunning, "fresh launch does not start a core");
            Check(typeof(MainWindow).GetField("OnboardingCard", BindingFlags.Instance | BindingFlags.NonPublic) is null
                && !Descendants<Button>(window).Any(button => Equals(button.Content, "Импорт профиля"))
                && !((Button)Field("PowerButton")).IsEnabled,
                "fresh launch has no onboarding overlay or import button in the main toolbar, and blocks empty-template VPN start");
            var toggle = (Button)Field("JournalTabButton");
            var text = (TextBox)Field("LogText");
            Check(toggle.IsVisible && !text.IsVisible,
                "fresh launch opens routes while keeping the journal tab available");
            Check(Descendants<Button>(window).Any(button => Equals(button.ToolTip, "Настройки") && button.IsVisible),
                "fresh launch offers visible settings to choose profile and core");
            Check(!Descendants<Button>(window).Any(button => Equals(button.ToolTip, "Проверка скорости")),
                "the release does not show the removed speed-test tool");
            CaptureInteraction(window, Path.Combine(scratch, "release-first-launch.png"));
            var scaleMethod = typeof(MainWindow).GetMethod("ApplyInterfaceScale", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var status = (TextBlock)Field("StatusText");
            var baselineFont = RenderedFontSize(status, window);
            foreach (var scale in new[] { 1.25, 1.5 })
            {
                scaleMethod.Invoke(window, new object[] { scale });
                window.UpdateLayout();
                Check(Math.Abs(RenderedFontSize(status, window) / baselineFont - scale) < .02,
                    $"first-run window fonts scale to {scale * 100:0} percent");
                CaptureInteraction(window, Path.Combine(scratch, $"release-scale-{scale * 100:0}.png"));
            }
            scaleMethod.Invoke(window, new object[] { 1d });
            window.Width = 1120;
            window.Height = 760;
            window.UpdateLayout();
            settings.Save();
            var saved = AppSettings.Load(settingsPath);
            Check(saved.ConfigPath == settings.ConfigPath && saved.CorePath == settings.CorePath && saved.InterfaceScale == 1 && !saved.AutoCheckUpdates,
                "fresh settings persist the prepared defaults without enabling automatic network checks");
            return count;
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
        }
    }
}
