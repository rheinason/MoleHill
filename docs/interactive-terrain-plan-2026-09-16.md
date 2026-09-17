# Interactive terrain plan — 2026-09-16

Status: proposed architecture and implementation sequence, revised after inspecting the current code.
No new performance measurements or realtime implementation are claimed by this document.

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
| 0 | Capture fixtures and instrument complete interactions | Baseline traces, exact references, native-input feasibility |
| 1 | Remove wall-specific redundant preparation | Preview makes no wall solids; unchanged plans survive upstream-only edits |
| 2 | Implement minimal prepared session and bounded scheduler | Sustained input advances previews; undo/interruption reject stale work |
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
