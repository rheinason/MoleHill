# MoleHill architecture map

The one-page map to read first. For per-file detail see `docs/file-index.md` and the `README.md` in
each source folder. Build/test commands live in `AGENTS.md`.

## Projects (dependency direction →)

```
TriangleNet (vendored)  ←  MoleHill.Core  ←  MoleHill.Grasshopper (GH host)
                                          ←  MoleHill.Rhino       (Rhino host)
                            MoleHill.Shared (small shared types)
```

- **`src/TriangleNet/`** — vendored Triangle.NET CDT engine. Do not refactor; treat as a library.
- **`src/MoleHill.Core/`** — all reusable terrain logic, **no Rhino/GH dependency**, unit-tested.
  Sub-namespaces: `Engine/` (triangulation), `Processing/` (input prep), `Grading/` (pad/path),
  `Analysis/` (contours, slope), `Scattering/` (object scatter sampling).
- **`src/MoleHill.Grasshopper/`** — GH components; thin wrappers over Core. Merged into `MoleHill.gha`.
- **`src/MoleHill.Rhino/`** — the Rhino plugin: dockable panel UI (`UI/`, Eto.Forms), commands
  (`Commands/`), the terrain definition model (`Model/`), and the build/persistence services
  (`Services/`). **All Rhino API use lives here; all reusable math lives in Core.**

## Two hosts, one core

```
Grasshopper:  GH inputs → Core (TinEngine / PadGrader / …) → RhinoConverter → GH outputs
Rhino panel:  TerrainDefinition (modifier stack, saved in .3dm)
                → TerrainBuildService.Build (staged pipeline) → TerrainBuildResult
                → TerrainDisplayConduit (live preview)  +  BakeTerrain (real doc objects)
```

## Flat-array data format (Core)

All Core pipeline data uses flat arrays for cache-friendliness:
`XY = [x0,y0,…]`, `XYZ = [x0,y0,z0,…]`, `Z = [z0,…]`, `Faces = [i0,i1,i2,…]`, `Edges = [a0,b0,…]`.

## Core: triangulation

`PointCloudProcessor` (Z-aware dedup) + `BreaklineDiscretizer` feed `TinEngine.Build`, which fingerprints
XY topology and Z separately (XxHash64) and takes the cheapest path: **Z-only update** →
**incremental single-point edit** → **full rebuild** via `TriangulationHelper` (a 5-tier CDT fallback
chain). `TinBoundaryPreparer` turns an optional boundary into an explicit constraint loop.
`ConformingDelaunay=true` is avoided (fails on tight parallel segments).

## Core: grading (the watertight invariant)

**Grading output is ALWAYS a watertight 2.5D mesh — never holes/spikes.** `PadGrader.Grade` dispatches
a tier cascade, taking the first that produces a watertight, manifold result:

1. **explicit batter** (`PadGrader.Explicit.cs`) — exact ruled side-slopes; crispest.
2. **split-keep** (`GradeWithSplitKeep` + `GradedRegionAssembler.SplitConform`) — conform the terrain to
   the daylight/footprint loops and keep the whole mesh, assigning Z by section. Watertight by
   construction (no weld). Uses a Triangle.NET CDT conform when the hand-rolled splitter goes
   non-manifold.
3. **region-remesh** (`PadGrader.RegionRemesh.cs`) — replace the affected region with a fresh dense
   patch graded by distance field; soft but always watertight.
4. **legacy** (`ConstraintFirstGradingEngine` + refined-Z `PadGrader.RefinedFallback.cs`) — whole-mesh
   rebuild; spiky last resort. **Runtime-proven unreached across the test suite; pending deletion
   (see `docs/cleanup-plan.md`).**

`PathGrader.*` is the corridor analogue. Shared: `GradingGeometry2D` (all 2D primitives —
point-in-polygon, distance, interior point; `PadGrader.Spatial.cs` are thin compat wrappers),
`BatterStripBuilder`, `MeshAreaTopologySplitter`, `GradedRegionAssembler.WeldGradedRegion`.

## Rhino: the build pipeline

`TerrainBuildService.Build(snapshot, runtimeCache, mode)` runs stages, most behind a per-stage
fingerprint cache (`runtimeCache.StageEntries`); decomposed into `TerrainBuildService.*.cs` partials
(`.Tin`, `.MeshConstraints`, `.Grading`, `.Zones`, `.Analysis`, `.Objects`, `.Scatter`, plus `.Cache`,
`.Fingerprints`, `.Types`). Order: TIN → modifiers (smooth/remesh/grade pad/grade path) → analysis →
zones → object placements → scatter. **The analysis/zones/objects/scatter block runs only in
`TerrainBuildMode.Final` and is not stage-cached** (so it recomputes per Final build).

- **TIN inputs** are resolved by `TerrainBuildSnapshotResolver` from a `TerrainBuildSnapshot` (built by
  `TerrainBuildSnapshotBuilder` from the live doc). A Triangulate **Boundary** now pre-filters inputs to
  its area (`FilterInputsToWorkBoundary` + Core `RegionInputFilter`) — the fast "work region".
- **Contours** use a single-pass marching-triangles `ContourGenerator` (not one mesh-plane per level).

## Rhino: preview vs bake (generated objects)

Generative outputs (markers, scatter, analysis annotations) are `GeneratedRhinoObject`s (geometry or
block-instance). They are:
- **previewed** transiently by `TerrainDisplayConduit` (drawn from `TerrainDisplayState`, no doc
  objects), and
- **materialised** as real doc objects only by `TerrainController.BakeTerrain` (or the managed
  `SyncOutputs`). Scatter is conduit-preview + bake only (it can produce thousands of instances).

`TerrainController` owns document state (load/save JSON in the .3dm), build scheduling, display-state
publication, source-object editing, and bake. It is the largest service and a decomposition target
(`docs/cleanup-plan.md`).

## Determinism & gotchas

- Deterministic seeded randomness (FNV / SplitMix64) so scatter and grading don't reshuffle per solve.
- TriangleNet RNG is fixed-seeded for reproducibility.
- The `.gha`/`.dll` is locked while Rhino is open → the build's output-copy step fails (MSB3021/MSB3027)
  even after a clean compile; grep `error CS` to judge a build.
- Env-gated `File.AppendAllText` debug probes flake under xunit's parallel runner — never trust a
  probe-on test count (see the agent memory `feedback_probe_file_contention`).
