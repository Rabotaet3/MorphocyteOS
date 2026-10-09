using System.Windows;

namespace MorphocyteRouter;

public partial class MainWindow
{
    private CancellationTokenSource? _manualDiagnosticLifetime;
    private IReadOnlyList<DiagnosticStep> _lastDiagnostic = Array.Empty<DiagnosticStep>();
    private bool _manualDiagnosticRunning, _diagnosticCoreWasRunning;
    private DateTimeOffset _diagnosticCheckedAt;
    private Window? _diagnosticWindow;
    private string _diagnosticStatus = "Проверка ещё не выполнялась.";
    internal event Action? DiagnosticChanged;
    internal IReadOnlyList<DiagnosticStep> DiagnosticSteps => _lastDiagnostic;
    internal bool DiagnosticRunning => _manualDiagnosticRunning;
    internal bool CanCopyDiagnostic => _lastDiagnostic.Count > 0 && !_manualDiagnosticRunning;
    internal string DiagnosticStatus => _diagnosticStatus;
    internal string DiagnosticReport => ConnectionDiagnostic.Report(_lastDiagnostic, _diagnosticCoreWasRunning, _diagnosticCheckedAt);

    private void RunDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_diagnosticWindow is { } existing) { existing.Activate(); return; }
        ConnectionDiagnosticDialog.Show(this);
    }

    internal void SetDiagnosticWindow(Window? dialog) => _diagnosticWindow = dialog;
    internal void CancelConnectionDiagnostic() => _manualDiagnosticLifetime?.Cancel();
    internal bool CopyConnectionDiagnostic()
    {
        if (!CanCopyDiagnostic) return false;
        try { Clipboard.SetText(DiagnosticReport); SetLog("Диагностика скопирована без профиля, ссылок и ключей."); return true; }
        catch { SetLog("Не удалось скопировать диагностику."); return false; }
    }

    internal async Task RunConnectionDiagnosticAsync()
    {
        if (_manualDiagnosticRunning || _closing) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _manualDiagnosticLifetime = lifetime;
        var diagnostics = _diagnostics;
        _diagnosticCheckedAt = DateTimeOffset.Now; _diagnosticCoreWasRunning = _core.IsRunning;
        _manualDiagnosticRunning = true; _lastDiagnostic = Array.Empty<DiagnosticStep>();
        _diagnosticStatus = "Проверяю подключение…";
        DiagnosticChanged?.Invoke();
        var steps = new List<DiagnosticStep>();
        try
        {
            var runtimeDocument = _runtimeConfigPath is { } runtime && !CoreBackend.IsSingBox(_settings.CorePath)
                ? ClashConfigDocument.Load(runtime) : _document;
            _lastDiagnostic = await ConnectionDiagnostic.RunAsync(_core.IsRunning, runtimeDocument, diagnostics, lifetime.Token, step =>
            {
                if (lifetime.IsCancellationRequested || _closing) return;
                steps.Add(step); _lastDiagnostic = steps.ToArray(); DiagnosticChanged?.Invoke();
            }, fullTunnel: _settings.FullTunnel);
            _diagnosticStatus = "Проверка завершена · " + _diagnosticCheckedAt.ToString("HH:mm:ss");
        }
        catch (OperationCanceledException) { _diagnosticStatus = lifetime.IsCancellationRequested ? "Проверка отменена." : "Время проверки истекло."; }
        catch { _diagnosticStatus = "Не удалось завершить проверку. Подробности — в журнале."; }
        finally
        {
            _manualDiagnosticLifetime = null; _manualDiagnosticRunning = false;
            if (!_closing) DiagnosticChanged?.Invoke();
        }
    }
}
