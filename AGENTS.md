# Repository Guidelines

## Project Structure & Module Organization
`MoleHill.sln` is the solution entry point. Active source projects live under `src/`:
- `src/TriangleNet/`: vendored triangulation engine and meshing internals.
- `src/MoleHill.Core/`: reusable terrain logic in `Engine/`, `Processing/`, `Grading/`, and `Analysis/`.
- `src/MoleHill.Grasshopper/`: Grasshopper plugin code in `Components/`, `Utilities/`, `Resources/`, and plugin metadata in `MoleHillInfo.cs`.
- `src/MoleHill.Rhino/`: Rhino plugin code in `Commands/`, `UI/`, `Services/`, `Model/`, `Resources/`, and `EmbeddedResources/`.

Automated tests live in `tests/MoleHill.Core.Tests/` and `tests/MoleHill.Grasshopper.Tests/`. `tests/TopoTIN.Tests/` is a legacy empty placeholder. Additional docs live in `docs/` (currently `docs/retainingWall.md`). Utility scripts remain at repo root, including `generate-icons.ps1`, `generate-new-icons.ps1`, and `build-yak-package.ps1`.

## Build, Test, and Development Commands
- `dotnet restore MoleHill.sln`: restore NuGet dependencies.
- `dotnet build MoleHill.sln`: build all projects in Debug.
- `dotnet build MoleHill.sln -c Release`: produce Release artifacts.
- `dotnet test MoleHill.sln`: run the full test suite.
- `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj`: run core geometry/grading tests only.
- `dotnet test tests/MoleHill.Grasshopper.Tests/MoleHill.Grasshopper.Tests.csproj`: run Grasshopper smoke tests only.
- `dotnet clean MoleHill.sln`: useful before rebuilding if Rhino or Grasshopper is holding a plugin file lock.
- `pwsh ./generate-icons.ps1`: regenerate 24x24 Grasshopper component icons.
- `pwsh ./build-yak-package.ps1`: build the combined Rhino + Grasshopper Yak package in `.artifacts/yak/`.
- `pwsh ./build-yak-package.ps1 -Push`: build and publish the Yak package to the configured server.

Close Rhino before rebuilding when possible; the Grasshopper build copies `MoleHill.gha` to `%AppData%\Grasshopper\Libraries\`, and Rhino can keep that file locked. Grasshopper builds a merged plugin at `src/MoleHill.Grasshopper/bin/Debug/net7.0/MoleHill.gha`, and the Rhino plugin output is `src/MoleHill.Rhino/bin/Debug/net7.0/MoleHill.Rhino.rhp`.

## Yak Release Notes
- Use `build-yak-package.ps1` for Yak packages instead of relying on the Grasshopper project's local Yak target. The script stages a combined package under `.artifacts/yak/MoleHill-<version>/` and includes both `MoleHill.Rhino.rhp` and `MoleHill.gha` under `net7.0/`.
- Source the release version from `Directory.Build.props` (`MoleHillVersion`). Keep prerelease tags short, for example `0.5.0-beta` instead of `0.5.0-beta.1`, because longer version strings make the Package Manager listing wrap awkwardly inside Rhino.
- Keep the Yak package id as `MoleHill`. Yak will warn that the Rhino content name `MoleHill.Rhino` does not match the package id; this is acceptable for the current combined package.
- Do not rename the Rhino assembly to `MoleHill` unless the package layout changes too. The Rhino and Grasshopper builds would then collide on `MoleHill.deps.json` and `MoleHill.runtimeconfig.json` inside the same Yak package.

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
