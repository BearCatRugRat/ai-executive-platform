# AEP Platform Architecture

## Repository topology

Use sibling repositories under `C:\Development`:

```text
C:\Development\
  ai-executive-platform\
  collector-intelligence-engine\
  myers-wolin-ip-intelligence\        (future)
  prediction-intelligence-platform\   (future)
```

Do not place one Git repository inside another.

## Solution topology

```text
AIExecutivePlatform.sln
  src/
    Aep.CommandCenter
    Aep.Core
    Aep.PlatformServices
    Aep.ModuleContracts
  modules/
    CollectorIntelligence
    MyersWolinIP
    PredictionIntelligence
  docs/
  tools/
  tests/
```

## Layering

1. Command Center — user interface and navigation.
2. Shared Platform Services — morning briefing, search, AI orchestration, notifications, development services.
3. Module Contracts — stable interfaces for status, tasks, documents, commands, and navigation.
4. Domain Applications — independent bounded contexts.
5. Infrastructure — storage, connectors, GitHub, Dropbox, Microsoft 365, telemetry, backup, and security.

## Migration rule

The existing Collector repository remains operational. No bulk move is authorized. Integration occurs first through adapters and module manifests. Code is moved only when a shared responsibility is proven and covered by tests.
