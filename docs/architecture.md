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
  `Analysis/` (contours, slope), `Scattering/` (object scatter sampling), `Sculpting/` (brush engine +
  displacement field).
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

The **Remesh** modifier has two modes. New modifiers default to **"Local refine"** (`LocalMeshRefiner`, a
`RemeshModifierDefinition.LocalRefine` toggle), a connectivity-preserving retopology: keep every input edge,
split only coarse triangles in place at edge midpoints (Rivara — new vertices stay exactly on the surface),
and preserve the existing flow lines. The Core refiner still supports guarded Lawson quality flips for
callers that opt in, but Rhino Remesh leaves them off because they can scramble graded corridor topology.
Features (boundary ∪ creases at `CreaseAngle` ∪ breaklines) are pinned.
The legacy **global constrained-Delaunay** rebuild (`SurfaceRemesher`) remains available by turning local
refine off; it is good for constraint insertion / dense analysis meshes, but it re-triangulates from scratch
and can discard graded flow lines / creases.

The **Retopo** modifier (finishing, meant to run last) is field-guided **quad** retopology
(`Core/Retopo/`, staged). Stage 1 (now) computes a 2-D **cross-field** (`CrossFieldSolver` — 4-RoSy
directions pinned to feature tangents — from boundary ∪ creases ∪ the whole constraint stack incl.
grade-path road edges — smoothed by matrix-free diffusion) and, in preview, draws it as a flow-cross overlay
so the flow can be validated. Stage 2/3 (now, `Quads` toggle) builds and cleans the quad-dominant mesh:
`GuidedParametrizer` (field-guided u,v via a matrix-free Poisson / cotangent-Laplacian CG) →
`QuadExtractor` (integer (u,v) lattice, dedup by (i,j), Z lifted from the source triangle) →
`QuadRetopoCleanup` (fan-fill only small internal lattice holes and weld assembled quad sets) →
`QuadRemesher` orchestrates. This is still non-seamless; large extraction gaps are left open instead of
being turned into bad fan geometry. Retaining
walls (near-vertical → a sliver in plan) are excluded from the field by `WallFaceMinSlopeDeg` and rebuilt as
dedicated quad strips (`WallQuadStripBuilder`, rails from `RetainingWallPlannerCore`) before the final weld.
Quad output is terminal: display/bake handle quads and `RhinoGeometryConversions.BuildMeshData` is quad-aware
(quad → 2 tris) so downstream reads stay correct, but Retopo is meant to run last.

## Core + Rhino: sculpting

The **Sculpt** modifier is Blender-style 2.5D brush sculpting (Draw/Subtract/Smooth/Flatten/Grab/
Clay/Noise; F = radius, Shift+F = strength, Ctrl = invert, Shift = temp smooth, Ctrl+Z = stroke undo).
**The durable data is a sparse world-XY displacement field** (64×64-sample deflated tiles on the
modifier definition, `Core/Sculpting/SculptDisplacementField`), applied at build time as
`z += field.Sample(x, y)` — never vertex indices — so the modifier is fully stackable: upstream edits
re-flow and the sculpt re-applies on top; multiple sculpts compose. Smooth/Flatten therefore bake a
static delta (Displace-style), by design. **DynTopo** (toggle) refines triangles under the field/brush
to a Detail edge length (`LocalMeshRefiner` with `RegionFilter`), re-derived every build.

The interactive session (`Services/SculptSessionController`) runs a long-lived `GetPoint` loop
(mouse-up = stroke end; inside a get, Rhino's own Ctrl+Z accelerator is blocked, so stroke-undo is
safe) painting dabs on a working copy of the sculpt stage's cached output
(`Core/Sculpting/SculptBrushEngine`); no pipeline runs mid-stroke. At stroke end the delta rasterizes
into the field (`SculptFieldRasterizer`), commits via `MutateTerrain` (deferred save), and the normal
debounced rebuild reruns downstream stages while a **display lock** (`TerrainController.Sculpt.cs`)
keeps the working mesh on screen. A floating Eto mini-toolbar (`UI/SculptToolbarForm`) hosts
brush/radius/strength/falloff/DynTopo/Done. Session exit = one document undo record.

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

If all three tiers defer, `Grade` fails cleanly (returns null with a diagnostic) rather than emit a
non-watertight mesh. (The former tier 4 — `ConstraintFirstGradingEngine` + refined-Z whole-mesh rebuild —
was deleted; it produced spikes on exactly the degenerate scenes that reached it.)

`PathGrader.*` is the corridor analogue. Shared: `GradingGeometry2D` (all 2D primitives —
point-in-polygon, distance, interior point; `PadGrader.Spatial.cs` are thin compat wrappers),
`BatterStripBuilder`, `MeshAreaTopologySplitter`, `GradedRegionAssembler.WeldGradedRegion`.

## Rhino: the build pipeline

`TerrainBuildService.Build(snapshot, runtimeCache, mode)` runs stages, most behind a per-stage
fingerprint cache (`runtimeCache.StageEntries`); decomposed into `TerrainBuildService.*.cs` partials
(`.Tin`, `.MeshConstraints`, `.Grading`, `.Zones`, `.Analysis`, `.Objects`, `.Scatter`, `.Sculpt`,
plus `.Cache`, `.Fingerprints`, `.Types`). Order: TIN → modifiers (smooth/remesh/sculpt/grade pad/
grade path) → analysis → zones → markers → object placements → scatter. **The generated-output stages run only in
`TerrainBuildMode.Final` and are fingerprint-cached** (analysis, zones, markers, objects, scatter).

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
