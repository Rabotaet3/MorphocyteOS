namespace MorphocyteRouter;

internal enum RecoveryAttemptResult { Connected, Retry, Stop }

internal static class ConnectionRecovery
{
    internal static async Task<bool> RunAsync(Func<CancellationToken, Task<RecoveryAttemptResult>> attempt,
        Func<bool> requested, CancellationToken token, Func<int, CancellationToken, Task>? wait = null)
    {
        for (var index = 0; index < 3; index++)
        {
            token.ThrowIfCancellationRequested();
            if (!requested()) return false;
            var result = await attempt(token);
            token.ThrowIfCancellationRequested();
            if (!requested()) return false;
            if (result == RecoveryAttemptResult.Connected) return true;
            if (result == RecoveryAttemptResult.Stop || !requested()) return false;
            if (index < 2)
            {
                if (wait is not null) await wait(index, token);
                else await Task.Delay(TimeSpan.FromSeconds(index == 0 ? 3 : 6), token);
            }
        }
        return false;
    }
}
