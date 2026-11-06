using System.IO;
using System.Net.Http;
using System.Text.Json;
using MorphocyteRouter;
using YamlDotNet.RepresentationModel;

internal partial class Program
{
    private static int RunInformationEntryChecks(string scratch)
    {
        var count=0;
        void Check(bool ok,string label) { if(!ok) throw new Exception("FAIL: "+label); count++; Console.WriteLine("PASS: "+label); }
        const string yaml="proxies:\n - {name: '⬇ WiFi ⬇', type: socks5, server: 100, port: 443}\n - {name: Actual, type: socks5, server: 192.0.2.1, port: 1080}\n - {name: '⬇ LTE ⬇', type: socks5, server: 110, port: 443}\n - {name: Second, type: socks5, server: 192.0.2.2, port: 1080}\nproxy-groups:\n - {name: VPN, type: select, proxies: ['⬇ WiFi ⬇', Actual, '⬇ LTE ⬇', Second]}\nrules:\n - MATCH,VPN\n";
        var path=Path.Combine(scratch,"information.yaml"); var document=ClashConfigDocument.Parse(path,yaml);
        Check(document.ConnectionCount==2 && VpnServerCatalog.Choices(document).SequenceEqual(new[]{"Actual","Second"}),"numeric section heading endpoints are not counted or offered as VPN servers");
        Check(SubscriptionInformation.ResolveSelection(document,"⬇ LTE ⬇")=="Actual"
            && SubscriptionInformation.ResolveSelection(document,null)=="Actual","old remembered headings and heading defaults migrate to the first actual server");
        Check(SubscriptionInformation.ResolveSelection(document,"Second")=="Second","a valid remembered server is never replaced by heading migration");
        var direct=ClashConfigDocument.Parse(path,yaml.Replace("proxies: ['⬇ WiFi ⬇', Actual, '⬇ LTE ⬇', Second]","proxies: [DIRECT, '⬇ WiFi ⬇', Actual, '⬇ LTE ⬇', Second]"));
        Check(SubscriptionInformation.ResolveSelection(direct,null) is null,"an explicit DIRECT group default is not overridden merely because headings exist");
        foreach(var mode in new[]{false,true})
        {
            using var json=JsonDocument.Parse(SingBoxConfig.Build(document,12345,new string('A',64),mode,selectedServer:"⬇ LTE ⬇",includeTun:false));
            var outbounds=json.RootElement.GetProperty("outbounds").EnumerateArray().ToArray();
            var group=outbounds.Single(node=>node.GetProperty("tag").GetString()=="VPN");
            Check(group.GetProperty("default").GetString()=="Actual"
                && group.GetProperty("outbounds").EnumerateArray().Select(item=>item.GetString()).SequenceEqual(new[]{"Actual","Second"})
                && !outbounds.Any(node=>node.TryGetProperty("server",out var host) && host.GetString() is "100" or "110"),
                $"sing-box runtime excludes dummy servers and members with fullTunnel={mode}");
        }
        var runtime=document.BuildRuntimeText(12345,new string('B',64),selectedServer:"⬇ LTE ⬇");
        var stream=new YamlStream(); stream.Load(new StringReader(runtime));
        var root=(YamlMappingNode)stream.Documents[0].RootNode;
        Check(((YamlSequenceNode)root.Children[new YamlScalarNode("proxies")]).Children.Count==2
            && runtime.Contains("store-selected: false"),"Mihomo runtime also removes heading entries and prevents stale selection cache override");
        Check(document.SourceHash==ClashConfigDocument.Hash(yaml) && document.ProxyNodes.Count()==4,"runtime cleanup never rewrites the user's source profile");
        var imported=SubscriptionImporter.ParseContent(yaml);
        Check(ClashConfigDocument.Parse(path,imported.Yaml).ConnectionCount==2 && imported.Description.Contains("разделителей: 2"),"YAML subscription import ignores information entries and reports their count");
        const string real="vless://11111111-1111-4111-8111-111111111111@vpn.example:443?security=tls#Actual";
        var section=real.Replace("vpn.example","100").Replace("#Actual","#"+Uri.EscapeDataString("⬇ WiFi ⬇"));
        var links=SubscriptionImporter.ParseContent(section+"\n"+real);
        Check(ClashConfigDocument.Parse(path,links.Yaml).ConnectionCount==1 && !links.Yaml.Contains("server: 100"),"VLESS subscription links cannot make a section heading the initial server");
        try { SubscriptionImporter.ParseContent(section); throw new Exception("Heading-only subscription accepted"); }
        catch(InvalidDataException) { Check(true,"heading-only subscriptions fail instead of pretending to contain a working VPN"); }
        var normal=document.ProxyNodes.First();
        normal.Children[new YamlScalarNode("server")]=new YamlScalarNode("192.0.2.1");
        Check(!SubscriptionInformation.IsSection(normal),"decorative arrows never hide a server with a real address");
        normal.Children[new YamlScalarNode("server")]=new YamlScalarNode("127.0.0.1");
        Check(!SubscriptionInformation.IsSection(normal),"legitimate loopback proxy configurations remain available");
        normal.Children[new YamlScalarNode("server")]=new YamlScalarNode("0.0.0.110");
        Check(SubscriptionInformation.IsSection(normal),"explicit and abbreviated zero-network IPv4 heading addresses are treated identically");
        var failure=VpnSpeedTest.FailureMessage(new HttpRequestException(HttpRequestError.ProxyTunnelError));
        Check(failure.Contains("VPN-сервер") && !failure.Contains("Перезапусти"),"a proxy handshake failure no longer falsely diagnoses a missing local measurement channel");
        return count;
    }
}
