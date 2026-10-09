using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MorphocyteRouter;

internal partial class Program
{
    private static async Task<int> RunSingBoxProcessChecks(string scratch)
    {
        var home = Path.Combine(scratch, "process-routing"); Directory.CreateDirectory(home);
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        using var api = new CoreDiagnostics();
        var prefix = "mixed-port: 0\nproxies:\n - {name: VPN, type: socks5, server: 127.0.0.1, port: 1}\nrules:\n";
        var document = ClashConfigDocument.Parse(Path.Combine(home, "case.yaml"), prefix + " - PROCESS-NAME,SampleBrowser.exe,DIRECT\n - PROCESS-PATH,C:/Program Files/Sample/App.exe,DIRECT\n - MATCH,REJECT\n");
        using (var json = JsonDocument.Parse(SingBoxConfig.Build(document, api.Port, api.Secret, true)))
        {
            var processRules = json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().Where(rule => rule.TryGetProperty("process_path_regex", out _)).ToArray();
            var name = processRules[0].GetProperty("process_path_regex")[0].GetString()!;
            Check(Regex.IsMatch(@"C:\Browser\samplebrowser.EXE", name) && Regex.IsMatch("SAMPLEBROWSER.EXE", name), "Windows process-name exceptions ignore filename case");
            Check(!Regex.IsMatch(@"C:\Browser\OtherSampleBrowser.exe", name) && !Regex.IsMatch(@"C:\Browser\SampleBrowser.exe.helper", name), "process-name exceptions cannot match another executable by substring");
            var path = processRules[1].GetProperty("process_path_regex")[0].GetString()!;
            Check(Regex.IsMatch(@"c:\PROGRAM FILES\sample\app.EXE", path) && Regex.IsMatch("C:/Program Files/Sample/App.exe", path), "Windows full-path exceptions ignore case and slash direction");
            Check(!Regex.IsMatch(@"C:\Other\App.exe", path), "full-path exceptions still require the exact directory");
        }
        // End-to-end process detection on the real Windows engine, with loopback only.
        // No TUN, DNS queries, Internet requests or user processes are started/stopped.
        foreach (var fullTunnel in new[] { false, true })
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var destination = new TcpListener(IPAddress.Loopback, 0); destination.Start();
            var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
            using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var proxyPort = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            var processName = Path.GetFileName(Environment.ProcessPath!).ToUpperInvariant();
            var yaml = prefix.Replace("mixed-port: 0", "mixed-port: " + proxyPort) + " - PROCESS-NAME," + processName + ",DIRECT\n - MATCH,REJECT\n";
            var native = ClashConfigDocument.Parse(Path.Combine(home, "loopback.yaml"), yaml);
            var runtime = Path.Combine(home, "runtime.json");
            File.WriteAllText(runtime, SingBoxConfig.Build(native, api.Port, api.Secret, fullTunnel, includeTun: false));
            var server = Task.Run(async () =>
            {
                using var socket = await destination.AcceptTcpClientAsync(lifetime.Token);
                await ReadHeader(socket.GetStream(), lifetime.Token);
                await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok"), lifetime.Token);
                try { await Task.Delay(Timeout.Infinite, lifetime.Token); } catch (OperationCanceledException) { }
            });
            var core = new CoreProcessManager();
            try
            {
                await core.StartAsync(CoreProcessManager.CreateStartInfo(Path.Combine(AppContext.BaseDirectory, "sing-box.exe"), runtime, home), lifetime.Token);
                using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, proxyPort, lifetime.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{destinationPort} HTTP/1.1\r\nHost: 127.0.0.1:{destinationPort}\r\n\r\n"), lifetime.Token);
                var established = await ReadHeader(stream, lifetime.Token);
                Check(established.StartsWith("HTTP/1.1 200"), "real sing-box accepts direct exception with mismatched process case, full TUN=" + fullTunnel);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /fixture HTTP/1.1\r\nHost: localhost\r\n\r\n"), lifetime.Token);
                var response = await ReadHeader(stream, lifetime.Token);
                var body = new byte[2]; await stream.ReadExactlyAsync(body, lifetime.Token);
                Check(response.StartsWith("HTTP/1.1 200") && Encoding.ASCII.GetString(body) == "ok", "fixture response arrives through direct outbound, full TUN=" + fullTunnel);
                var connections = await api.ReadAsync(null, lifetime.Token);
                Check(connections.Connections.Any(connection => connection.Process.Equals(processName, StringComparison.OrdinalIgnoreCase) && connection.Route.Contains("DIRECT") && connection.Rule.Contains("Process", StringComparison.OrdinalIgnoreCase)), "real process identification and DIRECT route are visible in diagnostics, full TUN=" + fullTunnel);
            }
            finally
            {
                await core.StopAsync(); lifetime.Cancel(); destination.Stop();
                try { await server; } catch (OperationCanceledException) { }
            }
        }
        return count;
    }

    private static async Task<string> ReadHeader(Stream stream, CancellationToken token)
    {
        var header = new List<byte>(); var next = new byte[1];
        while (header.Count < 16384)
        {
            if (await stream.ReadAsync(next, token) == 0) throw new IOException("Fixture proxy closed before response");
            header.Add(next[0]);
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) return Encoding.ASCII.GetString(header.ToArray());
        }
        throw new IOException("Fixture response too large");
    }
}
