# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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

After a rebuild, Windows may block the new `.rhp` (Mark of the Web). If Rhino fails to load the plugin, unblock the file: right-click the `.rhp` → Properties → check **Unblock** → OK. The `.rhp` is at `src/MoleHill.Rhino/bin/Debug/net7.0/MoleHill.Rhino.rhp`.

The Grasshopper project builds two output dirs (`bin/Debug/net7.0/` and `bin/Debug/net7.0-windows/`). ILRepack merges `MoleHill.Core.dll` + `TriangleNet.dll` into the primary `MoleHill.gha` and deletes the intermediate DLLs. The Windows TFM also copies the merged `.gha` to `%AppData%\Grasshopper\Libraries\` and runs Yak to produce `manifest.yml` + `.yak`.

## Project Structure

Three source projects, two test projects:

- **TriangleNet** (`src/TriangleNet/`, net7.0) — Triangle.NET constrained Delaunay library, nullable disabled, included as source and merged into the .gha
- **MoleHill.Core** (`src/MoleHill.Core/`, net7.0) — Pure terrain logic (no Rhino/GH dependency). Sub-namespaces: `Engine/`, `Processing/`, `Grading/`, `Analysis/`
- **MoleHill.Grasshopper** (`src/MoleHill.Grasshopper/`, net7.0-windows + net7.0) — GH components, plugin metadata (`MoleHillInfo.cs`), icon resources, `RhinoConverter.cs`. No terrain logic here.
- **MoleHill.Rhino** (`src/MoleHill.Rhino/`, net7.0) — Native Rhino plugin (.rhp). Dockable panel UI (Eto.Forms in `UI/`), three commands (`MoleHillPanel`, `MoleHillCreateTerrain`, `MoleHillConvertToRhino`), terrain definition model (`Model/`), `TerrainController`/`TerrainBuildService` pipeline (`Services/`). Persists terrain state as JSON in .3dm via WriteDocument/ReadDocument. 16×16 PNG icons in `Resources/` loaded via `PanelIcons.Load()`. Bake and Detach are in the terrain picker `▾` dropdown, not inline buttons.
- **MoleHill.Core.Tests** / **MoleHill.Grasshopper.Tests** (`tests/`, xunit 2.9.2) — Test naming: `MethodName_Scenario_ExpectedResult`

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
- **Z-aware dedup**: breakline-to-breakline vertex merge requires XY AND Z proximity — preserves parallel retaining walls at different elevations
- **PadGrader helpers**: `FindNearVertex`, `InterpolateZ`, `PointInPolygon`, `DistToPolygon` are `public`; inner classes `SpatialHash` and `FaceGrid` are `internal` (accessible within the assembly)
- **Daylight line**: detected as zero-crossing of `newZ - origZ` across edges
- **Icons**: 24×24 PNG embedded resources (GH), 16×16 PNG for Rhino panel tabs and modifier badges. Generated by `generate-icons.ps1` (Blender-style: bold filled shapes, thick outlines, saturated colors). Loaded via `MoleHillInfo.LoadIcon()` (GH) and `PanelIcons.Load()` (Rhino).
- **TIN Surface component**: pure CDT only — no quality refinement. Quality lives in the Remesh component.
- **Panel top toolbar**: 3 rows — (1) name/picker/+/⎘/👁/🔒/Rebuild/🗑, (2) Terrain Layer + Aux Layer assignment, (3) Live update + status label. Tolerance and display/transparency settings were removed from the toolbar.
- **Zone cards**: no GroupBox border — `CreateZoneGroup` returns a plain `Panel`. Same for modifier and marker cards.
- **Source editor buttons**: `Sel` replaces input objects; `Layers` replaces input layers (not additive — tooltips clarify).
- **Zone layer editor**: `Use Current` · `▾` · `Clear` — consistent with all other layer assignment patterns.
- **Modifier name**: editable `TextBox` (collapsed state shows read-only bold label).
- **"Priority by elevation"**: checkbox on zone cards (was "Use input Z").

## Coding Style

4-space indentation, one type per file. Geometry edge cases to prioritize in tests: collinearity, duplicate points, breakline intersections, tolerance boundaries, grading/slope regression.

## Commit Style

Short imperative subjects (e.g. `Add retaining wall component`, `Fix Steiner Z interpolation for parallel breaklines`). Keep commits focused.
