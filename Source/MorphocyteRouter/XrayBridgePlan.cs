using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal sealed record XrayBridgeEndpoint(int Port, string Username, string Password);

// A single Xray child, no TUN, with one authenticated loopback SOCKS inlet per
// connection. Only sing-box handles user rules, DNS policy and the system tunnel.
internal sealed class XrayBridgePlan : IDisposable
{
    internal const string Version = "26.11.9";
    internal const string Hash = "1CA7A2CF61BA2C5D20B21A80D405B57933939751081CB289F07BD8DDF7CC3185";
    internal static string EnginePath => Path.Combine(AppContext.BaseDirectory, "ThirdPartySource", "Engine", "xray.exe");
    internal Dictionary<string, XrayBridgeEndpoint> Endpoints { get; } = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _reservations = [];
    private readonly JsonArray _inbounds = [], _outbounds = [], _rules = [];
    internal string? ConfigPath { get; private set; }
    internal bool HasConnections => Endpoints.Count > 0;
    private int _bootstrapPort;
    private JsonObject? _fullProfile;
    private readonly List<XrayBridgePlan> _profilePlans=[];
    internal IReadOnlyList<XrayBridgePlan> ProcessPlans => (_inbounds.Count>0?new[]{this}:Array.Empty<XrayBridgePlan>()).Concat(_profilePlans).ToArray();

    internal static bool ContainsConnections(ClashConfigDocument document) => Nodes(document).Any();
    private static IEnumerable<YamlMappingNode> Nodes(ClashConfigDocument document)
        => document.ProxyNodes.Where(node => !SubscriptionInformation.IsSection(node) && node.Children.TryGetValue(new YamlScalarNode("type"), out var type) && type.ToString() is XrayProfile.NodeType or XrayFullProfile.NodeType);

    internal static XrayBridgePlan Create(ClashConfigDocument document)
    {
        var plan = new XrayBridgePlan();
        try
        {
            foreach (var node in Nodes(document))
            {
                if(node.Children[new YamlScalarNode("type")].ToString()==XrayFullProfile.NodeType)
                {
                    if(plan._profilePlans.Count>=16) throw new InvalidDataException("Слишком много полных JSON-профилей Xray в одном списке.");
                    var child=new XrayBridgePlan();
                    plan._profilePlans.Add(child);
                    child.AddProfile(node);
                    foreach(var endpoint in child.Endpoints)
                        if(!plan.Endpoints.TryAdd(endpoint.Key,endpoint.Value)) throw new InvalidDataException("Повторяется имя подключения Xray.");
                }
                else plan.Add(node);
            }
            return plan;
        }
        catch { plan.Dispose(); throw; }
    }

    private void Add(YamlMappingNode node)
    {
        var outbound = XrayProfile.FromNode(node);
        var name = node.Children[new YamlScalarNode("name")].ToString();
        if (Endpoints.ContainsKey(name)) throw new InvalidDataException("Повторяется имя подключения Xray.");
        var port=ReservePort();
        var endpoint = new XrayBridgeEndpoint(port, "morphocyte", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Endpoints.Add(name, endpoint);
        var tag = "connection-" + Endpoints.Count;
        outbound["tag"] = tag;
        _outbounds.Add(outbound);
        _inbounds.Add(new JsonObject { ["tag"] = tag, ["listen"] = "127.0.0.1", ["port"] = port, ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["auth"] = "password", ["accounts"] = new JsonArray(new JsonObject { ["user"] = endpoint.Username, ["pass"] = endpoint.Password }),
                ["udp"] = true, ["ip"] = "127.0.0.1" } });
        _rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray(tag), ["outboundTag"] = tag });
    }

    private void AddProfile(YamlMappingNode node)
    {
        var profile=XrayFullProfile.FromNode(node);
        var name=node.Children[new YamlScalarNode("name")].ToString();
        var port=ReservePort();
        var endpoint=new XrayBridgeEndpoint(port,"morphocyte",Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Endpoints.Add(name,endpoint);
        _fullProfile=XrayFullProfile.Runtime(profile,endpoint);
    }

    private int ReservePort()
    {
        for(var attempt=0;attempt<64;attempt++)
        {
            var listener=new TcpListener(IPAddress.Loopback,0);
            try
            {
                listener.Start(); var port=((IPEndPoint)listener.LocalEndpoint).Port;
                var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));
                _reservations.Add(listener); _reservations.Add(udp); return port;
            }
            catch(SocketException error) when(error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { listener.Stop(); }
            catch { listener.Stop(); throw; }
        }
        throw new IOException("Не удалось выделить свободный локальный TCP/UDP-порт для Xray.");
    }

    internal string Configuration
    {
        get
        {
            if(_fullProfile is not null) return _fullProfile.ToJsonString(ProfileImporter.JsonWriteOptions);
            var result = new JsonObject { ["log"] = new JsonObject { ["loglevel"] = "none" },
                ["inbounds"] = _inbounds.DeepClone(), ["outbounds"] = _outbounds.DeepClone(),
                ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = _rules.DeepClone() } };
            if (_bootstrapPort != 0)
                result["dns"] = new JsonObject { ["servers"] = new JsonArray($"tcp+local://127.0.0.1:{_bootstrapPort}"), ["queryStrategy"] = "UseIP" };
            return result.ToJsonString(ProfileImporter.JsonWriteOptions);
        }
    }

    internal void ConfigureDnsFromRuntime(string runtime)
    {
        // Resolve only VPN peers through sing-box's existing bootstrap policy.
        // tcp+local bypasses Xray's own proxy routing and never uses Windows
        // Fake-IP. ForceIP prevents an OS-DNS fallback if bootstrap fails.
        var runtimeRoot=JsonNode.Parse(runtime);
        var uplink=runtimeRoot?["route"]?["default_interface"]?.ToString();
        var inbounds = runtimeRoot?["inbounds"] as JsonArray ?? throw new InvalidDataException("Не найден DNS-канал ядра.");
        var candidates = inbounds.Where(item => item?["type"]?.ToString() == "direct" && item?["listen"]?.ToString() == "127.0.0.1"
            && item?["tag"]?.ToString().EndsWith("-bootstrap-in", StringComparison.Ordinal) == true).ToArray();
        if (candidates.Length != 1 || candidates[0]?["listen_port"]?.GetValue<int>() is not > 0 or > 65535)
            throw new InvalidDataException("Не удалось подготовить DNS-канал Xray.");
        _bootstrapPort = candidates[0]!["listen_port"]!.GetValue<int>();
        foreach(var child in _profilePlans) child.ConfigureDnsFromRuntime(runtime);
        if(_fullProfile is not null)
        {
            if(_fullProfile["dns"] is null) _fullProfile["dns"]=new JsonObject { ["servers"]=new JsonArray($"tcp+local://127.0.0.1:{_bootstrapPort}") };
            foreach(var outbound in _fullProfile["outbounds"]!.AsArray()) ConfigureSocket(outbound!,uplink,false);
            return;
        }
        foreach (var outbound in _outbounds)
            ConfigureSocket(outbound!,uplink,true);
    }

    private static void ConfigureSocket(JsonNode outbound,string? uplink,bool forceDns)
    {
        var protocol=outbound["protocol"]?.ToString();
        if(protocol is not ("vless" or "hysteria" or "freedom")) return;
        outbound["streamSettings"]??=new JsonObject();
        outbound["streamSettings"]!["sockopt"]??=new JsonObject();
        var socket=outbound["streamSettings"]!["sockopt"]!;
        if(forceDns) socket["domainStrategy"]="ForceIP";
        var peer=protocol=="vless"?outbound["settings"]?["vnext"]?[0]?["address"]?.ToString():outbound["settings"]?["address"]?.ToString();
        if(peer=="localhost"||IPAddress.TryParse(peer,out var address)&&IPAddress.IsLoopback(address)) return;
        // A freedom route may legitimately target a local DNS listener. Keep
        // its destination-dependent routing; pin remote VPN transports only.
        if(protocol!="freedom"&&!string.IsNullOrWhiteSpace(uplink)&&socket["interface"] is null) socket["interface"]=uplink;
    }

    internal static async Task EnsureEngineAsync(CancellationToken token)
    {
        if (!File.Exists(EnginePath)) throw new IOException("Для этого подключения нужен комплектный Xray. Распакуй тестовую сборку целиком.");
        await using var stream = File.OpenRead(EnginePath);
        if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != Hash) throw new IOException("Контрольная сумма комплектного Xray не совпала.");
    }

    internal async Task<(bool Success, string Message)> ValidateAsync(CancellationToken token)
    {
        if (!HasConnections) return (true, "");
        await EnsureEngineAsync(token);
        return await ValidateFilesAsync(token);
    }

    private async Task<(bool Success, string Message)> ValidateFilesAsync(CancellationToken token)
    {
        foreach(var child in _profilePlans)
        {
            var check=await child.ValidateFilesAsync(token);
            if(!check.Success) return check;
        }
        if(_inbounds.Count==0 && _fullProfile is null) return (true,"");
        ConfigPath ??= Path.Combine(Path.GetTempPath(), "morphocyte-xray-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(ConfigPath, Configuration, new UTF8Encoding(false), token);
        return await CoreProcessManager.ValidateAsync(EnginePath, ConfigPath, token);
    }

    internal void ReleasePorts()
    {
        foreach(var child in _profilePlans) child.ReleasePorts();
        foreach (var reservation in _reservations) reservation.Dispose();
        _reservations.Clear();
    }

    internal async Task VerifyReadyAsync(CancellationToken token)
    {
        // Authenticate without requesting any remote connection. A listening port
        // alone could belong to another process that won a port-allocation race.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        foreach (var endpoint in Endpoints.Values)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, deadline.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 2 }, deadline.Token);
            var reply = new byte[2]; await stream.ReadExactlyAsync(reply, deadline.Token);
            if (reply[0] != 5 || reply[1] != 2) throw new IOException("Xray не подтвердил защищённый локальный канал.");
            var user = Encoding.UTF8.GetBytes(endpoint.Username); var password = Encoding.UTF8.GetBytes(endpoint.Password);
            var auth = new byte[3 + user.Length + password.Length]; auth[0] = 1; auth[1] = (byte)user.Length;
            user.CopyTo(auth, 2); auth[2 + user.Length] = (byte)password.Length; password.CopyTo(auth, 3 + user.Length);
            await stream.WriteAsync(auth, deadline.Token); await stream.ReadExactlyAsync(reply, deadline.Token);
            if (reply[0] != 1 || reply[1] != 0) throw new IOException("Xray не подтвердил авторизацию локального канала.");
        }
    }

    public void Dispose()
    {
        foreach(var child in _profilePlans) child.Dispose();
        ReleasePorts();
        if (ConfigPath is { } path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            ConfigPath = null;
        }
    }
}
