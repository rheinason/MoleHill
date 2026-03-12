# Repository Guidelines

## Project Structure & Module Organization
`MoleHill.sln` is the solution entry point. Active source projects live under `src/`:
- `src/TriangleNet/`: vendored triangulation engine and meshing internals.
- `src/MoleHill.Core/`: reusable terrain logic in `Engine/`, `Processing/`, `Grading/`, and `Analysis/`.
- `src/MoleHill.Grasshopper/`: Grasshopper plugin code in `Components/`, `Utilities/`, `Resources/`, and plugin metadata in `MoleHillInfo.cs`.
- `src/MoleHill.Rhino/`: Rhino plugin code in `Commands/`, `UI/`, `Services/`, `Model/`, `Resources/`, and `EmbeddedResources/`.

Automated tests live in `tests/MoleHill.Core.Tests/` and `tests/MoleHill.Grasshopper.Tests/`. `tests/TopoTIN.Tests/` is a legacy empty placeholder. Additional docs live in `docs/` (currently `docs/retainingWall.md`). Utility scripts remain at repo root, including `generate-icons.ps1` and `generate-new-icons.ps1`.

## Build, Test, and Development Commands
- `dotnet restore MoleHill.sln`: restore NuGet dependencies.
- `dotnet build MoleHill.sln`: build all projects in Debug.
- `dotnet build MoleHill.sln -c Release`: produce Release artifacts.
- `dotnet test MoleHill.sln`: run the full test suite.
- `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj`: run core geometry/grading tests only.
- `dotnet test tests/MoleHill.Grasshopper.Tests/MoleHill.Grasshopper.Tests.csproj`: run Grasshopper smoke tests only.
- `dotnet clean MoleHill.sln`: useful before rebuilding if Rhino or Grasshopper is holding a plugin file lock.
- `pwsh ./generate-icons.ps1`: regenerate 24x24 Grasshopper component icons.

Close Rhino before rebuilding when possible; the Grasshopper build copies `MoleHill.gha` to `%AppData%\Grasshopper\Libraries\`, and Rhino can keep that file locked. Grasshopper builds a merged plugin at `src/MoleHill.Grasshopper/bin/Debug/net7.0/MoleHill.gha` and also a `net7.0-windows` output that may emit `manifest.yml` and a `.yak` package when Yak is installed. The Rhino plugin output is `src/MoleHill.Rhino/bin/Debug/net7.0/MoleHill.Rhino.rhp`.

## Coding Style & Naming Conventions
Use C# with 4-space indentation, file-scoped namespaces, and one type per file. Follow existing naming:
- `PascalCase` for types, methods, and properties.
- `camelCase` for locals and parameters.
- `_camelCase` for private fields.

Keep nullable annotations intentional: `MoleHill.*` projects have nullable enabled, while `TriangleNet` does not. Keep reusable computation in `MoleHill.Core`, Grasshopper-specific component and conversion code in `MoleHill.Grasshopper`, and Rhino command/panel/document workflows in `MoleHill.Rhino`.

## Testing Guidelines
Use xUnit test projects under `tests/`. Name files `<ClassName>Tests.cs` and test methods `MethodName_Scenario_ExpectedResult`. Prioritize geometry edge cases (collinearity, duplicate points, breakline intersections, tolerance boundaries), grading and slope regressions, and smoke tests for Grasshopper component behavior.

## Commit & Pull Request Guidelines
Use short, imperative commit subjects consistent with recent history (examples: `Add Rhino plugin and retaining wall workflows`, `Preserve remesh edges and planar smoothing`). Keep commits focused and logically grouped.

For PRs, include:
- a concise summary of behavior changes,
- linked issue(s),
- validation steps with the exact build/test commands used,
- screenshots for Grasshopper UI/icon changes and Rhino panel or command workflow changes.
