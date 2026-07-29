# dotnet-forge

`dotnet-forge` is a .NET 10 port of [swarm-forge](https://github.com/Porsa-Platform/swarm-forge) for .NET-oriented projects.

It keeps the upstream worktree + handoff model, but replaces branch-specific packs with a single runtime-toggle configuration model and replaces tmux with direct child process management.

## Highlights

- .NET 10 + C# implementation
- `.slnx` solution layout
- Reusable core library (`DotnetForge.Core`) so the console front end can be replaced by a UI
- Console front end (`DotnetForge.Cli`) built with `ConsoleAppFramework`, distributed as a `dotnet tool`
- Rich terminal output via `Spectre.Console`
- Runtime pack toggles for `two-pack`, `four-pack`, and `six-pack`
- Configurable coding-agent backend, including `codex`, `claude`, and `opencode` (also supports `copilot` and `grok`)
- JSON configuration persistence in `dotnet-forge/dotnet-forge.json`
- Embedded upstream-derived constitution and role prompt assets
- Handoff queue/next/done flows and an integrated handoff daemon
- Direct child-process management with stdin manipulation (no tmux required)
- Wrapper scripts generated as `.cs` file-based apps (`.NET 10`)

## Solution structure

```text
DotnetForge.slnx
src/
  DotnetForge.Core/
  DotnetForge.Cli/
tests/
  DotnetForge.Core.Tests/
```

## Installation

Install as a global .NET tool:

```bash
dotnet tool install --global dotnet-forge
```

Or from a local build:

```bash
dotnet pack src/DotnetForge.Cli/DotnetForge.Cli.csproj -o artifacts/packages
dotnet tool install --global --add-source artifacts/packages dotnet-forge
```

## Architecture

### Core library

`DotnetForge.Core` owns the reusable application logic:

- pack abstractions and registry
- configuration loading/saving
- pack materialization from embedded assets
- workspace preparation and orchestration
- direct child-process launch and stdin-based agent notification
- handoff parsing, queuing, delivery, and completion semantics

### CLI

`DotnetForge.Cli` is a thin shell over the core library. It handles command routing and terminal presentation only.

## Runtime configuration model

Unlike upstream `swarm-forge`, packs are not selected by switching branches.

Instead, one codebase contains all pack variants and the active/default pack is controlled by `dotnet-forge/dotnet-forge.json`.

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
dotnet-forge packs list
```

### Enable a pack

```bash
dotnet-forge packs enable --pack two-pack
```

### Disable a pack

```bash
dotnet-forge packs disable --pack six-pack
```

### Show the effective project configuration

```bash
dotnet-forge config show
```

### Run with the configured default pack

```bash
dotnet-forge run
```

### Run with an explicit pack

```bash
dotnet-forge run --pack two-pack
```

### Run with a specific agent backend

```bash
dotnet-forge run --agent opencode
```

Other useful backends:

```bash
dotnet-forge run --agent codex
dotnet-forge run --agent claude
```

### Preview generated orchestration without launching

```bash
dotnet-forge run --dry-run --no-attach
```

### Stop a running forge

```bash
dotnet-forge stop
```

A convenience `close-forge.cs` script is also generated in the target project root.

## Handoff commands

`dotnet-forge` also exposes helper flows that mirror the upstream agent interaction pattern.

### Queue a handoff

```bash
dotnet-forge handoff queue --draft-file ./draft.handoff --role coder
```

### Accept the next handoff

```bash
dotnet-forge handoff next --role cleaner
```

### Complete the current handoff

```bash
dotnet-forge handoff done --role cleaner
```

When `run` materializes a pack, wrapper scripts are generated under `dotnet-forge/scripts/`:

- `handoff_queue.sh`
- `ready_for_next.sh`
- `done_with_current.sh`

These are .NET 10 file-based apps (runnable via `dotnet run <script>.cs`) that delegate to `dotnet-forge`.

## What `run` materializes

For the selected pack, `dotnet-forge run` writes or refreshes:

- `dotnet-forge/dotnet-forge.conf`
- `dotnet-forge/constitution.prompt`
- `dotnet-forge/constitution/articles/*`
- `dotnet-forge/roles/*.prompt`
- `dotnet-forge/handoff-protocol.md`
- `dotnet-forge/scripts/*.cs`
- `.dotnet-forge/roles.tsv`
- `.dotnet-forge/sessions.tsv`
- `.dotnet-forge/pids/*.pid`
- per-role handoff inbox/outbox state

## Process management

`dotnet-forge run` is a blocking process that:

1. Spawns each agent role as a direct child process with stdin redirected
2. Stores each process PID in `.dotnet-forge/pids/{role}.pid`
3. Runs the handoff daemon inline (no separate process required)
4. Monitors for a stop signal (via `dotnet-forge stop` or Ctrl+C)
5. On stop: kills all child agent processes cleanly

To run in the background, use `&` or a process manager of your choice.

## Upstream parity

This port preserves these upstream semantics where practical:

- one worktree per non-master role
- pack-defined role topology
- file-based handoff workflow
- task and batch receive modes
- upstream pack prompt content and constitution layering

## Intentional deviations

- Branch selection is replaced with runtime configuration toggles.
- The main runtime is a reusable .NET library plus CLI, not shell + Babashka scripts.
- tmux is replaced by direct child-process management with stdin/stdout control.
- Generated wrapper scripts are `.cs` file-based apps instead of `.sh` shell scripts.
- The handoff daemon runs inline within `dotnet-forge run` rather than as a separate process.

## Migration from branch-based packs

| Upstream branch | `dotnet-forge` equivalent                                                          |
| --------------- | ---------------------------------------------------------------------------------- |
| `two-pack`      | `dotnet-forge packs enable --pack two-pack` + `dotnet-forge run --pack two-pack`   |
| `four-pack`     | `dotnet-forge packs enable --pack four-pack` + `dotnet-forge run --pack four-pack` |
| `six-pack`      | `dotnet-forge packs enable --pack six-pack` + `dotnet-forge run --pack six-pack`   |

Recommended migration path:

1. keep one branch in your project
2. enable the pack variants you want available
3. set the default pack in `dotnet-forge/dotnet-forge.json`
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
