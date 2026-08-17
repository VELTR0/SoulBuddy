# AGENTS.md

## Purpose
SoulBuddy is a .NET 8 / Avalonia desktop application for Pokémon SoulLink/Nuzlocke runs. It reads live game state from DeSmuME through Lua collectors, turns that data into SoulBuddy runtime state, synchronizes compatible online trackers, shows in-game event messages, and can expose a partner stream on the local network.

## Read this first
Before making changes, inspect the relevant implementation and follow the data flow end-to-end instead of patching UI symptoms. Prefer root-cause fixes, preserve existing tracker compatibility, and add diagnostics when behavior depends on emulator memory, network responses, or external tracker schemas.

## Repository map
- `Program.cs`, `App.cs`: application bootstrap and app lifecycle.
- `Views/`, `ViewModels/`: Avalonia UI and presentation state.
- `Models/`: shared application models.
- `Services/`: runtime orchestration, tracker clients, synchronization, diagnostics, SoulLink messaging, streaming, localization and UI helpers.
- `Sources/`: source-side/runtime input components.
- `collectors/desmume-gen4/`: Lua scripts running inside DeSmuME. These scripts read Pokémon/game memory and publish live state to SoulBuddy.
- `Data/`: application data.
- `Ressources/` / `Resources/`: static resources. Be careful: both spellings currently appear in the project.

## Important files
Tracker synchronization:
- `Services/ITrackerClient.cs`
- `Services/TrackerClientFactory.cs`
- `Services/TrackerLinkParser.cs`
- `Services/SyncService.cs`
- `Services/SoullockeClient.cs`
- `Services/SoullockeDotComTrackerClient.cs`
- `Services/VercelSoullockeClient.cs`
- `Services/VercelTrackerClient.cs`
- `Services/SoullockeLaunchSettings.cs`
- `Services/DiagnosticLog.cs`

Game-state / DeSmuME pipeline:
- `collectors/desmume-gen4/soulbuddy_all.lua` when present in the current revision/package
- `collectors/desmume-gen4/bootstrap.lua`
- `collectors/desmume-gen4/live.lua`
- `collectors/desmume-gen4/live_state.lua`
- `collectors/desmume-gen4/pokemon.lua`
- `collectors/desmume-gen4/pokemon_memory_map_gen_4_gen_5.lua`
- `Services/SoulBuddyRuntime.cs`
- `Services/PartyStateService.cs`

SoulLink / in-game messages:
- `Services/SoulLinkRegistry.cs`
- `Services/OverlayMessageWriter.cs`
- `Services/MainWindowSoulLinkUi.cs`

Partner streaming:
- `Services/LocalStreamService.cs`
- `Services/LanStreamDiscoveryService.cs`
- `Services/HeadlessStreamCoordinator.cs`
- `Services/StreamPreviewClient.cs`

## Current development context
The project recently added support for tracker URLs hosted at `soullocke.vercel.app`. That integration initially worked, but the UI later started reporting `Server Synchronisation unsuccessful`. Extra diagnostics were added so the failing request flow can be traced. When working on this area, inspect `VercelSoullockeClient`, `SyncService`, tracker link parsing/factory selection, HTTP status/response bodies and `DiagnosticLog` before changing behavior.

Another recently investigated area is battle-state detection. SoulBuddy can recognize a newly started battle and the opponent, but historically the state could remain stuck after fleeing/ending a battle. Changes in `live_state.lua` or downstream state handling must distinguish battle start, active battle and overworld/battle end. Do not infer battle state solely from a stale opponent Pokémon value.

## Supported behavior that must not regress
- Automatic game-state collection from DeSmuME.
- Party/Pokémon state updates.
- Nuzlocke/SoulLink tracker synchronization.
- Existing `soullocke.com` behavior while supporting `soullocke.vercel.app`.
- In-game event/partner messages.
- Local-network partner streaming.
- HeartGold/SoulSilver flow documented in the README.

## Development rules
1. Inspect callers and consumers before editing a shared service or model.
2. Prefer small, explicit changes over broad rewrites unless the architecture itself is the root cause.
3. Never silently swallow network, JSON, Lua parsing or memory-read failures. Use the existing diagnostic path.
4. Diagnostic logs should include operation, endpoint/host, status/result and enough context to reproduce the failure, but never passwords or secrets.
5. Treat external tracker response formats as untrusted. Validate null/missing fields and unexpected HTTP responses.
6. Keep tracker-specific behavior behind `ITrackerClient`/factory abstractions where possible.
7. Preserve nullable reference type correctness.
8. Do not introduce a new framework unless clearly justified.
9. Avoid changing generated/vendor Lua helpers such as `dkjson.lua` unless necessary.
10. If changing memory addresses or battle heuristics, document why the address/heuristic is valid for the supported game/version.

## Build and verification
The app targets `.NET 8` and Avalonia.

Run at minimum:
```bash
dotnet restore
dotnet build SoulBuddy.csproj
```

There is no dedicated test project in the current repository root. For behavioral fixes, add focused tests if the affected logic can be isolated without emulator/UI dependencies. Otherwise, provide a deterministic manual verification procedure and keep diagnostics sufficient for a real DeSmuME run.

## Manual verification for tracker changes
At minimum verify:
1. A valid `soullocke.com` run still selects the correct tracker client.
2. A valid `soullocke.vercel.app` run selects the Vercel/Soullocke client expected by the factory.
3. Authentication/session setup succeeds or emits a useful diagnostic explaining the exact failing stage.
4. Initial synchronization works.
5. A Pokémon/encounter update is sent and accepted.
6. Error UI is cleared/recovered after a later successful synchronization.
7. Passwords/tokens are not written to logs.

## Manual verification for battle-state changes
Verify from a fresh overworld state:
1. SoulBuddy reports no active battle.
2. Enter a wild battle: battle becomes active and opponent is populated.
3. Flee: battle becomes inactive and opponent is cleared promptly.
4. Enter a second battle: state updates to the new opponent without stale data.
5. Repeat with a trainer battle when possible.

## When debugging
Read `docs/DEBUGGING.md` and inspect the newest diagnostic output before changing code. If logs are incomplete, improve instrumentation first, reproduce once, then fix the root cause.

## Definition of done
A task is done only when the relevant code path is understood, the fix is scoped, `dotnet build` succeeds, regressions were considered, and the response includes what changed plus how it was verified.