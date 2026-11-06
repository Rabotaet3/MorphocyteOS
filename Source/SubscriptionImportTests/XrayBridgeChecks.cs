using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal static class XrayBridgeChecks
{
    private const string Id = "12345678-1234-4234-8234-123456789abc";
    internal static async Task Run(Action<bool, string> check)
    {
        const string extra = "{\"xmux\":{\"maxConcurrency\":\"8-16\"},\"headers\":{\"X-Fixture\":\"unchanged\"}}";
        const string fm = "{\"quicParams\":{\"congestion\":\"bbr\"}}";
        var links = new[] {
            $"vless://{Id}@vpn.example:443?security=tls&type=xhttp&path=%2Ftest&mode=auto&extra={Uri.EscapeDataString(extra)}#XHTTP",
            $"vless://{Id}@vpn.example:443?security=tls&type=grpc&serviceName=test&pcs={new string('a',64)}&fm=%7B%7D#Pinned",
            $"hy2://fixture-password@hy.example?fm={Uri.EscapeDataString(fm)}#QUIC"
        };
        var profile = SubscriptionImporter.ParseContent(string.Join('\n', links));
        check(profile.CompatibilityWarnings.Count == 0, "extended VLESS and Hysteria2 import together without partial mode");
        var home = Path.Combine(Path.GetTempPath(), "MorphocyteOS-xray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var document = ClashConfigDocument.Parse(Path.Combine(home, "profile.yaml"), profile.Yaml);
            using var plan = XrayBridgePlan.Create(document);
            var native = JsonNode.Parse(plan.Configuration)!;
            check(JsonNode.DeepEquals(native["outbounds"]![0]!["streamSettings"]!["xhttpSettings"]!["extra"], JsonNode.Parse(extra)), "XHTTP extra including nested settings survives unchanged");
            check(JsonNode.DeepEquals(native["outbounds"]![2]!["streamSettings"]!["finalmask"], JsonNode.Parse(fm)), "Hysteria2 QUIC parameters survive unchanged");
            check(native["outbounds"]![1]!["streamSettings"]!["tlsSettings"]!["pinnedPeerCertSha256"]!.GetValue<string>() == new string('a',64), "TLS certificate pin is retained as a pin, never insecure TLS");
            check(plan.Endpoints.Values.Select(e => e.Port).Distinct().Count() == 3 && plan.Endpoints.Values.Select(e => e.Password).Distinct().Count() == 3, "each Xray connection receives a separate authenticated channel");
            check(native["inbounds"]!.AsArray().All(i => i!["listen"]!.GetValue<string>() == "127.0.0.1" && i["settings"]!["auth"]!.GetValue<string>() == "password"), "Xray never exposes unauthenticated or external listeners");
            check((await plan.ValidateAsync(default)).Success, "official Xray validates XHTTP, pinned TLS and Hysteria2 FinalMask");
            using var api = new CoreDiagnostics();
            var json = SingBoxConfig.Build(document, api.Port, api.Secret, false, includeTun:false, xrayEndpoints:plan.Endpoints);
            plan.ConfigureDnsFromRuntime(json);
            native=JsonNode.Parse(plan.Configuration)!;
            check(native["dns"]!["servers"]![0]!.ToString().StartsWith("tcp+local://127.0.0.1:",StringComparison.Ordinal)
                && native["outbounds"]!.AsArray().All(o=>o!["streamSettings"]!["sockopt"]!["domainStrategy"]!.ToString()=="ForceIP"),
                "Xray resolves peers via the existing bootstrap policy without Windows Fake-IP or OS DNS fallback");
            check((await plan.ValidateAsync(default)).Success,"official Xray accepts the local DNS bootstrap and ForceIP settings");
            var runtime = JsonNode.Parse(json)!;
            check(runtime["route"]!["rules"]!.AsArray().Any(r => r!["process_path"] is JsonArray paths && paths.Any(p => p!.GetValue<string>() == XrayBridgePlan.EnginePath)
                && r!["outbound"]!.GetValue<string>() == "DIRECT"), "only the owned Xray executable bypasses the TUN rules to prevent recursion");
            var path = Path.Combine(home, "runtime.json"); File.WriteAllText(path,json);
            var singBox = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../Packaging/Engine/sing-box.exe"));
            check((await CoreProcessManager.ValidateAsync(singBox,path)).Success, "official sing-box accepts the complete bridge configuration");
            void Reject(Action action, string label) { try { action(); } catch (InvalidDataException) { check(true,label); return; } throw new Exception("FAIL: "+label); }
            Reject(() => SingBoxConfig.Build(document, api.Port, api.Secret, false), "missing bridge binding fails instead of taking a direct route");
            Reject(() => CoreBackend.RuntimeText("mihomo.exe",document,api,false,0), "Mihomo cannot silently discard Xray extension nodes");
            foreach (var suffix in new[] { "fm=null", "fm=%7B%22tcp%22%3A%5B%5D%2C%22tcp%22%3A%5B%5D%7D", "fm=%7B%22unknown%22%3Atrue%7D", "pcs=bad", "pcs="+new string('a',64)+"&insecure=1", "fm=%7B%22tcp%22%3A%5B%7B%22certificateFile%22%3A%22secret.pem%22%7D%5D%7D" })
                Reject(() => ProfileImporter.Parse($"vless://{Id}@vpn.example:443?security=tls&type=tcp&{suffix}#Invalid"), "invalid or unsafe Xray settings rejected: "+suffix.Split('=')[0]);
            await LocalChain(home,singBox,check);
        }
        finally { Directory.Delete(home,true); }
    }

    private static int Port() { using var tcp=new TcpListener(IPAddress.Loopback,0); tcp.Start(); return ((IPEndPoint)tcp.LocalEndpoint).Port; }
    private static async Task LocalChain(string home, string singBox, Action<bool,string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token=deadline.Token;
        using var echo = new TcpListener(IPAddress.Loopback,0); echo.Start();
        var echoPort=((IPEndPoint)echo.LocalEndpoint).Port;
        using var udpEcho=new UdpClient(new IPEndPoint(IPAddress.Loopback,echoPort));
        using var dnsMock=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var dnsPort=((IPEndPoint)dnsMock.Client.LocalEndPoint!).Port;
        var dnsQueries=0;
        using var dnsLifetime=CancellationTokenSource.CreateLinkedTokenSource(token);
        var dnsTask=Task.Run(async()=>
        {
            while(!dnsLifetime.IsCancellationRequested)
            {
                var question=await dnsMock.ReceiveAsync(dnsLifetime.Token); var query=question.Buffer;
                var end=12; while(query[end]!=0) end+=query[end]+1; end+=5;
                var addressQuery=query[end-4]==0 && query[end-3]==1;
                var answer=new byte[end+(addressQuery?16:0)]; Array.Copy(query,answer,end);
                answer[2]=0x81; answer[3]=0x80; answer[6]=0; answer[7]=(byte)(addressQuery?1:0);
                answer[8]=answer[9]=answer[10]=answer[11]=0;
                if(addressQuery) new byte[]{0xc0,0x0c,0,1,0,1,0,0,0,30,0,4,127,0,0,1}.CopyTo(answer,end);
                Interlocked.Increment(ref dnsQueries);
                await dnsMock.SendAsync(answer,question.RemoteEndPoint,dnsLifetime.Token);
            }
        },dnsLifetime.Token);
        var serverPort=Port();
        var serverJson = $$$"""
        {"log":{"loglevel":"none"},"inbounds":[{"listen":"127.0.0.1","port":{{{serverPort}}},"protocol":"vless","settings":{"clients":[{"id":"{{{Id}}}"}],"decryption":"none"},"streamSettings":{"network":"tcp","security":"none"}}],"outbounds":[{"protocol":"freedom","settings":{"finalRules":[{"action":"allow","ip":["127.0.0.1/32"],"port":"{{{echoPort}}}"}]}}]}
        """;
        var serverPath=Path.Combine(home,"server.json"); File.WriteAllText(serverPath,serverJson);
        var server=new CoreProcessManager(); var manager=new CoreProcessManager();
        string? privatePath=null;
        try
        {
            await server.StartAsync(CoreProcessManager.CreateStartInfo(XrayBridgePlan.EnginePath,serverPath),token);
            var profile=ProfileImporter.Parse($"vless://{Id}@peer.fixture.test:{serverPort}?security=none&type=tcp&fm=%7B%7D#Local");
            var yaml=new YamlStream(); yaml.Load(new StringReader(profile.Yaml));
            var root=(YamlMappingNode)yaml.Documents[0].RootNode;
            root.Children[new YamlScalarNode("mixed-port")]=new YamlScalarNode("0");
            root.Children[new YamlScalarNode("dns")]=new YamlMappingNode {
                { "enable", "true" }, { "ipv6", "false" },
                { "proxy-server-nameserver", new YamlSequenceNode(new YamlScalarNode($"udp://127.0.0.1:{dnsPort}")) },
                { "nameserver", new YamlSequenceNode(new YamlScalarNode($"udp://127.0.0.1:{dnsPort}")) } };
            using(var writer=new StringWriter()) { yaml.Save(writer,false); profile=profile with { Yaml=writer.ToString() }; }
            var document=ClashConfigDocument.Parse(Path.Combine(home,"local.yaml"),profile.Yaml);
            var plan=XrayBridgePlan.Create(document);
            using var api=new CoreDiagnostics(); var speedPort=Port();
            var json=SingBoxConfig.Build(document,api.Port,api.Secret,false,speedPort,"Local",includeTun:false,xrayEndpoints:plan.Endpoints);
            var clientPath=Path.Combine(home,"client.json"); File.WriteAllText(clientPath,json);
            await manager.StartWithBridgeAsync(CoreProcessManager.CreateStartInfo(singBox,clientPath),plan,token);
            privatePath=plan.ConfigPath;
            check(manager.IsRunning && manager.BridgeProcessId.HasValue,"bridge and main core start as owned children without TUN");
            using (var denied=new TcpClient())
            {
                await denied.ConnectAsync(IPAddress.Loopback,plan.Endpoints["Local"].Port,token);
                await denied.GetStream().WriteAsync(new byte[]{5,1,0},token);
                var reply=new byte[2]; await denied.GetStream().ReadExactlyAsync(reply,token);
                check(reply[1]==255,"Xray refuses a client without SOCKS authentication");
            }
            var tcpEcho=Task.Run(async()=> { using var peer=await echo.AcceptTcpClientAsync(token); var bytes=new byte[64]; var count=await peer.GetStream().ReadAsync(bytes,token); await peer.GetStream().WriteAsync(bytes.AsMemory(0,count),token); },token);
            using (var connection=await Socks(speedPort,"morphocyte",api.Secret,1,echoPort,token))
            {
                var bytes=Encoding.ASCII.GetBytes("MorphocyteOS-TCP-fixture"); await connection.GetStream().WriteAsync(bytes,token);
                var read=new byte[bytes.Length]; await connection.GetStream().ReadExactlyAsync(read,token);
                check(bytes.SequenceEqual(read),"TCP data crosses sing-box -> Xray -> local VLESS server -> echo server");
            }
            await tcpEcho;
            check(dnsQueries>0,"VPN peer name resolves through the configured bootstrap resolver, without system DNS");
            var udpTask=Task.Run(async()=> { var packet=await udpEcho.ReceiveAsync(token); await udpEcho.SendAsync(packet.Buffer,packet.RemoteEndPoint,token); },token);
            using(var connection=new TcpClient())
            {
                await connection.ConnectAsync(IPAddress.Loopback,speedPort,token); var stream=connection.GetStream();
                await Auth(stream,"morphocyte",api.Secret,token);
                await stream.WriteAsync(new byte[]{5,3,0,1,0,0,0,0,0,0},token);
                var bind=await Reply(stream,token);
                using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
                var data=Encoding.ASCII.GetBytes("MorphocyteOS-UDP-fixture");
                var packet=new byte[10+data.Length]; packet[3]=1; packet[4]=127; packet[7]=1; packet[8]=(byte)(echoPort>>8); packet[9]=(byte)echoPort; data.CopyTo(packet,10);
                await udp.SendAsync(packet,bind,token); var received=await udp.ReceiveAsync(token);
                check(received.Buffer.AsSpan(10).SequenceEqual(data),"UDP relay crosses both cores and retains the destination");
            }
            await udpTask;
            check(await manager.StopAsync() && !manager.HasTrackedProcess,"normal stop confirms both owned cores have exited");
            check(privatePath is not null && !File.Exists(privatePath),"private Xray runtime is deleted after confirmed stop");
            var mainFailurePlan=XrayBridgePlan.Create(document); var mainFailurePath=Path.Combine(home,"main-failure.json");
            File.WriteAllText(mainFailurePath,SingBoxConfig.Build(document,api.Port,api.Secret,false,speedPort,"Local",includeTun:false,xrayEndpoints:mainFailurePlan.Endpoints));
            await manager.StartWithBridgeAsync(CoreProcessManager.CreateStartInfo(singBox,mainFailurePath),mainFailurePlan,token);
            using(var child=Process.GetProcessById(manager.TrackedProcessId!.Value)) { child.Kill(true); await child.WaitForExitAsync(token); }
            var wait=Stopwatch.StartNew();
            while(manager.BridgeProcessId.HasValue && wait.Elapsed<TimeSpan.FromSeconds(6)) await Task.Delay(30,token);
            check(!manager.IsRunning && !manager.BridgeProcessId.HasValue,"unexpected main-core exit also stops the owned Xray bridge");
            check(await manager.StopAsync() && !manager.HasTrackedProcess,"terminated main core is reaped before another session can start");
            var secondPlan=XrayBridgePlan.Create(document); var secondPath=Path.Combine(home,"client-second.json");
            File.WriteAllText(secondPath,SingBoxConfig.Build(document,api.Port,api.Secret,false,speedPort,"Local",includeTun:false,xrayEndpoints:secondPlan.Endpoints));
            var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.StartupFailed += _=>failed.TrySetResult();
            await manager.StartWithBridgeAsync(CoreProcessManager.CreateStartInfo(singBox,secondPath),secondPlan,token);
            using(var child=Process.GetProcessById(manager.BridgeProcessId!.Value)) { child.Kill(true); await child.WaitForExitAsync(token); }
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(8),token);
            check(!manager.IsRunning && !manager.HasTrackedProcess,"unexpected Xray exit stops the main core and retains no orphan");
            var canceledPlan=XrayBridgePlan.Create(document); using var canceled=new CancellationTokenSource(); canceled.Cancel();
            try { await manager.StartWithBridgeAsync(CoreProcessManager.CreateStartInfo(singBox,secondPath),canceledPlan,canceled.Token); }
            catch(OperationCanceledException) { }
            check(!manager.HasTrackedProcess && canceledPlan.ConfigPath is null,"canceled bridge startup releases processes, ports and private config");
        }
        finally
        {
            await manager.StopAsync(); await server.StopAsync(); dnsLifetime.Cancel();
            try { await dnsTask; } catch(OperationCanceledException) { }
        }
    }

    private static async Task Auth(NetworkStream stream,string user,string password,CancellationToken token)
    {
        await stream.WriteAsync(new byte[]{5,1,2},token); var reply=new byte[2]; await stream.ReadExactlyAsync(reply,token);
        if(reply[1]!=2) throw new Exception("SOCKS authentication not selected");
        var auth=new byte[3+user.Length+password.Length]; auth[0]=1; auth[1]=(byte)user.Length; Encoding.ASCII.GetBytes(user).CopyTo(auth,2); auth[2+user.Length]=(byte)password.Length; Encoding.ASCII.GetBytes(password).CopyTo(auth,3+user.Length);
        await stream.WriteAsync(auth,token); await stream.ReadExactlyAsync(reply,token); if(reply[1]!=0) throw new Exception("SOCKS auth failed");
    }
    private static async Task<TcpClient> Socks(int port,string user,string password,byte command,int targetPort,CancellationToken token)
    {
        var client=new TcpClient();
        try { await client.ConnectAsync(IPAddress.Loopback,port,token); var stream=client.GetStream(); await Auth(stream,user,password,token);
            await stream.WriteAsync(new byte[]{5,command,0,1,127,0,0,1,(byte)(targetPort>>8),(byte)targetPort},token); await Reply(stream,token); return client; }
        catch { client.Dispose(); throw; }
    }
    private static async Task<IPEndPoint> Reply(NetworkStream stream,CancellationToken token)
    {
        var header=new byte[4]; await stream.ReadExactlyAsync(header,token); if(header[1]!=0) throw new Exception("SOCKS command rejected");
        var address=new byte[header[3]==1?4:header[3]==4?16:throw new Exception("Unexpected SOCKS address")];
        await stream.ReadExactlyAsync(address,token); var port=new byte[2]; await stream.ReadExactlyAsync(port,token);
        return new IPEndPoint(new IPAddress(address),port[0]*256+port[1]);
    }
}
