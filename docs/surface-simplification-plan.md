# B6 — Surface simplification with a stated error bound

Status: implemented and natively validated, 2026-09-14. Slices 1–3 and 5 are complete, with
deterministic 180k- and 1M-face checkpoints, a captured production grading case, and disposable Rhino
verification of every mode plus the constrained Grade Pad stack. Complete workflow timings and the
2M-input-vertex end-to-end checkpoint remain release-scale characterization work. Accidental icon
generator outputs were restored; only the intended Simplify asset and recipe remain changed.
Backlog: [B6](backlog.md#b6--surface-simplification-with-a-stated-error-bound).

## 1. Outcome and scope

Add **Simplify**, a terrain modifier that reduces mesh density while preserving the incoming
surface within a requested maximum vertical deviation. It belongs early in the stack, usually
immediately after Triangulate. Its parameters are the design; it takes no new geometry sources.

The motivating workload is a terrain derived from millions of survey or DEM points. A request such
as “retain at most 50,000 vertices” is useful, but is not a promise that every terrain can meet that
count at a given accuracy. Required constraints and measured error decide what is achievable.

Rhino now delivers tolerance, target-count, and percentage modes on the same Core contract.
Grasshopper exposure belongs to B7.

This modifier operates on an already built mesh. It can speed up downstream grading, analyses and
display, but cannot remove the initial triangulation cost or its peak memory. Reducing raw input
before Triangulate would be a separate pipeline change.

## 2. What exists, and what must change

`Core/Interop/ToposolidPointReducer.cs` already seeds mandatory points and spatial extrema, then
adds points with large reconstruction errors. `Grasshopper/Utilities/ToposolidPreparation.cs` uses
it to prepare elevation samples for another application's surface builder.

That implementation is useful algorithmic groundwork, but is not a general surface simplifier:

- Its input and output are points; it does not return the mesh whose accuracy it measured.
- Reconstruction uses an unconstrained convex hull, without boundary loops or breakline segments.
- Retaining breakline points does not guarantee retaining the edges between them.
- Error is sampled at omitted source points. Missed height projections are skipped, so missing
  coverage can appear to have no error.
- Ten refinement rounds are a work limit, not convergence. After the last insertion the returned
  point set can differ from the set used for the reported error.
- Success means it produced points; it does not distinguish meeting tolerance from reaching a cap.

Keep the existing Toposolid API and behavior stable during the first implementation. Extract shared
helpers only where their contracts actually match. Migrating Toposolid to a new reducer is a later,
explicit compatibility change: its downstream triangulation is outside MoleHill's control.

## 3. Product contract

### Error and reference

Maximum deviation means `max |Zout(x,y) - Zin(x,y)|` over the incoming terrain's complete XY domain.
The reference is the mesh immediately before this modifier. It is not the original survey points,
the base triangulation, or another terrain. The guarantee applies to this stage's output; later
modifiers may change it, and successive simplifications can accumulate error.

The domain must remain the same, including holes and disconnected islands. A coverage gap fails
verification; it never contributes zero error. The mesh is an open terrain surface: watertight here
means no unintended internal cracks, missing faces or non-manifold seams, not a closed solid.

### Modes

| Mode | User request | Acceptance |
|---|---|---|
| Maximum deviation | Maximum vertical deviation, in model length units | Return a verified reduced mesh within the bound; otherwise retain the input with a reason |
| Target vertex count | At most N output vertices | Return a verified mesh at or below N and report its achieved error; fail cleanly if mandatory geometry alone exceeds N |
| Retain percentage | Retain P% of the incoming mesh's used vertices | Convert to a vertex cap, then use the count contract |

Count modes do not imply an error tolerance. A combined cap-and-tolerance mode can wait for a user
need; adding it now obscures which requirement wins. Count includes all output vertices, including
mandatory and triangulator-inserted vertices. Define percentage rounding explicitly as floor, with
validation against the minimum valid mesh and mandatory set. At 100%, return the input unchanged.

Tolerance is nonnegative; zero requests exact preservation within documented numerical precision.
Keep numerical geometry tolerance separate from the design tolerance and report both where needed.
Never inflate the requested tolerance silently. Select a physical default only after the benchmark
fixtures establish a sensible value, then convert it through the existing model-unit contract.

### Constraints are always protected

There is no “preserve constraints” switch. Protect boundary segments and the
effective incoming persistent hard/elevation constraints, including wall rails and Grade Path edges.
Preserve their XYZ geometry and segment connectivity; retaining endpoints alone is insufficient.
For v1, retain all vertices belonging to these required chains, accepting that dense boundaries can
limit reduction. Ordinary untagged creases may simplify within the error bound.

Resolve constraints against the incoming stage, including any legitimate upstream elevation changes.
If constraint geometry conflicts with the incoming surface, diagnose it rather than restoring old Z
values or silently altering the design. Metadata and geometry must remain coherent for later stages.

True stacked-XY/vertical geometry has no single-valued height field. Initially leave such an input
unchanged with a diagnostic. Near-vertical but valid height fields require explicit coverage and
constraint tests. Supporting mixed vertical patches by preserving them separately is a later extension
if real scenes require it; do not collapse them into an arbitrary height.

## 4. Core design and the proof obligation

Proposed files under `Core/Processing`:

- `SurfaceDeviationEvaluator.cs`: compare two piecewise-linear terrain surfaces over an equal domain.
- `SurfaceSimplifier.cs`: construct, refine and validate constrained reduced meshes.
- Separate options/result types only if their size warrants dedicated files.

The API accepts flat XYZ vertices, triangle indices, resolved constraints, design and numerical
tolerances, and cancellation. It returns the exact accepted mesh plus input/output counts, protected
count, maximum deviation, coverage status, and a termination reason. Candidate rejection and
cancellation must not mutate input arrays or leave an apparently successful partial result.

### Verify the surface, not just its original vertices

Two different triangulations can agree at every original vertex and disagree where their edges cross.
For triangular height fields, their height difference is affine on each cell of the XY overlay. Its
maximum absolute value therefore occurs at an overlay vertex: a vertex of either mesh within the
other's domain, or an edge intersection. Those are the required comparison locations.

Implement an indexed, streaming traversal of overlapping triangle pairs, clipping their XY triangles
and evaluating both planes at the intersection polygon vertices. Handle coincident edges, degeneracy,
large coordinates and scale-aware tolerances explicitly. Verify domain coverage separately; a maximum
over only overlapping faces is insufficient. Record the worst location as a refinement witness.

Reuse existing face indexing and geometric predicates where suitable. Do not materialize a complete
overlay mesh or retain an object for every face pair. Worst-case overlap work can still be large:
measure it and support cancellation. Source-vertex sampling may rank candidates cheaply, but cannot
certify the final result. Build a small brute-force reference comparator for test fixtures.

### Reduction loop

1. Validate the input domain and constraint chains; establish mandatory vertices and segments.
2. Seed mandatory geometry and a deterministic spatial-extrema sample of interior points.
3. Build a constrained triangulation over the exact domain with quality refinement disabled unless
   required for validity. Preserve holes/islands and required segment geometry.
4. Measure actual candidate error and coverage. If tolerance mode passes and topology is valid,
   return this mesh and these measurements.
5. Insert deterministic batches of high-error samples/witnesses at incoming-surface elevations,
   rebuild, and verify again. An edge-crossing witness may require a new point, not just an original
   vertex. Account for all inserted vertices in the result.
6. Stop on success, cancellation, no progress or a documented resource limit. Re-measure after every
   final mutation. Never report a previous candidate's error against a newer mesh.

In tolerance mode the original valid mesh is the fallback and establishes zero change. If no smaller
verified mesh is found, return it with “no reduction achieved” or the specific limitation. A candidate
that grows beyond the input is not an improvement. Do not assume error decreases monotonically after
each insertion: retriangulation changes edges. Count mode needs its own tested stopping/selection
policy before it ships, retaining the best verified candidate that satisfies the cap.

Use `IndexedMeshTools.EdgeKeyComparer.Instance` for every packed-edge dictionary/set. Prefer flat
arrays and CSR adjacency; bound scratch memory and avoid repeatedly sorting all source samples.
Keep deterministic tie ordering for identical input and options.

## 5. Rhino integration

Add `SimplifyModifierDefinition`, `SimplifyModifierDescriptor` and a
`TerrainBuildService.Simplify.cs` partial, dispatched through `RunSimplifyStage`. Follow the existing
modifier registry, schema rows, serialization discovery and mesh-stage caching conventions.

The card exposes Maximum Deviation as `ParameterUnit.ModelLength`; mode-specific rows use `None` for
integer counts and `Percent` for retained percentage. Use normal descriptor rendering.
Add an icon through the established asset/generator workflow.

Read effective constraints using the existing remesh/build constraint path. Include constraint state,
incoming mesh, options and algorithm version in the cache key. Preserve constraint metadata through
the stage and exercise downstream grading/remeshing against the simplified mesh.

The triangulation baseline remains unchanged, as with other modifiers. Consequently an Earthworks
comparison against that baseline may measure a small volume difference from simplification; a vertical
error bound does not promise volume conservation. Make this behavior explicit in help and tests.
Outer/Hide/Show boundaries still run at their existing post-modifier stage. Preserve the incoming
mesh's actual perimeter; do not move boundary-role processing earlier as part of B6.

Report before/after vertex and face counts, achieved maximum deviation and termination reason. These
are runtime results. Choose storage alongside existing modifier diagnostics/cache results and ensure
cache hits restore the same information. Do not add an analysis or annotation definition for them.

Preview and final builds must honor the same design tolerance. Use the existing deferred-build and
cancellation mechanisms for expensive work, with no silently relaxed preview bound. Verify unit
scaling, undo/redo, duplicate, save/reopen, disabled modifiers and stack reordering.

## 6. Staged delivery

| Slice | Scope | Done when |
|---|---|---|
| 1 — next bite | Core surface-deviation evaluator and small fixtures | Detects error at crossing edges, missing coverage and holes; agrees with a brute-force comparator; records baseline timing |
| 2 | Core tolerance-mode simplifier | Returns a smaller certified mesh on planar/rolling fixtures, preserves required chains and topology, and falls back honestly on unsupported or stalled cases |
| 3 | Rhino tolerance-mode modifier | Card, units, serialization, cache results and pipeline integration work; plugin builds and disposable Rhino live checks pass |
| 4 | Scale and release validation | Representative large cases have reproducible timings/memory and verified error; docs describe actual behavior and limits |
| 5 | Count/percentage modes | Mandatory-set conflicts, inserted-point accounting, deterministic stopping and achieved-error reporting are covered end to end |

Slice 1 is independently reviewable and establishes whether the advertised bound is feasible at scale.
If the verifier proves too expensive, revisit the algorithm or explicitly revise the product contract
before implementing the card. Do not ship sample-only error under the name “maximum deviation”.

### Slice 1 outcome (2026-09-14)

`SurfaceDeviationEvaluator` now performs the indexed streaming triangle-overlay comparison. It clips
overlapping XY triangle pairs, evaluates both affine height fields at every resulting overlay vertex,
records the worst witness, compares overlap against both projected domain areas, rejects invalid or
degenerate input, and observes cancellation without returning a partial result. The fixtures cover the
opposite-diagonal crossing-edge maximum, missing candidate coverage, matching and filled holes, a dense
independent sampling comparison, large translated coordinates, non-finite input and cancellation.

The deterministic rolling-grid release checkpoint uses 90,601 vertices / 180,000 faces per mesh with
opposite diagonals. On the current Intel64 Family 6 Model 183 development host, a Debug test run tested
3,225,616 candidate face pairs in 2,543.9 ms, allocated 20,709,384 bytes, measured a maximum deviation
of 0.0024175608071281829, and verified equal coverage. Re-run with
`MOLEHILL_PERF=1 dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --filter
FullyQualifiedName~SurfaceDeviationEvaluatorBenchmarkTests` before drawing release-scale conclusions.

### Slice 2 outcome (2026-09-14)

`SurfaceSimplifier` now validates a single-valued 2.5D input, protects every naked boundary edge and
resolved required segment, seeds deterministic spatial Z extrema, builds constrained candidates with
quality insertion disabled, clips candidates back to the exact source domain, and certifies each round
with `SurfaceDeviationEvaluator`. Refinement inserts the worst overlay witness and deterministic batches
of high-error source samples. It returns the exact measured candidate only when it is smaller and within
the requested bound; unsupported topology, failed coverage, stalled/resource-limited refinement, and
non-reducing candidates return the unchanged input with an explicit termination reason. Cancellation
throws without publishing a partial result. Core fixtures cover planar and rolling grids, required
chains, exact-tolerance fallback, holes, disconnected islands, non-manifold/degenerate 2.5D input,
fully mandatory meshes, immutability, and cancellation.

### Slice 3 outcome (2026-09-14)

Rhino now discovers `SimplifyModifierDefinition`/`SimplifyModifierDescriptor` through the ordinary
registry and JSON resolver. The card exposes Maximum Deviation as a model-length value, scales with
document units, and reports the incoming-stage reference, constraint behavior, honest fallback, and
earthworks limitation in its help. `TerrainBuildService.Simplify` maps effective persistent hard and
elevation constraints to actual incoming mesh edges, rejects stale or Z-conflicting metadata, preserves
the mesh on failure, and uses a versioned mesh/option/constraint stage fingerprint. Diagnostics include
input/output counts, measured error, rounds, and termination; stage-cache hits restore them.

Final automated validation passed 850 Core tests, 637 Rhino tests, and 35 Grasshopper tests; 113
Rhino-native and 14 Grasshopper-native tests were skipped by the test host because the native runtime
was unavailable. The Rhino plug-in project also built cleanly and produced the `.rhp`.

A disposable Rhino 8.34 smoke test ran through a host that permits process breakaway. It verified the
exact Debug `.rhp` path and MVID, the ordinary Simplify card and all three mode-specific rows, tolerance,
target-count and percentage builds, diagnostics, cache-hit restoration, boundary retention, and sampled
domain/error checks. A 1,600-vertex rolling TIN reduced to 1,125 vertices at a 0.25 m bound (independent
22,801-ray maximum 0.2428 m, no coverage misses); both 400-vertex count requests returned exactly 400.
The slot was closed and no Rhino process remained.

That smoke test also found Grade Pad was republishing temporary zero-Z shoulder/stitch construction
loops as persistent elevation constraints even though its completed mesh had replaced or graded those
loops. This made every downstream Simplify reject the stage's own metadata. The host now persists only
Grade Pad's actual graded `OutputPolylines` as hard constraints; a native Grade Pad → Simplify regression
test pins the contract.

The first constrained recheck confirmed that metadata fix (`hard=1`, `elev=0`, with all 24 pad-boundary
edges resolved) and exposed a second preflight issue: the evaluator treated numerical/model tolerance as
a minimum face altitude. Two valid explicit-batter fan faces narrower than 0.01 m therefore made the
1,567-vertex graded mesh `UnsupportedInput` before simplification began. Face degeneracy now uses an
adaptive robust orientation predicate independent of model tolerance. A regression reproduces the
reported 6.998 mm-wide, 0.01808 m² batter triangle at 0.01 m numerical tolerance. The constrained live
recheck against the rebuilt evaluator passed. On the same cold 1,600-point rolling-grid scene, Grade Pad
produced 1,567 vertices / 2,976 faces and Simplify reduced it to 1,020 vertices / 1,882 faces in 10 rounds
and 0.79 s, terminating `ToleranceSatisfied`. It protected all 156 domain-boundary vertices plus the 24
pad-boundary vertices; the hard constraint resolved to all 24 required edges on the simplified mesh, and
both runtime stages reported `hard=1`, `elev=0`. Across 22,801 independent vertical rays, the maximum
deviation was 0.2418 m against the 0.25 m design bound, with no input-only hits, output-only hits, or
both-miss samples. The result remained valid, one-component terrain with its single outer loop intact.
The exact Debug `.rhp` and colocated `MoleHill.Core.dll` were verified by path and MVID before the run;
the disposable slot was closed and no Rhino process remained afterward.

### First scale checkpoint (2026-09-14)

The opt-in end-to-end analytic rolling-grid benchmark uses seed `analytic-300`: 90,601 input vertices,
180,000 input faces, no authored interior constraints, and 1,200 protected boundary vertices. In Debug
on the same host, a 0.05-unit design tolerance reduced it in 10 rounds to 32,068 vertices / 62,934 faces,
with certified maximum deviation 0.048979846398014892 and equal coverage. Against the current topology
gate, total time was 24,528.3 ms, current-thread managed allocation was 780,054,000 bytes, endpoint
process-private memory delta was 68,894,720 bytes, and the 10 ms sampler measured an 88,170,496-byte
peak process-private increase. Of the measured time, constraint preparation used 72.0 ms,
triangulation 1,077.6 ms, full verification 18,297.9 ms, and refinement 4,829.7 ms. The first
run exposed that 12.5% refinement batches exhausted the round cap; deterministic 50% geometric growth
reduced the same case successfully without changing acceptance rules. Re-run with
`MOLEHILL_PERF=1 dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --filter
FullyQualifiedName~SurfaceSimplifierBenchmarkTests`.

The same 180,000-face fixture with a 300-segment mandatory diagonal (`analytic-300-diagonal`) protected
1,499 vertices and reduced to 27,607 vertices / 54,012 faces in 10 rounds. Certified maximum deviation
was 0.048587204061092737 with equal coverage; Debug time was 22,944.3 ms, current-thread managed
allocation was 808,180,856 bytes, endpoint process-private memory grew by 108,445,696 bytes, and the
sampled peak increase was 108,666,880 bytes. Constraint preparation used 64.1 ms, triangulation
1,103.4 ms, full verification 16,964.8 ms, and refinement 4,602.9 ms. This establishes the first
dense-chain constraint checkpoint, though it is not a substitute for a captured production grading scene.

The captured `TerrainGradePathAfterProtectedPadsCopiedCase.json` production grading fixture exercises
the real grading pipeline before simplification. Its complete historical constraint set correctly
fails coherence validation because some source rails are stale after grading. After retaining the 256
segments that still lie on the built surface, a 5.0-unit tolerance reduced 4,928 vertices / 9,793 faces
to 1,221 vertices / 2,379 faces in eight rounds and 1,537.0 ms in Debug. Independent verification
measured 4.9146258927313671 maximum vertical deviation with equal coverage; a separate edge walk also
confirmed every required segment endpoint and edge in the returned mesh. This fixture pins both honest
stale-metadata rejection and preservation of coherent production constraints.

The configurable evaluator benchmark also passed the larger deterministic checkpoints. At grid size
707 it compared 501,264 vertices / 999,698 faces per mesh, tested 17,960,644 triangle pairs in
21,530.7 ms, allocated 113,815,424 bytes, and preserved equal coverage. At grid size 1,413—the
motivating approximately two-million-input-vertex scale—it compared 1,999,396 vertices / 3,993,138
faces per mesh, tested 71,808,676 pairs in 153,972.2 ms, allocated 438,343,976 bytes, and preserved
equal coverage. Both measured the expected 0.0024175608071281829 crossing-diagonal deviation. Set
`MOLEHILL_SURFACE_GRID_SIZE=707` or `1413` alongside `MOLEHILL_PERF=1` to reproduce those runs.

The 1M-face end-to-end tolerance checkpoint (`MOLEHILL_SIMPLIFIER_GRID_SIZE=707`) used 501,264 vertices /
999,698 faces and reduced them to 245,966 vertices / 489,102 faces in 14 rounds. It protected 2,828
boundary vertices and certified 0.049969886458358204 maximum deviation with equal coverage. Debug time
was 330,803.8 ms, current-thread managed allocation was 7,872,358,680 bytes, endpoint process-private
memory grew by 386,662,400 bytes, and the 10 ms sampler measured a 683,921,408-byte peak increase.
Constraint preparation used 351.7 ms, triangulation 11,428.0 ms, full verification 249,612.2 ms, and
refinement 67,030.7 ms. Verification is the dominant measured cost. The 2M-input-vertex end-to-end run
and complete initial Triangulate/downstream workflow timings remain open release-validation work; the
opt-in benchmark supports the larger grid but it was not run on this host because the measured 1M case
already took 5 minutes 31 seconds and peaked about 684 MB above baseline. Native cold graded
simplification measured 0.79 s on the 1,567-vertex input, and the earlier ungraded smoke test verified
cache-hit restoration.

### Slice 5 outcome (2026-09-14)

Core count mode now validates a minimum three-vertex target, rejects a cap smaller than the complete
mandatory set with `MandatorySetExceedsTarget`, and keeps the best fully verified deterministic
candidate at or below the cap. Refinement is bounded by the requested selected-point count, accounts
for the triangulator's actual output count, retains a prior verified candidate if a later rebuild grows
or fails, and re-reports the exact returned candidate's measured error and total rounds. A target at or
above the incoming used count returns the input unchanged. Tests pin cap compliance, mandatory-set
conflicts, determinism, 100%/input-count behavior, independent achieved-error verification, and a
last-round case that would expose stale measurements.

Rhino persists a three-way Mode plus Target Vertex Count and Retain Percentage. The generic schema shows
only the active row with units `ModelLength`, `None`, or `Percent`. Percentage conversion uses
`floor(used vertices × percentage / 100)`; out-of-range values are rejected, and the resulting cap uses
the Core count contract. Serialization, schema guards, unit scaling, floor rounding, diagnostics, and
native integration test definitions cover the three modes; the native cases remain skipped on this host
for the runtime-launch reason recorded above.

The 180,000-face count-mode scale checkpoint requested exactly 50,000 vertices and returned 50,000
vertices / 98,798 faces after 11 rounds. It reported achieved maximum deviation
0.015740248857076589 with equal coverage; Debug time was 26,623.7 ms, current-thread managed allocation
was 1,181,922,088 bytes, endpoint process-private delta was 93,163,520 bytes, and the sampled peak
increase was 94,273,536 bytes. Constraint preparation used 69.7 ms, triangulation 1,543.0 ms, full
verification 19,778.0 ms, and refinement 4,983.5 ms.
This pins actual-output
count accounting at scale; as above, the endpoint delta is not a peak-memory measurement.

## 7. Verification and benchmarks

Core fixtures must cover planar terrain, rolling ground, an isolated peak/pit, opposite diagonals of a
non-coplanar quad, concave outlines, holes, islands, boundary breakline intersections, dense mandatory
chains, near-vertical rails, stacked XY, invalid/non-finite input and cancellation. Assert domain
equality, required XYZ/edge preservation and no single-use interior edges, in addition to numerical
error. Run scaled copies in metre/millimetre equivalents and translated copies at large coordinates.

Include a case where source-point checks miss the maximum, a case where projection coverage fails,
and a last-round insertion case that would expose stale error reporting. Validate the returned mesh
with an independent small-fixture evaluator rather than only calling its production verifier again.

Rhino coverage includes serialization, model-unit scaling, schema guards, constraint-only invalidation,
cache-hit diagnostics, baseline behavior and a Simplify-to-grading/remesh sequence. Build the Rhino
plugin explicitly: test projects do not compile the panel. Follow
[rhino-live-testing.md](rhino-live-testing.md) for a disposable slot, exact plugin identity, saved-state
round trip and document-state assertions. UI captures support, rather than replace, those checks.

Benchmark a deterministic rolling grid, a real survey/DEM-derived terrain, and a constrained graded
scene at approximately 180k faces, 1M faces, and the motivating 2M input vertices. Keep counts labelled
as vertices or faces. Record source fixture/seed, options, hardware, input/output sizes, mandatory count,
error, coverage, rounds, termination reason, stage timings, allocations and peak process memory.
Separate constraint preparation, triangulation and verification costs. Include initial Triangulate
time and downstream timings so claimed savings reflect the complete workflow.

Use opt-in benchmark tests alongside the existing large-terrain benchmarks. Compare cold builds and
cache hits; do not promise a time or reduction ratio before measuring the constrained cases.

## 8. Documentation and completion

Each implementation slice updates its relevant folder README and regenerates `docs/file-index.md`
when source files change. At host integration, update `docs/architecture.md` with the stage contract,
error reference, constraints and baseline behavior. Update this plan with measured outcomes and
decisions, and close B6 in `docs/backlog.md` only when its delivered modes are clearly recorded.

Outside this plan: raw point import/reduction before triangulation, automatic terrain repair, volume
conservation, new boundary semantics, user-authored constraint inputs, Toposolid API migration and
Grasshopper components. Each can use the resulting Core work through a separately scoped change.
