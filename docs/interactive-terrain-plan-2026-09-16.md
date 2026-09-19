# Interactive terrain plan — 2026-09-16

Status: proposed architecture and implementation sequence, revised after inspecting the current code.
The realtime program itself is still proposed and unimplemented.

**Part of Step 0's evidence now exists** (2026-09-19): an end-to-end edit-to-visible trace, three
measured fixtures, and four scheduling/publication changes that came out of them. See
**Measured baseline** below before planning further work - several assumptions in this document are
now testable, and one of them was wrong.

## Product objective

Make common terrain edits feel continuous while preserving the exact, watertight 2.5D surface used
for final output. The first supported workflow is **Triangulate → Retaining Wall → optional second
Retaining Wall stage**. Each wall stage can contain several rail pairs; measure both stage count and
rail complexity. Pad grading follows as the second interaction family.

Realtime means the edited terrain visibly follows input, not merely that Rhino draws an old mesh at
30 fps. Distinguish continuous feedback during a gesture from fast rebuilding after a native Rhino
edit commits. Both are useful, but the latter must not be advertised as the former.

Initial targets, to validate on named fixtures and recorded hardware:

| Result | Target and scope |
|---|---|
| Immediate feedback | Guide/handle responds within 33 ms at p95; this alone does not qualify as realtime terrain |
| Interactive terrain | At least 30 surface updates/s during supported sustained gestures on a warmed 100k-face fixture; input-to-visible p95 ≤66 ms |
| Exact settlement | Target ≤200 ms after release for specifically qualified warm workflows; publish measured p50/p95 per workflow |
| Larger/harder cases | Test 500k-face terrain and dense imports separately; use bounded approximations and disclose slower exact settlement |
| Correctness | Final result matches the equivalent noninteractive edit; approximate geometry never supplies authoritative quantities or bake output |

An arbitrary stack with remeshing, complex walls, drainage, sections, and scatter is not promised a
200 ms final result. Exact geometry and dependent outputs may settle separately if their revisions
and freshness are explicit. Measure cold loading and session preparation separately; do not hide
repeated preparation behind warm benchmarks.

## Findings that change the previous plan

### Grading is not generally topology-invariant

`TerrainBuildService.Grading.cs::BuildGradePadMesh` looks up a topology cache, but on a miss it calls
`PadGrader.Grade` in preview as well as final mode. Only afterwards does preview call `ApplyGradingZ`.
`TerrainBuildService.cs::ComputeGradePadTopologyFingerprint` includes slope angles, boundary positions,
and the pad plane. Changing slope or elevation can move the daylight boundary and change constrained
edges and intersections. A pinned-topology preview is therefore an approximation for these edits,
not a generally exact substitute for grading.

Even `PadGrader.Topology.cs::ApplyGradingZ` clones vertices and prepares barriers, a boundary loop, and
a face grid. Fixed topology permits preparation reuse; it does not establish frame-budget cost.

### Triangulation already has useful, bounded reuse

`TinEngine.Build` reuses its result when its XY/settings and Z hashes match. With unchanged XY/settings
and changed Z it can use `TinResult.WithUpdatedZ`; slope-dependent boundary peeling instead rebuilds
the result over the retained triangulation. The Rhino worker cache retains the same `TinEngine`.

The incremental topology path is narrower: one point addition OR removal, unchanged segments and
compatible settings, no quality constraints, and retained topology state up to 250,000 input vertices.
It explicitly rejects a simultaneous removal and addition, so it is not a general point-move path.
Input preparation, deduplication, sampling, and boundary rules must also remain compatible before an
edit qualifies as an exact Z update. Face counts are not input vertex counts.

### Retaining walls already insert into existing topology

`TerrainBuildService.RetainingWalls.cs::ApplyRetainingWalls` performs:

1. Resolve curves and plan accepted rail pairs.
2. Check strip usability and construct rail constraints.
3. Snap/clean constraints against the upstream mesh.
4. Try `MeshConstraintTopologyInserter.TryInsert` and validate boundary safety.
5. Apply rail elevations and build a Rhino mesh.
6. Fall back to a constrained rebuild if insertion is rejected.
7. Persist constraints for later stages.

This is more promising than assuming every wall edit performs a global triangulation. However, the
path still extracts whole-mesh arrays during preparation and insertion, analyzes boundary graphs,
reconstructs output meshes, and incurs fingerprint/cache costs. Local insertion does not imply local
total execution cost.

The outer wall-stage cache includes the upstream mesh fingerprint. An upstream Z edit can miss the
whole stage and repeat rail planning even when the wall curves are unchanged. Planning is independent
of the terrain mesh and should have its own cache.

There is also a specific preview inefficiency: the host calls `RetainingWallPlannerCore.Plan`, whose
curve entry point calls `PlanPolylines(..., buildSolids: true)`. Preview subsequently skips publishing
those Breps. Separate rail planning from solid construction so preview avoids making discarded solids.

### Orchestration remains part of the cost

- `TerrainController.Build.cs` captures the snapshot synchronously before dispatch. Snapshot creation
  resolves document objects, computes fingerprints, and can read/sample DEM textures.
- `TerrainRuntimeCache.CreateWorkerCopy` shares cached mesh references; it is not a deep mesh clone.
  Stage restore/store and `TerrainDisplayState.Clone` do clone meshes. Measure actual call counts.
- Every newer rebuild request cancels the running build, and completion rejects older requested versions
  (`TerrainController.cs::RequestRebuild` sets `CancelRequested` and cancels the worker whenever a build
  is running). Applied to a stream of input samples this does not merely run slowly — each sample cancels
  the build the previous sample started, so no preview ever completes. **This is the blocking constraint
  for continuous input, and replacing it is the primary deliverable of Step 2.** No amount of geometry
  optimization produces a visible frame while it stands.
- The shared `TinEngine` serializes work through a gate. Retired workers can delay fresh work; adding
  more tasks is not a solution to that queue.
- Sculpt demonstrates preview ownership arbitration, but does not prove arbitrary downstream stacks
  can run within a frame budget.

Historical timings are regression references, not comparable end-to-end realtime baselines. Do not
infer a universal 30–100× required improvement from unlike cases.

## Measured baseline — 2026-09-19

Measured with `mhLatencyTrace` (`TerrainLatencyTrace` / `TerrainLatencyReport`), which records the whole
path from the edit to the redraw and attributes every interval to debounce, MoleHill working, waiting
for the host, or redraw. Full numbers and method in
[architecture.md](architecture.md) → "Rhino: edit-to-visible latency". Rhino 8, `rhino-mcp` slot, one
machine; treat as shape rather than as budgets.

**The wait has three distinct shapes, and they want different fixes.**

| Fixture | Edit to visible | Dominant cost |
|---|---|---|
| Small (2.6k faces, warm) | 385 ms | scheduling and the worker→UI marshal |
| Analysis-heavy (244k faces, 6 analyses) | 7,213 ms | dependent outputs — Ponding 3.72 s, Catchments 1.40 s |
| Geometry-heavy (124k faces, 5 modifiers, no analyses) | 5,203 ms | **98.5% the modifier chain** |

### What this changes in this plan

- **"Orchestration remains part of the cost" was overstated for the geometry-heavy case.** On that
  fixture debounce is 2 ms, host wait 1 ms, snapshot 1.1 ms, worker cache clone 0.4 ms, cache merge
  0.8 ms and display publish 1.6 ms — 6 ms of orchestration against 5,121 ms of geometry. Orchestration
  is a real cost on *small* terrains and a rounding error on large ones. Do not spend Step 1/Step 6
  effort on copy and publication overhead without measuring the fixture it is meant to help.
- **The three-channel freshness design is partly built.** `TerrainDisplayState` now carries
  `GeometryRevision` and `OutputsRevision` with `OutputsAreStale`, and a build publishes its completed
  mesh before its dependent outputs settle, carrying the previous build's outputs forward and marked
  stale. Bake, the interop mesh accessors and the Grasshopper bridge already refused a state carrying
  deferred output, so channel 3's completed-revision contract holds without touching those call sites.
  This is the authoritative-vs-interactive split of channels 2 and 3, for whole-build granularity only —
  it is not a gesture-rate interactive surface and does not pre-empt Step 2.
- **Interactive-surface targets must budget publication, not just evaluation.** Publishing a 244k-face
  mesh cost 692 ms of preview colouring plus ~1,000 ms of redraw until the interim path was changed to
  preview plain. Redraw tracks the *final* face count: 1,048 ms at 244k faces, 73 ms at 49k. A 66 ms
  input-to-visible target is a budget for the whole publication path, and on a large mesh the redraw
  alone exceeds it. Either the interactive surface is much smaller than the exact one, or the target
  applies only to bounded regions.
- **The debounce is no longer a fixed floor, but cancel-on-every-request is untouched.**
  `TerrainDebouncePolicy` is now leading-edge: an isolated edit waits 0 ms and only a gesture is
  rate-limited. Measured 944 ms → 1.8 ms. This does **not** address Step 2. `RequestRebuild` cancels the
  running build at *schedule* time, before any debounce applies, so during sustained input every sample
  still kills the evaluation the previous sample started. Removing the delay means dispatch now follows
  each cancellation sooner, so a sustained gesture starts and abandons *more* builds than before while
  still showing nothing until input stops. **Step 2 remains the blocking constraint and this raises its
  priority rather than reducing it.**

### A Step 1-shaped finding outside the wall path

Step 1 is about removing redundant preparation in the wall stages. The geometry-heavy trace found the
same species of waste in grading, and it is the largest single item in that build:

`PadGrader.CreateConstraints` spends 1.00 s constructing a `ConstraintCoincidenceSnapper`, of which
**0.91 s is `SpatialHashGrid2D.Build` over all 186,501 mesh edges** — to snap 3 constraint polylines
onto the terrain. `PathGrader.CreateConstraints` repeats it (0.72 s, 47 constraints). Together ~2.0 s of
a 7.0 s build. The grid's own `BuildStatistics` show 815,328 memberships for 186,501 edges with
`MaxCellOccupancy` 8 — not degenerate, simply indexing the whole terrain at a cell size below the
typical edge length.

Three remediations, none attempted, all of which change only *how* constraints are found and so need
geometry-equivalence tests rather than timing tests alone:

1. Clip the index to the pad/path influence envelope — already computed per pad, ~8% of that fixture's
   area. The largest expected win.
2. Match cell size to mesh edge length.
3. Share one index across the grading stages of a build instead of rebuilding per modifier.

This belongs in Step 1 alongside the wall preparation work, and is a prerequisite for Step 5 (pad and
slope editing) being interactive at all.

### Step 0 evidence: delivered and still missing

Delivered: the end-to-end trace with per-interval ownership; synthetic geometry-heavy and
analysis-heavy fixtures; per-stage and sub-stage timings for Grade Pad and Grade Path; measured
cancellation/abandoned-worker accounting.

Still missing, and Step 0 is not complete without it: 100k/500k wall fixtures specifically; a
five-second sustained native gesture (every measurement here is an isolated or burst edit, never
continuous input); allocation and peak-memory figures; TIN gate wait; and presentation latency beyond
the return of `doc.Views.Redraw()`.

### Method warnings

Two mistakes cost time and would recur:

- **`Thread.Sleep` inside a `rhino-mcp` `run_csharp` script runs on Rhino's UI thread**, so a script
  that sleeps while waiting for a build blocks the loop it is measuring. It produced an apparent 28.7 s
  host stall. Wait by returning from the script and polling with short calls.
- **`RhinoApp.InvokeOnUiThread` runs inline when already on the UI thread.** Using it to defer work
  started a build inside the `OnBeforeTransformObjects` handler that produced the edit, before the
  transform had been applied. `Eto.Forms.Application.Instance.AsyncInvoke` queues properly.

## Runtime design

### Three result channels with explicit freshness

1. **Interaction feedback:** current handles, rails, elevation guides, and provisional wall strips.
2. **Interactive surface:** bounded geometry evaluation, exact where proven and approximate otherwise.
3. **Authoritative result:** exact terrain followed by analyses, annotations, zones, and objects.

Keep the last completed exact state available while preview is active. Associate geometry and every
dependent result with a document/terrain revision. Retained numbers visibly belong to the previous
result; do not relabel them as current. Bake, export, and the Grasshopper final-state bridge must
enforce their completed-revision contract. Preserve authored appearance and layer roles. Never mix
outputs from different revisions into an apparently complete result.

### Capability declarations checked against actual session inputs

| Capability | During gesture | On release |
|---|---|---|
| Appearance only | Change display state | No geometry work |
| Exact fixed topology | Evaluate coordinates using validated mappings | Reuse exact work only if all invariants and downstream dependencies hold |
| Approximate fixed topology | Evaluate a prepared working surface | Run exact solver |
| Topology-changing | Supported bounded surface evaluation, otherwise guides and last valid terrain | Run exact solver |

Descriptors declare capabilities; evaluators validate them for current data and the downstream stack.
Unknown cases require exact rebuilding. Schema tests require explicit classification of declared
parameters, but a declaration alone never establishes correctness. Source-object edits need the same
classification even though they are not parameter rows.

An upstream Z-only change can alter later pairing, elevation precedence, clipping, or topology.
Distinguish source sampling, XY/connectivity, Z, constraints, appearance, and dependent outputs.
Do not introduce a universal dependency framework before the first wall workflow proves what it needs.

### Prepared interaction sessions

At session start, retain immutable upstream state, reusable source samples, topology/indices,
constraints, working buffers, and the downstream evaluation policy. Prepare asynchronously where safe
and report readiness; the UI must remain usable during preparation.

Per input sample, update changed parameters or source samples and evaluate affected data. Use the
union of old and new influence extents so moving a feature restores the terrain it leaves behind.
Never repeatedly grade the previously deformed preview: evaluate from a stable upstream baseline.

Use one bounded evaluator and one replaceable pending sample per active session. New input replaces
the pending sample rather than launching another worker. Publish monotonically advancing previews
within the same valid session even when a newer sample is pending, subject to a measured age limit.
Invalid document/session generations, undo, source replacement, and newer displayed results always
reject stale publication. Cancel long obsolete work at measured checkpoints.

Release schedules the latest exact state immediately, without the existing 500 ms final debounce. This
applies to gesture release only: edits arriving outside a session — typed field entry, card changes,
document events — keep the debounce, which is what stops a multi-field edit from starting a build per
field. Removing it globally would trade one latency problem for a rebuild storm.
Escape, Undo/Redo, document closure, source deletion, terrain switching, and sculpt handoff release
ownership and buffers deterministically. One authored gesture remains one undo operation.

### Source snapshots and mesh ownership

Capture document-owned state on the document thread; move only verified safe work on detached data
to workers. Retain unchanged captures by revision, including DEM samples, flattened curves, role/style
snapshots, and block metadata where applicable. Source membership, units, tolerances, external-file
changes, and document replacement invalidate the relevant captures.

Warmed dispatch should not scale with the entire source dataset. Initial capture still can. Moving
fingerprinting to a worker does not establish constant-time document capture.

Reduce mesh copying through explicit immutable ownership and audited mutation boundaries. A version
stamp does not enforce copy-on-write. Account for display, render, worker, cache, and sculpt readers,
including native mesh disposal. Start with boundaries needed by the first wall workflow; a broad
ownership refactor is not a prerequisite for its prototype.

## First delivery: triangulation with one or two retaining-wall stages

### Editing scenarios

| User edit | Reuse opportunity | Conditions and fallback |
|---|---|---|
| Wall edit, terrain sources unchanged | Pin base TIN | No source triangulation during wall gesture |
| Edit second wall stage | Pin TIN and first wall output | Invalidate if either upstream dependency changes |
| Edit first wall stage | Reuse second stage's unchanged rail plan | Re-evaluate second stage's insertion/elevation dependencies |
| Change point/contour/breakline heights at fixed sampled XY | TIN Z-update path and wall plans | Verify sample identity, merge policy, peeling, and wall insertion mapping |
| Change rail heights at fixed sampled XY | Reuse insertion connectivity and interpolation mappings where valid | Recheck top/toe identity, height thresholds, crossings, snapping, and acceptance |
| Move rail/control point in XY | Reuse unaffected upstream state; rebuild changed constraints | No general exact fixed-topology claim; restore old footprint |
| Change Max Wall Width | Reuse source capture | Changes pairing/acceptance and cleanup tolerances; not a simple thickness deformation |
| Add/remove source points or change TIN settings | Existing TIN reuse where eligible | General topology changes retain full-build fallback |

Continuous source editing is essential: the wall card exposes source curves and Max Wall Width, so
optimizing panel sliders alone misses the main workflow. Investigate what native Rhino gumball,
grip, and transform operations expose during a gesture. Existing replace/transform events must not
be assumed to provide per-frame provisional geometry. Verify in a disposable Rhino slot.

If native gestures expose only committed changes, ship fast post-edit updates first and explicitly
scope continuous preview to a supported hook or a focused MoleHill editing command. Do not build a
competing persistent curve model or independent undo stack.

### Wall preparation and cache boundaries

Split reusable work into:

- **Sampled rails:** keyed by source geometry and sampling/cleanup settings.
- **Accepted wall plan:** pairing, orientation, crossings, diagnostics, and constraints, independent
  of terrain mesh. Include Z in keys for height-dependent decisions.
- **Insertion preparation:** upstream topology, prepared rail XY, and coincidence decisions.
- **Elevation evaluation:** current upstream Z and rail Z, preserving constraint precedence.
- **Final wall geometry:** Breps and output rails, constructed when required for final output.

For fixed XY, investigate retaining provenance for every inserted vertex: original upstream vertex,
position along a rail, or interpolation within an upstream face/edge. Compose mappings across two
wall stages to update elevations without repeating intersections and splitting. This is a new
capability to prove, not something the current inserter guarantees. Preserve source lookup ties,
rail precedence, and tolerance behavior; invalidate when pairing or snapping changes. Initially
qualify simple noncrossing wall pairs before complex crossings.

Keep adjacent wall stages semantically ordered. Do not merge them into one constraint operation
without equivalence evidence: sequential snapping, insertion, and elevation ownership can differ.
For changed XY, evaluate from retained pre-wall geometry; adding a new wall onto the old wall mesh
leaves obsolete breaklines. The second stage must see the updated first-stage result.

A cheap strip between current rails can provide immediate provisional feedback while terrain updates,
but cannot satisfy the surface-update acceptance test alone. Do not display old final wall solids
as though they describe the new terrain.

## Sequence of action

| Step | Action | Exit evidence |
|---|---|---|
| 0 | Capture fixtures and instrument complete interactions | Baseline traces, exact references, native-input feasibility — **partly delivered 2026-09-19; see Measured baseline** |
| 1 | Remove wall-specific redundant preparation | Preview makes no wall solids; unchanged plans survive upstream-only edits - **code landed 2026-09-19, unmeasured; see Implementation log 1-5** |
| 2 | Implement minimal prepared session and bounded scheduler | Sustained input advances previews; undo/interruption reject stale work - **blocked on an interactive build mode that is actually dispatched; see Implementation log 5** |
| 3 | Deliver TIN + one wall stage for qualified height edits | Surface latency and exact convergence pass measured gates |
| 4 | Extend to two wall stages and XY rail movement | Ordered composition, old-footprint restoration, continuous surface feedback |
| 5 | Generalize to pad/slope editing | Prepared approximate grading converges with measured deviation |
| 6 | Scale to larger terrains and broader stacks | Measured LOD/copy/display changes improve fixtures without regressions |

### Step 0 — Establish evidence

Create reproducible 100k- and 500k-face cases for TIN alone, TIN + one wall stage, and TIN + two wall
stages. Include synthetic controls and representative real scenes. Record wall count, rail stations,
constraints, source object count, hardware, display mode, and settings.

Trace input timestamp → queue → snapshot → source preparation → TIN gate/solve → each wall's plan,
constraint prep, insertion/fallback, validation → normalization/fingerprint/copies → publication →
first redraw using the new revision. Include allocations, peak retained/native memory, cache hits,
cancellation latency, gate wait, and dependent-output settlement. CPU build times alone do not prove
visible latency; record limitations of presentation measurement explicitly.

`mhLatencyTrace` (On / Clear / Report / Save) now provides the request-level half of this trace and
writes CSV; extend it rather than starting a second mechanism. Its phase vocabulary already covers
queue, snapshot, worker start, geometry-ready, each dependent-output family, completion pickup,
publication and redraw, and it reports abandoned worker time for superseded requests.

Extend `mhBenchmarkLargeTin` or add an opt-in sibling, using the existing captured wall tests as a
starting point. Verify native curve gesture delivery through `docs/rhino-live-testing.md`. Include a
five-second sustained gesture, not only isolated edits. Decide which input mechanisms the first
release can actually support before committing to continuous native-gesture claims.

### Step 1 — Reduce existing wall cost without changing geometry

Separate planner sampling/rail decisions from final solid production; preserve default behavior for
callers needing Breps. Cache planning independently of upstream terrain. Reuse mesh extraction and
index preparation within an evaluation where safe. Add cancellation checkpoints around and, where
measurements justify it, inside wall phases; the current wall method does not accept the build
cancellation callback. That last part is smaller than it sounds: `ShouldCancel` is already threaded to
`ExecuteCachedMeshStage` at the wall call site (`TerrainBuildService.ModifierStages.cs::RunRetainingWallStage`)
and simply is not forwarded into `ApplyRetainingWalls`, so the plumbing is one parameter — the work is
choosing the checkpoints inside, not building the mechanism. Check exact output equivalence and fallback
frequency.

### Step 2 — Prove the interaction loop

**Primary deliverable: replace cancel-on-every-request for sampled input.** Within an active session,
a new sample must replace the pending sample rather than cancelling the running evaluation, so that
evaluations complete and previews can be published. The existing policy stays in force outside sessions,
where it is correct. Nothing later in this plan is observable until this changes.

Confirmed 2026-09-19: the cancellation fires in `TerrainController.cs::RequestRebuild`, at *schedule*
time and before any debounce applies, so no scheduling change can soften it. The 2026-09-19 leading-edge
debounce made isolated edits immediate and, as a side effect, lets a sustained gesture start and abandon
more evaluations than the old fixed delay did — the visible outcome is unchanged (nothing until input
stops) but the wasted work is greater. Measure abandoned worker time (the trace reports it) before and
after this step.

Around that, implement session ownership, revision-aware publication, one latest-pending sample, source
capture reuse, and immediate release settlement. Use a simple wall-height fixture and lightweight guides to
expose scheduling behavior. Audit only the ownership boundaries needed to pin immutable upstream
geometry. Preserve undo and the final-state bridge contract. Measure status/panel refresh overhead
as well as geometry work.

Exit evidence must include a sustained-input trace showing evaluations *completing* and previews
publishing — a trace showing only cancellations means this step has not landed.

### Step 3 — Fixed-XY TIN and one-wall edits

Prepare stable source mappings and wall insertion provenance. Exercise the TIN Z-update path without
repeating unchanged source resolution. Validate wall plan/insertion eligibility per update, then
re-evaluate elevations and affected display data. Compare with clean exact builds across height
ranges, including acceptance thresholds; leave the fast path automatically when a guard fails.

If full-resolution evaluation exceeds budget, use a prepared bounded preview surface and retain exact
final settlement. Label this as approximate. This milestone is complete only when terrain follows
input, not when guides alone are smooth.

### Step 4 — Two stages and moving rails

Prove edits to each wall stage separately, then upstream edits with both stages active. Reuse unchanged
plans while recomputing changed dependencies. For XY motion, first benchmark existing insertion on a
prepared bounded surface; do not start with a general local-CDT engine. Restore old/new affected areas
from upstream state. Measure preview error at rail edges, wall ends, and corners, not just across the
mostly unchanged terrain.

If insertion exceeds budget, adapt resolution/cadence within declared limits and retain guides plus
the last valid surface. Such cases remain outside the qualified realtime set until surface latency
passes. Report native-input limitations explicitly.

### Step 5 — Grading

Add prepared grading indices, a stable upstream baseline, affected-vertex evaluation, and pinned
preview topology. Treat slope/elevation edits as approximate unless an exact invariant is proven.
Test daylight movement, overlapping pads, constraints, and downstream walls. Measure deviation and
visible change on release; final equivalence alone does not establish preview quality.

### Step 6 — Scale from measured bottlenecks

- Reduce copying through enforced ownership. Count duplicated vertices/faces and bytes, including
  transient display copies; avoid O(stages × terrain size) copies on unchanged stacks.
- Prepare reusable preview LOD when it helps wall insertion, grading, smoothing, normals, conversion,
  or drawing. Its value is not limited to source triangulation. Preserve rails and boundaries and
  define error limits. Simplifying a finished mesh does not avoid its initial triangulation; dense
  imports may separately need input sampling or multiresolution loading.
- Evaluate dirty display chunks after measuring redraw/upload cost. Retain unchanged chunk identity
  and consistent shared-edge normals. Rebuilding every chunk defeats much of the purpose. Rhino
  buffer reuse is an experimental question, not an assumed guarantee.
- Extend local evaluation and incremental analyses only where dependency locality is proven. Global
  effects such as waterflow must not inherit local invalidation by assumption.

## Validation and release gates

### Geometry and semantics

- Compare qualified exact updates and final settlement with clean noninteractive builds of the same
  definition. Require deterministic topology where promised; otherwise compare surface geometry within
  declared model tolerance, constraints, boundaries, diagnostics, and outputs.
- Reject single-use interior edges, nonmanifold edges, degeneracies, spikes, and orphaned old wall
  constraints. Preserve intentional outer/hole boundaries and relevant constraint elevations.
- Cover open/closed rails, narrow/tapered walls, ambiguous pairs, near-coincidences, crossings at
  separated/overlapping heights, top/toe reversals, and constrained-rebuild fallback.
- For two stages, test disjoint, neighboring, and interacting walls; edit first, second, and upstream
  sources. Reusing the second plan must not reuse invalid terrain insertion state.
- Fixed-XY tests include point, contour, breakline, and exact-TIN inputs, source reordering, tolerance
  boundaries, and slope-dependent peeling. Reject unsafe reuse rather than silently drift.
- Set explicit approximation error thresholds from fixtures before qualifying an interactive path.
  Evaluate local wall/grade detail separately from whole-terrain averages. No authoritative quantity
  may come from approximate geometry.

### Interaction and performance

- Sustain five-second gestures, reverse direction, release, start another immediately, then Undo/Redo.
  Exercise Escape, deletion, source replacement, document closure, and sculpt handoff.
- Record actual surface updates/s, input-to-visible p50/p95/max, displayed revision age, UI stalls,
  exact geometry settlement, output settlement, and peak memory. Repeat runs to distinguish warm
  behavior from misses and GC stalls; report both.
- Move the camera while evaluation runs. Prevent repeated modal slow-build warnings from interrupting
  qualified interactive gestures; provide nonmodal progress when work exceeds the expected budget.
- Exact state cannot be overwritten by obsolete previews or retired workers. Bake/export/Grasshopper
  snapshots cannot consume mixed or unfinished revisions.
- Run relevant Core, Rhino, and Grasshopper tests for changed code. Native UI/display claims require
  live Rhino testing; skipped native tests and screenshots alone do not prove latency.

## Deferred work and decision gates

General local constrained triangulation with patch stitching remains deferred. Existing wall insertion,
TIN reuse, and grading's explicit/split-keep/region-remesh tiers already provide more nuanced locality
than a universal global-rebuild description suggests. Preserve their fallback and watertightness
checks. Revisit broader topology surgery only after wall milestones expose a measured gap that
prepared evaluation and bounded resolution cannot address.

Do not begin with a GPU solver, replacement terrain representation, automatic fusion of wall stages,
or universal dependency graph. Each needs separate evidence and a correctness case.

For implementation milestones, update architecture, affected folder READMEs, and the generated file
index when source structure or data flow changes. This plan describes proposed behavior; it does not
change the current architecture merely by describing the destination.

## Code and benchmark reference map

- `src/MoleHill.Rhino/Services/TerrainController.Build.cs` — dispatch, completion, publication, timings.
- `src/MoleHill.Rhino/Services/TerrainController.Events.cs` — idle pump and document events.
- `src/MoleHill.Rhino/Services/TerrainBuildSnapshotBuilder.cs` — capture and DEM preparation.
- `src/MoleHill.Rhino/Services/TerrainBuildService.Tin.cs` — source preparation and TIN host pipeline.
- `src/MoleHill.Core/Engine/TinEngine.cs` — cached, Z-only, bounded incremental, and full TIN routes.
- `src/MoleHill.Rhino/Services/TerrainBuildService.RetainingWalls.cs` — wall pipeline and fallback.
- `src/MoleHill.Shared/RetainingWallPlannerCore.cs` — rail planning and optional solids.
- `src/MoleHill.Core/Grading/MeshConstraintTopologyInserter.cs` — existing-topology insertion.
- `src/MoleHill.Rhino/Services/TerrainBuildService.ModifierStages.cs` — wall-stage cache dependency.
- `src/MoleHill.Rhino/Services/TerrainBuildService.Grading.cs` and `TerrainBuildService.cs` — pad
  topology lookup and fingerprint.
- `src/MoleHill.Rhino/Services/TerrainBuildService.Cache.cs`, `TerrainRuntimeCache.cs`, and
  `TerrainDisplayState.cs` — ownership, reuse, and copies.
- `docs/performance-optimization-routes-2026-07-30.md` and
  `docs/large-terrain-performance-review-2026-07-05.md` — historical measurements and decisions.
- `tests/MoleHill.Grasshopper.Tests/TerrainRetainingWallPlannerLarge20260705CopiedCaseTests.cs` —
  captured planner fixture; supplement with whole-stack and live-interaction fixtures.

## Implementation log

Running record of the first implementation pass. Each entry says what was attempted, what landed, how
it was verified, and what is still unproven. **Verification vocabulary:** *compiles* = builds clean;
*unit-tested* = a test in `tests/` covers it; *live-verified* = observed in Rhino via `rhino-mcp`;
*unmeasured* = believed faster but no trace taken. Treat anything not marked live-verified or measured
as unproven for latency claims.

Build note for this pass: the user's Rhino was open for entries 1-5, so `MoleHill.Rhino` could not write
its normal output directory and was compiled with `-p:OutputPath=<scratch>` as a compile check. It was
closed by entry 6; the full solution then built clean and `validate.ps1 managed` and `warnings` both
passed (980/981 Core, 726 Rhino, 39 Grasshopper; 0 owned-code warnings). **Nothing here is
live-verified** — see entry 7 for the attempt and why it failed — so no claim below is an end-to-end
latency result. Entry 3 is the one item with a real measurement behind it, and it is a micro-benchmark.

### 1 — Preview no longer builds retaining-wall solids (Step 1)

Plan item: "Separate rail planning from solid construction so preview avoids making discarded solids."

Implemented: `RetainingWallPlannerCore.Plan` (`src/MoleHill.Shared/`) gained a `buildSolids` parameter
defaulting to true, forwarded to the existing `PlanPolylines(..., buildSolids)` instead of the hardcoded
`true`. `TerrainBuildService.ApplyRetainingWalls` passes `mode == TerrainBuildMode.Final`. Preview was
already discarding the Breps — only the `Final` branch adds them to `AuxiliaryObjects` — so the pairing,
rails and constraints are untouched and no geometry changes on either path.

Deliberate behaviour difference: the two `SolidFailed` report entries in `BuildWalls` (Brep build failed;
Brep is open) are only raised when solids are built, so preview no longer shows those two warnings. They
still appear on the final build, which is the one that publishes the solid. Recorded here because it is a
visible diagnostic change, not a pure optimisation.

Verified: compiles; `MoleHill.Grasshopper.Tests` green (39 passed, 15 skipped — the skips are the
native-runtime planner geometry tests, which are exactly the ones that would exercise the Brep path, so
this is *not* evidence the solid path still works — the GH component still requests solids by default and
is unchanged). Unmeasured: no trace of the preview saving yet.

### 2 — Cancellation checkpoints inside the wall stage (Step 1)

Plan item: "the current wall method does not accept the build cancellation callback … the plumbing is
one parameter — the work is choosing the checkpoints inside."

Implemented: `ApplyRetainingWalls` takes `Func<bool>? shouldCancel` and
`RunRetainingWallStage` forwards `c.ShouldCancel`, which was already in hand at that call site. Seven
checkpoints, at the phase boundaries the stage already times, using the existing
`ThrowIfCancellationRequested` idiom (`TerrainBuildService.Analysis.cs`), so cancellation surfaces as
`OperationCanceledException` exactly as it does in the analysis stages:

after curve resolve · after planning · once per wall in the strip/constraint loop · before wall grading ·
before constraint prep · before topology insertion · before the constrained-rebuild fallback.

The last is the one that matters most: the fallback rebuild is the stage's worst case and previously ran
to completion on a build that had already been superseded. The per-wall check bounds a scene with many
pairs; the rest are cheap boundaries that cost nothing and shorten the tail.

What this does **not** do: it does not make cancellation *finer* than a wall — a single very large pair
still runs its insertion to completion — and it does not touch `RequestRebuild`'s cancel-on-every-request
policy, which is Step 2 and remains the blocking constraint. This only reduces the *cost* of an
abandoned build, which the trace already reports as abandoned worker time.

Verified: compiles; `MoleHill.Rhino.Tests` green (722 passed, 130 skipped). Unmeasured: no before/after
abandoned-worker-time figure yet — that measurement belongs with Step 2, where the sustained-gesture
fixture exists to produce cancellations on purpose.

### 3 — Clip the grading constraint snapper to the constraints' own footprint (Step 1)

Plan item, from "A Step 1-shaped finding outside the wall path": remediation 1, "clip the index to the
pad/path influence envelope … the largest expected win." Measured cost was 0.91 s of a 1.00 s
`PadGrader.CreateConstraints` building `SpatialHashGrid2D` over all 186,501 mesh edges, plus 0.72 s in
`PathGrader.CreateConstraints` — together ~2.0 s of a 7.0 s build.

**The plan proposed the wrong region.** The influence envelope would have to be estimated before the
constraints exist. It does not have to be: both graders construct the snapper at the top of the method
but do not *use* it until a single loop at the very bottom that snaps the finished constraint list. Moving
construction to just above that loop makes the region exact by construction — the tolerance-expanded
bounds of the constraint points that will actually be queried — with nothing estimated. Recording this
because it is a case where reading the call site beat implementing the plan as written.

Implemented in `ConstraintCoincidenceSnapper`:

- Optional `Bounds2D? region`; vertices and edges whose tolerance-expanded bounds miss it are not indexed.
  The region test runs *before* the edge-key hash, so a clipped build does not pay per terrain edge.
- `ForConstraints(...)` / `RegionCovering(constraints, tolerance)` compute the region from a constraint
  list. `RegionCovering` returns null — meaning "index everything" — for an empty list or a NaN point.
- **Exactness argument:** a query at `p` only reaches members whose expanded bounds meet `p ± tolerance`.
  If that query box lies inside the region, every such member met the region too, so it was indexed. The
  region is padded by `2 × tolerance` to cover both expansions.
- **Escape hatch:** `SnapPoint` checks the query box against the region and, if it is not contained,
  discards the region, rebuilds over the whole mesh and continues (`RegionWasAbandoned` records it). A
  wrong region therefore costs speed, never geometry. Nothing currently triggers it — the region is
  derived from the queries — but it is what makes the clipping safe to extend to an estimated envelope
  later.

Applied at all three call sites: `PadGrader.Topology.cs`, `PathGrader.Constraints.cs`, and
`TerrainBuildService.PrepareWallConstraintsForRemesh`. Also fixed at the wall site, while in there, the
`TryExtractMeshData` + `mesh.Vertices.Count` pairing CLAUDE.md warns about — one of the ~10 known sites,
now taking the counts from the extraction.

Not done from that finding: remediation 2 (match cell size to mesh edge length) and remediation 3 (share
one index across a build's grading stages). Clipping should subsume most of remediation 3's value, since
a clipped index is cheap enough that sharing it matters less; revisit only with a measurement.

Verified: `MoleHill.Core.Tests` 980 passed; `MoleHill.Rhino.Tests` 722 passed / 130 skipped; compiles.
New `ConstraintCoincidenceSnapperRegionTests` (6 tests) covers clipped-vs-full equality for a local
constraint and for one spanning the whole mesh, the index-size reduction, the out-of-region rebuild, and
both null-region cases. **Unmeasured:** no trace confirming the 0.91 s actually goes away. That is the
next thing owed, and it needs a Rhino session.

### 4 — Cache the wall plan independently of the upstream terrain (Step 1)

Plan item: "The outer wall-stage cache includes the upstream mesh fingerprint. An upstream Z edit can
miss the whole stage and repeat rail planning even when the wall curves are unchanged. Planning is
independent of the terrain mesh and should have its own cache."

Implemented: `TerrainRuntimeCache.RetainingWallPlanEntries`, keyed by stage key, holding a
`RetainingWallPlanCacheEntry` (fingerprint, `BuiltSolids`, the `PlanResult`).
`ComputeRetainingWallPlanFingerprint` covers exactly the planner's inputs — the wall-curve source set,
max wall width, wall tolerance, rail cleanup tolerance — and deliberately **not** the upstream mesh, which
is the entire point. The stage still misses on an upstream Z edit and still re-runs insertion; it just no
longer re-plans rails that did not change.

Lifecycle handled alongside the existing dictionaries: `CreateWorkerCopy`, `ReplaceBuildCachesFrom`,
`DetachMeshOutputs`, and both branches of `PruneUnused`. Entries are shared by reference exactly as
`SmoothEntries` and `GradingTopologyEntries` are, and the wall stage key is already registered in
`usedStageKeys` on every build (`TerrainBuildService.cs:67`) whether or not the stage cache hit, so the
plan entry is not pruned out from under a cached stage.

**Preview only, on purpose.** A plan built with solids carries `Brep`s — native objects whose lifetime
this cache does not own — and serving one instance to two builds' `AuxiliaryObjects` is a disposal
question with no good answer here. Final re-plans; preview, the path the interactive program is about,
reuses. `BuiltSolids` is stored and checked rather than assumed, so an entry that somehow carries solids
is refused rather than served.

Verified: compiles; `MoleHill.Rhino.Tests` 726 passed / 130 skipped, including 4 new
`TerrainRetainingWallPlanCacheTests` covering worker copy, merge-back (including that stale main-cache
entries are dropped), prefix-scoped pruning, and detach. **Unmeasured and not live-verified:** whether a
real upstream Z edit now reports "cache hit" on the Retaining Wall Plan timing row. That is a one-line
check in a Rhino session and is the first thing to do when one is available.

### 5 — Finding: `TerrainBuildMode.Preview` is never dispatched, and what that cost entries 1 and 4

While sizing Step 2 I traced every producer of a build request. There are exactly two calls to
`QueuePendingBuild` — `TerrainController.Build.cs:43` (the debounced document-edit path) and
`TerrainController.cs:749` (the immediate path) — **and both pass `TerrainBuildMode.Final`.** Nothing in
`src/` ever queues a `Preview` build. The mode is read all over the build service, the slow-build
warnings, the display state and the tests, but in a running Rhino session every build is Final.

This is not a small bookkeeping detail for this plan:

- **It changes Step 2's design.** The plan's primary deliverable is "within an active session, a new
  sample must replace the pending sample rather than cancelling the running evaluation". The obvious
  narrow version — coalesce previews, keep cancel-on-request for finals — **does nothing**, because the
  gesture path *is* the final path. And coalescing Final is not a free choice: publishing a superseded
  Final result writes authoritative state and `LastBuildUtc` for a sample the document has already moved
  past, which is precisely what this plan's own release gate forbids ("Exact state cannot be overwritten
  by obsolete previews"). Step 2 therefore needs an interactive mode that actually gets dispatched — the
  session is not optional scaffolding around the scheduling change, it is the thing that makes a
  coalesceable request exist. Recorded rather than guessed at: I did not implement a scheduling change
  on top of this.
- **It makes entry 1 inert today.** Preview skipping wall solids is correct and is what the interactive
  path will need, but with no preview dispatch, `buildSolids` is true on every build in a live session,
  so there is no saving to measure yet. The earlier claim of a preview saving was premature.
- **It made entry 4 dead code**, which is repaired below.

Repair to entry 4: the plan cache now serves **any** mode, with `BuiltSolids` part of the cache match
rather than a refusal. The Brep-ownership objection that drove the preview-only restriction turned out to
be already solved in this codebase: the stage cache holds canonical geometry and hands out
`Geometry.Duplicate()` copies (`TerrainRuntimeCacheCloner.CloneGeneratedObject`). The wall stage now does
the same — a plan served from cache publishes `wall.Brep.DuplicateBrep()`, a freshly planned one
publishes its own instance. So a Final build after an upstream Z edit reuses its rail plan, which is the
case the finding was about in the first place.

Verified: compiles; `MoleHill.Rhino.Tests` 726 passed / 130 skipped. **Still unmeasured**, and now clearly
the most valuable next measurement: a Rhino session showing "Retaining Wall Plan … cache hit" after an
upstream-only edit.

### 6 — Documentation pass for entries 1-5

`docs/architecture.md` gained the wall-planning cache, the `buildSolids` split and the stage's
cancellation checkpoints in its Retaining Wall section, and a marked "Addressed 2026-09-19 (first bullet
only, unmeasured)" note under the grading constraint-index measurements — the other two bullets there
(cell size, cross-stage sharing) are explicitly still open. `src/MoleHill.Core/Grading/README.md`
documents `ConstraintCoincidenceSnapper` and the build-it-after-the-constraints rule.
`docs/file-index.md` regenerated; both changed entries are better than before, because
`TerrainRuntimeCache` had no type-level summary and the generator was quoting an unrelated property
comment. It has one now.

No source file was added, removed or renamed, so nothing else in the index moved.

### What this pass did not do

Recorded so the next pass does not have to rediscover it:

- **Nothing here is live-verified.** Entry 7 records the attempt: the slot spawned and registered the
  build, but every `run_csharp`/`run_python` call failed. Entry 3 is measured by micro-benchmark; entries
  1, 2, 4 and 5 rest on unit tests and on reading the call sites. The highest-value next action is a
  Rhino session on the geometry-heavy fixture checking two timing rows: `PadGrader.CreateConstraints`
  (entry 3) and `Retaining Wall Plan … cache hit` after an upstream-only edit (entries 4-5).
- **Step 2 is untouched**, deliberately — see entry 5. `RequestRebuild` still cancels the running build
  on every request.
- Step 1's "reuse mesh extraction and index preparation within an evaluation" is only partly done: the
  snapper no longer indexes the whole mesh, but grading stages still each extract mesh data and build
  their own `TerrainFaceGrid`.
- Remediations 2 and 3 from the grading finding (cell size, one shared index per build) are not
  attempted and should not be, until entry 3 is measured — clipping may already have taken most of it.

### 7 — Measuring entry 3, and a failed attempt at live verification

**Live verification was attempted and did not work.** Rhino was closed by this point, the full solution
built clean (`validate.ps1 managed` and `warnings` both green, 0 owned-code warnings), and a disposable
`rhino-mcp` slot spawned fine — `get_commands` listed `mhLatencyTrace`, so the build registered. But
**every `run_csharp` and `run_python` call failed**, on two separately spawned slots, while `run_command`
and `get_context` worked. Without the script host there is no way to build a fixture or read a trace, so
the slot was closed and no live claim is made. If this recurs, `docs/rhino-live-testing.md` §2 describes
the `tools/rhino-live-client.py` fallback, which was written for spawn failures and is untried against a
script-host failure.

**So entry 3 was measured directly instead**, as an opt-in benchmark
(`ConstraintCoincidenceSnapperScalingBenchmarkTests`, `MOLEHILL_PERF=1`, using the existing
`PerformanceLane` gate). A 250×250 grid gives 63,001 vertices / 188,000 unique edges, within a couple of
percent of the traced fixture's 62,500 / 186,501:

| Case | Whole-mesh index | Clipped index |
|---|---|---|
| 188,000 edges, pad over 8% of the terrain | 1,138.0 ms (188,000 edges) | **8.4 ms** (1,408 edges) |
| 30,200 edges, pad over 8% | 87.9 ms | 1.4 ms |
| 188,000 edges, pad over **90%** of the terrain | 1,138.0 ms | 851.6 ms |

Two things worth keeping:

- **The baseline reproduces the trace.** 1,138 ms here for 188,000 edges against the traced 0.91 s for
  186,501 — so this micro-benchmark is measuring the same cost the end-to-end trace attributed to this
  constructor, and the ~135× reduction on the realistic case is not measuring something else.
- **The degenerate case does not regress.** A pad covering 90% of the terrain still indexes less than
  the whole mesh and costs less, so clipping has no case where it loses. That was the main risk of
  adding a filter to a hot loop and it is now checked rather than assumed.

What this still is **not**: an end-to-end latency measurement. It says the 0.91 s constructor becomes
milliseconds; it does not say what the 7.0 s build becomes, because the rest of
`PadGrader.CreateConstraints` and the stages around it are unchanged and unmeasured here. The live trace
remains owed.

### 8 — Entry 3 measured A/B, and a working route into a live Rhino

**The script host does work — through `_-RunPythonScript`, not `run_csharp`.** `run_csharp` and
`run_python` still fail on a spawned slot, but `run_command` does not, so a `.py` file on disk driven by
`_-RunPythonScript` gives a full scripting host; have the script write its results to a file and read
that file directly. Confirmed against slot `aardvark`: the loaded plugin is the exact build at
`src/MoleHill.Rhino/bin/Debug/net7.0/MoleHill.Rhino.rhp`. This supersedes entry 7's "live verification is
blocked" — it is not, and `docs/rhino-live-testing.md` should carry this route.

**Entry 3 now has a true A/B**, `PadGraderCreateConstraintsBenchmarkTests` (`MOLEHILL_PERF=1`), on one
fixture of 63,001 vertices / 125,000 faces / 188,000 edges with one pad over ~8% of the terrain — run
once with the clipped index and once with the call site reverted to the whole-mesh index, so the two
numbers differ only in the change:

| `PadGrader.CreateConstraints` | Time |
|---|---|
| Whole-mesh index (pre-change) | 899.8 ms |
| Constraint-clipped index | **345.6 ms** |

**554 ms removed, a 2.6× reduction of the method the trace named.** Note the shape: the index was 91% of
the constructor but the constructor was not all of `CreateConstraints`, so the method does not fall by
the index's 135×. The remaining 345 ms is the rest of constraint construction and is **not** attributed
yet — that is the next thing to drill into if grading is to be interactive, and it is now the dominant
term where the index used to be.

Comparability caveat, stated rather than buried: the pre-change figure here is 899.8 ms against the
live trace's 1,280 ms for the same method. Same machine, but this synthetic fixture produces 2
constraints where the traced pad produced 3, and has no lock curves. The A/B is internally valid; the
absolute numbers are not the traced build's.
