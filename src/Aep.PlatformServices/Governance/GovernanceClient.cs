using System.Net.Http.Json;
using System.Text.Json;

namespace Aep.PlatformServices.Governance;

/// <summary>
/// Thin client over collector-intelligence-engine's governance API
/// (apps/api/routers/governance.py). This is the one integration point the
/// Executive Command Center needs: /governance/briefing already aggregates
/// software projects, domain/command-center-area projects, open actions,
/// and recent decisions in a single call.
///
/// The API is bound to 127.0.0.1 only (no auth yet - see ADR 0003 Section 8
/// in collector-intelligence-engine), so this only ever talks to localhost.
/// If the Docker stack isn't running, calls fail fast with a clear message
/// rather than hanging - callers should surface that to the user rather
/// than crashing (e.g. "Is the Docker stack running?").
/// </summary>
public sealed class GovernanceClient
{
    public static readonly Uri DefaultBaseAddress = new("http://localhost:8000/");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _httpClient;

    public GovernanceClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.BaseAddress ??= DefaultBaseAddress;
    }

    public async Task<ExecutiveBriefingDto> GetBriefingAsync(CancellationToken cancellationToken = default)
    {
        var briefing = await _httpClient.GetFromJsonAsync<ExecutiveBriefingDto>(
            "governance/briefing", JsonOptions, cancellationToken);
        return briefing ?? throw new InvalidOperationException(
            "Governance API returned an empty response for /governance/briefing.");
    }
}
