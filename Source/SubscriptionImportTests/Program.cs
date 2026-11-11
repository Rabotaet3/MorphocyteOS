using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

if(args.Length==1&&args[0]=="--probe-private-refreshed")
{
    var settingsPath=Console.ReadLine()!; var number=Console.ReadLine()!;
    var settingsHash=ClashConfigDocument.Hash(File.ReadAllText(settingsPath));
    var settings=AppSettings.Load(settingsPath);
    var oldHash=ClashConfigDocument.Hash(File.ReadAllText(settings.ConfigPath));
    var freshProbePath=Path.Combine(Path.GetTempPath(),"MorphocyteOS-refreshed-probe-"+Guid.NewGuid().ToString("N")+".yaml");
    try
    {
        if(!File.Exists(Path.Combine(settings.StorageDirectory,"subscription-device-id.dat"))) throw new InvalidDataException();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var downloaded=await SubscriptionImporter.ImportAsync(settings.ProfileSubscriptions[settings.ConfigPath].ReadUrl(),timeout.Token,
            hardwareId:SubscriptionDeviceIdentity.GetOrCreate(settings.StorageDirectory),allowPartial:true);
        await File.WriteAllTextAsync(freshProbePath,downloaded.Yaml);
        var freshProbeDocument=ClashConfigDocument.Load(freshProbePath);
        var node=freshProbeDocument.ProxyNodes.FirstOrDefault(node=>System.Text.RegularExpressions.Regex.IsMatch(node.Children[new YamlScalarNode("name")].ToString(),@"(?:^|\s)№"+System.Text.RegularExpressions.Regex.Escape(number)+@"\s")
            && (number!="23"||node.Children[new YamlScalarNode("name")].ToString().Contains("Германия")));
        if(node is null) { Console.WriteLine("Requested fresh profile missing"); Environment.ExitCode=2; }
        else
        {
            var kind=node.Children[new YamlScalarNode("type")].ToString();
            Console.WriteLine("Fresh requested type="+kind+(kind==XrayProfile.NodeType?"; protocol="+XrayProfile.FromNode(node)["protocol"]?.ToString():""));
            var stored=ClashConfigDocument.Load(settings.ConfigPath).ProxyNodes.FirstOrDefault(item=>item.Children[new YamlScalarNode("name")].ToString()==node.Children[new YamlScalarNode("name")].ToString());
            Console.WriteLine("Fresh connection matches stored="+(stored is not null&&SubscriptionFormats.Key(stored)==SubscriptionFormats.Key(node)));
            Environment.ExitCode=await NativeProfileProbe.Run(freshProbePath,PhysicalUplink.Find(),node.Children[new YamlScalarNode("name")].ToString());
        }
    }
    catch(Exception error) { Console.WriteLine("Fresh read-only probe failed: "+error.GetType().Name); Environment.ExitCode=1; }
    finally
    {
        File.Delete(freshProbePath);
        Console.WriteLine("User profile unchanged="+(oldHash==ClashConfigDocument.Hash(File.ReadAllText(settings.ConfigPath)))+"; settings unchanged="+(settingsHash==ClashConfigDocument.Hash(File.ReadAllText(settingsPath))));
    }
    return;
}

if(args.Length==1&&args[0]=="--probe-private-selected")
{
    var selectedProfile=Console.ReadLine()!;
    var number=Console.ReadLine()!;
    var selectedDocument=ClashConfigDocument.Load(selectedProfile);
    var wanted=selectedDocument.ProxyNodes.Where(node=>System.Text.RegularExpressions.Regex.IsMatch(node.Children[new YamlScalarNode("name")].ToString(),@"(?:^|\s)№"+System.Text.RegularExpressions.Regex.Escape(number)+@"\s")
        && (number!="23"||node.Children[new YamlScalarNode("name")].ToString().Contains("Германия"))).Select(node=>node.Children[new YamlScalarNode("name")].ToString()).FirstOrDefault();
    Console.WriteLine("Physical uplink="+(PhysicalUplink.Find()??"auto"));
    if(wanted is null) { Console.WriteLine("Requested profile not present"); Environment.ExitCode=2; }
    else Environment.ExitCode=await NativeProfileProbe.Run(selectedProfile,PhysicalUplink.Find(),wanted);
    return;
}

if(args.Length==2 && args[0]=="--probe-private-compatible")
{
    var compatibleProbeUrl=Console.ReadLine()!; var compatibleProbeInterface=Console.ReadLine()!;
    var compatibleProbePath=Path.Combine(Path.GetTempPath(),"morphocyte-compatible-probe-"+Guid.NewGuid().ToString("N")+".yaml");
    using var compatibleProbeDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));
    try
    {
        var downloadedProfile=await SubscriptionImporter.ImportAsync(compatibleProbeUrl,compatibleProbeDeadline.Token,
            hardwareId:SubscriptionDeviceIdentity.GetOrCreate(args[1]),allowPartial:true);
        var yaml=new YamlStream(); yaml.Load(new StringReader(downloadedProfile.Yaml));
        var root=(YamlMappingNode)yaml.Documents[0].RootNode;
        foreach(var item in ((YamlSequenceNode)root.Children[new YamlScalarNode("proxies")]).Children.OfType<YamlMappingNode>())
        {
            var kind=item.Children[new YamlScalarNode("type")].ToString();
            var data=kind==XrayFullProfile.NodeType?XrayFullProfile.FromNode(item):XrayProfile.FromNode(item);
            var outbounds=kind==XrayFullProfile.NodeType?data["outbounds"]!.AsArray():new System.Text.Json.Nodes.JsonArray(data.DeepClone());
            foreach(var outbound in outbounds.Where(value=>value?["protocol"]?.ToString() is "vless" or "hysteria" or "freedom"))
            {
                if(outbound!["streamSettings"] is null) outbound["streamSettings"]=new System.Text.Json.Nodes.JsonObject();
                if(outbound["streamSettings"]!["sockopt"] is null) outbound["streamSettings"]!["sockopt"]=new System.Text.Json.Nodes.JsonObject();
                outbound["streamSettings"]!["sockopt"]!["interface"]=compatibleProbeInterface;
            }
            item.Children[new YamlScalarNode(kind==XrayFullProfile.NodeType?"xray-json":"xray-outbound")]=new YamlScalarNode((kind==XrayFullProfile.NodeType?data:outbounds[0]!).ToJsonString());
        }
        using(var writer=new StringWriter()) { yaml.Save(writer,false); await File.WriteAllTextAsync(compatibleProbePath,writer.ToString(),compatibleProbeDeadline.Token); }
        Environment.ExitCode=await NativeProfileProbe.Run(compatibleProbePath,compatibleProbeInterface);
    }
    catch(Exception error) { Console.WriteLine("Compatible direct probe failed: "+error.GetType().Name); Environment.ExitCode=1; }
    finally { File.Delete(compatibleProbePath); }
    return;
}

if(args.Length==2 && args[0]=="--inspect-private-compatible")
{
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
    try
    {
        var compatibleUrl=Console.ReadLine()!; var comparePath=Console.ReadLine(); var compareSelection=Console.ReadLine();
        var compatible=await SubscriptionImporter.ImportAsync(compatibleUrl,deadline.Token,
            hardwareId:SubscriptionDeviceIdentity.GetOrCreate(args[1]),allowPartial:true);
        var compatibleDocument=ClashConfigDocument.Parse(Path.Combine(Path.GetTempPath(),"compatible-profile.yaml"),compatible.Yaml);
        Console.WriteLine("Imported="+compatibleDocument.ConnectionCount+"; skipped="+compatible.CompatibilityWarnings.Count);
        foreach(var compatibleGroup in compatibleDocument.ProxyNodes.GroupBy(node=>node.Children[new YamlScalarNode("type")].ToString())) Console.WriteLine("Type "+compatibleGroup.Key+"="+compatibleGroup.Count());
        if(!string.IsNullOrWhiteSpace(comparePath))
        {
            var originalInventory=ClashConfigDocument.Load(comparePath).ProxyNodes.Where(node=>!SubscriptionInformation.IsSection(node)).ToArray();
            var completeKeys=compatibleDocument.ProxyNodes.Select(SubscriptionFormats.Key).ToHashSet(StringComparer.Ordinal);
            Console.WriteLine("Original inventory="+originalInventory.Length+"; missing original configurations="+originalInventory.Count(node=>!completeKeys.Contains(SubscriptionFormats.Key(node))));
            var oldSource=await File.ReadAllTextAsync(comparePath,deadline.Token);
            var selected=string.IsNullOrEmpty(compareSelection)?"":Encoding.UTF8.GetString(Convert.FromBase64String(compareSelection));
            var candidate=SubscriptionRefresh.Merge(comparePath,oldSource,compatible,selected);
            compatibleDocument=ClashConfigDocument.Parse(comparePath,candidate.Yaml);
            Console.WriteLine("Actual profile merge errors="+compatibleDocument.Validate().Count+"; original unchanged="+(await File.ReadAllTextAsync(comparePath,deadline.Token)==oldSource));
        }
        using var bridge=XrayBridgePlan.Create(compatibleDocument);
        using var api=new CoreDiagnostics();
        var compatibleRuntime=SingBoxConfig.Build(compatibleDocument,api.Port,api.Secret,false,includeTun:false,xrayEndpoints:bridge.Endpoints);
        bridge.ConfigureDnsFromRuntime(compatibleRuntime);
        var validation=await bridge.ValidateAsync(deadline.Token);
        Console.WriteLine("Xray accepted="+validation.Success+"; child processes="+bridge.ProcessPlans.Count);
        if(!validation.Success) Environment.ExitCode=1;
    }
    catch(Exception error) { Console.WriteLine("Compatible subscription inspection failed: "+error.GetType().Name); Environment.ExitCode=1; }
    return;
}

if(args.Length==1 && args[0]=="--probe-private-full-json")
{
    var fullJsonPath=Encoding.UTF8.GetString(Convert.FromBase64String(Console.ReadLine()!));
    var physicalInterface=Console.ReadLine()!;
    var tempProfile=Path.Combine(Path.GetTempPath(),"morphocyte-full-profile-probe-"+Guid.NewGuid().ToString("N")+".yaml");
    try
    {
        var diagnosticRoot=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fullJsonPath))!;
        foreach(var outbound in diagnosticRoot["outbounds"]!.AsArray().Where(item=>item?["protocol"]?.ToString() is "vless" or "freedom"))
        {
            if(outbound!["streamSettings"] is null) outbound["streamSettings"]=new System.Text.Json.Nodes.JsonObject();
            if(outbound["streamSettings"]!["sockopt"] is null) outbound["streamSettings"]!["sockopt"]=new System.Text.Json.Nodes.JsonObject();
            outbound["streamSettings"]!["sockopt"]!["interface"]=physicalInterface;
        }
        var fullImport=ProfileImporter.Parse(diagnosticRoot.ToJsonString());
        await File.WriteAllTextAsync(tempProfile,fullImport.Yaml);
        Environment.ExitCode=await NativeProfileProbe.Run(tempProfile,physicalInterface);
    }
    catch(Exception error) { Console.WriteLine("Direct full-profile probe failed: "+error.GetType().Name); Environment.ExitCode=1; }
    finally { File.Delete(tempProfile); }
    return;
}

if(args.Length==1 && args[0]=="--validate-private-full-json")
{
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        var fullPath=Encoding.UTF8.GetString(Convert.FromBase64String(Console.ReadLine()!));
        var full=ProfileImporter.Parse(await File.ReadAllTextAsync(fullPath,deadline.Token));
        var parsedFull=ClashConfigDocument.Parse(Path.Combine(Path.GetTempPath(),"full-profile-check.yaml"),full.Yaml);
        using var plan=XrayBridgePlan.Create(parsedFull);
        var check=await plan.ValidateAsync(deadline.Token);
        Console.WriteLine("Full JSON imported="+parsedFull.ConnectionCount+"; child processes="+plan.ProcessPlans.Count+"; Xray accepted="+check.Success);
        if(!check.Success)
        {
            Console.WriteLine("Needs geoip file="+check.Message.Contains("geoip",StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("Needs geosite file="+check.Message.Contains("geosite",StringComparison.OrdinalIgnoreCase));
            var safeCheck=System.Text.RegularExpressions.Regex.Replace(check.Message,@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}","[ID]");
            safeCheck=System.Text.RegularExpressions.Regex.Replace(safeCheck,@"https?://\S+","[URL]");
            Console.WriteLine(safeCheck[..Math.Min(safeCheck.Length,900)]);
            foreach(var child in plan.ProcessPlans)
            {
                var info=new System.Diagnostics.ProcessStartInfo(XrayBridgePlan.EnginePath) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true };
                foreach(var argument in new[]{"run","-test","-config",child.ConfigPath!}) info.ArgumentList.Add(argument);
                using var process=System.Diagnostics.Process.Start(info)!;
                var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(deadline.Token);
                var raw=(await stdout)+(await stderr);
                raw=System.Text.RegularExpressions.Regex.Replace(raw,@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}","[ID]");
                raw=System.Text.RegularExpressions.Regex.Replace(raw,@"https?://\S+","[URL]");
                Console.WriteLine(raw[..Math.Min(raw.Length,1800)]);
            }
            Environment.ExitCode=1;
        }
    }
    catch(Exception error) { Console.WriteLine("Full JSON validation failed: "+error.GetType().Name); Environment.ExitCode=1; }
    return;
}

if(args.Length==1 && args[0]=="--probe-private-ping-session")
{
    var path=Console.ReadLine()??"";
    var original=File.ReadAllText(path);
    var pingDocument=ClashConfigDocument.Parse(path,original);
    await using var session=new ServerPingSession(pingDocument);
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        Console.WriteLine("Offline PING delay="+await session.CheckAsync(VpnServerCatalog.Choices(pingDocument,subscriptionOrder:true)[0],timeout.Token)+" ms");
        Console.WriteLine("Source unchanged="+(File.ReadAllText(path)==original));
    }
    catch { Console.WriteLine("Offline PING failed (details omitted)"); Environment.ExitCode=1; }
    return;
}

if(args.Length==2 && args[0]=="--inspect-private-metadata")
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
    try
    {
        var result=await SubscriptionImporter.ImportAsync(Console.ReadLine()??"",timeout.Token,
            hardwareId:SubscriptionDeviceIdentity.GetOrCreate(args[1]),headersOnly:true);
        var data=result.Metadata!;
        Console.WriteLine("Metadata title="+(data.Title.Length>0)+"; traffic="+(data.Download is not null)+"; total="+(data.Total is >0)+"; expiry="+(data.Expires is >0)+"; announcement characters="+data.Announcement.Length);
    }
    catch { Console.WriteLine("Metadata read failed (details omitted)"); Environment.ExitCode=1; }
    return;
}

if(args.Length==1 && args[0]=="--probe-private-profile")
{
    Environment.ExitCode=await NativeProfileProbe.Run(Console.ReadLine()??"");
    return;
}

if(args.Length==1 && args[0]=="--inspect-profile-counts")
{
    var inspectedProfile=ClashConfigDocument.Load(Console.ReadLine()!);
    Console.WriteLine("Imported servers="+inspectedProfile.ConnectionCount);
    Console.WriteLine("Available choices="+VpnServerCatalog.Choices(inspectedProfile).Count);
    Console.WriteLine("Groups="+inspectedProfile.ProxyGroups.Count());
    return;
}

if (args.Length > 0 && args[0] == "--signal-owned-core")
{
    Environment.Exit(WindowsConsoleProcess.SignalOwnedCore(args));
    return;
}

if (args.Length == 2 && args[0] == "--inspect-private-subscription" || args.Length == 1 && args[0] is "--inspect-private-content" or "--probe-private-content")
{
    // Explicit diagnostic invocation only. The private URL arrives on stdin;
    // no URLs, credentials, identities or downloaded configuration are printed.
    string content;
    using var inspectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    if (args[0] is "--inspect-private-content" or "--probe-private-content")
    {
        content = await Console.In.ReadToEndAsync(inspectionDeadline.Token);
        if (content.Length > SubscriptionImporter.MaxBytes) throw new Exception("Response exceeds size limit.");
    }
    else
    {
    var target = SubscriptionImporter.ValidateUrl(Console.ReadLine() ?? "");
    var identity = SubscriptionDeviceIdentity.GetOrCreate(args[1]);
    using var http = SubscriptionImporter.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("MorphocyteOS/" + BuildInfo.Version);
    http.DefaultRequestHeaders.Add("x-hwid", identity);
    using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, inspectionDeadline.Token);
    Console.WriteLine("HTTP=" + (int)response.StatusCode);
    if (response.Content.Headers.ContentLength is > SubscriptionImporter.MaxBytes) throw new Exception("Response exceeds size limit.");
    using var stream = await response.Content.ReadAsStreamAsync(inspectionDeadline.Token);
    using var buffer = new MemoryStream();
    var chunk = new byte[8192]; int read;
    while ((read = await stream.ReadAsync(chunk, inspectionDeadline.Token)) > 0)
    {
        if (buffer.Length + read > SubscriptionImporter.MaxBytes) throw new Exception("Response exceeds size limit.");
        buffer.Write(chunk, 0, read);
    }
    content = Encoding.UTF8.GetString(buffer.ToArray());
    }
    Console.WriteLine("Characters=" + content.Length);
    var inspect = content.Trim();
    if (System.Text.RegularExpressions.Regex.IsMatch(inspect, @"\A[A-Za-z0-9+/_=\-\s]+\z"))
    {
        var encoded = string.Concat(inspect.Where(ch => !char.IsWhiteSpace(ch))).Replace('-', '+').Replace('_', '/');
        inspect = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
    }
    if (inspect.TrimStart().StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
    {
        var links = inspect.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Console.WriteLine("Link count=" + links.Length);
        var options = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var schemes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in links)
        {
            var node = new Uri(line);
            schemes[node.Scheme] = schemes.GetValueOrDefault(node.Scheme) + 1;
            foreach (var part in node.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = Uri.UnescapeDataString(part.Split('=', 2)[0]);
                options[key] = options.GetValueOrDefault(key) + 1;
            }
        }
        foreach (var entry in schemes) Console.WriteLine($"Scheme {entry.Key}: {entry.Value} nodes");
        foreach (var entry in options) Console.WriteLine($"Parameter {entry.Key}: {entry.Value} nodes");
    }
    try
    {
        using var json = System.Text.Json.JsonDocument.Parse(content);
        var roots = json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
            ? json.RootElement.EnumerateArray().ToArray() : new[] { json.RootElement };
        Console.WriteLine("JSON configurations=" + roots.Length);
        foreach (var root in roots.Take(4))
        {
            Console.WriteLine("Fields=" + string.Join(',', root.EnumerateObject().Select(p => p.Name)));
            if (root.TryGetProperty("outbounds", out var outbounds))
                foreach (var outbound in outbounds.EnumerateArray())
                {
                    Console.WriteLine("Protocol=" + (outbound.TryGetProperty("protocol", out var protocol) ? protocol.GetString() : "unknown"));
                    Console.WriteLine("OutboundFields=" + string.Join(',', outbound.EnumerateObject().Select(p => p.Name)));
                    if (outbound.TryGetProperty("streamSettings", out var settings))
                        Console.WriteLine("StreamFields=" + string.Join(',', settings.EnumerateObject().Select(p => p.Name)));
                }
        }
    }
    catch (System.Text.Json.JsonException) { Console.WriteLine("Not JSON"); }
    try
    {
        var parsed = SubscriptionImporter.ParseContent(content, allowPartial: true);
        Console.WriteLine("Parser accepted: " + parsed.Description + "; incompatible records=" + parsed.CompatibilityWarnings.Count);
        var home = Path.Combine(Path.GetTempPath(), "MorphocyteOS-private-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var candidate = Path.Combine(home, "profile.yaml");
            File.WriteAllText(candidate, parsed.Yaml);
            var engine = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Packaging/Engine/sing-box.exe"));
            var validation = await CoreProcessManager.ValidateAsync(engine, candidate, inspectionDeadline.Token);
            Console.WriteLine("Native sing-box validation=" + validation.Success + "; no live VPN started.");
            if (args[0] == "--probe-private-content" && validation.Success) await XrayPrivateProbe.Run(parsed,inspectionDeadline.Token);
        }
        finally { Directory.Delete(home, true); }
    }
    catch (InvalidDataException error) { Console.WriteLine("Parser rejected: " + error.Message); }
    return;
}

var passed = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
void Reject(Action action, string label) { try { action(); } catch (InvalidDataException) { Check(true, label); return; } throw new Exception("FAIL: accepted " + label); }
async Task RejectAsync(Func<Task> action, string label, string? forbidden = null)
{
    try { await action(); }
    catch (InvalidDataException ex) { Check(forbidden is null || !ex.ToString().Contains(forbidden), label); return; }
    throw new Exception("FAIL: accepted " + label);
}
const string first = "vless://12345678-1234-4234-8234-123456789abc@vpn.example:443?security=tls&sni=vpn.example&type=ws&path=%2Fws&host=vpn.example#Europe";
const string second = "vless://12345678-1234-4234-8234-123456789abd@other.example:443?security=tls&type=tcp#Europe";
const string url = "https://subscription.example/private-token";
YamlMappingNode Root(string text) { var yaml = new YamlStream(); yaml.Load(new StringReader(text)); return (YamlMappingNode)yaml.Documents[0].RootNode; }
YamlSequenceNode Nodes(ImportedProfile profile) => (YamlSequenceNode)Root(profile.Yaml).Children[new YamlScalarNode("proxies")];
string Name(YamlNode node) => ((YamlScalarNode)((YamlMappingNode)node).Children[new YamlScalarNode("name")]).Value!;
var two = SubscriptionImporter.ParseContent(first + "\r\n" + second);
string EncodeLink(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
var vmess = "vmess://" + EncodeLink("""{"v":"2","ps":"VMess Europe","add":"vmess.example","port":443,"id":"12345678-1234-4234-8234-123456789abc","aid":0,"net":"ws","host":"vmess.example","path":"/ws","tls":"tls"}""");
const string trojan = "trojan://p%3Aword@trojan.example:443?security=tls&sni=trojan.example&type=grpc&serviceName=fixture&mode=gun#Trojan";
var shadowsocks = "ss://" + EncodeLink("aes-256-gcm:p:word") + "@ss.example:8388#Shadowsocks";
var socks = "socks://" + EncodeLink("username:p:word@socks.example:1080") + "#SOCKS";
var mixedLinks = SubscriptionImporter.ParseContent(EncodeLink(string.Join('\n', first, vmess, trojan, shadowsocks, socks)), allowPartial: true);
Check(mixedLinks.CompatibilityWarnings.Count == 0
    && Nodes(mixedLinks).Children.Select(Name).SequenceEqual(new[] { "Europe", "VMess Europe", "Trojan", "Shadowsocks", "SOCKS" }),
    "a Base64 subscription imports all supported mixed link protocols and keeps VMess names");
Check(!SubscriptionImporter.ParseContent(vmess).NeedsName, "a standalone VMess record uses its embedded name");
Check(Nodes(SubscriptionImporter.ParseContent(EncodeLink("[" + FullProfileChecks.Fixture() + "]"))).Children.Count == 1,
    "Base64-wrapped Xray JSON subscriptions retain the automatic profile");
Check(SubscriptionImporter.ParseContent(first + "\n" + shadowsocks.Replace("#Shadowsocks", "?plugin=unsupported#Shadowsocks"), allowPartial: true).CompatibilityWarnings.Count == 1,
    "a Shadowsocks link with unsupported plugin is reported instead of silently losing the plugin");
const string hy2Link = "hy2://user%3Afixture-password@hy.example:8443/?sni=hy.example&obfs=salamander&obfs-password=fixture-obfs#Hysteria";
var hy2 = ProfileImporter.Parse(hy2Link);
var hy2Node = (YamlMappingNode)Nodes(hy2).Children[0];
string Field(YamlMappingNode mapping, string key) => ((YamlScalarNode)mapping.Children[new YamlScalarNode(key)]).Value!;
Check(Field(hy2Node, "password") == "user:fixture-password" && Field(hy2Node, "port") == "8443"
    && Field(hy2Node, "sni") == "hy.example" && Field(hy2Node, "obfs-password") == "fixture-obfs", "Hysteria2 URI preserves auth, SNI, port and obfuscation without decoding twice");
Check(Field((YamlMappingNode)Nodes(ProfileImporter.Parse("hysteria2://fixture@hy.example/#Default")).Children[0], "port") == "443", "Hysteria2 defaults omitted port to 443");
Check(ProfileImporter.Parse("hy2://fixture@hy.example?insecure=1").InsecureTls, "Hysteria2 insecure TLS still requires user confirmation");
Check(SubscriptionImporter.ParseContent(hy2Link + "\n" + first).CompatibilityWarnings.Count == 0, "mixed Hysteria2/VLESS imports completely when all parameters are supported");
foreach (var unsafeLink in new[] { "hy2://hy.example", "hy2://fixture@hy.example?pinSHA256=abcd", "hy2://fixture@hy.example?ech=abcd",
    "hy2://fixture@hy.example?obfs=salamander", "hy2://fixture@hy.example?obfs-password=secret", "hy2://fixture@hy.example?insecure=2",
    "hy2://fixture@hy.example?sni=one.example&sni=two.example", "hy2://fixture@hy.example:0", "hy2://fixture@hy.example/path" })
    Reject(() => ProfileImporter.Parse(unsafeLink), "unsupported or malformed Hysteria2 never loses connection/security options");
var incompatibleLink = first.Replace("#Europe", "&fm=fixture#Fragment");
Reject(() => SubscriptionImporter.ParseContent(first + "\n" + incompatibleLink), "strict subscription import remains all-or-nothing by default");
var compatiblePart = SubscriptionImporter.ParseContent(first + "\n" + incompatibleLink + "\n" + hy2Link, allowPartial: true);
Check(Nodes(compatiblePart).Children.Count == 2 && compatiblePart.CompatibilityWarnings.Count == 1 && compatiblePart.AllowPartialSubscription,
    "explicit compatible-part mode retains intact supported nodes and records every skipped entry");
Check(!compatiblePart.CompatibilityWarnings[0].Contains("fixture") && compatiblePart.CompatibilityWarnings[0].Contains("2"), "compatibility reports identify record position without leaking its URL or credentials");
Reject(() => SubscriptionImporter.ParseContent(incompatibleLink, allowPartial: true), "no compatible nodes refuses import rather than creating an empty working profile");
Check(SubscriptionImporter.ParseContent(Convert.ToBase64String(Encoding.UTF8.GetBytes(first + "\n" + incompatibleLink)), true).CompatibilityWarnings.Count == 1,
    "Base64 keeps the same explicit partial-import policy and warning count");
var nativeFixture = SingBoxConfig.Build(ClashConfigDocument.Parse("hy2.yaml", hy2.Yaml), 12345, new string('A', 64), false, includeTun: false);
using (var nativeJson = System.Text.Json.JsonDocument.Parse(nativeFixture))
{
    var outbound = nativeJson.RootElement.GetProperty("outbounds").EnumerateArray().Single(value => value.GetProperty("tag").GetString() == "Hysteria");
    Check(outbound.GetProperty("type").GetString() == "hysteria2" && outbound.GetProperty("password").GetString() == "user:fixture-password"
        && outbound.GetProperty("tls").GetProperty("enabled").GetBoolean() && !outbound.GetProperty("tls").GetProperty("insecure").GetBoolean()
        && outbound.GetProperty("obfs").GetProperty("password").GetString() == "fixture-obfs", "sing-box receives authentic Hysteria2 credentials, enabled TLS and exact obfuscation settings");
}
Check(Nodes(two).Children.Count == 2 && Name(Nodes(two).Children[0]) == "Europe" && Name(Nodes(two).Children[1]) == "Europe (2)", "multiple VLESS nodes preserve labels and get unique names");
var document = ClashConfigDocument.Parse("test.yaml", two.Yaml);
Check(document.ValidateForRouting().Count == 0 && document.HasVpnConnection && document.Rules.Count == 0 && two.Yaml.Contains("MATCH,DIRECT"), "subscription creates TUN profile with direct fallback, no hidden rules");
var group = (YamlMappingNode)((YamlSequenceNode)Root(two.Yaml).Children[new YamlScalarNode("proxy-groups")]).Children[0];
Check(((YamlSequenceNode)group.Children[new YamlScalarNode("proxies")]).Children.Count == 2, "all imported servers are available in VPN select group");
var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(first + "\n" + second));
Check(SubscriptionImporter.ParseContent(base64).Yaml == two.Yaml, "Base64 list decoded");
Check(SubscriptionImporter.ParseContent(base64.TrimEnd('=').Replace('+', '-').Replace('/', '_')).Yaml == two.Yaml, "unpadded URL-safe Base64 decoded");
Check(SubscriptionImporter.ParseContent("\uFEFF# servers\n" + first).Yaml.Contains("Europe"), "UTF-8 BOM and comment lines handled");
Check(SubscriptionImporter.ParseContent(first.Replace("Europe", "VPN")).Yaml.Contains("VPN (2)"), "server labels do not collide with policy group");
Check(SubscriptionImporter.ParseContent(first.Replace("security=tls", "security=tls&allowInsecure=1")).InsecureTls, "insecure VLESS still requires confirmation");
const string yamlContent = """
external-ui-url: https://untrusted.example/ui.zip
external-controller: 0.0.0.0:9090
allow-lan: true
proxies:
  - {name: Example, type: vless, server: vpn.example, port: 443, uuid: 12345678-1234-4234-8234-123456789abc, tls: true, servername: vpn.example}
  - {name: Example, type: trojan, server: other.example, port: 443, password: dummy, skip-cert-verify: true}
rules:
  - MATCH,Example
""";
var yamlImport = SubscriptionImporter.ParseContent(yamlContent);
Check(Nodes(yamlImport).Children.Count == 2 && yamlImport.InsecureTls, "Clash YAML imports multiple protocol nodes and detects insecure TLS");
Check(!yamlImport.Yaml.Contains("untrusted.example") && !yamlImport.Yaml.Contains("external-controller") && yamlImport.Yaml.Contains("allow-lan: false") && yamlImport.Yaml.Contains("MATCH,DIRECT"), "remote controller, UI download and routing are not imported");
Reject(() => SubscriptionImporter.ParseContent("<html>login</html>"), "HTML login page rejected");
Reject(() => SubscriptionImporter.ParseContent(""), "empty subscription rejected");
Reject(() => SubscriptionImporter.ParseContent("%%%broken%%%"), "malformed payload rejected");
Reject(() => SubscriptionImporter.ParseContent("aGVsbG8="), "Base64 without server data rejected");
Reject(() => SubscriptionImporter.ParseContent(first + "\nss://other"), "mixed unsupported links rejected, no partial import");
Reject(() => SubscriptionImporter.ParseContent(string.Join('\n', Enumerable.Repeat(first, 201))), "node count limited");
Reject(() => SubscriptionImporter.ParseContent(new string('a', ProfileImporter.MaxInputLength + 1)), "text length limited");
Reject(() => SubscriptionImporter.ParseContent("proxy-providers: {remote: {type: http, url: https://example.com/list}}"), "provider-only YAML rejected without fetching dependencies");
Reject(() => SubscriptionImporter.ParseContent("proxies: &list []\ncopy: *list"), "YAML anchors and alias expansion rejected");
Reject(() => SubscriptionImporter.ParseContent("proxies: !!str text"), "custom YAML tags rejected");
Reject(() => SubscriptionImporter.ParseContent("proxies: []\n---\nproxies: []"), "multiple YAML documents rejected");
Reject(() => SubscriptionImporter.ParseContent("proxies: []\nproxies: []"), "duplicate YAML keys rejected");
Reject(() => SubscriptionImporter.ParseContent(yamlContent.Replace("password: dummy", "password: dummy, private-key: C:/private.pem")), "local private-key dependencies rejected");
Reject(() => SubscriptionImporter.ParseContent(yamlContent.Replace("password: dummy", "password: dummy, dialer-proxy: hidden")), "hidden proxy dependencies rejected");
Reject(() => SubscriptionImporter.ParseContent(yamlContent.Replace("name: Example", "name: \"bad\\nname\"")), "control characters in server names rejected");
Reject(() => SubscriptionImporter.ParseContent("x: " + string.Concat(Enumerable.Repeat("[", 33)) + "0" + new string(']', 33)), "YAML nesting bounded");
foreach (var address in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "192.168.1.1", "169.254.169.254", "100.64.0.1", "0.0.0.0", "224.0.0.1", "192.0.2.1", "198.18.0.1", "198.51.100.1", "203.0.113.1", "::1", "::ffff:127.0.0.1", "fd00::1", "fe80::1", "2001:db8::1", "2002:7f00:1::1" })
    Check(!SubscriptionImporter.IsPublicAddress(IPAddress.Parse(address)), "non-public IP rejected: " + address);
foreach (var address in new[] { "1.1.1.1", "8.8.8.8", "2606:4700:4700::1111" })
    Check(SubscriptionImporter.IsPublicAddress(IPAddress.Parse(address)), "public IP accepted: " + address);
Check(SubscriptionImporter.ParseDnsAnswers("{\"Status\":0,\"Answer\":[{\"type\":5,\"data\":\"cdn.example\"},{\"type\":1,\"data\":\"1.1.1.1\"}]}").Single().Equals(IPAddress.Parse("1.1.1.1")), "Fake-IP DNS fallback accepts public answer and ignores CNAME metadata");
Check(SubscriptionImporter.ParseDnsAnswers("{\"Status\":0}").Length == 0, "DNS no-answer response handled");
foreach (var badDns in new[] { "{\"Status\":3}", "{\"Status\":\"0\"}", "[]", "{\"Status\":0,\"Answer\":{}}", "{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"127.0.0.1\"}]}", "{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"198.18.0.1\"}]}" })
{
    try { SubscriptionImporter.ParseDnsAnswers(badDns); throw new Exception("FAIL: unsafe DNS answer accepted"); }
    catch (HttpRequestException) { Check(true, "unsafe or malformed Fake-IP DNS fallback rejected"); }
}
foreach (var bad in new[] { "http://example.com", "https://localhost/file", "https://router/file", "https://127.0.0.1/file", "https://[::1]/file", "https://user:pass@example.com/file", "https://example.com/file#token", "https://example.com/a b" })
    Reject(() => SubscriptionImporter.ValidateUrl(bad), "unsafe subscription URL rejected");
using var successClient = new HttpClient(new FakeHandler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(base64) })));
Check((await SubscriptionImporter.ImportAsync(url, default, successClient)).Yaml == two.Yaml, "HTTPS response imports list");
var deviceRoot = Path.Combine(Path.GetTempPath(), "MorphocyteOS-device-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(deviceRoot);
try
{
    var id = SubscriptionDeviceIdentity.GetOrCreate(deviceRoot);
    var deviceFile = Path.Combine(deviceRoot, "subscription-device-id.dat");
    Check(Guid.TryParseExact(id, "D", out _) && SubscriptionDeviceIdentity.GetOrCreate(deviceRoot) == id,
        "stored device identity is stable across reads");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(deviceFile)).Contains(id), "device identity is protected on disk");
    using var optedIn = new HttpClient(new FakeHandler((request, token) =>
    {
        Check(request.Headers.GetValues("x-hwid").Single() == id && request.Headers.UserAgent.ToString().StartsWith("MorphocyteOS/"),
            "explicit opt-in sends the application's own identity without impersonating Happ");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(first) });
    }));
    var withId = await SubscriptionImporter.ImportAsync(url, default, optedIn, hardwareId: id);
    Check(withId.SendHardwareId && withId.SubscriptionUrl == url, "import remembers only the identity opt-in, not the raw identity");
    Check(ProfileImporter.NameConnection(withId, "Named").SendHardwareId, "manual naming preserves device opt-in");
    using var optedOut = new HttpClient(new FakeHandler((request, token) =>
    {
        Check(!request.Headers.Contains("x-hwid"), "existing subscriptions do not receive identity without explicit opt-in");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(first) });
    }));
    Check(!(await SubscriptionImporter.ImportAsync(url, default, optedOut)).SendHardwareId, "default import remains opted out");
    var countIdRedirects = 0;
    using var crossOrigin = new HttpClient(new FakeHandler((request, token) =>
    {
        countIdRedirects++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://other.example/list") } });
    }));
    await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, crossOrigin, hardwareId: id), "device identity never follows a cross-origin redirect", id);
    Check(countIdRedirects == 1, "redirect target is not contacted with or without identity");
    var sameOriginCount = 0;
    using var sameOrigin = new HttpClient(new FakeHandler((request, token) =>
    {
        Check(request.Headers.GetValues("x-hwid").Single() == id, "same-origin requests retain the opted-in identity");
        return Task.FromResult(++sameOriginCount == 1
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/next", UriKind.Relative) } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(first) });
    }));
    Check((await SubscriptionImporter.ImportAsync(url, default, sameOrigin, hardwareId: id)).SendHardwareId, "same-origin redirect imports successfully");
    await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, successClient, hardwareId: "bad\r\nheader"), "invalid identity is rejected before sending");
    var savedSource = SubscriptionSource.Create(url, sendHardwareId: true);
    Check(savedSource.SendHardwareId && savedSource.ReadUrl() == url && SubscriptionSource.Create(url).SendHardwareId,
        "subscription persistence retains automatic app identity for new sources");
    var savedSettingsPath = Path.Combine(deviceRoot, "settings.json");
    var savedSettings = AppSettings.Load(savedSettingsPath);
    savedSettings.ProfileSubscriptions[Path.Combine(deviceRoot, "profile.yaml")] = savedSource;
    savedSettings.Save();
    Check(AppSettings.Load(savedSettingsPath).ProfileSubscriptions.Values.Single().SendHardwareId
        && !File.ReadAllText(savedSettingsPath).Contains(id), "opt-in survives settings reload without storing identity in profile metadata");
    File.WriteAllBytes(deviceFile, new byte[] { 1, 2, 3 });
    Reject(() => SubscriptionDeviceIdentity.GetOrCreate(deviceRoot), "damaged identity is not silently replaced with a new device");
    Check(File.ReadAllBytes(deviceFile).SequenceEqual(new byte[] { 1, 2, 3 }), "damaged identity bytes are retained");
}
finally { Directory.Delete(deviceRoot, true); }
var hwidPlaceholder = first.Replace("vpn.example:443", "127.0.0.1:443").Replace("#Europe", "#" + Uri.EscapeDataString("Отсутствует HWID"));
Reject(() => SubscriptionImporter.ParseContent(hwidPlaceholder), "provider's HWID placeholder is not imported as a working server");
Reject(() => SubscriptionImporter.ParseContent(Convert.ToBase64String(Encoding.UTF8.GetBytes(hwidPlaceholder))), "encoded HWID placeholder is rejected too");
var redirects = 0;
using var redirectClient = new HttpClient(new FakeHandler((request, token) =>
{
    redirects++;
    var response = new HttpResponseMessage(redirects == 1 ? HttpStatusCode.Found : HttpStatusCode.OK) { Content = new StringContent(first) };
    if (redirects == 1) response.Headers.Location = new Uri("/list", UriKind.Relative);
    return Task.FromResult(response);
}));
Check(Nodes(await SubscriptionImporter.ImportAsync(url, default, redirectClient)).Children.Count == 1 && redirects == 2, "relative HTTPS redirect handled");
foreach (var destination in new[] { "http://example.com/list", "https://127.0.0.1/list", "https://[::ffff:127.0.0.1]/list" })
{
    using var badRedirect = new HttpClient(new FakeHandler((request, token) => { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(destination); return Task.FromResult(response); }));
    await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, badRedirect), "unsafe redirect rejected", "private-token");
}
using var endless = new HttpClient(new FakeHandler((request, token) => { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("/again", UriKind.Relative); return Task.FromResult(response); }));
await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, endless), "redirect count bounded", "private-token");
using var unauthenticated = new HttpClient(new FakeHandler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, unauthenticated), "HTTP errors sanitized", "private-token");
using var fail = new HttpClient(new FakeHandler((request, token) => throw new HttpRequestException("failure " + url)));
await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, fail), "connection errors do not leak private URL", "private-token");
using var oversized = new HttpClient(new FakeHandler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[SubscriptionImporter.MaxBytes + 1]) })));
await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, oversized), "large declared response rejected");
using var undeclared = new HttpClient(new FakeHandler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[SubscriptionImporter.MaxBytes + 1]) })));
await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, undeclared), "response without Content-Length is also bounded");
using var invalidEncoding = new HttpClient(new FakeHandler((request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xff, 0xff }) })));
await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, invalidEncoding), "invalid UTF-8 response rejected");
using var slow = new HttpClient(new FakeHandler(async (request, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new HttpResponseMessage(HttpStatusCode.OK); }));
await RejectAsync(() => SubscriptionImporter.ImportAsync(url, default, slow, TimeSpan.FromMilliseconds(25)), "timeout gives readable error");
using var cancellation = new CancellationTokenSource(25);
try { await SubscriptionImporter.ImportAsync(url, cancellation.Token, slow); throw new Exception("FAIL: cancellation ignored"); }
catch (OperationCanceledException) { Check(true, "user cancellation preserved"); }
Check((await SubscriptionImporter.ImportAsync(first, default)).Yaml == ProfileImporter.Parse(first).Yaml, "local VLESS import remains compatible");
var unnamed = ProfileImporter.Parse(first.Split('#')[0]);
Check(unnamed.NeedsName && !ProfileImporter.Parse(first).NeedsName, "only an unnamed single VLESS connection requests a name");
Check(SubscriptionImporter.ParseContent(first.Split('#')[0]).NeedsName, "single unnamed subscription link requests a name");
Check(!SubscriptionImporter.ParseContent(first.Split('#')[0] + "\n" + second.Split('#')[0]).NeedsName, "multiple unnamed subscription servers keep numbered names without repeated prompts");
Check(SubscriptionImporter.ParseContent("proxies: [{type: socks5, server: vpn.example, port: 443}]").NeedsName, "single unnamed YAML server requests a name");
var namingMetadata=new SubscriptionMetadata { Title="Named subscription" };
var renamed = ProfileImporter.NameConnection(unnamed with { Metadata=namingMetadata }, "🇳🇱 Мой VPN");
Check(ReferenceEquals(renamed.Metadata,namingMetadata),"naming an unnamed subscription server preserves its traffic, expiry and provider information");
Check(!renamed.NeedsName && Name(Nodes(renamed).Children[0]) == "🇳🇱 Мой VPN", "manual naming updates server and clears request");
var renamedGroup = (YamlMappingNode)((YamlSequenceNode)Root(renamed.Yaml).Children[new YamlScalarNode("proxy-groups")]).Children[0];
Check(((YamlScalarNode)((YamlSequenceNode)renamedGroup.Children[new YamlScalarNode("proxies")]).Children[0]).Value == "🇳🇱 Мой VPN", "manual naming updates VPN group reference");
Reject(() => ProfileImporter.NameConnection(unnamed, " "), "empty manual name rejected");
Reject(() => ProfileImporter.NameConnection(unnamed, "VPN"), "reserved manual name rejected");
Reject(() => ProfileImporter.NameConnection(unnamed, "bad,name"), "comma in manual name rejected");
Reject(() => ProfileImporter.NameConnection(two, "One"), "single naming cannot discard multiple imported nodes");
var scratch = Path.Combine(Path.GetTempPath(), "MorphocyteOS-subscriptions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var corePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Packaging/Engine/mihomo.exe"));
    foreach (var profile in new[] { two, yamlImport, hy2, compatiblePart, mixedLinks })
    {
        var file = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(file, profile.Yaml);
        var validation = await CoreProcessManager.ValidateAsync(corePath, file, default);
        Check(validation.Success, "actual Mihomo validates imported subscription without starting VPN");
        var singBoxPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Packaging/Engine/sing-box.exe"));
        if (ReferenceEquals(profile, hy2) || ReferenceEquals(profile, compatiblePart) || ReferenceEquals(profile, mixedLinks))
            Check((await CoreProcessManager.ValidateAsync(singBoxPath, file, default)).Success, "actual sing-box validates mixed subscriptions without starting TUN");
    }
}
finally { Directory.Delete(scratch, recursive: true); }
if (args.Contains("--live-public"))
{
    try
    {
        await SubscriptionImporter.ImportAsync("https://raw.githubusercontent.com/Rabotaet3/MorphocyteOS/v1.1.0/Source/MorphocyteRouter/Assets/profile-template.yaml", default);
        throw new Exception("FAIL: empty public template accepted");
    }
    catch (InvalidDataException ex)
    {
        Check(ex.Message.StartsWith("В YAML нет списка серверов proxies"), "real HTTPS DNS/TLS/download reaches payload parsing: " + ex.Message);
    }
}
SubscriptionIdentityChecks.Run(Check);
await XrayBridgeChecks.Run(Check);
await FullProfileChecks.Run(Check);
Console.WriteLine($"Subscription checks passed: {passed}");

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
}

sealed class UnknownLengthContent(byte[] data) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}
