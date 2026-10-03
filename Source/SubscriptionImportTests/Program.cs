using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

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
var renamed = ProfileImporter.NameConnection(unnamed, "🇳🇱 Мой VPN");
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
    foreach (var profile in new[] { two, yamlImport })
    {
        var file = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(file, profile.Yaml);
        var validation = await CoreProcessManager.ValidateAsync(corePath, file, default);
        Check(validation.Success, "actual Mihomo validates imported subscription without starting VPN");
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
