namespace Aep.PlatformServices.ModuleStatus;

/// <summary>
/// Known GET /module-status endpoints for domain apps that implement the
/// Module Contract — one entry per docker-compose port assignment. Step E's
/// Command Center UI is expected to iterate this list to build one
/// ModuleStatusClient per module; a new domain app only needs adding here
/// once it implements the contract (apps/api/routers/module_status.py in
/// its own repo) — no other Command Center code changes with it.
///
/// Both APIs are bound to 127.0.0.1 only (no auth yet), same posture as
/// GovernanceClient.DefaultBaseAddress.
/// </summary>
public static class KnownModules
{
    public static readonly Uri CollectorIntelligence = new("http://localhost:8000/");
    public static readonly Uri MyersWolinIp = new("http://localhost:8001/");
}
