# SoulBuddy Architecture

## Overview
SoulBuddy is a Windows-oriented .NET 8/Avalonia desktop application that bridges DeSmuME gameplay state with SoulLink/Nuzlocke functionality.

The important high-level flow is:

```text
DeSmuME game memory
      |
      v
Lua collectors in collectors/desmume-gen4
      |
      v
SoulBuddy runtime / state services
      |
      +--> Avalonia UI / Live Activity
      +--> Online tracker synchronization
      +--> SoulLink partner events / in-game messages
      +--> Local-network partner streaming
```

## 1. Emulator data collection
The Lua files in `collectors/desmume-gen4/` run inside DeSmuME and are the closest layer to emulator memory.

Notable files include:
- `bootstrap.lua`: collector bootstrap/runtime setup.
- `game_version.lua`: game/version detection support.
- `pokemon_memory_map_gen_4_gen_5.lua`: memory-map knowledge used by Pokémon readers.
- `pokemon_decrypt_gen_4_gen_5.lua`: Pokémon data decoding.
- `pokemon.lua`: Pokémon state extraction.
- `live.lua`: live collector/runtime entry behavior.
- `live_state.lua`: live game-state detection, including battle/overworld-related state.
- overlay files: in-emulator message rendering/reading support.

When a game-state bug occurs, determine first whether the wrong value originates in memory/Lua or whether correct Lua output becomes stale later in C#.

## 2. Application runtime
`Services/SoulBuddyRuntime.cs` is a central runtime-oriented service. Related state services, models and view models consume collector data and expose it to the UI and synchronization layers.

State should be treated as event-like/live data. In particular, values such as current opponent must not be used as permanent proof that a battle is still active: emulator memory can retain stale values after transitions.

## 3. Tracker abstraction
Tracker integrations are structured around `Services/ITrackerClient.cs` with selection/parsing helpers such as `TrackerLinkParser.cs` and `TrackerClientFactory.cs`.

Tracker implementations include `SoullockeClient.cs`, `SoullockeDotComTrackerClient.cs`, `VercelSoullockeClient.cs`, and `VercelTrackerClient.cs`.

`SyncService.cs` orchestrates synchronization behavior and should generally remain tracker-agnostic. Host/schema-specific behavior belongs in the corresponding tracker client where possible.

### Synchronization pipeline
```text
User tracker URL + credentials
      |
      v
TrackerLinkParser
      |
      v
TrackerClientFactory
      |
      v
ITrackerClient implementation
      |
      v
SyncService
      |
      v
Remote tracker API/backend
```

For failures, trace all five boundaries. A generic UI message such as `Server Synchronisation unsuccessful` is not sufficient diagnosis.

## 4. Diagnostics
`Services/DiagnosticLog.cs` is the preferred path for runtime diagnostics. Network integrations should log the stage of the operation and sanitized response context. Never log tracker passwords, session secrets, authentication tokens or full sensitive request payloads.

## 5. SoulLink communication and in-game messages
Relevant services include `SoulLinkRegistry.cs`, `MainWindowSoulLinkUi.cs`, and `OverlayMessageWriter.cs`. The emulator overlay crosses the C#/Lua boundary, so debug both sides when messages fail.

## 6. Partner streaming
The local partner-stream feature is primarily implemented through `LocalStreamService.cs`, `LanStreamDiscoveryService.cs`, `HeadlessStreamCoordinator.cs`, and `StreamPreviewClient.cs`. It is intended to work on the same local network with minimal setup.

## 7. UI
SoulBuddy uses Avalonia 12 with `Views/` and `ViewModels/`. UI state should report runtime truth but should not become the authoritative source for game state or synchronization state.

## 8. Architectural boundaries to preserve
Prefer:

```text
collector/input -> runtime/domain state -> services/integrations -> presentation
```

Avoid tracker clients manipulating Avalonia controls directly, UI code parsing external APIs, battle-state logic duplicated in several layers, tracker-specific host/schema checks scattered across the application, or network exceptions being reduced to a boolean before diagnostic context is recorded.

## 9. Current risk areas
### Vercel Soullocke synchronization
`soullocke.vercel.app` support was added recently and has shown a regression where synchronization reports failure. Relevant areas are URL recognition/client selection, authentication/session behavior, API endpoint assumptions, JSON compatibility and error handling.

### Battle-end detection
Battle starts/opponents can be detected, while battle completion historically risked remaining stale. A correct model should have explicit evidence for entering and leaving battle and clear opponent state when leaving.

### External schemas
Tracker websites may change independently of SoulBuddy. Keep parsers defensive and diagnostics useful enough to distinguish a SoulBuddy bug from a remote API/schema change.