using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

// Ordinary single-server records from a JSON subscription share the connection
// bridge; automatic profiles keep their independent DNS and routing processes.
internal static class XrayJsonConnection
{
    private static InvalidDataException Invalid()=>new("JSON-подключение Xray содержит неподдерживаемые параметры. Они не были удалены.");
    internal static YamlMappingNode Import(JsonElement profile,int index)
    {
        XrayFullProfile.ValidateTree(profile);
        var proxies=profile.GetProperty("outbounds").EnumerateArray().Where(item=>item.TryGetProperty("protocol",out var type)&&type.GetString() is "vless" or "hysteria").ToArray();
        if(proxies.Length!=1) throw Invalid();
        var outbound=Parse(proxies[0].GetRawText());
        var peer=Peer(outbound);
        var name=profile.TryGetProperty("remarks",out var label)&&label.ValueKind==JsonValueKind.String?label.GetString():null;
        name=ProfileImporter.ValidateConnectionName(string.IsNullOrWhiteSpace(name)?$"Сервер {index}":name,renameReserved:true);
        return new YamlMappingNode { {"name",name},{"type",XrayProfile.NodeType},{"server",peer["address"]!.ToString()},
            {"port",peer["port"]!.ToString()},{"xray-outbound",outbound.ToJsonString()} };
    }

    internal static JsonObject FromNode(YamlMappingNode node)
    {
        string Text(string key)=>node.Children.TryGetValue(new YamlScalarNode(key),out var value)&&value is YamlScalarNode scalar?scalar.Value??"":"";
        if(node.Children.Keys.Any(key=>key.ToString() is not ("name" or "type" or "server" or "port" or "xray-outbound"))) throw Invalid();
        var outbound=Parse(Text("xray-outbound")); var peer=Peer(outbound);
        if(Text("type")!=XrayProfile.NodeType || Text("server")!=peer["address"]!.ToString() || Text("port")!=peer["port"]!.ToString()) throw Invalid();
        return outbound;
    }

    private static JsonObject Peer(JsonObject outbound) => (outbound["protocol"]!.ToString()=="vless"
        ?outbound["settings"]?["vnext"]?[0]:outbound["settings"]) as JsonObject??throw Invalid();

    private static JsonObject Parse(string text)
    {
        try
        {
            if(text.Length>ProfileImporter.MaxInputLength) throw Invalid();
            using var document=JsonDocument.Parse(text,new JsonDocumentOptions { MaxDepth=48 }); XrayFullProfile.ValidateTree(document.RootElement);
            var outbound=JsonNode.Parse(text)!.AsObject();
            if(outbound.Any(item=>item.Key is not ("tag" or "protocol" or "settings" or "streamSettings" or "mux" or "sendThrough" or "targetStrategy"))) throw Invalid();
            if(outbound["protocol"]?.ToString() is not ("vless" or "hysteria")) throw Invalid();
            if(outbound["streamSettings"]?["sockopt"]?["dialerProxy"] is not null) throw Invalid();
            var peer=Peer(outbound);
            if(peer["address"]?.ToString() is not { Length:>0 } || !int.TryParse(peer["port"]?.ToString(),out var port) || port is <1 or >65535) throw Invalid();
            return outbound;
        }
        catch(InvalidDataException) { throw; }
        catch { throw Invalid(); }
    }
}
