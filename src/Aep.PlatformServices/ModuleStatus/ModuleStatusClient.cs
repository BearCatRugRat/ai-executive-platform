using System.Net.Http.Json;
using System.Text.Json;
using Aep.ModuleContracts;

namespace Aep.PlatformServices.ModuleStatus;

/// <summary>
/// Thin client over any domain app's GET /module-status endpoint (the
/// Module Contract — see Aep.ModuleContracts.ModuleStatusDto). Unlike
/// GovernanceClient, which only ever talks to collector-intelligence-
/// engine, one ModuleStatusClient instance is scoped to a single module's
/// base address — the Command Center is expected to hold one instance per
/// entry in <see cref="KnownModules"/> and poll each independently, so one
/// module being down never blocks another's status from rendering.
///
/// Same "fail fast and clearly, don't hang" posture as GovernanceClient:
/// every module here is bound to localhost only (no auth yet), so a
/// failure almost always means that module's own Docker stack isn't
/// running — callers should surface that per-module, not as a whole-
/// console outage.
/// </summary>
public sealed class ModuleStatusClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _httpClient;

    public ModuleStatusClient(Uri baseAddress, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.BaseAddress ??= baseAddress;
    }

    public async Task<ModuleStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await _httpClient.GetFromJsonAsync<ModuleStatusDto>(
            "module-status", JsonOptions, cancellationToken);
        return status ?? throw new InvalidOperationException(
            $"{_httpClient.BaseAddress}module-status returned an empty response.");
    }
}
