using System.Text.Json;
using Aep.ModuleContracts;

namespace Aep.PlatformServices.CommandCenterRegistry;

/// <summary>
/// Loads the Command Center's own tree shape from a plain JSON file rather
/// than hardcoding it in C# -- the whole point of the 2026-08-25 registry
/// design is that adding, renaming, or reshuffling a card is a data change,
/// not a rebuild. See Aep.ModuleContracts.CommandCenterNode for the node
/// shape and CommandCenter/command-center-registry.json for the actual data.
///
/// Deliberately dumb: no validation beyond "does it parse and does every
/// ParentId reference a real node" -- if the file is hand-edited into
/// something broken, this throws with a clear message rather than silently
/// dropping nodes, since a wrong tree is worse than a loud failure at
/// startup.
/// </summary>
public static class CommandCenterRegistryLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static List<CommandCenterNode> LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var nodes = JsonSerializer.Deserialize<List<CommandCenterNode>>(json, JsonOptions)
            ?? throw new InvalidOperationException($"{path} parsed to an empty registry.");

        var ids = nodes.Select(n => n.Id).ToHashSet();
        foreach (var node in nodes)
        {
            if (node.ParentId is not null && !ids.Contains(node.ParentId))
            {
                throw new InvalidOperationException(
                    $"Command Center registry node '{node.Id}' has ParentId '{node.ParentId}', " +
                    "which doesn't match any node's Id.");
            }

            // Caught here rather than at render time: a typo'd address
            // ("htp://localhost:8001") would otherwise blow up deep inside
            // MainViewModel.BuildNodeView. ReloadRegistry swallows this into a
            // clear status message and keeps the last good tree.
            if (node.ModuleBaseAddress is not null
                && !Uri.TryCreate(node.ModuleBaseAddress, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException(
                    $"Command Center registry node '{node.Id}' has ModuleBaseAddress " +
                    $"'{node.ModuleBaseAddress}', which isn't a valid absolute URI.");
            }
        }

        return nodes;
    }
}
