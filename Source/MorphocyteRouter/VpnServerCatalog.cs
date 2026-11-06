using System.IO;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal static class VpnServerCatalog
{
    private static string Text(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key),out var value) ? value.ToString() : "";
    internal static IReadOnlyList<string> Choices(ClashConfigDocument document, bool subscriptionOrder = false)
    {
        var nodes=document.ProxyNodes.GroupBy(node=>Text(node,"name"),StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>group.First(),StringComparer.Ordinal);
        var groups=document.ProxyGroups.GroupBy(node=>Text(node,"name"),StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>group.First(),StringComparer.Ordinal);
        var found=new List<string>(); var visited=new HashSet<string>(StringComparer.Ordinal);
        void Visit(string name,int depth)
        {
            if(depth>=12 || visited.Count>=512 || !visited.Add(name) || name is "DIRECT" or "REJECT" or "REJECT-DROP") return;
            if(nodes.TryGetValue(name,out var node))
            {
                if(Text(node,"type") is not ("direct" or "reject") && !SubscriptionInformation.IsSection(node)) found.Add(name);
                return;
            }
            if(!groups.TryGetValue(name,out var group)) return;
            if(Text(group,"type")!="select") { found.Add(name); return; }
            if(group.Children.TryGetValue(new YamlScalarNode("proxies"),out var members) && members is YamlSequenceNode list)
                foreach(var member in list.Children.OfType<YamlScalarNode>()) Visit(member.Value??"",depth+1);
        }
        if(document.VpnRoute is { } route) Visit(route,0);
        if (subscriptionOrder)
        {
            var positions = nodes.Keys.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
            return found.OrderBy(name => positions.GetValueOrDefault(name, int.MaxValue)).ToArray();
        }
        return found;
    }

    internal static IReadOnlyDictionary<string,string> SelectionPath(ClashConfigDocument document,string selected)
    {
        var groups=document.ProxyGroups.ToDictionary(node=>Text(node,"name"),StringComparer.Ordinal);
        var visited=new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string,string>? Find(string name,int depth)
        {
            if(name==selected) return new(StringComparer.Ordinal);
            if(depth>=12 || visited.Count>=512 || !visited.Add(name) || !groups.TryGetValue(name,out var group)
                || Text(group,"type")!="select" || !group.Children.TryGetValue(new YamlScalarNode("proxies"),out var members) || members is not YamlSequenceNode list) return null;
            foreach(var member in list.Children.OfType<YamlScalarNode>())
                if(Find(member.Value??"",depth+1) is { } path) { path[name]=member.Value!; return path; }
            return null;
        }
        return document.VpnRoute is { } root && Find(root,0) is { } path ? path
            : throw new InvalidDataException("Этот сервер недоступен для выбора в текущей VPN-группе.");
    }
}
