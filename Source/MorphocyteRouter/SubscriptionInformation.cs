using System.IO;
using System.Net;
using System.Net.Sockets;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

// Some providers encode section headings as proxy links with decorative arrows
// and dummy 0.0.0.x endpoints (also written as the short IPv4 forms 100 / 110).
// Do not classify a real endpoint from its name, country, protocol or reachability.
internal static class SubscriptionInformation
{
    private static string Text(YamlMappingNode node,string key) => node.Children.TryGetValue(new YamlScalarNode(key),out var value)
        && value is YamlScalarNode scalar?scalar.Value??"":"";

    internal static bool IsSection(YamlMappingNode node)
        => IsSection(Text(node,"server"),Text(node,"name"));

    internal static bool IsSection(Uri uri) => IsSection(uri.Host,Uri.UnescapeDataString(uri.Fragment.TrimStart('#')));

    private static bool IsSection(string host,string name)
    {
        if(!name.Contains('⬇') && !name.Contains('↓')) return false;
        if(!IPAddress.TryParse(host,out var address) || address.AddressFamily!=AddressFamily.InterNetwork) return false;
        var bytes=address.GetAddressBytes();
        return bytes[0]==0 && bytes[1]==0 && bytes[2]==0;
    }

    internal static string? ResolveSelection(ClashConfigDocument document,string? requested)
    {
        var headings=document.ProxyNodes.Where(IsSection).Select(node=>Text(node,"name")).ToHashSet(StringComparer.Ordinal);
        if(headings.Count==0) return requested;
        var current=requested;
        if(string.IsNullOrWhiteSpace(current))
        {
            current=document.VpnRoute;
            var groups=document.ProxyGroups.ToDictionary(node=>Text(node,"name"),StringComparer.Ordinal);
            var visited=new HashSet<string>(StringComparer.Ordinal);
            while(current is not null && visited.Add(current) && groups.TryGetValue(current,out var group))
                current=group.Children.TryGetValue(new YamlScalarNode("proxies"),out var members) && members is YamlSequenceNode list
                    ?list.Children.FirstOrDefault()?.ToString():null;
        }
        if(current is null || !headings.Contains(current)) return requested;
        return VpnServerCatalog.Choices(document,subscriptionOrder:true).FirstOrDefault()
            ?? throw new InvalidDataException("В подписке найдены только служебные разделители. Нет сервера для подключения.");
    }

    internal static void RemoveFromRuntime(YamlMappingNode root)
    {
        if(!root.Children.TryGetValue(new YamlScalarNode("proxies"),out var value) || value is not YamlSequenceNode proxies) return;
        var headings=proxies.Children.OfType<YamlMappingNode>().Where(IsSection).ToArray();
        if(headings.Length==0) return;
        var names=headings.Select(node=>Text(node,"name")).ToHashSet(StringComparer.Ordinal);
        foreach(var node in headings) proxies.Children.Remove(node);
        if(root.Children.TryGetValue(new YamlScalarNode("proxy-groups"),out var groups) && groups is YamlSequenceNode sequence)
            foreach(var group in sequence.Children.OfType<YamlMappingNode>())
                if(group.Children.TryGetValue(new YamlScalarNode("proxies"),out var entries) && entries is YamlSequenceNode members)
                {
                    foreach(var member in members.Children.Where(member=>names.Contains(member.ToString())).ToArray()) members.Children.Remove(member);
                    if(members.Children.Count==0) throw new InvalidDataException("В VPN-группе остались только служебные разделители. Добавь настоящий сервер.");
                }
    }
}
