# SoulBuddy Debugging Playbook

## Goal
Use this document before making speculative fixes. SoulBuddy crosses several failure boundaries: DeSmuME memory, Lua collection, local C# runtime state, external HTTP APIs, Avalonia UI state, and LAN streaming.

## General debugging order
1. Reproduce the issue once with diagnostics enabled.
2. Identify the first layer where reality diverges from expected state.
3. Add targeted diagnostics there if the evidence is insufficient.
4. Reproduce again.
5. Fix the earliest incorrect layer rather than masking the symptom downstream.
6. Build and run the smallest useful regression/manual verification.

## Diagnostics
The existing diagnostic utility is `Services/DiagnosticLog.cs`.

Good diagnostic entries answer:
- What operation was attempted?
- Which subsystem/client handled it?
- Which host/endpoint was involved?
- What status/result was returned?
- What exception type/message occurred?
- Which non-secret identifiers are needed to correlate the request?

Never log passwords, auth tokens, cookies/session secrets, or credentials embedded in URLs.

## Current issue: `soullocke.vercel.app` synchronization
### Symptom
SoulBuddy can show `Server Synchronisation unsuccessful` after configuring a Vercel-hosted Soullocke tracker. The integration reportedly worked earlier, so treat this as a regression or changed remote contract until evidence says otherwise.

### Files to inspect first
- `Services/TrackerLinkParser.cs`
- `Services/TrackerClientFactory.cs`
- `Services/VercelSoullockeClient.cs`
- `Services/VercelTrackerClient.cs`
- `Services/SoullockeClient.cs`
- `Services/SyncService.cs`
- `Services/FirebaseJsonCompatibility.cs`
- `Services/DiagnosticLog.cs`

### Trace checklist
Check in this order:
1. The pasted URL is parsed as the intended tracker type.
2. `TrackerClientFactory` selects the intended implementation.
3. Any run/session/user identifier extracted from the URL is correct.
4. Authentication/session bootstrap reaches the expected endpoint.
5. HTTP method, path, query parameters and body match the current remote contract.
6. HTTP status code is logged before response parsing.
7. A short sanitized response body is available for non-success responses.
8. JSON parsing handles missing/null/changed fields defensively.
9. `SyncService` distinguishes initialization failure, fetch failure, update failure and transient connectivity failure where possible.
10. A later successful sync clears stale failure UI/state.

### Compare against working tracker behavior
Do not fix Vercel support by breaking `soullocke.com`. Compare the working implementation and reuse shared semantics through `ITrackerClient` where they are actually common.

### Useful log sequence
A healthy trace should make the flow understandable without a debugger, for example conceptually:

```text
[Tracker] parsed host=soullocke.vercel.app type=VercelSoullocke
[Tracker] client=VercelSoullockeClient
[Tracker] session initialization started run=<sanitized-id>
[Tracker] request operation=... endpoint=/...
[Tracker] response operation=... status=200
[Sync] initial synchronization completed
```

On failure, record the precise failed operation/status/exception rather than only `synchronization failed`.

## Battle-state debugging
### Historical symptom
SoulBuddy detects a new battle and opponent but can remain in battle after fleeing/ending it. Starting another battle updates the opponent, implying battle entry is observed while exit/reset can be missed.

### Files to inspect first
- `collectors/desmume-gen4/live_state.lua`
- `collectors/desmume-gen4/live.lua`
- `collectors/desmume-gen4/pokemon.lua`
- `collectors/desmume-gen4/pokemon_memory_map_gen_4_gen_5.lua`
- `Services/SoulBuddyRuntime.cs`
- relevant model/view-model consumers of live activity state

### Key rule
Do not equate `opponent memory still contains a valid Pokémon` with `battle is active`. Emulator/game memory may retain the previous opponent after the battle scene ends.

### Reproduction matrix
Run each from a known overworld state:
- overworld -> wild battle -> flee -> overworld
- overworld -> wild battle -> defeat/catch -> overworld
- overworld -> trainer battle -> victory -> overworld
- battle A -> overworld -> battle B

Record the raw battle indicator(s), opponent species/value, location/map state, and emitted SoulBuddy state across the transition.

### Correct outcome
When battle exit is observed, downstream state should explicitly clear battle-active state and stale opponent data. Do not wait for a future battle to overwrite it.

## DeSmuME / Lua issues
When changing Lua collector behavior:
- verify the supported game/version first;
- identify the exact memory address/flag and its meaning;
- sample values during transitions rather than at one static frame;
- guard invalid/uninitialized reads;
- avoid editing third-party/vendor helper code unless necessary;
- keep C# tolerant of temporarily incomplete collector frames.

## Tracker schema/API issues
External tracker websites can change without a SoulBuddy commit. If requests suddenly fail:
- log status code and sanitized body;
- compare current remote request/response shape with assumptions in the client;
- distinguish DNS/TLS/connectivity errors from 4xx auth/contract errors and 5xx remote errors;
- do not endlessly retry deterministic 4xx failures;
- keep parsing defensive.

## Build verification
Run:

```bash
dotnet restore
dotnet build SoulBuddy.csproj
```

If a fix cannot be automatically tested because it requires DeSmuME or a real tracker session, document the exact manual steps and expected logs/state transitions in the final task summary.