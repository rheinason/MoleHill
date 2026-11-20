# Scatter: realtime review and plan

A sub-review of [realtime-review-2026-11-09.md](realtime-review-2026-11-09.md), which covers the
whole edit loop.

2026-11-09. A read-through of the scatter path (sampler, build stage, cache, display state, conduit,
render provider, native preview, bake) against one question: how far is it from feeling realtime, what
holds it back, and how much is left to win. Section 6 is a separate correctness list, written to be
handed to a smaller model.

Nothing here was measured in this pass. The numbers are from `.artifacts/scatter-stress/REPORT.md`
(2026-11-07, single runs, a 12,800-triangle terrain) and from comments in the code. Every "expected"
figure below is an estimate from reading the code and must be replaced by a measurement in Phase 0
before it is quoted anywhere.

## 1. Verdict

"Realtime" is three different budgets, and scatter stands differently against each.

| Budget | Target | Today | Distance |
|---|---|---|---|
| **Edit to visible** (drag a density or rotation slider) | under 100 ms, ideally one frame | about 120 ms debounce, then 154 ms build at 10,000 instances, 484 ms at 50,000, 1,571 ms at 200,000, on a tiny terrain with nothing else on it | Close at 10,000 on a bare terrain. Unknown, probably far, on a real one (see S3, S4). Fixable with plumbing, no research. |
| **Viewport frame** (orbit with the preview on) | 30 fps | Real geometry: about 0.09 ms per instance per frame (0.18 s for 2,000 two-Brep instances). Point modes: never measured, one `DrawPoint` call per point per pass. | Point modes can be made realtime at any count cheaply. Real geometry is bounded by Rhino's pipeline; a realistic ceiling is tens of thousands of instances with batching and level of detail, not unlimited. |
| **Scatter's tax on other edits** (move a pad while 50,000 trees exist) | near zero | A cache-hit rebuild still costs 73-85 ms at 10,000, 187 ms at 50,000, 758 ms at 200,000 | Entirely structural (S1) and entirely removable. |

So: the build side is a few structural changes from realtime at 100,000+. The draw side splits. Dots
and boxes can be realtime at any count. Full block geometry cannot be made unlimited from RhinoCommon,
because the display pipeline has no instancing call, but it can move from "capped at 2,000" to "a
triangle budget that covers a normal planting plan".

The largest single unknown is terrain size. Every stress number comes from a 12,800-face terrain.
Real terrains here are 100,000 to 250,000 faces, and the projection (S4) and the whole-pipeline tail
(S3) both scale with the terrain, not the scatter.

## 2. What happens on one edit today

1. Slider `ValueChanged` arms a 120 ms `UITimer` (`UiTiming.SliderCommitSeconds`); the commit writes
   the definition and schedules a Final build, with its own debounce policy.
2. The Final build runs the **whole pipeline**: snapshot on the UI thread, every modifier stage
   fingerprinted (cache hits), analyses, zones, markers, objects, then scatter, then report tables
   (uncached).
3. The scatter stage has **one cache key for all cards**. Any change to any card reruns every card.
4. Per card: sample the region, then per sample: mask tests, a Rhino `MeshLineSorted`, a Rhino
   `ClosestMeshPoint`, a `NormalAt`, two to four FNV hashes with three heap allocations each, a few
   `Transform` products, and one `GeneratedRhinoObject` (a class with two dictionaries).
5. The stage clones every `GeneratedRhinoObject` into the cache entry. On a cache hit it clones them
   all back out.
6. `UpdateDisplayState` copies the list, rebuilds ranges, computes content bounds by transforming
   eight corners per instance on the UI thread, runs `SyncScatterPreview`, recolours the analysis
   preview mesh, rebuilds legends, and notifies the RDK.
7. Each frame, each display pass (about ten in a shadow-casting mode): for every drawn instance, a
   `PushModelTransform` and one draw call per material, or one `DrawPoint` per shape point.

## 3. Structural blockers

Ordered by how much each one holds back the answer in section 1.

### S1. An instance is a heavyweight object, and it is copied at every boundary

`GeneratedRhinoObject` allocates two dictionaries in its field initialisers and carries a dozen
reference fields a scatter instance never uses. A scatter placement needs a block index and a
transform. The list is cloned into the stage cache (`TerrainBuildService.cs:606`), cloned out on
every cache hit (`:585`), copied into the display state, and walked again for ranges and bounds.

This is why a build that changes nothing costs 758 ms at 200,000 instances, and why scatter taxes
every unrelated edit.

**Change:** an immutable `ScatterInstanceSet` per card (block name table, `int[]` block index,
`Transform[]` or a flat `double[]`, precomputed bounds), shared by reference between cache, build
result and display state. No clone anywhere, because nothing mutates it. `GeneratedRhinoObject`
stays for bake only, materialised on demand per group. Expected: cache-hit cost near zero at any
count; memory per instance from several hundred bytes to about 130.

### S2. One stage, one key, one phase

`ComputeScatterFingerprint` hashes every enabled card plus the whole-mesh fingerprint into a single
key. Consequences: editing card A rebuilds card B; any terrain change anywhere rebuilds all scatter;
changing only rotation or scale re-samples and re-projects everything.

**Change:** key per card (`scatter:{id}`, which is also the documented stage-key convention), and
split the card into three cached phases with their own fingerprints:

- **Samples** (region, pattern, density, seed): the 2D points.
- **Ground** (samples + terrain + filters): Z, normal, accept or reject.
- **Poses** (ground + rotation, scale, offsets, block mix, alignment): the transforms.

A rotation drag then only recomputes poses, which is arithmetic. A terrain edit only recomputes
ground.

### S3. A scatter edit goes through the whole terrain build

Scatter runs only in `TerrainBuildMode.Final`, after everything else, and the result lands through
`UpdateDisplayState`, which also recolours the analysis preview mesh (a code comment measures that
at 692 ms on 244,000 faces) and rebuilds legends. None of that depends on scatter. The stress
numbers hide this because the stress terrain has no analyses, no zones and no reports.

**Change:** a scatter-only lane. When an edit touches nothing but scatter definitions, run only the
affected card against the retained final mesh and swap only that card's instance set in the display
state. There is precedent: ramp edits recolour without a rebuild, and legends refresh outside the
build. With S2 this is what makes dragging during the gesture possible, so the 120 ms commit timer
can become "coalesce to the latest value" for scatter rows.

This is the largest change and the one most likely to decide whether it feels realtime on a real
project. Report tables that include scatter quantities need to be refreshed by the same lane, or
shown as pending the way card quantities already are.

### S4. Ground projection is three Rhino calls per sample, single-threaded

`TryResolveTerrainPoint` calls `mesh.GetBoundingBox(true)` per sample, then `MeshLineSorted`, then
`ClosestMeshPoint`, then `NormalAt`. A comment in `TerrainMeshProjection` records that Rhino's
line-mesh intersection cost tens of milliseconds per call on a million-face mesh. At 12,800 faces
the whole per-sample cost is about 8 µs, so the stress run says nothing about 200,000 faces.

**Change:** project in managed code through a plan-grid face index (Core already has
`MeshHeightProjector` for exactly this), returning Z and the face, and take the normal from the
face. Per-index deterministic hashing already makes the loop order-independent, so it parallelises
with `Parallel.For` without changing results. Move the loop into `MoleHill.Core/Scattering` so it
can be unit-tested and benchmarked without Rhino. Expected: well under 1 µs per sample, flat in
terrain size.

Decide deliberately whether the slope filter wants the face normal or a smoothed one (see C3).

### S5. The viewport draws one instance at a time, every pass

RhinoCommon exposes no instanced draw. `DrawInstanceDefinition` is the nearest thing and it leaked
GPU memory until the driver killed Rhino. So each instance is a transform push and a draw call per
material, repeated in every pass, including shadow-map passes.

**Change, in order of value:**

1. **Point modes as one cached point cloud per card.** Build a `PointCloud` when the instance set is
   built (off the UI thread) and draw it with one `DrawPointCloud` call. This removes the need for a
   cap in Points and Shape Points mode altogether.
2. **Real geometry as merged chunk meshes.** Per card and material, bake instances into a few
   spatially chunked meshes built off-thread and cached with the instance set. A static mesh is one
   draw call and Rhino keeps its GPU buffer between frames. Chunk bounds give free frustum culling.
3. **A triangle budget, not an instance cap.** 2,000 bollards and 2,000 trees of 41,000 faces are
   not the same load. Spend a triangle budget; fill the remainder with a proxy (shape points or a
   decimated hull).
4. **Shadow passes draw the proxy**, or nothing above a threshold.

Do not plan on raw OpenGL instancing from the conduit. It would be the only true fix, but it is
Windows-only, bypasses Rhino's materials and shadows, and would be owned forever.

### S6. Nothing measures any of this

`hosted-perf` records a `Scatter` stage at 0.001 ms: no scenario contains a scatter card. No lane
measures frame time. The stress harness uses a terrain a twentieth the size of a real one and hides
baked instances before redraw.

### S7. Smaller frictions

- `ScatterPreviewDrawFrame` is constructed per draw call, so the shape points and the box of every
  block are recomputed from block geometry in every pass of every frame. Cache them beside
  `ScatterBlockPreview`.
- `SampleDeterministicUnit` allocates a 40-byte buffer and two `Guid.ToByteArray()` arrays per call,
  and `InstanceKeyFromIndex` allocates per call. Use `stackalloc` or hash the index directly, keeping
  the output bit-identical so existing scatters do not reshuffle (add a golden test first).
- The feather hash runs for every sample even with no setback and no feather.
- `ScatterRegionMask` tests every loop edge for every sample (`Contains`, `IsExcluded`,
  `BoundaryDistance`), with LINQ per call. Fine for a rectangle, quadratic for a zone border with
  thousands of vertices. Give loops bounding boxes and, for distance, an edge grid.
- `ApplyScatterAssetAttributes` walks every reference of the block, which after a bake is every
  baked instance.
- Native-block (Enscape) preview purges and re-adds document objects on any change. That is
  inherently not realtime; keep it capped and say so on the card.
- Poisson sampling fills the whole bounding box at 30 candidates per point, including area outside
  the region. Unmeasured at high counts.

## 4. How much is left to win

Estimates, to be replaced by Phase 0 measurements.

| Area | Today | After | From |
|---|---|---|---|
| Cache-hit rebuild, 200,000 instances | 758 ms | under 10 ms | S1 |
| Cold card build, 10,000 | 154 ms | 10-20 ms | S1, S4, S7 |
| Cold card build, 200,000 | 1,571 ms | 100-200 ms | S1, S4 parallel |
| Rotation or scale drag, 50,000 | full rebuild, 484 ms plus pipeline tail | 10-30 ms | S2, S3 |
| Point preview frame, 200,000 | not possible (capped at 2,000) | one draw call | S5.1 |
| Real geometry frame, 2,000 simple blocks | 180 ms | under 16 ms | S5.2 |
| Real geometry, 50,000 trees | not possible | proxy beyond budget | S5.3 |

After this the remaining floor is Rhino's: the UI-thread snapshot, the display-state swap, and
whatever a frame of several million triangles costs.

## 5. Plan

Each phase is shippable alone and each ends with a measurement.

**Phase 0. Measure.** Add a scatter scenario to `hosted-perf` (a 100,000+ face terrain, one region
card at 10,000 and at 100,000, cold, cache-hit, and single-row edit). Add a frame-time probe around
`DrawScatterObjects`, summed per frame across passes, exposed through the existing latency trace.
Record baselines. Re-check every estimate in section 4.

**Phase 1. Cheap wins, no architecture.** S7 items, S5.1 (point cloud), drop the per-sample
bounding box (pass bounds in), skip the feather hash when unused. Fix the section 6 list first so
the behaviour being optimised is the intended one.

**Phase 2. Instance set.** S1. Touches the build result, stage cache, display state, conduit,
render provider, native preview, bake and the report builder. Mechanical but wide; guard with a
golden test that poses are bit-identical before and after.

**Phase 3. Managed projection in Core.** S4, with the parallel loop.

**Phase 4. Per-card keys and phases.** S2.

**Phase 5. Scatter-only lane.** S3, and relax the commit timer for scatter rows.

**Phase 6. Geometry batching and budget.** S5.2 to S5.4.

Phases 1 to 3 make scatter cheap. Phases 4 and 5 make editing it feel live. Phase 6 raises what can
be shown. If only one of the larger ones is done, do Phase 5: it is the one the user feels on every
drag.

## 6. Correctness pass (for a smaller model)

Found by reading, not by running. Each item says how sure it is. Do them one at a time, each with
its own test, and do not fold in refactors from sections 3 to 5. Follow `CLAUDE.md` conventions;
items touching Rhino types need a `[RhinoNativeFact]` or a hosted check, and `dotnet test` alone is
not acceptance for those.

### C1. A capped preview shows a corner, not a thinned field (confirmed by reading)

`TerrainDisplayConduit.DrawScatterObjects` draws the first `PreviewCap` instances of a card's range,
and `SyncScatterPreview` does `Take(cap)`. Instances are in sample order. For Random that is a fair
subsample. For Grid and Jittered Grid it is the bottom rows; for Poisson it is a blob grown from the
seed point. A capped preview of a grid therefore shows a filled strip and an empty remainder.

Fix: when `range.Count > cap`, draw an evenly strided subset (index `i * Count / cap`) in both
places. Test: a pure helper that returns the drawn indices, asserted to span the range.

### C2. Multiple ground hits pick the one nearest Z = 0 (confirmed by reading)

`TerrainMeshProjection.TryProjectPointAlongWorldZ` chooses the hit closest to `point.Z`, and scatter
passes `Z = 0`. Where the vertical line meets more than one face (a retaining wall face, an
overlap), a terrain above zero gets the lowest hit and a terrain below zero gets the highest. The
result depends on the site's elevation datum.

Fix: scatter should ask for the topmost hit. Add a parameter or a scatter-specific overload rather
than changing the behaviour for Plant/Orient, which pass a meaningful Z. Test: a two-level mesh
above and below zero gives the same choice.

### C3. The slope filter reads a vertex-averaged normal (confirmed; the intent needs a decision)

`TryResolveTerrainPoint` uses `mesh.NormalAt(meshPoint)`, which interpolates vertex normals. Beside
a wall the averaged normal is tilted, the reason `TerrainPresentationMesh` exists for display. A
slope filter of 0 to 10 degrees will reject flat ground next to a wall. Align to Slope tilts blocks
there too.

Ask the owner before changing: face normal for the filter (exact, matches Slope analysis), smoothed
normal for alignment (looks better), is the likely answer. Do not change silently.

### C4. Box preview and content bounds ignore nested blocks (likely; verify in Rhino)

`ScatterPreviewDrawFrame.TryBuildDefinitionBox` and `TerrainDisplayState.UnionGeneratedObject` union
`member.Geometry.GetBoundingBox(true)` over direct members only. For a nested instance member that
is the bounding box of an `InstanceReferenceGeometry`, not of what it references. The snapshot
builder already has the correct recursive `TryGetInstanceDefinitionBoundingBox`.

Expected symptom: Bounding Box preview of a block made of nested blocks draws a wrong or missing
box, and the conduit's bounding box can clip tall nested canopies. Verify with the stress fixture's
"Stress nested canopy". Fix: one shared recursive helper used by all three sites.

### C5. Scatter mutates the shared terrain mesh on the worker (confirmed by reading)

`BuildScatterPlacements` calls `mesh.Normals.ComputeNormals()` on `analysisMesh`, which is the
build's `PrimaryMesh`. It rewrites normals on a mesh other stages and the display own, outside
`MeshNormalOrientation`, and costs a full pass on every scatter cache miss.

Fix: do not recompute if `mesh.Normals.Count == mesh.Vertices.Count`; better, stop depending on
vertex normals (C3, S4). Check `MeshNormalOrientationGuardTests` still passes.

### C6. Render path can hand over a joined mesh without normals (likely; verify)

The new `TerrainRenderMeshProvider.GetDefinitionMeshes` appends member meshes into one per material
and never checks normals. `ScatterBlockPreview.PrepareForDisplay` does. A member mesh without vertex
normals leaves the joined mesh with a mismatched normal count. Fix: same check after joining. Also
note these joined meshes are rebuilt and never disposed on each `RenderMeshes` call with a new hash.

### C7. Viewport and render disagree on inherited materials in nested blocks (likely; verify)

The render provider now passes a parent material down through nested instances.
`ScatterBlockPreview.Build` does not: it calls `member.GetMaterial(true)` per leaf with no knowledge
of the nesting parent. A leaf set to By Parent inside a nested block can shade differently in the
viewport than in a render. Fix: carry the inherited material through `Collect` the same way.

### C8. Stage fingerprint may miss the layer table (unverified)

`ComputeScatterFingerprint` does not include `snapshot.LayerRoles.Fingerprint`, but every cached
instance carries a `LayerPath` resolved from it. If a terrain rename or a layer-template change does
not otherwise invalidate the stage, cached instances bake to the old path. Check what rename does to
the runtime cache first; if nothing clears it, add the fingerprint. Test in
`TerrainBuildServiceFinalOutputCacheTests`.

### C9. A drag is lost if the tab unloads within the commit window (confirmed by reading, minor)

In the uncommitted `MoleHillPanel.Objects.cs` change, `UnLoad` sets `unloaded` and disposes the
timer without committing `pendingValue`, and `Commit` now returns early when unloaded. A value
dragged and followed within 120 ms by a tab switch or card refresh is dropped. Fix: commit the
pending value in `UnLoad` before setting the flag, if a slider edit is active.

### C10. Native preview rebuilds on every sync when any block is missing (confirmed, minor)

`SyncScatterPreview` compares `current.Count == desired.Count`. Instances whose block is not found
are skipped, so the counts never match and all preview objects are purged and re-added on every
display update. Fix: filter `desired` to resolvable blocks before comparing.

### C11. Curve mode by Count places Count + 1 per path (known, tolerated by a test)

`SampleCurve` steps `s = 0 .. Length` inclusive, so Count 20 gives 21, and a closed curve gets two
instances at the seam. `ScatterSamplerCurveTests` asserts `InRange(20, 21)`, so this was accepted.
The closed-curve duplicate is the part worth fixing: detect a closed path and stop one step short.
Ask the owner whether open curves should give exactly Count.

### Not bugs, checked

- Per-index hashing keeps poses stable when filters or counts change. Good; keep it through S7.
- Whole-block containment with hole loops in zone mode is safe: `ContainsFootprint` rejects what
  `ScatterFootprint.IsInside` would wrongly admit through a hole loop.
- Nesting depth limits in the preview and the render provider agree (eight levels).
