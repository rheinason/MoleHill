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
  `Analysis/` (contours, slope, waterflow), `Scattering/` (object scatter sampling), `Sculpting/` (brush engine +
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
- Terrain input preparation commands are selected-geometry workflows in the Rhino host: `mhValidateTerrainInputs`
  cleans selected points/curves through a parented Eto dialog, `mhSplitAtIntersections` splits only selected
  curves, `mhDrapeCurve` samples curves onto a selected mesh/surface along World Z, and `mhCreateWall`
  interactively draws a rail plus a parallel plan/elevation-offset rail. These commands create ordinary
  Rhino geometry for later assignment as points, breaklines, contours, or boundaries; they do not mutate
  managed terrain definitions. Validation replaces surviving objects in place to preserve ids and complete
  Rhino attributes; joining is layer-scoped, with one original object surviving each many-to-one join.
- Project-local ↔ real-world coordinates use the validated horizontal `MoleHill_ProjectBase` plane as a
  reversible local-to-real-world rigid transform; object replacement ids are reconciled across terrain
  source/output tracking. Legacy Python `FOTM` planes (the inverse convention) and legacy C# `Georef`
  planes migrate only after confirmation and are retained unchanged. Georeferenced Paste/File import
  diffs the complete object table, transforms all new ModelSpace objects, and removes them on failure.
  GeoTIFF import
  reads embedded model and linear-unit tags without GDAL, asks for source units when metadata is absent,
  falls back to a full-affine world file, converts coordinates into document units, and applies the saved
  real-world → project transform. See `docs/project-base-georeference.md`.

LandXML support is provided by `mhImportLandXml` and `mhExportLandXml` for TIN point/triangle
surfaces. The streaming importer validates point ids and face references, imports every surface,
converts its declared linear unit into document units, swaps LandXML Northing/Easting into Rhino Y/X,
and retains source face topology through a hidden exact-TIN mesh input. Export converts the completed
final mesh to metres, writes complete metric unit metadata, and applies the saved project-base transform
when present. The panel `DEM` action places the GeoTIFF image and reads a capped regular grid directly
from its numeric single-band samples; integer and floating-point bands, GDAL scale/offset, and NoData are
handled while RGB/multi-band imagery is rejected. Both import workflows are one undoable transaction and
remove all created state if terrain creation fails. Multiple images can be imported independently; CRS
reprojection remains out of scope.

## Two hosts, one core

```
Grasshopper:  GH inputs → Core (TinEngine / PadGrader / …) → RhinoConverter → GH outputs
Rhino panel:  TerrainDefinition (modifier stack, saved in .3dm)
                → TerrainBuildService.Build (staged pipeline) → TerrainBuildResult
                → TerrainDisplayConduit      (live viewport preview)
                + TerrainRenderMeshProvider  (render engines, no doc objects)
                + BakeTerrain                (real doc objects)
```

## Rhino ↔ Grasshopper terrain exchange

The host boundary uses an open `MoleHill Terrain` Grasshopper goo rather than treating the Rhino panel
mesh as a Revit-ready object. `MoleHill Terrain Snapshot` reflects across the optional host assemblies
(avoiding a Grasshopper → Rhino-plugin project reference) and reads only the latest completed **final**
display state. `TerrainGrasshopperBridge` raises a small invalidation event when controller state changes,
so an open Grasshopper definition schedules a fresh solve. The display state retains final hard and
elevation constraints plus the already-resolved named collage-zone boundaries alongside the mesh;
snapshot DTOs cheaply duplicate that build-consistent geometry. Preview or deferred states are rejected
rather than exported as apparently final terrain.

`Construct Terrain` and `Deconstruct Terrain` make the wrapper reversible: mesh, breaklines, zone tree,
zone keys, name, stable key, lossless revision, diagnostics, document units, and optional Project Base
metadata are all ordinary Grasshopper data at the boundary. Both custom goo types use versioned persistence.
Zones are
region metadata and never implicitly mean a separate terrain or Revit subdivision. `Partition Terrain`
is the explicit conversion from regions to pieces. It flattens each zone branch to Core
`MeshAreaSplitter.SplitPreservingTopology`, inserts every boundary in one operation, then derives all
piece meshes and the optional remainder from that single split result. Consequently adjoining outputs
reuse exactly the same seam coordinates while untouched source triangles retain their topology. Multiple
outlines in one zone use odd/even containment, allowing disjoint parts and nested holes, and each output
carries only source breakline segments projected onto its mesh. Users remain free to deconstruct,
split/join/merge with standard Grasshopper tools, and reconstruct.

`Prepare Toposolid` is the Revit-neutral compiled preparation boundary. One terrain item becomes one set of
horizontal outer/hole profiles, a bounded list of elevation points, stable identity, a deterministic
geometry fingerprint, source units, subdivision profile metadata, and point/error diagnostics. It rejects
stacked-XY or near-vertical surfaces and invalid/intersecting/disjoint profile domains before any Revit
transaction. Boundary and breakline-critical samples are mandatory; Core `ToposolidPointReducer` adds
spatial extrema and iteratively inserts the largest measured reconstruction errors until the requested
tolerance or point cap is reached. Coordinates are deliberately unchanged so project/shared-coordinate
transforms remain visible and user-controlled in Grasshopper; the downstream adapter converts the declared
document units to Revit internal feet exactly once.

The default downstream shape is `Partition Terrain -> ordinary GH edits/transforms -> Prepare Toposolid`,
with one independent Toposolid per branch. Optional Python 3 adapters under `examples/RhinoInside.Revit/`
perform only create/update/inspect/subdivision transactions and stable-key/fingerprint synchronization.
They are not shipped inside `MoleHill.gha`, so neither MoleHill host has a Revit or Rhino.Inside.Revit
assembly dependency. Subdivisions remain a separate opt-in operation because they follow their host rather
than behaving as independently editable terrain surfaces.

The Rhino panel retains runtime-only `ZoneAnalysisSummary` values from the last completed final build.
These summaries are calculated from resolved zone output after overlap and priority rules, so plan area,
surface area, elevation, slope, and mesh counts do not double-count overlapping zones. Cut/fill is shown
only when an enabled Earthworks analysis has an explicit reference and uses the same estimated/exact
convention as the global Earthworks summary. Zone summaries are display/cache state, not persisted settings.

## Model-unit contract

`MoleHill.Shared/ModelUnitContext` is the single model-unit boundary for both hosts. It supports every
Rhino length unit plus valid custom units, provides length/area/volume/inverse-area conversion and
formatting, and intentionally avoids Rhino's native unit-conversion call. Documents with `None`/`Unset`
units remain readable, but terrain creation, editing, builds, dimensional commands, and Grasshopper
solves are blocked until real model units are set.

Physical defaults are authored in metres and converted by the Rhino registries or GH solve context.
When Rhino changes units with geometry scaling, `TerrainController` applies the exact event scale to all
persisted dimensional state: tolerances; modifier, analysis, section, annotation, and scatter lengths;
areas, volumes, inverse-area density; object-placement translations; cached summaries; and sculpt cell
sizes/displacement payloads. Ratios, angles, percentages, counts, axes, random/block scale, and paper-
space plot weights remain unchanged. Runtime caches are discarded and live terrains rebuild.

Core remains unitless: `ScaleAwareTolerance` and scale-relative daylight convergence derive numerical
floors from caller tolerance and geometry extent, so uniformly scaled inputs take the same topology path.

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
When boundary peeling is enabled, extraction retains Triangle.NET's native triangle references and a
dense-id face map, validates reciprocal shared-edge adjacency, and uses that graph both for exact-median
unique-edge traversal and incremental boundary exposure. Invalid adjacency falls back to the generic
sorted-edge/dictionary path; callers requesting output edge arrays still receive the generic topology.
`TinBoundaryPreparer` turns an optional user boundary into an explicit constraint loop; open
contour/breakline endpoints never infer a perimeter, so an absent boundary uses the ordinary convex hull.
`ConformingDelaunay=true` is avoided (fails on tight parallel segments).
Triangle.NET's large quality-refinement state is created only when quality, conforming Delaunay, or an
incremental mesh mutation requests it. Plain and per-face conform triangulations avoid the otherwise
unused 4,096-bucket refinement queues, while `IMesh.Refine` retains its lazy creation path.

The Rhino **Retaining Wall** stage first uses `MeshConstraintTopologyInserter` to split only the faces
crossed by accepted toe/top rails. Untouched terrain faces and vertices retain their existing topology,
and inserted rails join the persistent hard-constraint stack for later modifiers. A full constrained
`SurfaceRemesher` rebuild is used only when the local insertion cannot produce an accepted mesh.

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
  gate only requires the output to be no worse than the input's topology. Split/collapse thresholds have
  a non-overlapping hysteresis band and each round splits before it collapses, preventing newly split
  edges from being immediately undone. Best overall quality, but can be slow on very large terrains and
  may occasionally cross a wall on a shallow wall angle.
- **Full Rebuild** (`Engine/SurfaceRemesher` via the shared `RebuildMeshWithConstraints` helper, also
  used by Retaining Wall's local-insertion fallback and the GH Remesh component) — classic constrained-Delaunay
  re-triangulation from scratch; every constraint including wall rails becomes a hard edge, so it
  structurally cannot cross a wall. Coarser triangle shapes than Isotropic.
- **Local Refine** (`Engine/LocalMeshRefiner`, also the dormant Sculpt DynTopo engine) — connectivity-preserving:
  only splits/flips triangles in place, never re-triangulates from scratch. Fastest and safest on huge
  terrains/delicate wall topology since it can't introduce new topology at all, but coarsest quality.

Params: Algorithm, Edge Length (0 = preserve approximate face density from plan area / face count), and
Crease Angle. Auto-density remeshes use three settle rounds; explicit targets use five in final builds.

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
`z += field.Sample(x, y) * constraintMask(x, y)` — never vertex indices — so the modifier is fully stackable: upstream edits
re-flow and the sculpt re-applies on top; multiple sculpts compose. Smooth/Flatten therefore bake a
static delta (Displace-style), by design. **DynTopo is currently disabled and hidden** because the
subdivision path can be unstable on real graded terrain; sculpt replay is displacement-only against
the incoming mesh.

Sculpt's persistent **Constraints** source set is resolved to a Core `SculptConstraintMask`: ordinary
closed curves protect their interiors, open curves protect the breakline, and a selected curve used
by an enabled earlier Grade Path expands to that modifier's configured design width. The feature is
fully pinned inside and feathers back to full sculpt influence outside. Live dabs, BaseZ recovery,
stroke rasterization, and build replay share the same mask. Raw field samples remain stored beneath
protected areas, so adding/removing a constraint is non-destructive and feathering is applied once.

The interactive session (`Services/SculptSessionController`) runs a long-lived `GetPoint` loop
(mouse-up = stroke end; inside a get, Rhino's own Ctrl+Z accelerator is blocked, so stroke-undo is
safe) painting dabs on a working copy of the sculpt stage's cached output
(`Core/Sculpting/SculptBrushEngine`); no pipeline runs mid-stroke. At stroke end the delta rasterizes
into the field (`SculptFieldRasterizer`), commits via `MutateTerrain` (deferred save), and the normal
debounced rebuild reruns downstream stages while a **display lock** (`TerrainController.Sculpt.cs`)
keeps the working mesh on screen. A floating Eto mini-toolbar (`UI/SculptToolbarForm`) hosts
brush/radius/strength/falloff/Done; persistent constraint sources and feather distance stay on the
modifier card alongside Sculpt/Clear and the stored-field summary. Session exit = one document undo record.

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
   non-manifold. The local splitter maps each segment/face candidate with one fixed-buffer three-edge
   pass and avoids constructing unused quality-refinement state for its many small constrained
   triangulations.
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

Grading search work is spatialized without changing deterministic tie order. `TerrainFaceGrid`
collects candidate faces from the finite daylight-ray corridor, deduplicates them, and evaluates them
in original face order. Grade Path builds one bounds grid for resampled paths with at least 64
segments, limits closest-segment queries to the path's maximum influence distance, and evaluates
candidates in original segment order; shorter paths retain the lower-overhead linear loop.

Watertightness gates and boundary-loop extraction share `MeshTopologyValidator`'s flat sorted-edge
analysis. Edge run lengths identify naked/non-manifold edges; boundary ids are compressed before flat
offset/neighbor adjacency is built, so sparse source ids do not cause dense max-id arrays. Grade Path
split-keep reuses the conformed analysis when applying Z. This generic primitive does not replace the
TIN production path's faster Triangle.NET-native adjacency.

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
  replayed cold-run timing diagnostics. Grade Pad topology entries retain flat geometry for their
  topology cache hits; Grade Path topology entries retain only output counts/fingerprint, patch
  summaries, and diagnostics because no Path restore consumes their full vertex/face arrays.
- Cold TIN conversion marks normalized Rhino meshes, allowing stage-cache storage to skip the otherwise
  redundant second normalization while still normalizing meshes produced by other paths when needed.
- Background builds enqueue live phase/elapsed/memory telemetry for the controller's UI-thread idle loop
  and detailed panel Status log. Rhino command history receives only one start and one terminal line per
  build, keeping phase diagnostics and timings out of the modeling command stream.
  `mhBenchmarkLargeTin` supplies a deterministic 247k-point diagnostic that separates shared TIN time
  from Rhino conversion, normalization, fingerprinting, and cache duplication, with managed-allocation,
  process-private, and working-set snapshots around every phase.
- **Contours** use a single-pass marching-triangles `ContourGenerator` (not one mesh-plane per level).
  Their configured output layer falls back to the terrain Annotation layer when unset or whitespace, so
  baked contours never leak onto Rhino's current layer.
- **Waterflow from Points** resolves point sources against the final terrain mesh and uses Core's
  `WaterflowTracer` to follow each triangle's exact downhill gradient through shared edges. Paths end
  at a naked mesh boundary, a local flat/sink, or the configured maximum plan length; generated curves
  are transient previews and bakeable auxiliary outputs.
- **Analysis coloring** is shared by slope, elevation, cut/fill, and sculpt preview through
  `AnalysisColorMapper`. Each preview supports a smooth gradient or fixed-width stepped bands,
  with auto-fit or explicit bounds. Cut/fill bands stay symmetric around zero; interval lengths
  scale with model units while slope intervals follow the selected slope unit.
- **Slope summaries** use `SlopeAnalyzer.Summarize` so final-build panel numbers do not allocate
  preview color arrays. Both summary accumulation and preview color generation parallelize above the
  large-face threshold. Slope preview coloring still uses `SlopeAnalyzer.Analyze`.
- **Cut/fill and earthwork reference comparisons** share one centroid-delta pass per reference/boundary
  fingerprint. The 2.5D case projects reference Z through Core `MeshHeightProjector`; overlapping or
  near-vertical XY regions fall back to the legacy Rhino world-Z mesh-line projection and report a
  diagnostic.
- **Section Cut, Cross-Sections, and Section Along Curve** can overlay the owning proposed terrain with
  any number of other MoleHill terrains. One selected comparison is the existing/reference profile;
  piecewise-linear profile comparison inserts exact crossings, respects coverage gaps, and emits
  translucent cut/fill meshes beneath terrain-coloured profile curves. Background snapshots duplicate
  only completed final meshes, fingerprint their geometry/name/colour, and rebuild live dependents when
  a referenced terrain changes without allowing cyclic references to loop indefinitely.

## Rhino: preview vs render vs bake (generated objects)

Generative outputs (markers, scatter, analysis annotations) are `GeneratedRhinoObject`s (geometry or
block-instance). They reach three consumers:
- **previewed** transiently by `TerrainDisplayConduit` (drawn from `TerrainDisplayState`, no doc
  objects),
- **rendered** by `TerrainRenderMeshProvider` (also from `TerrainDisplayState`, also no doc objects —
  see below), and
- **materialised** as real doc objects only by `TerrainController.BakeTerrain` (or the managed
  `SyncOutputs`). Scatter is conduit-preview + render + bake only (it can produce thousands of
  instances).

### Rendering without baking

Conduit geometry is viewport-only and invisible to render engines, so historically rendering a terrain
meant baking it. `TerrainRenderMeshProvider` closes that gap using the RDK custom render mesh system:
it derives from `Rhino.Render.CustomRenderMeshes.RenderMeshProvider` and advertises each
`TerrainDefinition.TerrainId` through `NonObjectIds` — GUIDs that are deliberately *not* `RhinoObject`s
in the document. It is registered once from `MoleHillRhinoPlugin.OnLoad` via
`RenderMeshProvider.RegisterProviders(assembly, plugin)`, and must stay `public` with a public
parameterless constructor for that discovery to find it.

Colour/transparency fallback is shared with the conduit through `TerrainDisplayColors`. The render path
first retains explicit, layer, and block-member Rhino render materials; when none exists, or when a
transparency override requires simulation, it creates a transient `RenderMaterial` without adding
materials to the document.

Cache coherence uses `TerrainDisplayState.RenderHash`, a sequence number stamped on each new display
state and advanced for display-only, layer-material, and mutable sculpt-preview changes. The controller
then notifies the RDK from the same build/display/sculpt lifecycle points. The notification statics still
live on the deprecated `Rhino.Render.CustomRenderMeshProvider` class, as they were never carried over to
the Rhino 8 API.

**Per-renderer support is opt-in.** A render engine only sees this geometry if it walks the provider's
non-object id list. Verified working against Rhino's own `ChangeQueue` pipeline (which Raytraced/Cycles
consumes); V-Ray has historically honoured it; there is no evidence Enscape does — the same limitation
that makes Grasshopper `CustomPreview` geometry invisible in Enscape. For engines that ignore the RDK
subsystem, Bake remains the only route.

Brep previews use the document's render-meshing parameters and cache the resulting mesh on each
`GeneratedRhinoObject` instead of asking the display pipeline to tessellate the Brep again on every
frame. This keeps long, thin retaining-wall faces stable and makes transient wall corners agree much
more closely with the baked document object.

`TerrainController` owns document state (load/save JSON in the .3dm), build scheduling, display-state
publication, source-object editing, and bake. It is the largest service and a decomposition target
(`docs/cleanup-plan.md`).

### Runtime viewport overlays

`RuntimeOverlay.cs` defines a small, explicit viewport vocabulary (marker, dot, text, polyline, mesh)
with stable ids, owner kind/id, Diagnostic or Guide channel, severity, code, message, and short label.
Overlay items travel with build results and stage-cache entries into `TerrainDisplayState`; they are
separate from `GeneratedRhinoObject`, document sync, and bake.

Guides are displayed when their feature requests them (Retopo's cross-field preview uses this channel).
Diagnostics default off and are enabled per owning modifier/analysis/object by a card checkbox. That
visibility set lives only on the UI-owned `TerrainRuntimeCache`: worker copies do not copy it and cache
merges do not overwrite it. Toggling visibility invalidates bounds and redraws without a build, save,
fingerprint, or undo record.

`TerrainDisplayConduit` prioritizes errors, then warnings, then information in stable source order and
caps each terrain at 50,000 line segments, 50,000 mesh faces, and 500 annotations. Lines/meshes use the
normal depth-tested pass; dots/text draw as depth-disabled foreground annotations. Central palette defaults are
red for errors, amber for warnings, and muted blue-grey for information, while Guide items may override
color per primitive.

Retaining-wall planner reports may carry an exact location and a small set of focus segments. Their
overlay keeps the full source rail as quiet context, emphasizes only the local failure, and places the
plain-language label at that failure. Internal codes such as `invalid_station_mapping` remain stable,
but the UI describes the repair as either **Ends do not match** or **Rail doubles back**.

Open retaining-wall rails that would otherwise fail self-crossing or ordered-station validation may use
a bounded repair candidate. Rhino limits it to
`max(wallTolerance, min(detailSize / 10, wallWidth / 20))`; the shared planner also preserves
endpoints/Z extrema and caps removed path detour before re-running all normal acceptance checks. Valid
rails and closed rails are not rewritten. Successful cleanup is an Information overlay. Finite
wall-centerline crossings are split into vertically separated plan crossings (Information) and
overlapping height ranges (Warning), with exact crossing geometry and all four rails.

## Determinism & gotchas

- Deterministic seeded randomness (FNV / SplitMix64) so scatter and grading don't reshuffle per solve;
  cache fingerprints use the shared Core XXH64 builder.
- TriangleNet RNG is fixed-seeded for reproducibility.
- The `.gha`/`.dll` is locked while Rhino is open → the build's output-copy step fails (MSB3021/MSB3027)
  even after a clean compile; grep `error CS` to judge a build.
- Env-gated `File.AppendAllText` debug probes flake under xunit's parallel runner — never trust a
  probe-on test count (see the agent memory `feedback_probe_file_contention`).
