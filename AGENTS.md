# Repository Guidelines

## Project Structure & Module Organization
`MoleHill.sln` is the solution entry point. Core code lives in `src/`:
- `src/TriangleNet/`: triangulation engine and geometry/meshing internals.
- `src/MoleHill.Core/`: terrain logic (`Engine/`, `Grading/`, `Analysis/`, `Processing/`).
- `src/MoleHill.Grasshopper/`: Grasshopper components, plugin metadata, and `Resources/*.png` icons.

Tests are expected under `tests/` (currently `tests/TopoTIN.Tests/` exists as a placeholder). Utility scripts live at repo root (`generate-icons.ps1`, `generate-new-icons.ps1`).

## Build, Test, and Development Commands
- `dotnet restore MoleHill.sln`: restore NuGet dependencies.
- `dotnet build MoleHill.sln`: build all projects in Debug.
- `dotnet build MoleHill.sln -c Release`: produce Release artifacts.
- `dotnet test`: run automated tests (once test projects are added).
- `pwsh ./generate-icons.ps1`: regenerate component icon assets.

Primary plugin output is `src/MoleHill.Grasshopper/bin/Debug/net7.0/MoleHill.gha`. The Grasshopper project also copies output to `%AppData%\Grasshopper\Libraries\`.

## Coding Style & Naming Conventions
Use C# with 4-space indentation, file-scoped namespaces, and one type per file. Follow existing naming:
- `PascalCase` for types, methods, properties.
- `camelCase` for locals/parameters.
- `_camelCase` for private fields.

Keep nullable annotations intentional (`MoleHill.*` projects have nullable enabled). Place Rhino/Grasshopper API code in `MoleHill.Grasshopper`; keep reusable computation in `MoleHill.Core`.

## Testing Guidelines
Add tests under `tests/` using a .NET test project. Name files `<ClassName>Tests.cs` and methods like `MethodName_Scenario_ExpectedResult`. Prioritize geometry edge cases (collinearity, duplicate points, breakline intersections, tolerance boundaries) and grading/slope regression checks.

## Commit & Pull Request Guidelines
Use short, imperative commit subjects consistent with history (examples: `Rename TopoTIN -> MoleHill`, `Add README and MIT license`). Keep commits focused and logically grouped.

For PRs, include:
- concise summary of behavior changes,
- linked issue(s),
- validation steps (build/test commands),
- screenshots for Grasshopper UI/icon/component output changes.
