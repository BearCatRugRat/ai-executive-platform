// DTOs mirroring collector-intelligence-engine's governance API response
// shapes (apps/api/schemas/governance.py -> ExecutiveBriefing). The API
// serializes Python's snake_case field names as-is (no alias generator),
// so these are deserialized with JsonNamingPolicy.SnakeCaseLower rather
// than per-property [JsonPropertyName] attributes - see GovernanceClient.

namespace Aep.PlatformServices.Governance;

/// <summary>
/// Tier values for <see cref="GovernanceProjectDto.Tier"/>, matching
/// collector-intelligence-engine's ADR 0003 addendum (2026-08-03, part 2):
/// Domain Applications get their own database/API/module contract eventually;
/// Command Center Areas get a button and live status only, unless they graduate.
/// </summary>
public static class GovernanceTiers
{
    public const string DomainApplication = "Domain Application";
    public const string CommandCenterArea = "Command Center Area";
}

public sealed record SoftwareProjectDto(
    int Id,
    string Name,
    string? RepoPath,
    string? RepoUrl,
    string? Description,
    string Status,
    string? CurrentPhase,
    string? ActiveBranch,
    string? LastCommitSha,
    string? LastCommitMessage,
    string? NextAction,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GovernanceProjectDto(
    int Id,
    string Name,
    string? Domain,
    string? Tier,
    string? DropboxPath,
    string Status,
    string? Priority,
    string? NextAction,
    string? Owner,
    string? TargetDate,
    string? WaitingOn,
    string? Risk,
    string? LastReview,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ActionDto(
    int Id,
    string? ExternalId,
    string? Priority,
    string? Area,
    string Action,
    string Status,
    string? Owner,
    string? NextReview,
    int? GovernanceProjectId,
    int? SoftwareProjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DecisionDto(
    int Id,
    string? DecidedOn,
    string Decision,
    string? Reason,
    string? Impact,
    string Status,
    int? GovernanceProjectId,
    int? SoftwareProjectId,
    DateTimeOffset CreatedAt);

public sealed record ExecutiveBriefingDto(
    List<SoftwareProjectDto> SoftwareProjects,
    List<GovernanceProjectDto> GovernanceProjects,
    List<ActionDto> OpenActions,
    List<DecisionDto> RecentDecisions);
