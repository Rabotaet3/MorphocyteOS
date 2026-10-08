using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunTelemetryChecks()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        var history = new ConnectionHealth();
        history.Record(20); history.Record(null); history.Record(80);
        Check(history.Count == 3 && history.LastDelay == 80, "ping retains the latest measurement without a misleading availability percentage");
        for (var i = 0; i < 20; i++) history.Record(30);
        Check(history.Count == 20 && history.LastDelay == 30, "ping sample count stays bounded");
        history.Clear();
        Check(history.Count == 0 && history.LastDelay is null && ConnectionHealth.FormatRate(0) == "0 Б/с" && ConnectionHealth.FormatRate(-1) == "—",
            "reset clears ping history and distinguishes measured zero traffic from unavailable data");
        var authorized = false;
        using var api = new CoreDiagnostics(new DiagnosticHandler(request =>
        {
            authorized |= request.Headers.Authorization?.Scheme == "Bearer";
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/traffic" => "{\"up\":1024,\"down\":1048576}\n{\"up\":999,\"down\":999}\n",
                "/proxies/VPN" => "{\"now\":\"Nested\"}",
                "/proxies/Nested" => "{\"now\":\"DIRECT\"}",
                _ => throw new Exception("Unexpected diagnostic request")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
        }));
        var traffic = await api.ReadTrafficAsync(default);
        Check(traffic is { Upload: 1024, Download: 1048576 } && authorized, "authenticated traffic reader uses the first streamed frame and keeps upload/download directions");
        Check(await api.ReadSelectionAsync("VPN", default) == "DIRECT", "nested group selection detects a direct route before claiming VPN reachability");
        foreach (var invalid in new[] { "{\"up\":-1,\"down\":1}\n", new string('x', 32769) + "\n" })
        {
            using var invalidApi = new CoreDiagnostics(new DiagnosticHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(invalid) }));
            try { await invalidApi.ReadTrafficAsync(default); throw new Exception("Invalid traffic accepted"); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { Check(true, "invalid or oversized traffic data is rejected without showing fabricated speeds"); }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await api.ReadTrafficAsync(cancelled.Token); throw new Exception("Cancelled telemetry accepted"); }
        catch (OperationCanceledException) { Check(true, "traffic read respects core-lifetime cancellation"); }
        return count;
    }

    private sealed class DiagnosticHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
