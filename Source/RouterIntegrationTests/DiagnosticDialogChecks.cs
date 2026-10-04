using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MorphocyteRouter;

internal partial class Program
{
    private sealed class DialogDiagnosticHandler : HttpMessageHandler
    {
        internal bool Block;
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Block) await Task.Delay(Timeout.Infinite, token);
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/version" => "{\"version\":\"Fixture\"}",
                "/proxies/VPN" => "{\"type\":\"Selector\",\"now\":\"Server\"}",
                "/proxies/Server" => "{\"type\":\"Hysteria2\"}",
                "/dns/query" => "{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"1.1.1.1\"}]}",
                "/proxies/VPN/delay" => "{\"delay\":48}",
                _ => throw new Exception("Unexpected test endpoint: " + request.RequestUri.AbsolutePath)
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private static async Task<int> RunDiagnosticDialogChecks(string scratch, string fakeExe)
    {
        var root = Path.Combine(scratch, "diagnostic-dialog"); Directory.CreateDirectory(root);
        var config = Path.Combine(root, "profile.yaml");
        File.WriteAllText(config, "tun:\n  enable: true\nproxies:\n  - {name: Server, type: hysteria2, server: vpn.example, port: 443, password: test-only}\nproxy-groups:\n  - {name: VPN, type: select, proxies: [Server]}\ndns:\n  enable: true\nrules:\n  - MATCH,DIRECT\n");
        var settings = AppSettings.Load(Path.Combine(root, "settings.json")); settings.ConfigPath = config;
        settings.CorePath = fakeExe; settings.AutoCheckUpdates = false; settings.Theme = "Закат";
        var window = new MainWindow(settings, Path.Combine(root, "test.log")) { ShowInTaskbar = false };
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var core = (CoreProcessManager)Field("_core");
        var handler = new DialogDiagnosticHandler(); using var diagnostics = new CoreDiagnostics(handler);
        CoreDiagnostics? previous = null; var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var gate = (SemaphoreSlim)Field("_operations"); await gate.WaitAsync(); gate.Release();
            await (Task)typeof(MainWindow).GetMethod("StartCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { false })!;
            previous = (CoreDiagnostics)Field("_diagnostics");
            typeof(MainWindow).GetField("_diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, diagnostics);
            var page = (FrameworkElement)Field("JournalPage");
            Check(!Descendants<TextBlock>(page).Any(text => text.Name == "DiagnosticResultsText") &&
                !Descendants<Button>(page).Any(button => button.Name == "CopyDiagnosticButton"), "journal has no inline diagnostic statistics or copy button taking table space");
            Exception? error = null; var phase = 0; var seen = false;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                var dialog = window.OwnedWindows.OfType<Window>().FirstOrDefault(value => value.Title == "ДИАГНОСТИКА ПОДКЛЮЧЕНИЯ");
                if (dialog is null) return;
                seen = true;
                try
                {
                    if (DateTime.UtcNow > deadline) throw new Exception("Diagnostic dialog test timed out: " + window.DiagnosticStatus + " / " + string.Join("; ", window.DiagnosticSteps.Select(step => step.Name + ": " + step.Result)));
                    var repeat = Descendants<Button>(dialog).Single(button => button.Name == "RepeatDiagnosticButton");
                    var copy = Descendants<Button>(dialog).Single(button => button.Name == "CopyDiagnosticButton");
                    if (phase == 0 && !window.DiagnosticRunning && window.DiagnosticSteps.Count == 5)
                    {
                        Check(window.DiagnosticSteps.Count == 5 && copy.IsEnabled && repeat.IsEnabled, "temporary diagnostic window displays all stages and copy/repeat controls");
                        Check(window.DiagnosticSteps.Count(step => step.State == DiagnosticState.Success) == 4, "diagnostic badges distinguish actual success from informational TCP skip");
                        Check(((FrameworkElement)Field("WindowContent")).Effect is System.Windows.Media.Effects.BlurEffect, "diagnostic window uses the shared blurred backdrop");
                        Check(!window.DiagnosticReport.Contains("test-only") && !window.DiagnosticReport.Contains("vpn.example"), "copyable diagnostic report excludes credentials and server address");
                        foreach (var scale in new[] { .8, 1d, 1.5 })
                        {
                            UiScaleManager.Apply(dialog, (FrameworkElement)dialog.Content, scale);
                            dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight; dialog.UpdateLayout();
                            foreach (var button in new[] { repeat, copy })
                            {
                                var bounds = button.TransformToAncestor((FrameworkElement)dialog.Content).TransformBounds(new Rect(button.RenderSize));
                                Check(bounds.Left >= 0 && bounds.Right <= ((FrameworkElement)dialog.Content).ActualWidth + 1, "diagnostic footer retains controls at scale " + scale);
                            }
                            CaptureInteraction(dialog, Path.Combine(root, "diagnostic-" + (int)(scale * 100) + ".png"));
                        }
                        UiScaleManager.Apply(dialog, (FrameworkElement)dialog.Content, 1);
                        var before = handler.Requests; repeat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(handler.Requests > before, "repeat button performs a fresh diagnosis rather than showing a cached report");
                        handler.Block = true; phase = 1; repeat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (phase == 1 && window.DiagnosticRunning)
                    {
                        Check(!repeat.IsEnabled && !copy.IsEnabled && Descendants<Button>(dialog).Single(button => button.Name == "CancelDiagnosticButton").IsVisible, "pending diagnosis disables repeat and exposes cancellation");
                        timer.Stop(); dialog.Close();
                    }
                }
                catch (Exception ex) { error = ex; timer.Stop(); dialog.Close(); }
            };
            timer.Start();
            ((Button)Field("RunDiagnosticButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            timer.Stop(); if (error is not null) throw error;
            await Task.Delay(150);
            Check(seen && !window.DiagnosticRunning && window.DiagnosticStatus.Contains("отменена"), "closing the temporary window cancels its pending network check");
            handler.Block = false; await window.RunConnectionDiagnosticAsync();
            Check(window.DiagnosticSteps.Count == 5, "diagnosis can run again after cancellation without a stale lifetime or disabled state");
        }
        finally
        {
            window.CancelConnectionDiagnostic();
            if (previous is not null) typeof(MainWindow).GetField("_diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, previous);
            await core.StopAsync();
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close(); UiScaleManager.CurrentScale = 1; ThemeManager.Apply(null);
        }
        return count;
    }
}
