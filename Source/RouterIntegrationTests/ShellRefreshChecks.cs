using System.IO;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunShellRefreshChecks()
    {
        var count = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
        using (var refresh = new ShellThemeRefresh())
        {
            var attempts = 0; var failures = 0;
            refresh.Request(() => { if (++attempts < 3) throw new IOException("fixture busy shortcut"); }, _ => failures++);
            await refresh.Completion;
            Check(attempts == 4 && failures == 0, "temporary shortcut write failure is retried without a premature error");
            var themes = new List<string>();
            refresh.Request(() => themes.Add("previous"), _ => failures++);
            var previous = refresh.Completion;
            refresh.Request(() => themes.Add("current"), _ => failures++);
            await refresh.Completion; await previous;
            Check(themes.Count(theme => theme == "previous") == 1 && themes.Count(theme => theme == "current") == 4 && themes.Last() == "current", "fast theme changes cancel all stale shell refreshes");
            var rollbacks = new List<string>();
            refresh.Request(() => rollbacks.Add("preview"), _ => failures++);
            refresh.Request(() => rollbacks.Add("saved"), _ => failures++);
            await refresh.Completion;
            Check(rollbacks.Count(theme => theme == "preview") == 1 && rollbacks.Last() == "saved", "canceling settings leaves the saved theme as the final shell update");
            var finalFailure = 0;
            refresh.Request(() => throw new IOException("fixture persistent failure"), _ => finalFailure++);
            await refresh.Completion;
            Check(finalFailure == 1, "persistent shell refresh failure is reported exactly once after retries");
        }
        var stopped = new ShellThemeRefresh(); var calls = 0;
        stopped.Request(() => calls++, _ => throw new Exception("unexpected failure"));
        var completion = stopped.Completion; stopped.Dispose(); await completion;
        stopped.Request(() => calls++, _ => throw new Exception("unexpected failure"));
        Check(calls == 1, "closing the application cancels pending refreshes and never touches disposed icon handles");
        return count;
    }
}
