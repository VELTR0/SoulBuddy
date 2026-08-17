# Codex Handoff

## Starting point
Open the `VELTR0/SoulBuddy` repository in Codex and let Codex read `AGENTS.md` before working on a task.

The repository already contains project-specific instructions in `AGENTS.md`, an architectural overview in `docs/ARCHITECTURE.md`, and a debugging playbook in `docs/DEBUGGING.md`.

## Recommended first Codex task
Use this as the first prompt after opening the repository:

```text
Read AGENTS.md, docs/ARCHITECTURE.md and docs/DEBUGGING.md first.

Then inspect the current repository and orient yourself around the complete data flow from DeSmuME Lua collectors to SoulBuddy runtime state and tracker synchronization.

The immediate issue to investigate is the soullocke.vercel.app tracker integration. It initially worked, but SoulBuddy now reports "Server Synchronisation unsuccessful". Diagnostic logging was recently added to make this reproducible.

Do not make a speculative patch immediately. First:
1. inspect TrackerLinkParser, TrackerClientFactory, VercelSoullockeClient, VercelTrackerClient, SoullockeClient, SyncService and DiagnosticLog;
2. trace how a soullocke.vercel.app URL is classified and which client is selected;
3. identify every HTTP/authentication/API step involved in initial synchronization;
4. inspect the existing diagnostics and improve them only if they cannot reveal the failing stage;
5. compare the Vercel path against the working soullocke.com path without regressing it;
6. run dotnet restore and dotnet build SoulBuddy.csproj after changes;
7. add a regression test if the failing logic can be isolated, otherwise provide an exact manual reproduction/verification procedure.

Never log passwords, authentication tokens or session secrets.

At the end, report:
- root cause;
- files changed and why;
- build/test results;
- exact manual steps I should perform in DeSmuME/SoulBuddy to verify the fix;
- which diagnostic log lines I should send you if it still fails.
```

## Follow-up prompt for battle-state work
After the synchronization issue is resolved, this is a good task prompt:

```text
Read AGENTS.md and docs/DEBUGGING.md.

Investigate battle-end detection in SoulBuddy. Historically SoulBuddy correctly recognized a new battle and the opponent, but after fleeing or ending a battle the Live Activity could remain stuck in battle with the old opponent until another battle started.

Trace the state from collectors/desmume-gen4/live_state.lua through the C# runtime and UI. Do not treat a stale opponent Pokémon value as proof that battle is still active. Determine the actual battle-exit signal for the supported HeartGold/SoulSilver flow, clear stale opponent state on exit, and avoid breaking battle-start detection.

Verify at least:
- fresh overworld -> no battle;
- wild battle -> active + opponent;
- flee -> inactive + opponent cleared;
- new battle -> new opponent;
- trainer battle exit where practical.

Run dotnet build and report both automated and manual verification.
```

## How to give Codex tasks in this repo
Prefer outcome-oriented tasks with observable reproduction steps. A strong request includes:
- the visible symptom;
- expected behavior;
- exact reproduction path when known;
- relevant logs or screenshots;
- permission to inspect related callers/consumers rather than editing only one named file;
- explicit request to build/test before finishing.

Avoid prompts like `fix sync` without context. Codex can navigate the repository, but clear reproduction criteria make regressions much less likely.

## Logs to provide Codex
For runtime-only bugs, paste the smallest log slice covering:
1. app/collector startup;
2. the user action that triggers the problem;
3. the first error/failure;
4. one subsequent retry or state transition.

Redact credentials before pasting logs.

## Branch strategy
Use a dedicated branch per task, for example:
- `fix/vercel-soullocke-sync`
- `fix/battle-exit-detection`
- `feat/menu-state-detection`

Keep `main` releasable. Ask Codex to summarize the diff and build status before merging.

## Definition of a useful Codex result
Do not accept a task as complete only because the UI message disappeared. For integration bugs, require an identified root cause and evidence that the underlying request/state transition is correct. For emulator-state bugs, require observed transition evidence across both entry and exit states.