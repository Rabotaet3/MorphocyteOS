using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal sealed record VpnServerPresentation(string Title, string Details, string? CountryCode)
{
    private static readonly (string Code,string Names)[] Countries = [
        ("DE","Германия|Germany"),("FI","Финляндия|Finland"),("SE","Швеция|Sweden"),
        ("PL","Польша|Poland"),("NL","Нидерланды|Netherlands|Голландия"),("LT","Литва|Lithuania"),
        ("CZ","Чехия|Czechia|Czech Republic"),("IT","Италия|Italy"),("CH","Швейцария|Switzerland"),
        ("BG","Болгария|Bulgaria"),("EE","Эстония|Estonia"),("FR","Франция|France"),
        ("RU","Россия|Russia"),("UA","Украина|Ukraine"),("AT","Австрия|Austria"),
        ("BE","Бельгия|Belgium"),("IE","Ирландия|Ireland"),("DK","Дания|Denmark"),
        ("NO","Норвегия|Norway"),("IS","Исландия|Iceland"),("GB","Великобритания|United Kingdom|Britain"),
        ("US","США|United States|USA"),("JP","Япония|Japan"),("EU","Европа|Europe")
    ];

    internal static VpnServerPresentation Describe(string name, YamlMappingNode? node)
    {
        var title=name; string? country=null;
        for(var index=0;index<name.Length-3;index++)
            if(Rune.TryGetRuneAt(name,index,out var first) && first.Value is >=0x1f1e6 and <=0x1f1ff
                && Rune.TryGetRuneAt(name,index+2,out var second) && second.Value is >=0x1f1e6 and <=0x1f1ff)
            {
                country??=$"{(char)('A'+first.Value-0x1f1e6)}{(char)('A'+second.Value-0x1f1e6)}";
                title=title.Replace(name.Substring(index,4),"",StringComparison.Ordinal); index+=3;
            }
        title=Regex.Replace(title.Trim(),@" {2,}"," ");
        if(country is null)
            foreach(var candidate in Countries)
                if(Regex.IsMatch(name,@"(?<![\p{L}\p{N}])(?:"+candidate.Names+@")(?![\p{L}\p{N}])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))
                { country=candidate.Code; break; }
        if(node is null) return new(title,"",country);
        string Text(string key) => node.Children.TryGetValue(new YamlScalarNode(key),out var value) && value is YamlScalarNode scalar ? scalar.Value??"":"";
        bool Has(string key) => node.Children.ContainsKey(new YamlScalarNode(key));
        var kind=Text("type").ToLowerInvariant();
        if(kind==XrayFullProfile.NodeType) return new(title,"XRAY / АВТО / JSON","AUTO");
        if(kind==XrayProfile.NodeType && Has("xray-outbound"))
        {
            try
            {
                var outbound=XrayJsonConnection.FromNode(node);
                var stream=outbound["streamSettings"];
                var jsonProtocol=outbound["protocol"]?.ToString()=="hysteria"?"HYSTERIA2":"VLESS";
                var jsonNetwork=stream?["network"]?.ToString()=="hysteria"?"QUIC":Transport(stream?["network"]?.ToString()??"tcp");
                var jsonSecurity=stream?["security"]?.ToString()?.ToUpperInvariant()??"";
                return new(title,string.Join(" / ",new[]{jsonProtocol,jsonNetwork,jsonSecurity,"JSON"}.Where(part=>part.Length>0 && part!="NONE")),country);
            }
            catch { return new(title,"XRAY / JSON",country); }
        }
        var protocol=kind switch { "ss"=>"SHADOWSOCKS", "hysteria2" or "hy2"=>"HYSTERIA2", "url-test"=>"Автовыбор", "select"=>"Группа серверов", _=>kind.ToUpperInvariant() };
        var transport=""; var security="";
        if(kind==XrayProfile.NodeType)
        {
            try
            {
                var uri=new Uri(Text("xray-link")); var query=XrayProfile.Query(uri);
                var hy=uri.Scheme is "hy2" or "hysteria2";
                protocol=hy?"HYSTERIA2":uri.Scheme.ToUpperInvariant();
                transport=hy?"QUIC":Transport(query.GetValueOrDefault("type","tcp"));
                security=hy?"TLS":query.GetValueOrDefault("security","").ToLowerInvariant() switch { "reality"=>"REALITY", "tls"=>"TLS", _=>"" };
            }
            catch { protocol="XRAY"; }
        }
        else
        {
            if(kind is "hysteria2" or "hy2" or "hysteria" or "tuic") { transport="QUIC"; security="TLS"; }
            else if(kind is "vless" or "vmess" or "trojan") transport=Transport(Text("network") is { Length:>0 } network?network:"tcp");
            if(Has("reality-opts")) security="REALITY";
            else if(kind=="trojan" || Text("tls").Equals("true",StringComparison.OrdinalIgnoreCase)) security="TLS";
        }
        return new(title,string.Join(" / ",new[]{protocol,transport,security}.Where(value=>value.Length>0)),country);
    }

    private static string Transport(string type) => type.ToLowerInvariant() switch {
        "tcp" or "raw"=>"TCP", "ws"=>"WS", "grpc"=>"gRPC", "h2" or "http"=>"HTTP/2",
        "xhttp" or "splithttp"=>"XHTTP", "httpupgrade"=>"HTTP Upgrade", "quic"=>"QUIC", _=>""
    };
}
