# Agent Instructions

## Solution

This repository contains the `MediaArchive.sln` .NET 10 solution:

- `src/MediaArchive.CLI` — command-line application.
- `src/MediaArchive.Core` — application and domain logic.
- `tests/MediaArchive.Tests` — unit tests.

The CLI and test projects reference Core. Keep application logic in Core rather than in the CLI entry point.

## Build, Run, and Test

Run these PowerShell scripts from the repository root:

- `.\scripts\build.ps1` builds `MediaArchive.sln` in Debug.
- `.\scripts\test.ps1` builds the solution, then runs all tests in Debug.
- `.\scripts\buildRun.ps1` builds the solution, then forwards remaining arguments to `MediaArchive.CLI`.
- `.\scripts\run.ps1` launches the already-built Debug CLI and forwards remaining arguments; it does not build.

When running `dotnet test` directly, include `--disable-build-servers`; test runs may hang without it in this environment. The repository test script already supplies this option.

## Architecture Contracts

The following files are placeholders for contracts that will be documented as the corresponding behavior is established. Follow them when they contain guidance, and update them with tests when changing an established contract.

| Concern | Contract |
|---|---|
| CLI commands, arguments, options, stdout/stderr | [CLI Interface](context/contracts/cli-interface.md) |
| Application data model and storage formats | [Data Model](context/contracts/data-model.md) |
| Logging, log levels and diagnostic verbosity | [Logging](context/contracts/logging.md) |

## Subdirectory Instructions

Component-specific instructions may be added at these locations:

- [MediaArchive.Core instructions](src/MediaArchive.Core/AGENTS.md)
- [MediaArchive.CLI instructions](src/MediaArchive.CLI/AGENTS.md)
- [MediaArchive.Tests instructions](tests/MediaArchive.Tests/AGENTS.md)

## Working Rules

- Don't use Serena integration.
- Read only the contracts relevant to the task.
- Follow the [solution-wide code organization guidelines](context/guidelines/code-organization.md).
- Prefer existing libraries and project patterns; do not add dependencies or abstractions without a demonstrated need.
- Keep changes focused on the requested task and preserve existing behavior unless a change explicitly requires otherwise.
- Validate code changes with the relevant build and tests. If validation cannot run, report why.
