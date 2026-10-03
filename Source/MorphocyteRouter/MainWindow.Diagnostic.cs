using System.Windows;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private CancellationTokenSource? _manualDiagnosticLifetime;
    private IReadOnlyList<DiagnosticStep> _lastDiagnostic = Array.Empty<DiagnosticStep>();
    private bool _manualDiagnosticRunning, _diagnosticCoreWasRunning;
    private DateTimeOffset _diagnosticCheckedAt;
    private async void RunDiagnostic_Click(object sender, RoutedEventArgs e) => await RunConnectionDiagnosticAsync();
    private void CancelDiagnostic_Click(object sender, RoutedEventArgs e) => _manualDiagnosticLifetime?.Cancel();
    private void CopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ConnectionDiagnostic.Report(_lastDiagnostic, _diagnosticCoreWasRunning, _diagnosticCheckedAt)); SetLog("Диагностика скопирована без профиля, ссылок и ключей."); }
        catch { SetLog("Не удалось скопировать диагностику."); }
    }

    internal async Task RunConnectionDiagnosticAsync()
    {
        if (_manualDiagnosticRunning || _closing) return;
        _manualDiagnosticLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var lifetime = _manualDiagnosticLifetime;
        var diagnostics = _diagnostics;
        _diagnosticCheckedAt = DateTimeOffset.Now; _diagnosticCoreWasRunning = _core.IsRunning;
        _manualDiagnosticRunning = true; _lastDiagnostic = Array.Empty<DiagnosticStep>();
        DiagnosticResultsText.Text = "Проверяю подключение…";
        DiagnosticResultsScroll.Visibility = Visibility.Visible;
        CancelDiagnosticButton.Visibility = Visibility.Visible;
        RunDiagnosticButton.IsEnabled = false; CopyDiagnosticButton.IsEnabled = false;
        var steps = new List<DiagnosticStep>();
        try
        {
            var runtimeDocument = _runtimeConfigPath is { } runtime ? ClashConfigDocument.Load(runtime) : _document;
            _lastDiagnostic = await ConnectionDiagnostic.RunAsync(_core.IsRunning, runtimeDocument, diagnostics, lifetime.Token, step =>
            {
                if (lifetime.IsCancellationRequested || _closing) return;
                steps.Add(step); DiagnosticResultsText.Text = string.Join("\n", steps.Select(item => item.Name + ": " + item.Result));
            });
        }
        catch (OperationCanceledException) { if (!_closing) DiagnosticResultsText.Text = lifetime.IsCancellationRequested ? "Проверка отменена." : "Проверка не завершилась за отведённое время."; }
        catch { if (!_closing) DiagnosticResultsText.Text = "Не удалось завершить проверку. Проверь журнал."; }
        finally
        {
            lifetime.Dispose(); _manualDiagnosticLifetime = null; _manualDiagnosticRunning = false;
            if (!_closing)
            {
                RunDiagnosticButton.IsEnabled = true;
                CopyDiagnosticButton.IsEnabled = _lastDiagnostic.Count > 0;
                CancelDiagnosticButton.Visibility = Visibility.Collapsed;
            }
        }
    }
}
