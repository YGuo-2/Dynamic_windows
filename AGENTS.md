# Repository Guidelines

## Project Structure & Module Organization

This repository contains a Windows Dynamic Island prototype for visualizing Claude Code and Codex agent activity.

- `DynamicIsland.slnx` is the .NET solution file. Use the `.slnx` file, not a generated `.sln`.
- `src/DynamicIsland.App/` contains the WPF application (`net10.0-windows`). Key areas are `Ingest/` for the local HTTP hook receiver, `Model/` for normalized event and UI state records, and `IslandWindow.xaml(.cs)` for the floating window UI.
- `src/DynamicIsland.IngestProbe/` is a small console probe for ingest experiments and is not currently included in the solution.
- `setup/` contains Claude/Codex configuration snippets.
- `DESIGN.md`, `CLAUDE.md`, and `ISSUE.md` document architecture, implementation notes, and open issues.
- `bin/` and `obj/` are generated build outputs and should not be edited.

## Build, Test, and Development Commands

```powershell
dotnet restore .\DynamicIsland.slnx
dotnet build .\DynamicIsland.slnx -c Debug
dotnet run --project .\src\DynamicIsland.App\DynamicIsland.App.csproj
dotnet run --project .\src\DynamicIsland.IngestProbe\DynamicIsland.IngestProbe.csproj
```

If the WPF app is already running, stop it before rebuilding because the executable may be locked:

```powershell
taskkill /F /IM DynamicIsland.App.exe
```

To manually test ingest, start the app and POST hook JSON to `http://127.0.0.1:7777/claude` using `curl.exe --data-binary @file.json`. Received events are appended to `%TEMP%\dynamicisland-events.log`.

## Coding Style & Naming Conventions

Use C# with nullable reference types and implicit usings enabled, matching the project files. Prefer 4-space indentation. Use PascalCase for types, public members, XAML control names, and record properties; use camelCase for locals and parameters. Keep ingest parsing and state data independent of WPF types so it can later move into a core library. Do not change global Claude or Codex configuration without explicit approval; prefer project-level snippets and local files.

## Testing Guidelines

There is no automated test project yet. For now, validation should include `dotnet build`, a manual hook POST, and inspection of `%TEMP%\dynamicisland-events.log`. When adding tests, create a dedicated test project such as `tests/DynamicIsland.App.Tests/` and focus first on event normalization, transcript reading, and state transitions.

## Commit & Pull Request Guidelines

This checkout is not a Git repository, so no commit history is available to infer conventions. Use short, imperative commit subjects such as `Fix transcript tail fallback` or `Add ingest probe docs`. Pull requests should describe the user-visible behavior, list validation commands, link related issues, and include screenshots or short clips for UI changes.
