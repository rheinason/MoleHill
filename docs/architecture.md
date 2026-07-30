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
  The dock panel uses local Eto responsive primitives (`PropertyRow`, adaptive button groups, and
  `UiMetrics`) instead of rebuilding the full panel on width changes. Modifier, analysis, and object
  settings are descriptor-driven; only specialized summaries and the scatter block-mix editor remain
  bespoke. Compact action buttons use
  `UI/PanelButtonIcons.cs`, a theme-aware vector icon set rendered to Eto images.
- Rhino command names use the compact `mh...` prefix. The installed toolbar exposes Geometry, Blocks,
  and Document utilities; terrain creation, editing, and bake/convert workflows stay in the dock panel.
- Project-local ↔ real-world coordinates use the named `MoleHill_ProjectBase` plane as a reversible rigid
  transform; object replacement ids are reconciled across terrain source/output tracking. GeoTIFF import
  reads embedded model tags without GDAL, falls back to a full-affine world file, and applies the saved
  real-world → project transform. See `docs/project-base-georeference.md`.

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
chain). Edge topology is optional: the Rhino panel consumes only vertices/faces and omits the expensive
edge sort. Incremental-edit coordinate indexes are retained through 250,000 vertices; larger meshes keep
exact-result and Z-only caching without retaining several multi-million-entry dictionaries.
`TinBoundaryPreparer` turns an optional user boundary into an explicit constraint loop; open
contour/breakline endpoints never infer a perimeter, so an absent boundary uses the ordinary convex hull.
`ConformingDelaunay=true` is avoided (fails on tight parallel segments).

The **Remesh** modifier offers three algorithms via its **Algorithm** dropdown (`Mode`: `"isotropic"`
default, `"rebuild"`, `"local"`), all sharing the same constraint stack (persistent hard constraints —
walls, breaklines, grade-path road edges — plus the modifier's own Constraints input):

- **Isotropic** (`Engine/IsotropicRemesher`, the Botsch–Kobbelt loop: split long / collapse short /
  Lawson flips / tangential relax / back-project). 2.5D makes the loop safe: every moved or added vertex
  re-samples Z from the ORIGINAL mesh at its new XY, so the output sits exactly on the input surface.
  Feature polylines (`Engine/FeaturePolylineGraph`: boundary ∪ creases at `CreaseAngle` ∪ the whole
  constraint stack) are pinned — vertices slide 1-D along them, corners stay fixed, no edge flips
  across, no collapse merges across features. Steep retaining-wall faces (≥ 70°) and faces touching
  non-manifold edges (imperfect upstream welds) are frozen and pass through verbatim; the acceptance
  gate only requires the output to be no worse than the input's topology. Best overall quality, but can
  be slow on very large terrains and may occasionally cross a wall on a shallow wall angle.
- **Full Rebuild** (`Engine/SurfaceRemesher` via the shared `RebuildMeshWithConstraints` helper, also
  used by Retaining Wall rebuilds and the GH Remesh component) — classic constrained-Delaunay
  re-triangulation from scratch; every constraint including wall rails becomes a hard edge, so it
  structurally cannot cross a wall. Coarser triangle shapes than Isotropic.
- **Local Refine** (`Engine/LocalMeshRefiner`, also the dormant Sculpt DynTopo engine) — connectivity-preserving:
  only splits/flips triangles in place, never re-triangulates from scratch. Fastest and safest on huge
  terrains/delicate wall topology since it can't introduce new topology at all, but coarsest quality.

Params: Algorithm, Edge Length (0 = keep the mesh's own median density), and Crease Angle.

The **Retopo** modifier (finishing, meant to run last) is field-guided **quad** retopology
(`Core/Retopo/`): `CrossFieldSolver` (a 2-D 4-RoSy cross-field pinned to feature tangents — boundary ∪
creases ∪ the whole constraint stack incl. grade-path road edges — smoothed by matrix-free diffusion;
previewable as a flow-cross overlay) → the isotropic remesh with **field-aligned relaxation**
(`IsotropicRemesher.Options.FieldTheta` — vertices slide along field lines) → `TriQuadPairer` (adjacent
triangle pairs merge into quads scored by corner angles, field alignment, and planarity). Pairing never
moves geometry and every triangle appears exactly once as a tri or half a quad, so the output is one
connected quad-dominant mesh, **hole-free by construction**; retaining walls stay frozen in place and
pair among themselves in their own plane. Quad output is terminal: display/bake handle quads and
`RhinoGeometryConversions.BuildMeshData` is quad-aware (quad → 2 tris) so downstream reads stay
correct, but Retopo is meant to run last.

## Core + Rhino: sculpting

The **Sculpt** modifier is Blender-style 2.5D brush sculpting (Draw/Subtract/Smooth/Flatten/Grab/
Clay/Noise; F = radius, Shift+F = strength, Ctrl = invert, Shift = temp smooth, Ctrl+Z = stroke undo).
**The durable data is a sparse world-XY displacement field** (64×64-sample deflated tiles on the
modifier definition, `Core/Sculpting/SculptDisplacementField`), applied at build time as
`z += field.Sample(x, y)` — never vertex indices — so the modifier is fully stackable: upstream edits
re-flow and the sculpt re-applies on top; multiple sculpts compose. Smooth/Flatten therefore bake a
static delta (Displace-style), by design. **DynTopo is currently disabled and hidden** because the
subdivision path can be unstable on real graded terrain; sculpt replay is displacement-only against
the incoming mesh.

The interactive session (`Services/SculptSessionController`) runs a long-lived `GetPoint` loop
(mouse-up = stroke end; inside a get, Rhino's own Ctrl+Z accelerator is blocked, so stroke-undo is
safe) painting dabs on a working copy of the sculpt stage's cached output
(`Core/Sculpting/SculptBrushEngine`); no pipeline runs mid-stroke. At stroke end the delta rasterizes
into the field (`SculptFieldRasterizer`), commits via `MutateTerrain` (deferred save), and the normal
debounced rebuild reruns downstream stages while a **display lock** (`TerrainController.Sculpt.cs`)
keeps the working mesh on screen. A floating Eto mini-toolbar (`UI/SculptToolbarForm`) hosts
brush/radius/strength/falloff/Done; the modifier card stays limited to Sculpt/Clear and the
stored-field summary. Session exit = one document undo record.

Expanded **Smooth** and **Sculpt** cards inspect their incoming cached mesh when no enabled Remesh
precedes them. A sampled Core regularity check warns when the mesh is very sparse or contains a
meaningful proportion of triangles below an 8-degree minimum angle, recommending Remesh below the
modifier before vertex-based editing.

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

`PathGrader.Grade` is the corridor analogue with its own cascade: **explicit corridor**
(`PathGrader.Explicit.cs`, carve/fill with density-guarded batter + station seeds) → **split-keep**
(`PathGrader.SplitKeep.cs`, conform corridor daylight + road-edge footprint loops in place — also
covers daylight reaching the terrain edge) → **constraint insertion** (`PathGrader.Patches.cs`, last
resort). Final Rhino builds prefer split-keep first for large meshes with persistent hard constraints;
this avoids paying for an explicit carve/weld that commonly defers on those already-complex meshes,
while direct Core callers keep explicit-first behavior by default. `GradedRegionAssembler.SplitOutside`
repairs pinched hole boundaries (outside faces at
irregular vertices are pulled into the carve) and re-conforms via a single CDT when the hand-rolled
splitter emits an untraceable boundary. Shared: `GradingGeometry2D` (all 2D primitives —
point-in-polygon, distance, interior point; `PadGrader.Spatial.cs` are thin compat wrappers),
`BatterStripBuilder`, `MeshAreaTopologySplitter`, `GradedRegionAssembler.WeldGradedRegion`.

## Rhino: the build pipeline

`TerrainBuildService.Build(snapshot, runtimeCache, mode)` runs stages, most behind a per-stage
fingerprint cache (`runtimeCache.StageEntries`); decomposed into `TerrainBuildService.*.cs` partials
(`.Tin`, `.MeshConstraints`, `.Grading`, `.Zones`, `.Analysis`, `.Objects`, `.Scatter`, `.Sculpt`,
plus `.Cache`, `.Fingerprints`, `.Types`). Order: TIN → modifiers (smooth/remesh/sculpt/grade pad/
grade path) → analysis → zones → markers → object placements → scatter. **The generated-output stages run only in
`TerrainBuildMode.Final` and are fingerprint-cached**. Analyses are cached independently by analysis id;
zones, markers, objects, and scatter retain stage-level entries.

- **TIN inputs** are resolved by `TerrainBuildSnapshotResolver` from a `TerrainBuildSnapshot` (built by
  `TerrainBuildSnapshotBuilder` from the live doc). A Triangulate **Boundary** now pre-filters inputs to
  its area (`FilterInputsToWorkBoundary` + Core `RegionInputFilter`) — the fast "work region".
- Layer-backed inputs use Rhino's native `FindByLayer` lookup, then re-resolve every result by ID through the
  active object table before including normal, locked, or hidden document geometry. This rejects stale wrappers
  and transform predecessors that Rhino's layer lookup can retain. Terrain sources are restricted to ModelSpace,
  excluding PageSpace objects sharing the layer. Instance-definition, reference, grip,
  light, and phantom objects are excluded.
- Project-base document orientation transforms ModelSpace objects only; PageSpace layout geometry is not moved.
- Tessellated contours and breaklines retain the panel's spacing conditioner, but its target comes from
  observed source-segment medians rather than document tolerance. Segment lengths are collected once
  per source class and collinear runs are scanned linearly, preserving straight-run normalization and
  intermediate long-breakline stations without large-site vertex explosions or quadratic stalls.
- Triangulate **Contour Mode** controls the large-input tradeoff: `Constrained` inserts every contour
  segment, `Vertices only` matches an exploded-points Grasshopper solve, and the default `Auto` switches
  contours to vertex samples at 250,000 source vertices. Breaklines and the terrain boundary always
  remain constrained. The build diagnostics report every unconstrained contour solve.
- Sparse point dedup uses a shared per-cell index store instead of millions of small cell lists. Panel TIN
  conversion trusts Core's validated triangle topology, so it skips redundant duplicate/unused/degenerate
  Rhino scans; cached flat arrays are also fingerprinted in bulk rather than through Rhino item accessors.
- `TerrainRuntimeCache.CreateWorkerCopy()` shares the persistent `TinEngine` instance with each
  background build worker (rather than a fresh one per build), so `TinEngine`'s Z-only/incremental-edit
  shortcuts are reachable from Rhino, not just Grasshopper — safe because `TinEngine.Build` is
  internally serialized by its own gate and re-keys `Vertex.ID` after every incremental edit.
- Stage-cache mesh outputs are shallow-copied into worker caches for fast dispatch. When a worker is
  retired by a newer build, the controller keeps its task around and defers disposal of displaced
  main-cache meshes until those retired workers have finished reading them. Hot restores duplicate the
  normalized cached mesh but do not normalize it again; timing records identify cache hits and omit
  replayed cold-run timing diagnostics.
- Cold TIN conversion marks normalized Rhino meshes, allowing stage-cache storage to skip the otherwise
  redundant second normalization while still normalizing meshes produced by other paths when needed.
- Background builds enqueue live phase/elapsed/memory telemetry for the controller's UI-thread idle loop.
  `mhBenchmarkLargeTin` supplies a deterministic 247k-point diagnostic that separates shared TIN time
  from Rhino conversion, normalization, fingerprinting, and cache duplication, with managed-allocation,
  process-private, and working-set snapshots around every phase.
- **Contours** use a single-pass marching-triangles `ContourGenerator` (not one mesh-plane per level).
  Their configured output layer falls back to the terrain Annotation layer when unset or whitespace, so
  baked contours never leak onto Rhino's current layer.
- **Slope summaries** use `SlopeAnalyzer.Summarize` so final-build panel numbers do not allocate
  preview color arrays. Both summary accumulation and preview color generation parallelize above the
  large-face threshold. Slope preview coloring still uses `SlopeAnalyzer.Analyze`.
- **Cut/fill and earthwork reference comparisons** share one centroid-delta pass per reference/boundary
  fingerprint. The 2.5D case projects reference Z through Core `MeshHeightProjector`; overlapping or
  near-vertical XY regions fall back to the legacy Rhino world-Z mesh-line projection and report a
  diagnostic.

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

- Deterministic seeded randomness (FNV / SplitMix64) so scatter and grading don't reshuffle per solve;
  cache fingerprints use the shared Core XXH64 builder.
- TriangleNet RNG is fixed-seeded for reproducibility.
- The `.gha`/`.dll` is locked while Rhino is open → the build's output-copy step fails (MSB3021/MSB3027)
  even after a clean compile; grep `error CS` to judge a build.
- Env-gated `File.AppendAllText` debug probes flake under xunit's parallel runner — never trust a
  probe-on test count (see the agent memory `feedback_probe_file_contention`).
