# Performance optimization routes — 2026-07-30

## Executive summary

The strongest measured opportunity remains the TIN boundary-peel path, but the original benchmark
overstated the production-shaped cost by including an extra topology construction. Triangle.NET
already retains triangle-to-triangle adjacency, so an adjacency-aware peel should be compared with a
new packed-edge representation before selecting the implementation.

Grade Path CPU and allocation reduction is the second clear target. Its route should include not only
daylight face search, but also closest-path-segment lookup and allocation churn in the terrain
conform splitter.

Recommended order:

1. Benchmark and implement a production-shaped TIN peel using native Triangle.NET adjacency or a
   measured packed-edge fallback.
2. Reduce Grade Path conform-split allocation churn, which now measures as its largest CPU and
   allocation phase.
3. Spatialize Grade Path and Pad daylight searches, closest-segment lookup, and corridor
   preprocessing.
4. Replace repeated dictionary-based topology validation with a shared flat topology primitive.
5. Apply the low-risk cache trims, then make larger cache changes only if Rhino measurements justify
   them.
6. Revisit isotropic-remesher connectivity only after the higher-impact routes are measured.

The original review made no source changes. The follow-up measurement work described at the end of
this document adds benchmark and opt-in diagnostic instrumentation only; it does not change production
geometry behaviour.

Implementation work is recorded route-by-route below. Each route is validated and committed before the
next begins.

## Review scope

The review covered:

- the Core TIN, grading, topology-validation, analysis, and remeshing paths;
- the Rhino preview/final build pipeline and stage-cache ownership model;
- the existing large-terrain performance review and its completed optimization work;
- Release-mode runs of the existing large-dataset, Grade Path, height-projector, and remesher
  benchmarks.

The earlier analysis projection, slope-summary, retaining-wall planner, stage-level analysis caching,
and cache-hit diagnostics work is already complete. Those items are not repeated here as new
recommendations.

## Measured evidence

### 1.20 million-point TIN dataset

The existing `120K Pointstest.csv` fixture currently contains approximately 1.20 million usable
points.

| Phase | Observed time |
|---|---:|
| CSV load | 536 ms |
| Point deduplication | 315 ms |
| Bare Dwyer triangulation | 1,631 ms |
| Raw Triangle.NET triangulation | 1,863–2,127 ms |
| Triangle.NET extraction | 512 ms |
| Z lookup | 8 ms |
| Initial edge topology | 483 ms |
| Boundary culling | 1,782 ms |
| Post-cull edge topology | 238 ms |
| Full `TinEngine.Build` | 4,428 ms |

The full build produced approximately 1,201,507 vertices and 2,402,475 faces. Boundary peeling
removed only a small number of faces, but its topology preparation remained mesh-wide.

The original breakdown requires an important correction. It first builds edge topology explicitly,
then calls `TriangleBoundaryCuller.Cull` with an automatic threshold. That overload internally builds
edge topology again to calculate its median edge-length threshold. `TinEngine.BuildResult` does not:
it computes the threshold from the topology it already built before calling the culler. The measured
`483 + 1,782 ms` therefore includes one topology construction that is not present in the real
production path.

The Rhino TIN path also requests `includeEdgeTopology: false`, which avoids the post-cull topology
build and retained output edge arrays. A production-shaped breakdown must precompute the automatic
threshold from the initial topology, pass a positive threshold to the culler, and omit the post-cull
topology phase.

### Grade Path copied large-terrain case

Three repeated Release-mode runs produced:

| Metric | Observed result |
|---|---:|
| Core time | 763–808 ms |
| Managed allocation delta | approximately 94.84 MB |
| Input shape | 6,341 vertices / 12,411 faces |
| Output shape | 8,410 vertices / 16,547 faces |
| Non-manifold edges | 0 |
| Open boundary chains | false |

The allocation result was effectively identical across repeated runs, so allocation is clearly a
reliable optimization target rather than a one-off GC artifact. The absolute value is not yet a
production total, however:

- the benchmark uses `GC.GetAllocatedBytesForCurrentThread`, while Z application uses
  `Parallel.For`, so worker-thread allocations are omitted; and
- the copied-case wrapper performs a test-only topology validation after `PathGrader.Grade`.

Process-wide allocation should be measured immediately around `PathGrader.Grade`, separately from
fixture construction and test assertions.

### Mesh height projector

For a reference mesh containing 50,625 vertices and 100,352 faces:

| Phase | Observed time |
|---|---:|
| Projector construction | approximately 47 ms normally |
| 100,000 projection queries | 74–77 ms |
| Misses | 0 |

One projector-construction run was a GC-related outlier at 147 ms, while query time remained stable.
The Core `MeshHeightProjector` kernel is already healthy and should not be a near-term optimization
target. This does not cover high-volume Rhino scatter placement, which currently uses
`MeshLineSorted` followed by `ClosestMeshPoint` for every sample.

### Isotropic remesher

| Case | Observed time |
|---|---:|
| 12,800 faces coarsened to 2,693 | 465 ms |
| Coarse field with dense corridor, 1,960 to 19,610 faces | 713 ms |

These are acceptable current results. Remesher connectivity churn is a worthwhile later route, but
the measured opportunity is smaller than TIN and Grade Path.

## Follow-up measurements

The revised benchmarks were run in Release mode after the review corrections. They add opt-in timing
and allocation instrumentation only; geometry output remains on the existing code paths.

### Production-shaped TIN peel

The corrected breakdown precomputes the automatic threshold from the initial topology, passes the
positive threshold into the culler, and does not build post-cull topology:

| Phase | Time | Process-wide allocation |
|---|---:|---:|
| Initial generic edge topology | 483 ms | 86,509,848 bytes |
| Exact median-edge threshold | 314 ms | 28,836,856 bytes |
| Culler total | 1,228 ms | 475,776,784 bytes |
| └ edge dictionaries | 777 ms | 406,099,928 bytes |
| └ initial boundary seed scan | 417 ms | 2,404,592 bytes |
| └ actual peel traversal | 0.8 ms | 0 bytes |
| └ filtering and vertex compaction | 33 ms | 67,271,616 bytes |

Only 472 of approximately 2.40 million faces were removed. Topology, exact-median, and culler
preparation consumed approximately 2.03 seconds and allocated approximately 591.1 million bytes in
total; the actual iterative peel was negligible. This strengthens Route 1 while making its target
more precise: remove the generic topology/dictionary preparation, not the queue algorithm.

### Native adjacency versus sorted key/face arrays

On the same approximately 2.40 million-face Triangle.NET mesh:

| Representation prototype | Time | Allocation | Validation |
|---|---:|---:|---|
| Native triangle references + dense triangle-id map | 240 ms | 28,840,816 bytes | 0 invalid / 0 non-reciprocal links |
| Sorted `long[]` edge keys + parallel incident-face ids | 499 ms | 86,517,248 bytes | 3,604,453 unique / 65 naked edges |

The native figure includes materializing triangle references, building the id map, and scanning all
7.21 million neighbour references with reciprocal-link validation. Extraction already materializes
the triangle references today, so a fused extractor could reuse part of that allocation. Native
adjacency is now the preferred Route 1 prototype; packed generic edge records remain the fallback and
comparison baseline.

### Route 1 implementation result

Route 1 now retains Triangle.NET triangle references during extraction, builds a dense
triangle-id-to-face map when ids are reasonably dense, and validates every neighbour as reciprocal and
edge-sharing. The TIN builder uses the validated graph for unique-edge median traversal and incremental
peel exposure. It falls back to the previous generic topology/dictionary path when validation fails,
while callers requesting edge output still receive the existing generic edge arrays.

On the same 1.20 million-vertex / 2.40 million-face input:

| Phase | Previous path | Native-adjacency path |
|---|---:|---:|
| Culler elapsed | 1,459 ms | 91 ms |
| Culler allocation | 475,776,784 bytes | 72,084,616 bytes |
| Initial boundary scan | 323 ms | 54 ms |
| Actual peel | 0.8 ms | 0.3 ms |
| Compaction | 37 ms | 36 ms |

The native result matched the dictionary result exactly: threshold, changed flag, compacted face
array, old/new vertex map, 1,201,507 output vertices, and 2,402,475 output faces. The native
exact-median pass took 341 ms versus 347 ms for the generic-topology median and allocated the same
28.84 MB length buffer.

Adjacency retention and validation increased extraction from the earlier approximately 554 ms
reference to 704 ms in this run, but removed the 403 ms generic topology build and almost all culler
preparation. Three complete `TinEngine.Build` runs took 3,382–3,535 ms versus the earlier 4,428 ms
reference, a measured approximately 20–24% cold-build reduction with identical output shape.

### Grade Path phase and allocation profile

Measuring immediately around `PathGrader.Grade` produced 768 ms and 92,187,632 process-wide allocated
bytes. Current-thread allocation was 92,092,360 bytes, so this particular case performs little
allocation on worker threads; the former wrapper-level 94.84 MB figure was close but included about
2.7 MB of fixture/test work.

| Phase | Time | Process-wide allocation |
|---|---:|---:|
| Input validation | 1.6 ms | 2,048 bytes |
| Terrain face grid | 2.8 ms | 878,112 bytes |
| Barrier preparation | 2.5 ms | 339,080 bytes |
| Corridor and daylight construction | 146 ms | 316,544 bytes |
| Conform-loop preparation | 1.8 ms | 16,480 bytes |
| Terrain conform split | 391 ms | 76,650,456 bytes |
| Topology validation | 6.4 ms | 3,621,312 bytes |
| Z application | 176 ms | 7,556,248 bytes |
| Result assembly | 37.8 ms | 2,330,232 bytes |

Terrain conform splitting accounts for approximately 83% of measured Grade Path allocation and 51%
of elapsed time. Topology validation accounts for approximately 4% of allocation and less than 1% of
time. This confirms the new conform-split route should precede shared topology work.

Corridor/daylight and Z application remain the next CPU targets. Closest-path lookup scales linearly
at approximately 3.2 ns per segment-query after warmup; 10,000 queries against 462 segments took
14.8 ms. It is a real scaling route for many or very long paths, but not the dominant cost in this
single-path copied case.

### Scaling and omitted-workload results

- Daylight no-hit queries were linear at approximately 8–11 ns per face-station. On 100,352 faces,
  128 stations took 100 ms, confirming the value of grid traversal.
- Conforming a 4,608-face grid allocated 29.3 MB with a 32-segment loop and 30.5 MB with a
  384-segment loop. Runtime rose only from 10.6 to 14.2 ms. The large fixed per-face setup dominates
  allocation more than pairwise segment count in this simple case.
- Explicit-boundary preparation for 1,201,507 input vertices took 177 ms, allocated 277,304,096 bytes,
  and increased process-private memory by approximately 176.8 MB. This is a high-memory route even
  though its CPU time is below TIN peeling.
- Poisson sampling produced 127 visible points from both a compact and a thin diagonal region. The
  compact case took 0.8 ms and allocated 11,048 bytes; the thin case took 17.2 ms and allocated
  356,072 bytes because its mostly empty bounding-box grid contained approximately 20,164 cells.
  Two widely separated components showed the same pattern at 15.5 ms and 357,256 bytes.

## Route 1 — Reuse or fuse TIN topology and boundary peeling

**Priority:** Highest
**Expected impact:** High on very large TIN inputs
**Risk:** Medium

### Current cost

`TinEngine.BuildResult` first calls `IndexedMeshTools.BuildEdgeTopology`, which builds and sorts all
three edge references per face. `TriangleBoundaryCuller.Cull` then performs another full face scan and
constructs:

- a dictionary of active edge counts; and
- a dictionary mapping each edge to its incident faces.

For approximately 2.4 million faces, the original benchmark reported about 2.27 seconds for initial
topology plus culling. Because the culling measurement included an internal topology rebuild, that is
not the production-shaped total. The opportunity is still large, but expected savings must be based
on the corrected measurement.

### Proposed route

Prototype the native-adjacency route first. Triangle.NET triangles already expose the neighbour
opposite each local vertex. For the common manifold TIN path, extraction can retain:

- the materialized triangle-reference array it already creates;
- a compact triangle-id-to-output-face map; and
- the native neighbour relationship.

An active-face bitset plus those neighbours is sufficient to:

- identify initial and newly exposed boundary faces;
- enqueue the face across an edge when its neighbour is removed; and
- avoid both culler dictionaries and the edge-key sort when no other operation needs generic edge
  topology.

Retain the existing array-based culler as a validated fallback for arbitrary or invalid adjacency and
for paths that still need generic topology, such as some Steiner interpolation cases.

In parallel, benchmark a packed fallback made from normalized edge key plus incident face id. A C#
struct containing `long + int` commonly occupies 16 bytes, or roughly 115 MB for 7.2 million
half-edge references, so peak memory must be measured. Parallel `long[]` and `int[]` arrays use less
space but still exceed the current key-only array.

The automatic threshold also needs separate treatment. Sorting edge records by key does not order
edge lengths. Preserving the exact median requires either:

- the existing edge-length array and sort;
- an exact selection algorithm such as quickselect; or
- a separately validated change in threshold semantics.

This preserves the existing peeling behaviour and avoids changing the default user-facing setting.
Changing or disabling default peeling would be faster but carries greater output-shape risk.

### Validation

- Compare face and vertex arrays against current boundary-peel regression cases.
- Measure topology, exact median selection, culler edge-map construction, queue seeding, peel
  traversal, and compaction independently.
- Compare native adjacency with packed edge references for elapsed time, total allocation, and peak
  working set.
- Add enabled-versus-disabled peel timings to the large-dataset benchmark.
- Confirm incremental and Z-only TIN cache paths remain byte-for-byte stable.
- Validate reciprocal Triangle.NET neighbour links and retain a fallback when the native graph is not
  trustworthy.
- Run the Rhino `mhBenchmarkLargeTin` command to capture conversion, normalization, fingerprint, and
  clone timings after the Core improvement.

### Relevant files

- `src/MoleHill.Core/Engine/TinEngine.cs`
- `src/MoleHill.Core/Engine/IndexedMeshTools.cs`
- `src/MoleHill.Core/Engine/TriangleBoundaryCuller.cs`
- `tests/MoleHill.Core.Tests/LargeDatasetBenchmarkTests.cs`
- `src/MoleHill.Rhino/Services/LargeTinDiagnostic.cs`

## Route 2 — Spatialize Grade Path and Pad search work

**Priority:** High
**Expected impact:** High for long paths and large terrains
**Risk:** Medium

### Current cost

`TerrainFaceGrid` builds a spatial grid over terrain faces, but
`TryFindRayDaylightReach` does not use it. Each daylight station scans every terrain face and clips
the search ray against each triangle. Grade Path creates stations along both sides of the corridor,
so the cost trends toward:

`station count × terrain face count`

The copied large-terrain Grade Path case currently spends roughly 0.78 seconds in Core and allocates
about 94.84 MB.

`MeshAreaTopologySplitter.BuildBoundarySegments` also checks every boundary segment against every
later segment. Densely sampled, long, or multi-path corridor loops can therefore introduce a second
quadratic path.

Z application performs another repeated linear search: every candidate output vertex scans every
segment in each resampled path to find the closest path location. The copied case has 463 path
vertices, so its approximately 8,410 output vertices can perform several million segment tests even
after the whole-path bounds check.

### Proposed route

For daylight search:

1. Traverse the grid cells intersected by the finite daylight ray using a 2D DDA traversal, or gather
   candidates from the ray bounds when that produces a tighter implementation.
2. Deduplicate candidate faces because a large triangle can occupy several cells.
3. Process candidate face ids in ascending original order. The current method returns the first
   qualifying face, not necessarily the closest ray solution, so arbitrary grid-bucket order can
   change geometry even when all predicates are unchanged.
4. Fall back to the existing complete scan when grid traversal detects an invalid or pathological
   grid range.

For path and boundary segments:

1. Build segment-bounds spatial indexes for both resampled path segments and conform-loop segments.
2. Compare a boundary segment only against overlapping candidate bounds.
3. Query only path segments whose expanded bounds can influence the output vertex.
4. Preserve the existing first-segment tie-break by processing or resolving candidate ids in ascending
   order.
5. Return both segments' parameters from one exact boundary-intersection calculation instead of
   recalculating the same pair in reverse.

### Validation

- Add phase timings for terrain-grid construction, corridor/daylight construction, loop preparation,
  conform splitting, topology validation, Z application, and result assembly.
- Compare every daylight station status and reach against the existing implementation.
- Run copied Grade Path, tight-bend, barrier, terrain-edge, and non-daylighting regression cases.
- Track current-thread and process-wide allocation separately in the existing performance benchmark.
- Add scaling cases for station count × terrain faces, path vertices × output vertices, and conform
  segment count.

### Relevant files

- `src/MoleHill.Core/Grading/TerrainFaceGrid.cs`
- `src/MoleHill.Core/Grading/BatterStripBuilder.cs`
- `src/MoleHill.Core/Grading/PathGrader.Explicit.cs`
- `src/MoleHill.Core/Grading/PathGrader.SplitKeep.cs`
- `src/MoleHill.Core/Grading/MeshAreaTopologySplitter.cs`
- `tests/MoleHill.Core.Tests/LargeTerrainPerformanceBenchmarkTests.cs`

## Route 3 — Reduce Grade Path conform-split allocation churn

**Priority:** High; first Grade Path implementation route
**Expected impact:** Potentially high allocation reduction
**Risk:** Low-to-medium

### Current cost

`MeshAreaTopologySplitter` and the closely related `MeshConstraintTopologyInserter` allocate:

- one reference-type `FaceData` per terrain face;
- one reference-type `SegmentIntersection` for every exact intersection attempt, including misses;
- new edge-point, clipping-parameter, and clipped-piece lists for every segment/face candidate; and
- a `Dictionary<cell,List<int>>` point lookup that commonly creates one tiny list per input vertex.

For each candidate segment/face pair, edge touches and triangle clipping independently calculate the
same three segment/triangle-edge intersections. Boundary segment pairs are also intersected twice to
obtain parameters in each direction.

These structures are plausible contributors to the Grade Path allocation result and should be
measured before assigning most of that allocation to topology validation.

### Proposed route

- Return intersection results as readonly value types.
- Combine edge-touch collection and segment-to-triangle clipping into one three-edge pass.
- Use fixed-size or reusable scratch storage for the small parameter and result sets.
- Precompute per-face barycentric denominators and edge data.
- Replace per-cell tiny lists with a compact bucket-chain or flat cell-offset representation.
- Extract the shared conform-intersection kernel used by both `MeshAreaTopologySplitter` and
  `MeshConstraintTopologyInserter` so the optimization is implemented and validated once.

### Validation

- Use process-wide allocation profiling around `PathGrader.Grade`.
- Record allocations separately for conform splitting and Z application.
- Compare output vertex/face arrays and topology fingerprints on copied, tight-bend, overlapping,
  barrier, and terrain-edge cases.
- Add segment-count scaling cases that distinguish pair preprocessing from segment/face mapping.

### Relevant files

- `src/MoleHill.Core/Grading/MeshAreaTopologySplitter.cs`
- `src/MoleHill.Core/Grading/MeshConstraintTopologyInserter.cs`
- `src/MoleHill.Core/Engine/SpatialHashGrid2D.cs`
- `tests/MoleHill.Core.Tests/LargeTerrainPerformanceBenchmarkTests.cs`

## Route 4 — Share a flat, low-allocation topology analysis primitive

**Priority:** Medium-to-high after allocation profiling
**Expected impact:** Medium allocation reduction, with CPU benefits
**Risk:** Low-to-medium

### Current cost

`MeshTopologyValidator.AnalyzeBoundaryGraph` creates an edge-count dictionary with an initial
capacity of eight, even though it will contain up to approximately three entries per face. It then
creates adjacency dictionaries, one small list per boundary vertex, a visited set, and a traversal
stack.

Grade Path split-keep validates both:

- the conformed output; and
- the original terrain.

The benchmark wrapper validates the result again. Similar edge structures are independently built by
TIN peeling, grading assembly, feature detection, and remeshing.

`MeshBoundaryLoopBuilder` separately implements nearly the same edge-count and boundary-adjacency
construction, so a shared primitive should support ordered loop extraction as well as validation.

### Proposed route

Extend the flat sorted-edge topology code so one analysis can expose:

- edge run lengths;
- naked and non-manifold edges;
- boundary vertex degrees;
- boundary-component count; and
- optional incident-face adjacency.

Boundary components can be counted with flat degree/adjacency arrays or union-find rather than a
dictionary of lists. Callers that already have compatible topology data should pass or reuse it
instead of rebuilding it.

As a quick interim improvement, dictionary capacities can be sized from `faceCount`, but the shared
flat representation is the better long-term route. Do not assume it must also be the TIN peel
representation: native Triangle.NET adjacency may be both faster and smaller for Route 1.

The implementation must preserve the current special case where a mesh with no boundary edges reports
`HasOpenBoundaryChains: true`. Flat vertex-indexed arrays also need an explicit dense/sparse guard;
allocating by maximum referenced vertex id can be pathological for sparse ids.

### Validation

- Preserve all `BoundaryGraphAnalysis` values on the existing topology test suite.
- Add allocation measurements for 10k, 100k, and million-face synthetic meshes.
- Verify non-manifold runs greater than two remain correctly detected.
- Confirm grading watertightness gates remain unchanged.

### Relevant files

- `src/MoleHill.Core/Engine/MeshTopologyValidator.cs`
- `src/MoleHill.Core/Engine/IndexedMeshTools.cs`
- `src/MoleHill.Core/Grading/MeshBoundaryLoopBuilder.cs`
- `src/MoleHill.Core/Grading/GradedRegionAssembler.cs`
- `src/MoleHill.Core/Engine/IsotropicRemesher.cs`

## Route 5 — Reduce cache duplication and redundant retained geometry

**Priority:** Medium
**Expected impact:** High memory reduction on very large cached stacks; CPU benefit depends on Rhino
clone measurements
**Risk:** Medium-to-high

### Current cost

`StoreMeshStageCache` duplicates each Rhino mesh for cache ownership. A hot cache restore duplicates
the cached mesh again. This is safe and avoids disposed or mutated shared native meshes, but its cost
scales with every cached stage and every mesh vertex/face.

Worker cache creation already shallow-shares each stage entry's `MeshOutput`; it does not duplicate
the Rhino mesh again. Its remaining copy cost is primarily entry objects, constraints, diagnostics,
and retained managed arrays.

Grade Path topology entries also clone and retain flat vertex and face arrays. Current code only
consumes cached topology geometry for Grade Pad; Path entries primarily provide fingerprints,
diagnostics, and patch summaries.

### Proposed route

Start with the low-risk reductions:

1. Stop retaining full vertex and face arrays in Grade Path topology entries when no current consumer
   needs them.
2. Record time and managed/native memory around cache storage, hot restore, worker cache creation,
   cache merge, and display publication for large meshes.
3. Avoid cloning unchanged diagnostics, constraints, and generated-output collections where immutable
   ownership is already guaranteed.

If Rhino mesh cloning is material on 250k–1.2M vertex meshes, consider a larger architectural route:

- cache immutable flat mesh data between Core stages;
- share it across worker copies with explicit ownership;
- materialize Rhino meshes at preview, display, and bake boundaries; and
- retain conservative disposal rules for native Rhino objects.

The immutable-flat-cache route should only follow measurements because it changes a deliberate native
ownership boundary.

### Validation

- Extend `mhBenchmarkLargeTin` to report peak managed/native memory and hot-cache restore timings.
- Verify retired background workers cannot observe disposed or mutated mesh data.
- Test repeated cancel/rebuild/merge cycles.
- Compare preview and bake meshes before and after any ownership change.

### Relevant files

- `src/MoleHill.Rhino/Services/TerrainBuildService.Cache.cs`
- `src/MoleHill.Rhino/Services/TerrainRuntimeCache.cs`
- `src/MoleHill.Rhino/Services/TerrainController.Build.cs`
- `src/MoleHill.Rhino/Services/TerrainBuildService.Grading.cs`
- `src/MoleHill.Rhino/Services/LargeTinDiagnostic.cs`

## Route 6 — Reuse isotropic-remesher connectivity

**Priority:** Lower until a real model identifies Remesh as dominant
**Expected impact:** Medium on large or repeatedly edited remesh stages
**Risk:** High

### Current cost

`FlipForQuality` rebuilds full edge adjacency, a touched-face array, and a created-edge set on every
sweep, with up to sixteen sweeps. Collapse and relaxation phases also construct vertex-face and
neighbour dictionaries.

The current synthetic benchmarks complete in 465–713 ms, so this is not the first optimization to
undertake.

### Proposed route

- Maintain edge-to-face incidence inside `MeshState`.
- Update only the local one-ring after split, collapse, or flip operations.
- Drive flipping from a dirty-edge work queue rather than rescanning every edge every sweep.
- Pool or reuse touched and candidate buffers between iterations.

This would be a meaningful rewrite of the remesher kernel and needs strong geometry and determinism
coverage.

### Relevant files

- `src/MoleHill.Core/Engine/IsotropicRemesher.cs`
- `src/MoleHill.Core/Engine/FeaturePolylineGraph.cs`
- `tests/MoleHill.Core.Tests/IsotropicRemesherBenchTests.cs`

## Additional opportunities and measurements

### Explicit-boundary TIN preparation

`TinBoundaryPreparer` copies the complete XY and Z arrays into lists, builds a
`Dictionary<cell,List<int>>` over every terrain vertex, then copies the lists back to arrays. On a
million-point TIN with an explicit outer boundary this can create millions of managed objects and
multiple simultaneous full-size coordinate buffers. The existing large fixture uses convex-hull mode
and does not measure this route.

Add a million-point explicit-boundary preparation benchmark before changing it. Candidate
implementations include a compact bucket-chain lookup, sorted quantized keys, and allocating final
arrays once rather than round-tripping through `List<T>`.

### Triangle.NET extraction and XYZ materialization

`TriangleNetExtractor` materializes all triangle references, builds a vertex-reference dictionary,
creates XY and face arrays, and then `TinEngine.BuildResult` copies XY into a separate XYZ array.
Extraction alone measured approximately 0.5 seconds on the large fixture and has a meaningful peak
memory footprint.

An adjacency-aware extractor for Route 1 should also test fusing XY/Z materialization and avoiding
transient triangle/list copies. This work is lower priority than removing the peel dictionaries but
fits naturally into the same implementation.

### Scatter Poisson sampling and Rhino projection

Poisson sampling currently admits candidates outside the requested region into its internal sample
grid and active frontier, while adding only inside points to the result. This allows one seed to reach
disconnected boundaries, but makes runtime and memory scale with the aggregate bounding box rather
than occupied polygon area. Thin, concave, or widely separated regions can therefore fill a large
mostly invisible Poisson grid. The dense `int[gridWidth * gridHeight]` allocation is also not bounded
by `MaxSamples`.

After sampling, every placement calls Rhino `MeshLineSorted` and then `ClosestMeshPoint`. A
scatter-heavy model can issue up to 200,000 such pairs. Benchmark:

- a compact polygon;
- a thin diagonal polygon with the same area;
- widely separated boundary components; and
- terrain projection at 1k, 10k, and 100k samples.

If material, seed each connected component while enforcing global spacing, cap grid memory
independently of result count, and extend the fast Core projector to return face/barycentric/normal
data with Rhino fallback for stacked or near-vertical geometry.

### Reusable flat spatial index

`TerrainFaceGrid`, `MeshHeightProjector`, `SpatialHashGrid2D`, and several grading/remeshing helpers
independently use `Dictionary<cell,List<int>>`. A reusable sorted-cell or CSR-style representation
could reduce small-object allocation and improve locality across several routes. Treat it as a shared
primitive only after per-phase allocation measurements identify repeated cell-list construction as a
material cost.

### Boundary clipping, nearest-vertex fallback, and contour stitching

- `BoundaryClipper.ClipSegmentToBoundary` scans and sorts against every boundary edge for each
  shoulder ray and allocates parameter/result lists per call.
- `TerrainFaceGrid.InterpolateZ` falls back to a full vertex scan when no containing face is found;
  repeated near-boundary misses can become `query count × vertex count`.
- `ContourGenerator.StitchSegments` uses dictionary-of-lists adjacency, LINQ ordering, and one
  `LinkedList<int>` per chain.

These are credible medium-priority routes, but none currently has a representative performance
benchmark.

## Recommended implementation order

### 1. Production-shaped TIN adjacency/peel

**Implemented.** Native Triangle.NET adjacency is validated and used for exact-threshold traversal and
peeling, with the generic topology path retained for invalid adjacency, Steiner interpolation needs,
and requested edge output.

### 2. Grade Path conform-split allocation

Remove reference-type intersection results, duplicate triangle-edge work, and tiny per-candidate
lists. The phase profile now justifies doing this before the search-index work.

### 3. Grade Path spatialization

Spatialize daylight traversal, closest-path-segment lookup, and conform-loop pair preprocessing while
preserving original face/segment order semantics.

### 4. Shared topology analysis

Unify validation and boundary-loop extraction after its share of Grade Path allocation is known.
Reuse it where callers already hold compatible generic topology, without forcing the TIN-specific
path away from native adjacency.

### 5. Cache ownership and duplication

Apply the easy Path topology-entry trim first. Make larger cache changes only after the Rhino
diagnostic shows that clone time or memory is materially affecting real builds.

### 6. Remesher connectivity

Defer until a captured Rhino build shows Remesh dominating after the earlier work. The current
benchmarks do not justify taking this higher-risk rewrite first.

## Measurement programme status

Completed in the Core Release benchmarks:

1. Corrected large TIN breakdown matching `includeEdgeTopology:false` production behaviour, including
   exact-median, edge-map, seed, peel, compaction, and allocation phases.
2. Native Triangle.NET adjacency indexing compared with sorted key/face arrays.
3. Direct `PathGrader.Grade` current-thread/process-wide allocation and opt-in phase timing/allocation.
4. Scaling cases for daylight stations/faces, closest path segments, conform-loop segment count,
   explicit TIN boundary preparation, and compact versus sparse Poisson domains.

Implemented and ready to run inside Rhino:

5. `mhBenchmarkLargeTin` now reports managed heap, total allocation, process-private bytes, and
   working-set deltas around Rhino conversion, cache clone, and base clone.

Validation of the follow-up harness:

- all 437 Core Release tests pass after Route 1;
- the Rhino Release diagnostic build succeeds with zero warnings or errors; and
- no Rhino slot was connected during this pass, so the enhanced command was not run against an
  active model.

Remaining measurements that require a representative Rhino session/model:

- run the enhanced `mhBenchmarkLargeTin` command;
- measure scatter terrain projection at 1k, 10k, and 100k placements;
- capture hot stage-cache restore and repeated cancel/rebuild/merge cycles; and
- use a native-memory profiler if process-private deltas show material clone retention.

## Measurement gates

Before merging each route, record:

- cold elapsed time;
- hot-cache elapsed time where applicable;
- managed allocation delta;
- managed and native memory where Rhino meshes are involved;
- input/output vertex and face counts;
- boundary, component, and non-manifold topology results; and
- deterministic output fingerprints.

The target is not merely lower elapsed time. Every optimization must retain the grading invariant:
watertight 2.5D output with topology no worse than the input.
