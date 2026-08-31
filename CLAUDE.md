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
```

Close Rhino before rebuilding — Rhino holds a lock on the `.gha` file in `%AppData%\Grasshopper\Libraries\`.

For live Rhino testing, drive a disposable `rhino-mcp` slot (`spawn_slot` → `run_csharp`/`run_command`
→ `close_slot`) as described in `docs/rhino-live-testing.md`. Assert on document state, not on
keystrokes: never `SendKeys` a workflow, never terminate a broad set of Rhino processes, and never
use guessed desktop coordinates as evidence — window bounds come from `GetWindowRect` on the slot PID.

After a rebuild, Windows may block the new `.rhp` (Mark of the Web). If Rhino fails to load the plugin, unblock the file: right-click the `.rhp` → Properties → check **Unblock** → OK. The `.rhp` is at `src/MoleHill.Rhino/bin/Debug/net7.0/MoleHill.Rhino.rhp`.

The Grasshopper project builds two output dirs (`bin/Debug/net7.0/` and `bin/Debug/net7.0-windows/`). ILRepack merges `MoleHill.Core.dll` + `TriangleNet.dll` into the primary `MoleHill.gha` and deletes the intermediate DLLs. The Windows TFM also copies the merged `.gha` to `%AppData%\Grasshopper\Libraries\` and runs Yak to produce `manifest.yml` + `.yak`.

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
- **Edge-key hashing**: a `Dictionary`/`HashSet` keyed by a packed edge key (`(min << 32) | max`) MUST be constructed with `IndexedMeshTools.EdgeKeyComparer.Instance`. The default `long` hash is `lo ^ hi`, which for adjacent mesh indices collapses nearly every edge into a handful of buckets and turns an O(n) pass into a quadratic scan — it cost 7 s of a 10 s remesh on a 180k-face terrain. The same applies to per-vertex adjacency: prefer the flat CSR `MeshVertexAdjacency` over a dictionary of `List`/`HashSet` in any loop that rebuilds it per round.
- **Z-aware dedup**: breakline-to-breakline vertex merge requires XY AND Z proximity — preserves parallel retaining walls at different elevations
- **PadGrader helpers**: `FindNearVertex`, `InterpolateZ`, `PointInPolygon`, `DistToPolygon` are `public`; inner classes `SpatialHash` and `FaceGrid` are `internal` (accessible within the assembly)
- **Daylight line**: detected as zero-crossing of `newZ - origZ` across edges
- **Icons**: 24×24 PNG embedded resources (GH), 16×16 PNG for Rhino panel tabs and modifier badges. Generated by `generate-icons.ps1` (Blender-style: bold filled shapes, thick outlines, saturated colors). Loaded via `MoleHillInfo.LoadIcon()` (GH) and `PanelIcons.Load()` (Rhino).
- **TIN Surface component**: pure CDT only — no quality refinement. Quality lives in the Remesh component.
- **Panel top toolbar**: 2 rows — (1) name/picker/+/⎘/👁/🔒/Rebuild/🗑, (2) Live update + status label. Tolerance, display/transparency and layer settings are not in the toolbar; output layers are named by one "Output Layers" row in the Settings card.
- **Output layer routing goes through `LayerRole`.** Never hardcode or plumb a layer path for generated output, and never append a suffix to build one. `GeneratedRhinoObject.Role` is `required` and `LayerRoleTable.Path` is never null, so every producer names a destination and every destination resolves — that is what stops output baking onto Rhino's current layer. Appearance (colour, print width, linetype, annotation style, hatch) comes from the same role, so preview and bake cannot drift apart. See `docs/architecture.md` → "Output layer roles".
- **Analyses and annotations are separate content families.** An analysis *evaluates* the terrain (slope, elevation, cut/fill, earthworks, waterflow — the result is a measurement); an annotation *describes* it (contours, spot labels, callouts, sections — the result is drawing). They are peers, like modifiers/markers/objects: separate definition root, registry, descriptor, parameter descriptor, schema row builder, JSON family, and collection on `TerrainDefinition`. Never add a member to one that only the other needs. Annotations have **no** terrain-level visibility flag — an annotation is the drawing, so the per-card `IsEnabled` checkbox is the only control; `ShowAnalysisOutputs` governs analyses alone and must never gate annotation output. `ITerrainContentItem` is identity-only scaffolding, not a shared base. See `docs/architecture.md` → "Analysis vs annotation".
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
