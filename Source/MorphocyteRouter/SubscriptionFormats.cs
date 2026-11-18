using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

// The provider's share-link and desktop JSON views can contain different
// servers. Keep the union, preferring JSON only for equivalent connections.
internal static class SubscriptionFormats
{
    internal static IReadOnlyList<YamlMappingNode> Nodes(ImportedProfile profile)
        =>ClashConfigDocument.Parse("subscription-view.yaml",profile.Yaml).ProxyNodes.ToArray();

    internal static List<YamlMappingNode> Merge(ImportedProfile original,ImportedProfile desktop)
    {
        var originals=Nodes(original);
        var originalNames=originals.GroupBy(node=>Text(node,"name"),StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>Key(group.First()),StringComparer.Ordinal);
        var nodes=Nodes(desktop).ToList();
        foreach(var node in nodes)
        {
            var name=Text(node,"name");
            if(originalNames.TryGetValue(name,out var originalKey) && originalKey!=Key(node))
                node.Children[new YamlScalarNode("name")]=new YamlScalarNode((name.Length>245?name[..245]:name)+" · JSON");
        }
        var known=nodes.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach(var node in originals)
            // The canonical outbound is only a comparison key. Keep the
            // original engine and transport for nodes unique to the link view.
            if(known.Add(Key(node))) nodes.Add(node);
        return nodes;
    }

    internal static string Key(YamlMappingNode node)
    {
        if(!TryOutbound(node,out var outbound)) return "yaml:"+node.ToString();
        outbound.Remove("tag");
        if(outbound["settings"]?["vnext"] is JsonArray peers)
            foreach(var peer in peers)
                if(peer?["users"] is JsonArray users)
                    foreach(var user in users)
                        if(user?["flow"]?.ToString()=="") user.AsObject().Remove("flow");
        var stream=outbound["streamSettings"] as JsonObject;
        if(stream is not null)
        {
            if(stream["network"]?.ToString()=="raw") stream["network"]="tcp";
            if(stream["tcpSettings"] is JsonObject tcp)
            {
                if(tcp["header"] is JsonObject header && header.Count==1 && header["type"]?.ToString()=="none") tcp.Remove("header");
                if(tcp.Count==0) stream.Remove("tcpSettings");
            }
            if(stream["grpcSettings"] is JsonObject grpc)
            {
                foreach(var key in new[]{"mode","multiMode"}) if(grpc[key]?.ToJsonString()=="false") grpc.Remove(key);
                if(grpc["authority"]?.ToString()=="") grpc.Remove("authority");
            }
            foreach(var key in new[]{"tlsSettings","realitySettings"})
                if(stream[key] is JsonObject tls)
                    foreach(var flag in new[]{"allowInsecure","show"}) if(tls[flag]?.ToJsonString()=="false") tls.Remove(flag);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sort(outbound)!.ToJsonString(ProfileImporter.JsonWriteOptions))));
    }

    private static JsonNode? Sort(JsonNode? value) => value switch {
        JsonObject map=>new JsonObject(map.OrderBy(item=>item.Key,StringComparer.Ordinal).Select(item=>KeyValuePair.Create(item.Key,Sort(item.Value)))),
        JsonArray list=>new JsonArray(list.Select(Sort).ToArray()), _=>value?.DeepClone()
    };

    private static string Text(YamlMappingNode node,string key)=>node.Children.TryGetValue(new YamlScalarNode(key),out var value)&&value is YamlScalarNode scalar?scalar.Value??"":"";
    private static bool TryOutbound(YamlMappingNode node,out JsonObject outbound)
    {
        outbound=null!;
        try
        {
            if(Text(node,"type")==XrayProfile.NodeType) { outbound=XrayProfile.FromNode(node); return true; }
            var kind=Text(node,"type");
            if(kind is not ("vless" or "hysteria2")) return false;
            var allowed=kind=="vless"?new[]{"name","type","server","port","uuid","udp","tls","servername","sni","skip-cert-verify","client-fingerprint","alpn","flow","reality-opts","network","grpc-opts"}
                :new[]{"name","type","server","port","password","udp","sni","servername","skip-cert-verify","alpn","obfs","obfs-password"};
            if(node.Children.Keys.Any(key=>!allowed.Contains(key.ToString(),StringComparer.Ordinal))) return false;
            var network=Text(node,"network"); if(network.Length==0) network="tcp";
            if(kind=="vless" && network is not ("tcp" or "raw" or "grpc")) return false;
            var options=new Dictionary<string,string>();
            if(kind=="vless")
            {
                options["type"]=network;
                options["security"]=node.Children.ContainsKey(new YamlScalarNode("reality-opts"))?"reality":Text(node,"tls")=="true"?"tls":"none";
                if(Text(node,"client-fingerprint").Length>0) options["fp"]=Text(node,"client-fingerprint");
                if(Text(node,"flow").Length>0) options["flow"]=Text(node,"flow");
                if(node.Children.TryGetValue(new YamlScalarNode("reality-opts"),out var reality) && reality is YamlMappingNode realityMap)
                { options["pbk"]=Text(realityMap,"public-key"); options["sid"]=Text(realityMap,"short-id"); }
                if(node.Children.TryGetValue(new YamlScalarNode("grpc-opts"),out var grpc) && grpc is YamlMappingNode grpcMap)
                {
                    if(grpcMap.Children.Keys.Any(key=>key.ToString()!="grpc-service-name")) return false;
                    options["serviceName"]=Text(grpcMap,"grpc-service-name");
                }
            }
            else foreach(var key in new[]{"obfs","obfs-password"}) if(Text(node,key).Length>0) options[key]=Text(node,key);
            var sni=Text(node,"servername"); if(sni.Length==0) sni=Text(node,"sni"); if(sni.Length>0) options["sni"]=sni;
            if(Text(node,"skip-cert-verify")=="true") return false;
            if(node.Children.TryGetValue(new YamlScalarNode("alpn"),out var alpn)&&alpn is YamlSequenceNode alpns) options["alpn"]=string.Join(',',alpns.Children.Select(item=>item.ToString()));
            var host=Text(node,"server"); if(host.Contains(':')) host="["+host+"]";
            var link=(kind=="vless"?"vless":"hysteria2")+"://"+Uri.EscapeDataString(Text(node,kind=="vless"?"uuid":"password"))+"@"+host+":"+Text(node,"port")+"?"
                +string.Join('&',options.Select(item=>Uri.EscapeDataString(item.Key)+"="+Uri.EscapeDataString(item.Value)));
            outbound=XrayProfile.Parse(link).Outbound; return true;
        }
        catch { return false; }
    }
}
