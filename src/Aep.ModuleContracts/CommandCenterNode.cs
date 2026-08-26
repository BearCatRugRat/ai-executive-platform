// The Command Center's own tree registry -- confirmed 2026-08-25, the
// "flatten the top, layer buttons as you move down" restructure. Deliberately
// separate from ModuleStatus.cs: ModuleStatusDto/ModuleStatusCardDto are
// what a domain app's own API returns; CommandCenterNode is how the Command
// Center itself describes its tree shape and decides, per node, where that
// node's content (if any) comes from.
//
// A node's content is composable, not an exclusive choice between types:
//   - GovernanceProjectName set -> render that governance project's status/
//     Open/Develop block (the same data MainViewModel already reads from
//     GovernanceClient's briefing).
//   - SoftwareProjectName set -> render that software project's status block.
//   - ModuleBaseAddress (a base URL string, e.g. "http://localhost:8000/") +
//     ModuleCardId both set -> render that one card from the named domain's
//     own GET /module-status response.
//   - None of the above set, and the node has no children either -> render
//     StubText as a plain "not built yet" placeholder, optionally with
//     StubDeepLink as a "go build this" claude://code/new link.
// A node can carry content AND have children at the same time (e.g. the top
// "Personal Finance" node shows the old Personal Finance & Tax project's
// status while also being a folder for Coins and Bullion, Bullion market
// analysis, and Tax underneath). "Has children" is computed by the loader
// (any other node whose ParentId equals this Id), not stored here.
namespace Aep.ModuleContracts;

public sealed record CommandCenterNode(
    string Id,
    string Title,
    string? ParentId,
    string? GovernanceProjectName,
    string? SoftwareProjectName,
    string? ModuleBaseAddress,
    string? ModuleCardId,
    string? StubText,
    string? StubDeepLink)
{
    /// <summary>Parsed on demand rather than stored as a Uri directly -- kept as
    /// a plain string on the record so JSON round-trips without depending on
    /// System.Text.Json's Uri converter behavior.</summary>
    public Uri? ModuleBaseAddressUri => ModuleBaseAddress is null ? null : new Uri(ModuleBaseAddress);
}
