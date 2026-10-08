using System.IO;
using System.Net;
using System.Net.Sockets;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunSpeedTransportChecks(string scratch)
    {
        var home = Path.Combine(scratch, "speed-transport"); Directory.CreateDirectory(home);
        using var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var hits = 0;
        var accept = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var socket = await upstream.AcceptTcpClientAsync(lifetime.Token);
                    var hello = new byte[3];
                    var length = await socket.GetStream().ReadAsync(hello, lifetime.Token);
                    if (length > 0 && hello[0] == 5) Interlocked.Increment(ref hits);
                    // Refuse the fixture upstream before DNS, TLS or external traffic.
                }
            }
            catch (OperationCanceledException) { }
        });
        using var api = new CoreDiagnostics();
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var document = ClashConfigDocument.Parse(Path.Combine(home, "profile.yaml"),
            $"mode: rule\nlog-level: warning\ntun: {{enable: false}}\ndns: {{enable: false}}\nproxies:\n - {{name: Server, type: socks5, server: 127.0.0.1, port: {upstreamPort}}}\nproxy-groups:\n - {{name: VPN, type: select, proxies: [Server]}}\nrules:\n - MATCH,REJECT\n");
        var runtime = Path.Combine(home, "runtime.yaml");
        File.WriteAllText(runtime, document.BuildRuntimeText(api.Port, api.Secret, false, port));
        var core = new CoreProcessManager();
        try
        {
            await core.StartAsync(CoreProcessManager.CreateStartInfo(Path.Combine(AppContext.BaseDirectory, "mihomo.exe"), runtime, home));
            using var test = new VpnSpeedTest(port, api.Secret);
            try { await test.MeasureAsync(lifetime.Token); throw new Exception("Refused upstream returned a speed result"); }
            catch (System.Net.Http.HttpRequestException exception)
            { Console.WriteLine("Transport result: " + exception.HttpRequestError + ", status=" + exception.StatusCode); }
            if (hits == 0) throw new Exception("Default speed transport did not authenticate or reach the forced VPN listener");
            Console.WriteLine("PASS: default speed transport authenticates with real Mihomo and reaches the forced VPN even when normal rules block all traffic");
            return 1;
        }
        finally { lifetime.Cancel(); upstream.Stop(); await core.StopAsync(); await accept; }
    }
}
