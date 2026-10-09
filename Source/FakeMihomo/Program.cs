var configPath = args[Array.IndexOf(args, "-f") + 1];
var config = File.ReadAllText(configPath);
if (args.Contains("-t"))
{
    if (config.Contains("slow-validation")) await Task.Delay(2000);
    if (config.Contains("INVALID")) { Console.Error.WriteLine("Simulated validation failure"); Environment.Exit(2); }
    Console.WriteLine("Validation passed");
    return;
}
if (config.Contains("startup-fail")) Environment.Exit(3);
Console.WriteLine("Fake core started, no networking.");
// Opt-in loopback API for lifecycle tests; never establishes a tunnel or a proxy.
if (config.Contains("morphocyte-fake-api: true"))
{
    var match = System.Text.RegularExpressions.Regex.Match(config, @"external-controller:\s*'?127\.0\.0\.1:(\d+)");
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, int.Parse(match.Groups[1].Value));
    listener.Start();
    _ = Task.Run(async () =>
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                    var request = await reader.ReadLineAsync() ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    var body = request.Contains("/version ") ? "{\"version\":\"fixture\"}"
                        : request.Contains("/delay?") ? "{\"delay\":42}"
                        : request.Contains("/connections ") ? "{\"connections\":null}"
                        : request.Contains("/traffic ") ? "{\"up\":0,\"down\":0}\n"
                        : request.Contains("/proxies/VPN ") ? "{\"type\":\"Selector\",\"now\":\"Server\"}"
                        : "{\"type\":\"Socks5\"}";
                    var data = System.Text.Encoding.UTF8.GetBytes(body);
                    var headers = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers); await stream.WriteAsync(data);
                }
            });
        }
    });
}
await Task.Delay(Timeout.Infinite);
