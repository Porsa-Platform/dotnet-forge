# dotnet-forge

`dotnet-forge` is a .NET 10 port of [swarm-forge](https://github.com/Porsa-Platform/swarm-forge) for .NET-oriented projects.

It keeps the upstream tmux + worktree + handoff model, but replaces branch-specific packs with a single runtime-toggle configuration model.

## Highlights

- .NET 10 + C# implementation
- `.slnx` solution layout
- Reusable core library (`DotnetForge.Core`) so the console front end can be replaced by a UI
- Console front end (`DotnetForge.Cli`) built with `ConsoleAppFramework`
- Rich terminal output via `Spectre.Console`
- Runtime pack toggles for `two-pack`, `four-pack`, and `six-pack`
- Configurable coding-agent backend, including `codex`, `claude`, and `opencode` (also supports `copilot` and `grok`)
- JSON configuration persistence in `swarmforge/dotnet-forge.json`
- Embedded upstream-derived constitution and role prompt assets
- Handoff queue/next/done flows and a background handoff daemon

## Solution structure

```text
DotnetForge.slnx
src/
  DotnetForge.Core/
  DotnetForge.Cli/
tests/
  DotnetForge.Core.Tests/
```

## Architecture

### Core library

`DotnetForge.Core` owns the reusable application logic:

- pack abstractions and registry
- configuration loading/saving
- pack materialization from embedded assets
- workspace preparation and orchestration
- tmux launch command construction
- handoff parsing, queuing, delivery, and completion semantics

### CLI

`DotnetForge.Cli` is a thin shell over the core library. It handles command routing and terminal presentation only.

## Runtime configuration model

Unlike upstream `swarm-forge`, packs are not selected by switching branches.

Instead, one codebase contains all pack variants and the active/default pack is controlled by `swarmforge/dotnet-forge.json`.

Example config:

```json
{
  "schemaVersion": "1.0",
  "enabledPacks": ["four-pack", "two-pack", "six-pack"],
  "defaultPack": "four-pack",
  "agentBackend": "codex",
  "terminalMode": "none",
  "preventSleep": false,
  "agentStartDelayMs": 1500
}
```

## Packs

### two-pack

Fast implementation loop.

Flow:

```text
coder -> cleaner -> coder
```

### four-pack

Compact specification-oriented flow.

Flow:

```text
specifier -> coder -> refactorer -> architect -> specifier
```

### six-pack

Full quality-gate flow.

Flow:

```text
specifier -> coder -> cleaner -> architect -> hardener -> QA
```

## Commands

### List packs

```bash
dotnet run --project src/DotnetForge.Cli -- packs list
```

### Enable a pack

```bash
dotnet run --project src/DotnetForge.Cli -- packs enable --pack two-pack
```

### Disable a pack

```bash
dotnet run --project src/DotnetForge.Cli -- packs disable --pack six-pack
```

### Show the effective project configuration

```bash
dotnet run --project src/DotnetForge.Cli -- config show
```

### Run with the configured default pack

```bash
dotnet run --project src/DotnetForge.Cli -- run
```

### Run with an explicit pack

```bash
dotnet run --project src/DotnetForge.Cli -- run --pack two-pack
```

### Run with a specific agent backend

```bash
dotnet run --project src/DotnetForge.Cli -- run --agent opencode
```

Other useful backends:

```bash
dotnet run --project src/DotnetForge.Cli -- run --agent codex
dotnet run --project src/DotnetForge.Cli -- run --agent claude
```

### Preview generated orchestration without launching

```bash
dotnet run --project src/DotnetForge.Cli -- run --dry-run --no-attach
```

### Stop a running swarm

```bash
dotnet run --project src/DotnetForge.Cli -- stop
```

A convenience `close-swarm` script is also generated in the target project root.

## Handoff commands

`dotnet-forge` also exposes helper flows that mirror the upstream agent interaction pattern.

### Queue a handoff

```bash
dotnet run --project src/DotnetForge.Cli -- handoff queue --draft-file ./draft.handoff --role coder
```

### Accept the next handoff

```bash
dotnet run --project src/DotnetForge.Cli -- handoff next --role cleaner
```

### Complete the current handoff

```bash
dotnet run --project src/DotnetForge.Cli -- handoff done --role cleaner
```

When `run` materializes a pack, wrapper scripts are generated under `swarmforge/scripts/`:

- `swarm_handoff.sh`
- `ready_for_next.sh`
- `done_with_current.sh`

That preserves the familiar upstream agent UX while delegating execution to the .NET tool.

## What `run` materializes

For the selected pack, `dotnet-forge run` writes or refreshes:

- `swarmforge/swarmforge.conf`
- `swarmforge/constitution.prompt`
- `swarmforge/constitution/articles/*`
- `swarmforge/roles/*.prompt`
- `swarmforge/handoff-protocol.md`
- `swarmforge/scripts/*.sh`
- `.swarmforge/roles.tsv`
- `.swarmforge/sessions.tsv`
- per-role handoff inbox/outbox state

## Upstream parity

This port preserves these upstream semantics where practical:

- tmux-backed agent sessions
- one worktree per non-master role
- pack-defined role topology
- file-based handoff workflow
- task and batch receive modes
- upstream pack prompt content and constitution layering

## Intentional deviations

- Branch selection is replaced with runtime configuration toggles.
- The main runtime is a reusable .NET library plus CLI, not shell + Babashka scripts.
- Terminal automation is intentionally simplified: the tool focuses on tmux orchestration and current-shell attachment rather than upstream multi-terminal adapter automation.
- Generated wrapper scripts call back into `dotnet-forge` instead of duplicating separate script implementations.

## Migration from branch-based packs

| Upstream branch | `dotnet-forge` equivalent |
|---|---|
| `two-pack` | `dotnet-forge packs enable --pack two-pack` + `dotnet-forge run --pack two-pack` |
| `four-pack` | `dotnet-forge packs enable --pack four-pack` + `dotnet-forge run --pack four-pack` |
| `six-pack` | `dotnet-forge packs enable --pack six-pack` + `dotnet-forge run --pack six-pack` |

Recommended migration path:

1. keep one branch in your project
2. enable the pack variants you want available
3. set the default pack in `swarmforge/dotnet-forge.json`
4. use `run --pack <name>` for one-off overrides

## Building and testing

```bash
dotnet build DotnetForge.slnx
dotnet test DotnetForge.slnx --no-build
```

## Current test coverage

The included tests cover:

- pack registry and embedded asset availability
- configuration enable/disable/default behavior
- pack materialization
- handoff ready/done/queue core flows
- dry-run orchestration planning
