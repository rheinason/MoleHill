# Repository Guidelines

## Project Structure & Module Organization
`MoleHill.sln` is the solution entry point. Active source projects live under `src/`:
- `src/TriangleNet/`: vendored triangulation engine and meshing internals.
- `src/MoleHill.Core/`: reusable terrain logic in `Engine/`, `Processing/`, `Grading/`, `Analysis/`, and `Scattering/`.
- `src/MoleHill.Grasshopper/`: Grasshopper plugin code in `Components/`, `Utilities/`, `Resources/`, and plugin metadata in `MoleHillInfo.cs`.
- `src/MoleHill.Rhino/`: Rhino plugin code in `Commands/`, `UI/`, `Services/`, `Model/`, `Resources/`, and `EmbeddedResources/`.

Automated tests live in `tests/MoleHill.Core.Tests/` and `tests/MoleHill.Grasshopper.Tests/`. Utility scripts remain at repo root, including `generate-icons.ps1`, `generate-toolbar-icons.ps1`, `generate-new-icons.ps1`, `build-yak-package.ps1`, and `generate-file-index.ps1`.

**Start here for navigation:** `docs/architecture.md` (the high-level map — pipeline, grading tier
cascade, preview vs bake), `docs/file-index.md` (path → one-line summary for every source file;
regenerate with `generate-file-index.ps1`), and the `README.md` in each source folder. Completed plan
docs are archived under `docs/archive/`.

**Keep the docs current:** any architectural change (new/removed/renamed source file, new component or
service, changed pipeline/data flow, or a shifted convention) must update the docs in the same change —
regenerate `docs/file-index.md` with `generate-file-index.ps1`, revise `docs/architecture.md` and the
relevant folder `README.md`, and update `CLAUDE.md`/`AGENTS.md` when conventions move.

**Load-bearing conventions** an agent must know:
- `MoleHill.Core` is pure and unit-tested; `MoleHill.Rhino`/`MoleHill.Grasshopper` are thin hosts. Put
  reusable math in Core (with a test), Rhino/GH API calls in the host projects.
- Large classes are decomposed into `partial class` files (`TerrainBuildService.*.cs`,
  `MoleHillPanel.*.cs`). Moving static methods between partials is byte-identical: compile-clean ⟹
  behavior-identical.
- The `.gha`/`.dll`/`.rhp` is locked while Rhino is open, so the build's copy step fails (MSB3021/
  MSB3027) even after a clean compile — judge a build by `error CS`, not the copy error.
- Grading is always watertight 2.5D (no holes/spikes); see the tier cascade in `docs/architecture.md`.

## Build, Test, and Development Commands
- `dotnet restore MoleHill.sln`: restore NuGet dependencies.
- `dotnet build MoleHill.sln`: build all projects in Debug.
- `dotnet build MoleHill.sln -c Release`: produce Release artifacts.
- `dotnet test MoleHill.sln`: run the full test suite.
- `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj`: run core geometry/grading tests only.
- `dotnet test tests/MoleHill.Grasshopper.Tests/MoleHill.Grasshopper.Tests.csproj`: run Grasshopper smoke tests only.
- `dotnet clean MoleHill.sln`: useful before rebuilding if Rhino or Grasshopper is holding a plugin file lock.
- `pwsh ./generate-icons.ps1`: regenerate 24x24 Grasshopper component icons.
- `pwsh ./generate-toolbar-icons.ps1`: regenerate the Rhino toolbar button bitmaps inside
  `src/MoleHill.Rhino/Toolbars/MoleHill.Toolbar.rui`. Prefers the committed hand-drawn PNGs in
  `src/MoleHill.Rhino/Toolbars/icons/`, and fails if a tile has neither an asset nor a `$designs` entry.
- `python tools/render-toolbar-artboards.py`: re-render those PNGs from the Illustrator vector source
  (`Python Commands Source/Master.ai`). Needs PyMuPDF; run only when an artboard changes, since its
  output is committed.
- `pwsh ./build-yak-package.ps1`: build the combined Rhino + Grasshopper Yak package in `.artifacts/yak/`.
- `pwsh ./build-yak-package.ps1 -Push`: build and publish the Yak package to the configured server.

Close Rhino before rebuilding when possible; the Grasshopper build copies `MoleHill.gha` to `%AppData%\Grasshopper\Libraries\`, and Rhino can keep that file locked. Grasshopper builds a merged plugin at `src/MoleHill.Grasshopper/bin/Debug/net7.0/MoleHill.gha`, and the Rhino plugin output is `src/MoleHill.Rhino/bin/Debug/net7.0/MoleHill.Rhino.rhp`.

## Rhino Live Testing

Follow [`docs/rhino-live-testing.md`](docs/rhino-live-testing.md) for native UI testing. The short
version: build with Rhino closed, `spawn_slot` a disposable Rhino through the `rhino-mcp` router, load
the exact `.rhp` with `Rhino.PlugIns.PlugIn.LoadPlugIn` and confirm it was not blocked, build the
scene and the selection from `run_csharp`, invoke the command with `run_command`, and assert on
document state via `run_csharp`/`list_objects`/`get_context`. `close_slot` the exact slot when done.

Screenshots are supporting evidence only: `get_viewport_image` shows the viewport, and Eto forms must
be captured from real `GetWindowRect` bounds filtered by the slot PID. Never `SendKeys` a workflow,
never use a COM-created hidden Rhino server or blind `/runscript`, and never terminate a broad set of
Rhino processes. If a capture is blank or bounds are unavailable, report it as unverified and request
manual confirmation rather than claiming either success or a bug.

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

- **Edge-key hashing**: a `Dictionary`/`HashSet` keyed by a packed edge key (`(min << 32) | max`) MUST be constructed with `IndexedMeshTools.EdgeKeyComparer.Instance`. The default `long` hash is `lo ^ hi`, which for adjacent mesh indices collapses nearly every edge into a handful of buckets and turns an O(n) pass into a quadratic scan — it cost 7 s of a 10 s remesh on a 180k-face terrain. The same applies to per-vertex adjacency: prefer the flat CSR `MeshVertexAdjacency` over a dictionary of `List`/`HashSet` in any loop that rebuilds it per round.

- **Zone/area splitting:** construct face geometry on demand; do not retain an object for every
  terrain face. Index boundary intersections and map segments with face-owned parallel scratch,
  sorting candidates to preserve accumulation order. Clamp spatial queries to index extents.
  Classify through indexed rays with the original polygon predicate. Preserve insertion-order
  vertex lookup ties when compacting storage. Cut slots and output arrays still use linear memory;
  watertightness tests must reject single-use interior edges, not only edges used more than twice.

- **Analyses and annotations are separate content families.** An analysis *evaluates* the terrain (slope, elevation, cut/fill, earthworks, waterflow — the result is a measurement); an annotation *describes* it (contours, spot labels, callouts, sections — the result is drawing). They are peers, like modifiers/markers/objects: separate definition root, registry, type descriptor, JSON family, and collection on `TerrainDefinition`. Never add a member to one that only the other needs. The parameter vocabulary and the schema row builder are deliberately **not** separate: every family declares its card rows as `ParameterDescriptor<TDefinition>` and renders them through one generic `BuildSchemaRow`. The type parameter is what keeps the families apart — an analysis accessor cannot be handed an annotation — so they cannot be mixed and cannot drift. Annotations have **no** terrain-level visibility flag — an annotation is the drawing, so the per-card `IsEnabled` checkbox is the only control; `ShowAnalysisOutputs` governs analyses alone and must never gate annotation output. `ITerrainContentItem` is identity-only scaffolding, not a shared base. See `docs/architecture.md` → "Analysis vs annotation".
- **Slope input is a unit, not a number.** Slope is stored as an angle in degrees on the grading
  definitions, but never shown or typed that way by assumption: every slope field displays in the user's
  chosen unit (`SlopeUnitPreference`, a per-user preference in plug-in settings — not document state) and
  accepts any unit typed into it (`25%`, `150prom`, `14deg`, `1:3`, `1v:3h` — every unit has an ASCII
  spelling because `‰` and `°` are unreachable from a keyboard) via
  `MoleHill.Core.Analysis.SlopeInput`, the single parse/format point. `a:b` is read vertical:horizontal,
  so `1:3` is the flat batter — the same reading `OffsetVerticalMode.Ratio` has always used. Declare a
  slope row with the `Slope` / `OptionalSlope` / `SlopeSlider` parameter factories, never a plain
  `Number`; `ParameterSchemaGuardTests` fails the build otherwise — but it only sees *declared* rows, so
  a card needing unusual layout should declare the row and position it via
  `IsBespokePositionedModifierParameter` (as the "Peel Border" group does) rather than hand-write it. Every other numeric row still declares
  a `ParameterUnit` (`ModelLength`, `Degrees`, `Percent`, `None`) so no card shows a bare unlabelled
  number. `ParameterUnit.Degrees` means a **true angle** — a dihedral crease, a rotation — and is never
  converted; a fold between two faces has no rise over run. Slope-taking commands share one
  `SlopeCommandOption` (value + `Units` list) so every prompt reads alike. See
  `docs/architecture.md` → "Slope units".
- **Output layer routing goes through `LayerRole`**: never hardcode or plumb a layer path for generated output, and never append a suffix to build one. `GeneratedRhinoObject.Role` is `required` and `LayerRoleTable.Path` is never null, so every producer names a destination and every destination resolves — that is what stops output baking onto Rhino's current layer. Appearance (colour, print width, linetype, annotation style, hatch) comes from the same role, so preview and bake cannot drift apart. See `docs/architecture.md` → "Output layer roles".

Keep nullable annotations intentional: `MoleHill.*` projects have nullable enabled, while `TriangleNet` does not. Keep reusable computation in `MoleHill.Core`, Grasshopper-specific component and conversion code in `MoleHill.Grasshopper`, and Rhino command/panel/document workflows in `MoleHill.Rhino`.

## Testing Guidelines
Use xUnit test projects under `tests/` (`MoleHill.Core.Tests`, `MoleHill.Grasshopper.Tests`, `MoleHill.Rhino.Tests`). Name files `<ClassName>Tests.cs` and test methods `MethodName_Scenario_ExpectedResult`. The Rhino tests link `Model/`, `Registry/` and most of `Services/` as source; tests needing Rhino's native runtime use `[RhinoNativeFact]` and skip where it is unavailable, so a green run there does not mean they executed. Prioritize geometry edge cases (collinearity, duplicate points, breakline intersections, tolerance boundaries), grading and slope regressions, and smoke tests for Grasshopper component behavior.

## Commit & Pull Request Guidelines
Use short, imperative commit subjects consistent with recent history (examples: `Add Rhino plugin and retaining wall workflows`, `Preserve remesh edges and planar smoothing`). Keep commits focused and logically grouped.

For PRs, include:
- a concise summary of behavior changes,
- linked issue(s),
- validation steps with the exact build/test commands used,
- screenshots for Grasshopper UI/icon changes and Rhino panel or command workflow changes.
