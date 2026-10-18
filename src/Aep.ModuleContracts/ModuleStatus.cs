// DTOs mirroring the Module Contract every domain app exposes at
// GET /module-status (apps/api/schemas/module_status.py, identical in both
// collector-intelligence-engine and myers-wolin-ip-intelligence as of
// 2026-08-25). This is the "Module Contracts" layer from AEP-0001-PLATFORM-
// ARCHITECTURE.md (layer 3 of 5) — the whole point is that these two DTOs
// are the only thing the Command Center needs to know to render *any*
// domain app's status cards, with no per-domain branching.
//
// Same JSON convention as GovernanceModels.cs: the API serializes Python's
// snake_case field names as-is, deserialized with
// JsonNamingPolicy.SnakeCaseLower (see ModuleStatusClient) rather than
// per-property [JsonPropertyName] attributes.

namespace Aep.ModuleContracts;

/// <summary>
/// Severity values for <see cref="ModuleStatusCardDto.Severity"/>, matching
/// the Literal["ok", "attention", "alert"] on the Python side.
/// </summary>
public static class ModuleStatusSeverity
{
    public const string Ok = "ok";
    public const string Attention = "attention";
    public const string Alert = "alert";
}

/// <summary>
/// One status card a domain app wants rendered on the Command Center.
/// <see cref="WebUrl"/> and <see cref="ClaudeDeepLink"/> are both optional
/// "go deeper" links — a card can offer either, both, or neither. Either
/// URL is just launched via Process.Start; the Command Center never needs
/// to know what kind of link it is.
/// </summary>
public sealed record ModuleStatusCardDto(
    string Id,
    string Title,
    string Headline,
    string? Detail,
    string Severity,
    DateTimeOffset? UpdatedAt,
    string? WebUrl,
    string? ClaudeDeepLink);

/// <summary>
/// The full GET /module-status response from one domain app.
/// </summary>
public sealed record ModuleStatusDto(
    string Module,
    string DisplayName,
    DateTimeOffset GeneratedAt,
    List<ModuleStatusCardDto> Cards);
