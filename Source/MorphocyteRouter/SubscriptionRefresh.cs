using System.IO;
using YamlDotNet.RepresentationModel;

namespace MorphocyteRouter;

internal sealed record SubscriptionRefreshResult(string Yaml, int Added, int Removed, bool SelectionRemoved, string SelectedServer);

internal static class SubscriptionRefresh
{
    internal static SubscriptionRefreshResult Merge(string path, string original, ImportedProfile imported, string selected)
    {
        var document = ClashConfigDocument.Parse(path, original);
        if (document.HadMarkdownFence) throw new InvalidDataException("Сначала примени конфигурацию, затем обнови подписку.");
        var prior = Load(original); var next = Load(imported.Yaml);
        var oldNodes = Names(Sequence(prior, "proxies"));
        var newNodes = Names(Sequence(next, "proxies"));
        if (string.IsNullOrWhiteSpace(selected)) selected = ResolveSelection(prior, document.VpnRoute, oldNodes);
        var selectionRemoved = oldNodes.Contains(selected) && !newNodes.Contains(selected);
        var newOrder = Sequence(next, "proxies").Children.OfType<YamlMappingNode>().Select(node =>
            ((YamlScalarNode)node.Children[new YamlScalarNode("name")]).Value!).ToList();
        if (newNodes.Count == 0) throw new InvalidDataException("Подписка не содержит доступных серверов. Профиль сохранён.");
        var replacements = new Dictionary<string, YamlNode> { ["proxies"] = Sequence(next, "proxies") };
        if (prior.Children.TryGetValue(new YamlScalarNode("proxy-groups"), out var groupsNode) && groupsNode is YamlSequenceNode groups)
        {
            foreach (var group in groups.Children.OfType<YamlMappingNode>())
            {
                if (!group.Children.TryGetValue(new YamlScalarNode("proxies"), out var membersNode) || membersNode is not YamlSequenceNode members) continue;
                var names = members.Children.OfType<YamlScalarNode>().Select(node => node.Value ?? "").ToList();
                var oldMembers = names.Where(oldNodes.Contains).ToHashSet(StringComparer.Ordinal);
                var followsSubscription = oldMembers.SetEquals(oldNodes) && oldMembers.Count > 0;
                var newMembers = followsSubscription ? names.Where(name => !oldNodes.Contains(name)).Concat(newOrder).ToList()
                    : names.Where(name => !oldNodes.Contains(name) || newNodes.Contains(name)).ToList();
                if (newMembers.Count == 0)
                    throw new InvalidDataException("После обновления одна из групп останется без серверов. Измени её состав перед обновлением; текущий профиль сохранён.");
                // Keep a still available selection first, including when imported nodes were reordered.
                if (newNodes.Contains(selected) && newMembers.Remove(selected)) newMembers.Insert(0, selected);
                // A removed VPN selection must not silently fall back to DIRECT or REJECT.
                if (selectionRemoved && oldMembers.Contains(selected))
                {
                    var fallback = newMembers.FirstOrDefault(newNodes.Contains);
                    if (fallback is null) throw new InvalidDataException("В группе выбранного сервера не осталось VPN-подключений. Измени состав группы; текущий профиль сохранён.");
                    newMembers.Remove(fallback); newMembers.Insert(0, fallback);
                }
                group.Children[new YamlScalarNode("proxies")] = new YamlSequenceNode(newMembers.Select(name => new YamlScalarNode(name)));
            }
            replacements["proxy-groups"] = groups;
        }
        var updated = original;
        foreach (var section in replacements.OrderByDescending(pair => prior.Children.First(entry =>
            entry.Key is YamlScalarNode scalar && scalar.Value == pair.Key).Key.Start.Index))
        {
            var key = prior.Children.Keys.First(node => node is YamlScalarNode scalar && scalar.Value == section.Key);
            var start = LineStart(original, checked((int)key.Start.Index));
            var nextKey = prior.Children.Keys.Where(node => node.Start.Index > key.Start.Index).OrderBy(node => node.Start.Index).FirstOrDefault();
            var end = nextKey is null ? original.Length : LineStart(original, checked((int)nextKey.Start.Index));
            var root = new YamlMappingNode { { new YamlScalarNode(section.Key), section.Value } };
            using var writer = new StringWriter();
            new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
            var text = writer.ToString();
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^(?:\.\.\.|---)\r?\n", "");
            updated = updated[..start] + text + updated[end..];
        }
        var parsed = ClashConfigDocument.Parse(path, updated);
        if (parsed.Validate().Count > 0 || parsed.Rules.Any(rule => rule.Route is not ("DIRECT" or "REJECT") && !parsed.Routes.Contains(rule.Route)))
            throw new InvalidDataException("Правило ссылается на сервер, которого больше нет в подписке. Измени маршрут этого правила; текущий профиль сохранён.");
        return new(updated, newNodes.Except(oldNodes).Count(), oldNodes.Except(newNodes).Count(), selectionRemoved, selectionRemoved ? ResolveSelection(prior, parsed.VpnRoute, newNodes) : selected);
    }

    private static string ResolveSelection(YamlMappingNode root, string? route, HashSet<string> nodes)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(route) && visited.Add(route))
        {
            if (nodes.Contains(route) || route is "DIRECT" or "REJECT") return route;
            if (!root.Children.TryGetValue(new YamlScalarNode("proxy-groups"), out var value) || value is not YamlSequenceNode groups) break;
            var group = groups.Children.OfType<YamlMappingNode>().FirstOrDefault(group =>
                group.Children.TryGetValue(new YamlScalarNode("name"), out var name) && name is YamlScalarNode label && label.Value == route);
            if (group is null || !group.Children.TryGetValue(new YamlScalarNode("proxies"), out var members) || members is not YamlSequenceNode sequence) break;
            route = (sequence.Children.FirstOrDefault() as YamlScalarNode)?.Value;
        }
        return "";
    }

    private static YamlMappingNode Load(string text)
    {
        var yaml = new YamlStream(); yaml.Load(new StringReader(text));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root) throw new InvalidDataException("Некорректный профиль подписки.");
        return root;
    }
    private static YamlSequenceNode Sequence(YamlMappingNode root, string key) => root.Children.TryGetValue(new YamlScalarNode(key), out var node)
        && node is YamlSequenceNode sequence ? sequence : throw new InvalidDataException("В профиле нет списка серверов.");
    private static HashSet<string> Names(YamlSequenceNode sequence) => sequence.Children.OfType<YamlMappingNode>()
        .Select(node => node.Children.TryGetValue(new YamlScalarNode("name"), out var value) ? value as YamlScalarNode : null)
        .Where(node => !string.IsNullOrWhiteSpace(node?.Value)).Select(node => node!.Value!).ToHashSet(StringComparer.Ordinal);
    private static int LineStart(string text, int index) => index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
}
