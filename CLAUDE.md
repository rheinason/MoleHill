# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

**Navigation:** read `docs/architecture.md` first (high-level map — pipeline, grading tier cascade,
preview vs bake), then `docs/file-index.md` (path → one-line summary for every source file; regenerate
with `generate-file-index.ps1`) and the `README.md` in each source folder. `AGENTS.md` has the full
build/test/convention reference. For native Rhino UI work, also follow
`docs/rhino-live-testing.md`.

**Keep the docs current:** for any architectural change (new/removed/renamed source file, new component
or service, changed pipeline/data flow, or a shifted convention), update the docs in the same change:
regenerate `docs/file-index.md` via `generate-file-index.ps1`, revise `docs/architecture.md` and the
relevant folder `README.md`, and refresh these entry docs (`CLAUDE.md`/`AGENTS.md`) when conventions move.

## Build & Test

```bash
# PATH required on this machine
export PATH="$HOME/.dotnet:$PATH"

dotnet build MoleHill.sln           # Debug build (default)
dotnet build MoleHill.sln -c Release
dotnet test                          # All test projects
dotnet test --filter "ClassName"     # Run a single test class
dotnet clean                         # Use before retry if ILRepack locks .gha
pwsh ./generate-icons.ps1           # Regenerate 24x24 PNG icon assets
pwsh ./generate-toolbar-icons.ps1   # Regenerate the Rhino toolbar button bitmaps in the .rui
python tools/render-toolbar-artboards.py   # Re-render hand-drawn icons from Master.ai (rare)

pwsh ./validate.ps1 managed         # One validation lane; also native | perf | warnings | package | all
pwsh ./validate.ps1 hosted-perf     # Full-stack timings inside a spawned Rhino vs tests/perf-baselines/
```

Performance claims come from the `hosted-perf` lane: it runs the geometry-heavy, analysis-heavy and
interactive-scale builds inside a real Rhino (isolated Release Core), 5 samples each, and fails on a
per-stage regression over 20% and 25 ms. Re-baseline (`-UpdateBaseline`) in the same commit as a
deliberate speed change.

`dotnet test` alone is not acceptance: native tests skip without Rhino and benchmarks return early
without `MOLEHILL_PERF`. Use `validate.ps1` and read `docs/validation-lanes.md` before quoting a test
result as evidence. Build inputs are declared — `global.json` pins the SDK floor, and
`Directory.Build.props` holds `RhinoInstallDir`/`WindowsDesktopRefPackDir`.

Close Rhino before rebuilding — Rhino holds a lock on the `.gha` file in `%AppData%\Grasshopper\Libraries\`.

For live Rhino testing, drive a disposable `rhino-mcp` slot (`spawn_slot` → `run_csharp`/`run_command`
→ `close_slot`) as described in `docs/rhino-live-testing.md`. Assert on document state, not on
keystrokes: never `SendKeys` a workflow, never terminate a broad set of Rhino processes, and never
use guessed desktop coordinates as evidence — window bounds come from `GetWindowRect` on the slot PID.

After a rebuild, Windows may block the new `.rhp` (Mark of the Web). If Rhino fails to load the plugin, unblock the file: right-click the `.rhp` → Properties → check **Unblock** → OK. The `.rhp` is at `src/MoleHill.Rhino/bin/Debug/net7.0/MoleHill.Rhino.rhp`.

The Grasshopper project builds two output dirs (`bin/Debug/net7.0/` and `bin/Debug/net7.0-windows/`). ILRepack merges `MoleHill.Core.dll` + `TriangleNet.dll` into the primary `MoleHill.gha` and deletes the intermediate DLLs. The Windows TFM also copies the merged `.gha` and `MoleHill.Interop.dll` to `%AppData%\Grasshopper\Libraries\`. It does **not** build a Yak package: `build-yak-package.ps1` is the only packaging path, because it is the only one that stages the `.rhp` beside the `.gha`. See AGENTS.md → Yak Release Notes.

## Project Structure

Four source projects, three test projects:

- **TriangleNet** (`src/TriangleNet/`, net7.0) — Triangle.NET constrained Delaunay library, nullable disabled, included as source and merged into the .gha
- **MoleHill.Core** (`src/MoleHill.Core/`, net7.0) — Pure terrain logic (no Rhino/GH dependency). Sub-namespaces: `Engine/`, `Processing/`, `Grading/`, `Analysis/`, `Scattering/`, `Sculpting/` (each has a `README.md`)
- **MoleHill.Grasshopper** (`src/MoleHill.Grasshopper/`, net7.0-windows + net7.0) — GH components, plugin metadata (`MoleHillInfo.cs`), icon resources, `RhinoConverter.cs`. No terrain logic here.
- **MoleHill.Rhino** (`src/MoleHill.Rhino/`, net7.0) — Native Rhino plugin (.rhp). Dockable panel UI (Eto.Forms in `UI/`), three commands (`MoleHillPanel`, `MoleHillCreateTerrain`, `MoleHillConvertToRhino`), terrain definition model (`Model/`), `TerrainController`/`TerrainBuildService` pipeline (`Services/`). Persists terrain state as JSON in .3dm via WriteDocument/ReadDocument. 16×16 PNG icons in `Resources/` loaded via `PanelIcons.Load()`. Bake and Detach are in the terrain picker `▾` dropdown, not inline buttons.
- **MoleHill.Core.Tests** / **MoleHill.Grasshopper.Tests** / **MoleHill.Rhino.Tests** (`tests/`, xunit 2.9.2) — Test naming: `MethodName_Scenario_ExpectedResult`. The Rhino tests link `Model/`, `Registry/` and most of `Services/` as source; tests needing Rhino's native runtime use `[RhinoNativeFact]` and skip where it is unavailable.

All Rhino/Grasshopper API calls belong in `MoleHill.Grasshopper`. All reusable computation belongs in `MoleHill.Core`.

## Data Flow

```
GH Inputs (points, curves)
  → BreaklineDiscretizer   (polylines → flat vertex array + segment index pairs)
  → PointCloudProcessor    (merge spots + breakline verts; Z-aware dedup)
  → TinEngine.Build()      (XY-hash cache → Z-only shortcut or incremental edit or full rebuild)
  → TriangulationHelper    (5-tier CDT fallback → IMesh)
  → TinResult              (flat XYZ verts, face indices, edges)
  → RhinoConverter         (TinResult → Rhino.Geometry.Mesh / Line[] / Point3d[])
  → GH Outputs
```

Grading components additionally call `PadGrader.Grade()` or `PathGrader`, which re-triangulate ALL vertices (never stitch locally).

## Flat Array Format

All pipeline data uses flat arrays for cache-friendliness:

| Name | Layout | Length |
|------|--------|--------|
| XY coords | `[x0, y0, x1, y1, …]` | 2 × vertexCount |
| XYZ vertices | `[x0, y0, z0, x1, y1, z1, …]` | 3 × vertexCount |
| Z values | `[z0, z1, …]` | vertexCount |
| Faces | `[i0, i1, i2, …]` (all triangles) | 3 × faceCount |
| Edges / Segments | `[a0, b0, a1, b1, …]` | 2 × count |

## TinEngine Caching

`TinEngine` is persistent across GH solves. `InputSnapshot` fingerprints XY topology and Z values separately (XxHash64 via `MemoryMarshal.AsBytes`):

1. **Z-only change** → `TinResult.WithUpdatedZ()` — updates elevations, no re-triangulation
2. **Single point add/remove** → `TryApplyIncrementalEdit()` — lightweight topology change
3. **Full topology change** → `FullRebuild()` via `TriangulationHelper`

## 5-Tier Triangulation Fallback (`TriangulationHelper`)

Used everywhere (TinEngine, PadGrader, RemeshComponent, MeshAreaSplitter):

1. CDT + quality options
2. Non-CDT + quality options
3. CDT, no quality
4. Non-CDT, no quality
5. Plain Delaunay (no constraints)

`ConformingDelaunay=true` fails with tight parallel segments — use `false`.

## Steiner Point Z Interpolation (Priority Order)

When Triangle.NET inserts Steiner points, their Z must be interpolated. Check in order:

1. Input breakline segments (parametric projection)
2. Output mesh edges (parametric)
3. Containing face (barycentric)
4. Iterative propagation (chain of Steiners)
5. Nearest known vertex (last resort)

## Key Conventions

- **Nullable**: enabled in `MoleHill.*` projects; disabled in TriangleNet
- **Namespaces**: file-scoped
- **Naming**: `PascalCase` types/methods/properties, `camelCase` locals/parameters, `_camelCase` private fields
- **RhinoMesh alias**: use `using RhinoMesh = Rhino.Geometry.Mesh;` when `Mesh` is ambiguous with TriangleNet types
- **Normals**: always call `ComputeNormals()` then `UnifyNormals()` on all output Rhino meshes
- **Edge-key hashing**: a `Dictionary`/`HashSet` keyed by a packed edge key (`(min << 32) | max`) MUST be constructed with `IndexedMeshTools.EdgeKeyComparer.Instance`. The default `long` hash is `lo ^ hi`, which for adjacent mesh indices collapses nearly every edge into a handful of buckets and turns an O(n) pass into a quadratic scan — it cost 7 s of a 10 s remesh on a 180k-face terrain. Prefer the factories `IndexedMeshTools.CreateEdgeKeySet` / `CreateEdgeKeyMap`, which attach the comparer while leaving the capacity explicit; a `ulong`-packed key (including the 3x21-bit face keys in `MeshTopologyOperations`) uses `IndexedMeshTools.PackedKeyComparer.Instance`. **Spatial-cell keys collapse too, and worse:** `(cx * 0x100000001) ^ (cy * K)` writes `cx` into both halves, so the default hash cancels it outright, and `(cx << 32) | cy` hashes to `cx ^ cy`. They take `IndexedMeshTools.CellKeyComparer.Instance` (a file outside Core carries its own, as `RetainingWallPlannerCore` does). Until 2026-09-26 cell keys were exempt on the belief that they were "already mixed". They were not: indexing 243k faces took ~1.7 s instead of ~0.1 s, and fixing every site halved Remesh and cut grading constraint resolution by two thirds. `PackedEdgeKeyComparerGuardTests` scans `src/` and fails the build on any default-comparer `long`/`ulong` collection. Its only exemption is a key the caller genuinely pre-mixes, listed with the reason. The same applies to per-vertex adjacency: prefer the flat CSR `MeshVertexAdjacency` over a dictionary of `List`/`HashSet` in any loop that rebuilds it per round.
- **Generated text is sized and aligned by the annotation style — so author it accordingly.** Baking
  stamps the terrain's dimension style onto every generated `TextEntity`, and a dimension style owns
  *both* size and justification: the stamp resets the entity's own values to the style's. Height following
  the style is the design, so never fight it — text drawn at a multiple of the style height previews large
  and bakes at 1×, and preview and bake disagreeing is the one thing this pipeline does not do (express
  hierarchy with spacing and rules instead). Alignment is the opposite case, because a producer *places*
  text according to it — a right-aligned figure sits at its column's right edge — so `AddTextEntity`
  (`TerrainController.Output.cs`) reads the alignment off before the stamp and sets it back after. Found
  live: without it every generated label baked top-left however it previewed.
- **Zone/area splitting:** construct face geometry on demand; do not retain an object for every
  terrain face. Index boundary intersections and map segments with face-owned parallel scratch,
  sorting candidates to preserve accumulation order. Clamp spatial queries to index extents.
  Classify through indexed rays with the original polygon predicate. Preserve insertion-order
  vertex lookup ties when compacting storage. Cut slots and output arrays still use linear memory;
  watertightness tests must reject single-use interior edges, not only edges used more than twice.
- **A terrain-level summary is persisted state, so it must be JSON-representable and it must be cloned
  in full.** `TerrainAnalysisSummary` is saved with the terrain (`LastAnalysisResults`) and served back
  through `TerrainRuntimeCacheCloner.CloneAnalysis`, a hand-written member-by-member copy. Two traps, both
  found live rather than by any test of the analysis itself: a field left out of the cloner reads back as
  **zero on every cached build**, indistinguishable from an analysis that measured nothing; and a field
  defaulting to `double.NaN` or an infinity stops `System.Text.Json` writing the document *at all*, so
  **every** terrain carrying **any** summary fails to save (and because every edit snapshots for undo, it
  surfaces as a failed edit, not a failed save). Use a nullable to mean "not measured" — null is what
  absence looks like in a document — and add the field to `CloneAnalysis`.
  `TerrainRuntimeCacheClonerTests` and `TerrainSummarySerializationTests` fail if you forget either.
- **Mesh counts must come from the same extraction as the mesh arrays.** `TryExtractMeshData` /
  `TryGetMeshData` normalize a *copy* (`ConvertQuadsToTriangles`, `CombineIdentical`, `CullUnused`,
  `CullDegenerateFaces`), so the arrays they return routinely describe a different vertex and face
  count than the Rhino mesh passed in. Pairing them with `mesh.Vertices.Count`/`mesh.Faces.Count`
  reads off the end — an `IndexOutOfRangeException` raised on a worker thread whose stack names no
  caller. Use the counts-returning `TryExtractMeshData` overload (`out vertexCount`, `out faceCount`)
  and pass those. `MeshAreaTopologySplitter.Split` validates the pair and returns a diagnosis rather
  than throwing, but that is a backstop, not a licence. No site in `TerrainBuildService.*` or
  `TerrainAnalysisPreviewBuilder` pairs the old way any more — keep it that way.
- **Z-aware dedup**: breakline-to-breakline vertex merge requires XY AND Z proximity — preserves parallel retaining walls at different elevations
- **PadGrader helpers**: `FindNearVertex`, `InterpolateZ`, `PointInPolygon`, `DistToPolygon` are `public`; inner classes `SpatialHash` and `FaceGrid` are `internal` (accessible within the assembly)
- **Daylight line**: detected as zero-crossing of `newZ - origZ` across edges
- **Icons**: 24×24 PNG embedded resources (GH), 16×16 PNG for Rhino panel tabs and modifier badges. Generated by `generate-icons.ps1` (Blender-style: bold filled shapes, thick outlines, saturated colors). Loaded via `MoleHillInfo.LoadIcon()` (GH) and `PanelIcons.Load()` (Rhino).
- **Rhino toolbar buttons** are a separate icon system with **two sources**, and the hand-drawn one
  wins. `Python Commands Source/Master.ai` is the Illustrator source for the original office toolset —
  25 vector artboards in a flat, high-contrast language (ink `#231F20`, one green accent `#199D49`,
  rare orange `#F15A29`, white fills) that stays legible at 16 px where softer procedural art goes
  muddy. `tools/render-toolbar-artboards.py` renders the mapped artboards to
  `src/MoleHill.Rhino/Toolbars/icons/<command>-<16|24|32>.png`; those PNGs are **committed**, so the
  build needs no Python. Only commands with no artboard are drawn procedurally, and they must be drawn
  in that same language — the palette and `InkPen`/`AccentPen` helpers at the top of the `$designs`
  section exist to keep a drawn icon from looking like it came from a different set.
  `generate-toolbar-icons.ps1` prefers the PNG asset and falls back to the procedural design, then
  packs the result into the `.rui` as three base64 sprite strips (16/24/32 px), one tile per row,
  addressed by a macro's `bitmap_id` — rewriting only the base64 so layout, GUIDs and indices survive.
  **Every tile must resolve to an asset or a design**: the script throws rather than shipping a
  placeholder, because a fallback icon is indistinguishable from a real one.
  Adding a toolbar button means adding a `macro_item`, a `tool_bar_item`, a `bitmap_item` row in all
  three strips, and either an artboard mapping or a `$designs` entry. A command that is a *variant* of
  another (`mhSlopeCurveSection`, `mhImportGeoTiffTerrain`, `mhClearProjectBase`) belongs on its
  sibling's `right_macro_id`, not on its own button. Note that Rhino paints a button from the **left**
  macro only: a right-click macro's own icon is never displayed, and when two macros share one
  `bitmap_id` the generator deliberately keys that tile to the left macro.

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
- **TIN Surface component**: pure CDT only — no quality refinement. Quality lives in the Remesh component.
- **Panel top toolbar**: 2 rows — (1) name/picker/+/⎘/👁/🔒/Rebuild/🗑, (2) Live update + status label. Tolerance, display/transparency and layer settings are not in the toolbar; output layers are named by one "Output Layers" row in the Settings card.
- **Output layer routing goes through `LayerRole`.** Never hardcode or plumb a layer path for generated output, and never append a suffix to build one. `GeneratedRhinoObject.Role` is `required` and `LayerRoleTable.Path` is never null, so every producer names a destination and every destination resolves — that is what stops output baking onto Rhino's current layer. Appearance (colour, print width, linetype, annotation style, hatch) comes from the same role, so preview and bake cannot drift apart. See `docs/architecture.md` → "Output layer roles".
- **Analyses and annotations are separate content families.** An analysis *evaluates* the terrain (slope, elevation, cut/fill, earthworks, waterflow — the result is a measurement); an annotation *describes* it (contours, spot labels, callouts, sections — the result is drawing). They are peers, like modifiers/markers/objects: separate definition root, registry, type descriptor, JSON family, and collection on `TerrainDefinition`. Never add a member to one that only the other needs. The parameter vocabulary and the schema row builder are deliberately **not** separate: every family declares its card rows as `ParameterDescriptor<TDefinition>` and renders them through one generic `BuildSchemaRow`. The type parameter is what keeps the families apart — an analysis accessor cannot be handed an annotation — so they cannot be mixed and cannot drift. Annotations have **no** terrain-level visibility flag — an annotation is the drawing, so the per-card `IsEnabled` checkbox is the only control; `ShowAnalysisOutputs` governs analyses alone and must never gate annotation output. `ITerrainContentItem` is identity-only scaffolding, not a shared base. See `docs/architecture.md` → "Analysis vs annotation".
- **Zone cards**: no GroupBox border — `CreateZoneGroup` returns a plain `Panel`. Same for modifier cards.
- **Source editor buttons**: `Sel` replaces input objects; `Layers` replaces input layers (not additive — tooltips clarify).
- **Zone layer editor**: `Use Current` · `▾` · `Clear` — consistent with all other layer assignment patterns.
- **Modifier name**: editable `TextBox` (collapsed state shows read-only bold label).
- **"Priority by elevation"**: checkbox on zone cards (was "Use input Z").
- **Sculpt modifier**: durable data = a sparse world-XY displacement field (tiles on the definition), never vertex indices — that's what makes it stackable. Brush prefs (radius/strength/falloff) live on `SculptSessionController`, NOT the definition (the stage fingerprint serializes the whole definition and would churn). The session GetPoint loop owns Ctrl+Z (stroke undo) because Rhino blocks its Undo accelerator inside a get.

## Coding Style

4-space indentation, one type per file. Geometry edge cases to prioritize in tests: collinearity, duplicate points, breakline intersections, tolerance boundaries, grading/slope regression.

## Commit Style

Short imperative subjects (e.g. `Add retaining wall component`, `Fix Steiner Z interpolation for parallel breaklines`). Keep commits focused.
