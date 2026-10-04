namespace MorphocyteRouter;

// Keep COM work on the requesting UI thread. Only the most recent theme may
// retry; no old preview is allowed to overwrite a newer theme or a rollback.
internal sealed class ShellThemeRefresh : IDisposable
{
    private CancellationTokenSource? _pending;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    private bool _disposed;

    internal void Request(Action apply, Action<Exception> failed)
    {
        if (_disposed) return;
        _pending?.Cancel();
        var lifetime = new CancellationTokenSource();
        _pending = lifetime;
        Completion = RefreshAsync(lifetime, apply, failed);
    }

    private async Task RefreshAsync(CancellationTokenSource lifetime, Action apply, Action<Exception> failed)
    {
        Exception? lastError = null;
        try
        {
            // Explorer may still be processing the former shortcut or window
            // icon. Re-notify even when the shortcut already has the right icon.
            foreach (var delay in new[] { 0, 200, 450, 950 })
            {
                if (delay != 0) await Task.Delay(delay, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                try { apply(); lastError = null; }
                catch (Exception error) { lastError = error; }
            }
            if (lastError is not null) failed(lastError);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_pending, lifetime)) _pending = null;
            lifetime.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _pending?.Cancel();
    }
}
