using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

// A complete provider profile is one selectable connection. Its DNS, balancing
// and observation state are kept in a separate owned Xray process.
internal static class XrayFullProfile
{
    internal const string NodeType="xray-profile";
    // Same ranges as v2fly/geoip's built-in private list, with adjacent ranges
    // merged. Avoid requiring a separate GeoIP database for this one category.
    // https://raw.githubusercontent.com/v2fly/geoip/release/text/private.txt
    private static readonly string[] PrivateNetworks=["0.0.0.0/8","10.0.0.0/8","100.64.0.0/10","127.0.0.0/8","169.254.0.0/16","172.16.0.0/12",
        "192.0.0.0/24","192.0.2.0/24","192.88.99.0/24","192.168.0.0/16","198.18.0.0/15","198.51.100.0/24","203.0.113.0/24","224.0.0.0/3","::/127","fc00::/7","fe80::/10","ff00::/8"];
    private static InvalidDataException Invalid()=>new("Полный JSON-профиль Xray содержит неподдерживаемые параметры. Профиль не упрощён.");

    internal static bool IsFull(JsonElement root) => root.ValueKind==JsonValueKind.Object && (
        root.TryGetProperty("burstObservatory",out _) || root.TryGetProperty("observatory",out _)
        || root.TryGetProperty("routing",out var routing) && routing.ValueKind==JsonValueKind.Object && routing.TryGetProperty("balancers",out _)
        || root.TryGetProperty("outbounds",out var outbounds) && outbounds.ValueKind==JsonValueKind.Array
            && outbounds.EnumerateArray().Count(item=>item.TryGetProperty("protocol",out var protocol)&&protocol.GetString()=="vless")>1);

    internal static ImportedProfile Import(string text)
    {
        var root=Parse(text);
        var first=root["outbounds"]!.AsArray().First(item=>item?["protocol"]?.ToString()=="vless")!;
        var peer=first["settings"]!["vnext"]![0]!;
        var name=ProfileImporter.ValidateConnectionName(root["remarks"]?.ToString() is { Length:>0 } title?title:"Авто · Xray",renameReserved:true);
        var node=new YamlMappingNode {
            {"name",name},{"type",NodeType},{"server",peer["address"]!.ToString()},{"port",peer["port"]!.ToString()},
            {"xray-json",root.ToJsonString()}
        };
        return ProfileImporter.BuildConnections([node],"Xray JSON · DNS и маршрутизация провайдера · Автовыбор",false);
    }

    internal static JsonObject FromNode(YamlMappingNode node)
    {
        string Text(string key)=>node.Children.TryGetValue(new YamlScalarNode(key),out var value)&&value is YamlScalarNode scalar?scalar.Value??"":"";
        if(node.Children.Keys.Any(key=>key.ToString() is not ("name" or "type" or "server" or "port" or "xray-json"))) throw Invalid();
        var result=Parse(Text("xray-json"));
        var first=result["outbounds"]!.AsArray().First(item=>item?["protocol"]?.ToString()=="vless")!["settings"]!["vnext"]![0]!;
        if(Text("type")!=NodeType || Text("server")!=first["address"]!.ToString() || Text("port")!=first["port"]!.ToString()) throw Invalid();
        return result;
    }

    private static JsonObject Parse(string text)
    {
        try
        {
            if(text.Length>ProfileImporter.MaxInputLength) throw Invalid();
            using var document=JsonDocument.Parse(text,new JsonDocumentOptions { MaxDepth=48 });
            ValidateTree(document.RootElement);
            var root=JsonNode.Parse(text)!.AsObject();
            if(root.Any(item=>item.Key is not ("dns" or "routing" or "inbounds" or "outbounds" or "policy" or "burstObservatory" or "observatory" or "remarks" or "meta" or "log" or "stats"))) throw Invalid();
            if(root["routing"]?["script"] is not null) throw Invalid();
            if(root["outbounds"] is not JsonArray outbounds || outbounds.Count is 0 or >64) throw Invalid();
            var tags=new HashSet<string>(StringComparer.Ordinal);
            foreach(var outbound in outbounds)
            {
                if(outbound is not JsonObject item || item["tag"]?.ToString() is not { Length:>0 and <=128 } tag || !tags.Add(tag)) throw Invalid();
                if(item["protocol"]?.ToString() is not ("vless" or "freedom" or "blackhole")) throw Invalid();
                if(item["protocol"]!.ToString()=="vless" && (item["settings"]?["vnext"] is not JsonArray peers || peers.Count!=1
                    || peers[0]?["address"]?.ToString() is not { Length:>0 } || !int.TryParse(peers[0]?["port"]?.ToString(),out var port) || port is <1 or >65535)) throw Invalid();
            }
            if(!outbounds.Any(item=>item?["protocol"]?.ToString()=="vless")) throw Invalid();
            if(root["inbounds"] is JsonArray inbounds)
                foreach(var inbound in inbounds)
                    if(inbound?["protocol"]?.ToString() is not ("socks" or "http")) throw Invalid();
            return root;
        }
        catch(InvalidDataException) { throw; }
        catch { throw Invalid(); }
    }

    internal static void ValidateTree(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in value.EnumerateObject())
            {
                if(!keys.Add(property.Name) || property.Name is "certificateFile" or "keyFile" or "masterKeyLog" or "webhook" or "script"
                    || property.Name=="allowInsecure" && property.Value.ValueKind==JsonValueKind.True) throw Invalid();
                ValidateTree(property.Value);
            }
        }
        else if(value.ValueKind==JsonValueKind.Array) foreach(var item in value.EnumerateArray()) ValidateTree(item);
        else if(value.ValueKind==JsonValueKind.String)
        {
            var text=value.GetString()!;
            var reference=text.TrimStart('!');
            if(reference.StartsWith("ext:",StringComparison.OrdinalIgnoreCase) || reference.StartsWith("geosite:",StringComparison.OrdinalIgnoreCase)
                || reference.StartsWith("geoip:",StringComparison.OrdinalIgnoreCase)&&text!="geoip:private") throw Invalid();
        }
    }

    internal static JsonObject Runtime(JsonObject profile,XrayBridgeEndpoint endpoint)
    {
        var result=(JsonObject)profile.DeepClone();
        var originalTags=(result["inbounds"] as JsonArray??[]).Select(item=>item?["tag"]?.ToString()??"").Where(tag=>tag.Length>0).ToHashSet(StringComparer.Ordinal);
        var sniffing=(result["inbounds"] as JsonArray)?.FirstOrDefault(item=>item?["protocol"]?.ToString()=="socks")?["sniffing"]?.DeepClone();
        result["inbounds"]=new JsonArray(new JsonObject {
            ["tag"]="morphocyte-profile-in",["listen"]="127.0.0.1",["port"]=endpoint.Port,["protocol"]="socks",
            ["settings"]=new JsonObject { ["auth"]="password",["udp"]=true,["ip"]="127.0.0.1",
                ["accounts"]=new JsonArray(new JsonObject { ["user"]=endpoint.Username,["pass"]=endpoint.Password }) },["sniffing"]=sniffing
        });
        if(result["routing"]?["rules"] is JsonArray rules)
            foreach(var rule in rules)
            {
                if(rule?["ip"] is JsonArray addresses && addresses.Any(item=>item?.ToString()=="geoip:private"))
                {
                    var expanded=new JsonArray();
                    foreach(var address in addresses)
                        if(address?.ToString()=="geoip:private") foreach(var range in PrivateNetworks) expanded.Add(range);
                        else expanded.Add(address?.DeepClone());
                    rule["ip"]=expanded;
                }
                if(rule?["inboundTag"] is JsonArray inboundTags)
                {
                    if(inboundTags.Any(tag=>!originalTags.Contains(tag!.ToString()))) throw Invalid();
                    rule["inboundTag"]=new JsonArray("morphocyte-profile-in");
                }
            }
        foreach(var outbound in result["outbounds"]!.AsArray().Where(item=>item?["protocol"]?.ToString()=="vless"))
        {
            var stream=outbound!["streamSettings"] as JsonObject;
            if(stream is null) { stream=new JsonObject(); outbound["streamSettings"]=stream; }
            var socket=stream["sockopt"] as JsonObject;
            if(socket is null) { socket=new JsonObject(); stream["sockopt"]=socket; }
            if(socket["domainStrategy"] is null) socket["domainStrategy"]="ForceIP";
        }
        result["log"]=new JsonObject { ["loglevel"]="none" };
        result.Remove("remarks"); result.Remove("meta"); result.Remove("stats");
        return result;
    }
}
