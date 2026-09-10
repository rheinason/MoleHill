# Terrain scalability review and deep-dive backlog

Date: 2026-09-09  
Status: active backlog; C01 implemented in the current working tree.

## Scope and confidence

This review examines the working tree **including the uncommitted zone-splitting improvements**. It is a source-level survey of Core geometry, Rhino build/output/cache/display paths, and selected Grasshopper consumers. It is not a measured ranking of the actual 15-million-face model. No new benchmarks or native Rhino tests were run for this document. The prior implementation turn reported 560 passing Core tests; that does not validate the hypotheses below.

“Confirmed pattern” means the allocations or loops are present in inspected code. “Deep dive” means frequency, real cost, and the safest replacement still need measurement. Priorities describe investigation order, not promises of speedup. A linear pass is not automatically a defect, and an index or parallel loop is not automatically cheap.

The already-addressed area splitter changes are not proposed again: on-demand face geometry, indexed boundary intersections, deterministic face-parallel mapping, bounded grid queries, indexed classification rays, compact vertex-cell links, chunked face output, and interior-edge assertions.

## Suggested order

Each unchecked row is an independent work item. Start with the correctness investigation, then choose the workload that matters most. Avoid one repository-wide rewrite.

| Status | ID | Priority | Deep dive | Main scale driver |
|---|---|---|---|---|
| [x] | C01 | Resolved | Reference-comparison cache identity | Multiple meshes in one zone |
| [x] | O01 | Resolved | Constraint topology insertion | Terrain faces and constraint segments |
| [x] | O02 | Resolved | Partition zone outputs in one pass | Faces × boundary entries |
| [x] | O03 | Resolved | Remesh flip adjacency and feature setup | Faces × sweeps × iterations |
| [x] | O04 | Resolved | Spatial-index construction and retained scratch | Face-cell memberships and worker count |
| [x] | O05 | Resolved | Localize stroke commit and constraint evaluation | Whole mesh per stroke; protection edges |
| [x] | O06 | Resolved | Sparse Poisson occupancy and prepared containment | Bounding-box area / spacing² |
| [x] | O07 | Resolved | Reuse reference projection contexts safely | Reference faces × comparisons |
| [x] | O08 | Resolved | Work-region and grading containment queries | Points/faces × polygon edges |
| [x] | O09 | Resolved | Cancellation latency in heavy Core stages | Superseded work and retained geometry |
| [x] | O10 | Resolved | Cache/display geometry ownership and peak memory | Mesh copies × stages/workers |
| [x] | O11 | Resolved | Waterflow setup and independent traces | Faces plus starts × path length |
| [x] | O12 | Resolved | Contour output and stitching allocations | Emitted segments and contour levels |
| [x] | O13 | Resolved | Seam deviation nearest-segment queries | Source vertices × target edges |
| [x] | O14 | Resolved | Cross-field solver convergence | Vertices × iterations |
| [ ] | O15 | **Blocked on measurement** | Scatter preview draw calls | Visible instances × shape points |
| [ ] | O16 | Follow-up | Remaining zone splitter memory and index behavior | Full terrain plus boundary distribution |

## C01 — Verify reference-comparison cache identity before sharing more caches

**Resolved 2026-09-09:** Reference-comparison statistics now include the extracted current vertex and
face array identities in their build-local key, so separate zone pieces cannot reuse one another's
volumes. Reference projection contexts have a separate key containing only reference identity and are
shared across pieces and zones. Per-call projection diagnostics subtract their starting counters so
reuse does not inflate later results. An always-runnable key-identity test guards the cache partition;
the native two-mesh test expects 0.5 and 1.5 fill volumes while one reference projector is retained.
The native behavioral test is currently skipped when Rhino's native test runtime is unavailable.

**Evidence:** [src/MoleHill.Rhino/Services/TerrainBuildService.Zones.cs:151](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainBuildService.Zones.cs:151) creates a comparison cache per zone and reuses it across that zone's meshes. [src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:667](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:667) keys statistics by reference/boundary fingerprints and reference mode, without the current mesh. A cache hit returns the previous statistics before examining the current geometry.

**Original concern:** For a zone with two output meshes and the same reference inputs, the second call could reuse the first mesh's volume result.

**First task:** Construct one zone with two disjoint pieces having deliberately different areas/elevation differences. Compare summed results to independent per-piece evaluations. Distinguish a reusable reference projector from statistics that depend on the current mesh.

**Done when:** The two-piece regression passes, current-geometry identity is represented or cache scope is restricted appropriately, and any projection reuse in O07 cannot reuse the wrong volume result.

## O01 — Constraint topology insertion repeats the old zone splitter patterns

**Resolved 2026-09-10:** `FaceData` is now a `readonly struct` built on demand from the flat arrays, so
the per-face `FaceData[]` is gone; the mapping pass keeps only a `Bounds2D[]` for the spatial index and
constructs face geometry for candidate faces only. The unconditional all-pairs constraint-segment sweep
is replaced by indexed pair discovery over a `SpatialHashGrid2D` of segment bounds — candidates are
sorted and filtered to `j > i` with the same bounds test, so the visited pair set and the resulting
split parameters are identical to the former sweep. The input arrays are no longer cloned before the
work decides whether any topology edit exists (`CloneInput` is called only on the paths that return the
input unchanged, and on failure), and the output face list is now sized from the touched-face count
instead of reserving twice the whole input face array. `MeshConstraintTopologyInserterTests` covers
crossing, collinear overlap, duplicate, closed-loop, constraint-order, sloped-elevation, away-from-mesh,
empty-mesh, and output-ownership cases. Not done here, deliberately: face-owned parallel mapping, and
the `GlobalPointLookup` per-cell vertex lists (its cell size is the tolerance, so its density needs
measurement before its tie-break order is disturbed).

**Confirmed patterns:** [src/MoleHill.Core/Grading/MeshConstraintTopologyInserter.cs:15](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Grading/MeshConstraintTopologyInserter.cs:15) retains a class per face; [src/MoleHill.Core/Grading/MeshConstraintTopologyInserter.cs:259](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Grading/MeshConstraintTopologyInserter.cs:259) clones input arrays before doing work; line 278 builds all face data; line 389 performs an all-pairs constraint-segment intersection sweep; line 478 maps through a terrain-face index. Output uses a twice-input-sized face list and per-cell vertex lists.

This is reached by [src/MoleHill.Core/Grading/PathGrader.Patches.cs:97](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Grading/PathGrader.Patches.cs:97) and [src/MoleHill.Rhino/Services/TerrainBuildService.RetainingWalls.cs:626](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainBuildService.RetainingWalls.cs:626). Path patches are a fallback, so measure reachability rather than assuming every path build pays this cost.

**First task:** Add phase allocation/timing counters and benchmark increasing face count independently from increasing constraint count. Port on-demand face data and indexed pair discovery first; evaluate face-owned parallel mapping separately. Audit the default packed-edge set at line 572, but it holds local triangulation segments, so do not rank it alongside whole-terrain hashing without evidence.

**Guardrails:** Preserve constraint elevation, intersections, insertion order, shared-edge conformity, and fallback behavior. Do not blindly merge the two splitters: their contracts differ.

**Done when:** Existing constraint/path/wall regressions pass, new crossing/overlap/duplicate tests pass, and doubling experiments eliminate the unconditional all-pairs sweep and per-face object population.

## O02 — Zone extraction rescans the complete result for every boundary

**Resolved 2026-09-10:** Face selection is now a single grouping pass. `FaceOwnerGroups` (Core) counts
then fills a flat CSR layout keyed by per-face owner — the split result's `FaceAreaIndex`, or the
partition component's classified owners — so extracting B sub-meshes costs O(F + B) instead of O(B × F).
Within each group the face indices stay ascending, which is exactly the order the per-area scan
produced. `SubMeshVertexRemap` (Core) replaces the per-area `HashSet` + `Dictionary` pair with two
arrays sized once by the source vertex count and separated by a stamp; vertices are claimed in
first-touch order, the order the hash-set form produced. Converted: the Rhino zone loop
(`TerrainBuildService.Zones` + `RhinoGeometryConversions.BuildSubMesh`), `MeshAreasComponent` (its
per-area `CountFaces` rescan is now the group length), `MeshCollageComponent`, and
`PartitionTerrainComponent` via a grouped `TerrainPartitionGeometry.BuildOwnedMesh` overload.
`TerrainPartitionGeometry`'s private builder keeps its ascending-source-order vertex emission, which is
part of that output's contract. `FaceOwnerGroupsTests` and `SubMeshVertexRemapTests` pin the grouping
against a linear-scan oracle and the remap against a per-sub-mesh dictionary.

**Confirmed pattern:** [src/MoleHill.Rhino/Services/TerrainBuildService.Zones.cs:107](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainBuildService.Zones.cs:107) calls BuildSubMesh once per entry. [src/MoleHill.Rhino/Services/RhinoGeometryConversions.cs:160](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/RhinoGeometryConversions.cs:160) scans all result faces on every call, then builds both a vertex HashSet and remap dictionary and normalizes the mesh. This creates O(B × F) face selection work even if each face has one owner.

Related host paths: [src/MoleHill.Grasshopper/Components/MeshAreasComponent.cs:143](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Grasshopper/Components/MeshAreasComponent.cs:143), [src/MoleHill.Grasshopper/Components/MeshCollageComponent.cs:328](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Grasshopper/Components/MeshCollageComponent.cs:328), and [src/MoleHill.Grasshopper/Utilities/TerrainPartitionGeometry.cs:105](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Grasshopper/Utilities/TerrainPartitionGeometry.cs:105).

**First task:** Count/group face indices by owner once, then extract each group. Measure native normalization separately; only use the existing validated-triangle finalizer if the splitter output meets its complete contract.

**Guardrails:** Preserve highest-priority ownership, remainder output, source layers, output names, vertex ordering expectations, normals, and zone summaries. Avoid allocating one full vertex-remap array per zone.

**Done when:** Varying zone count at fixed face count no longer repeats F-sized selection scans; output topology and host behavior remain equivalent.

## O03 — Remesh still rebuilds large edge structures repeatedly

**Resolved 2026-09-10:** Three rebuilds removed, all order-preserving.

1. `FlipForQuality` allocated an edge-incidence dictionary sized ~1.5x the face count, a face-sized
   `touched` array and a created-edge set for **each** of up to `MaxFlipSweeps` sweeps per outer
   iteration. Flips rewrite faces but never add or remove them, so all three are now sized once per call
   and cleared between sweeps. Clearing a dictionary keeps its buckets and resets its entry list, so
   refilling in the same face order gives the same enumeration order — the order flips are considered in.
   (The position copy the review lists at line 869 was already outside the sweep loop.)
2. `CollapseShortEdgesRound` allocated a candidate list and a `HashSet<int>` of one-ring locks per round,
   up to 8 rounds. Both are now call-scoped: the list is cleared, and the locks became a stamped array
   (`CollapseRoundLocks`) — membership only, never enumerated, so no ordering is involved. The CSR
   adjacency was already reused.
3. `FeaturePolylineGraph.Build` built the whole-mesh edge incidence **twice** — once inside
   `MeshConstraintTools.AddBoundarySegments`, which starts at capacity 8 and rehashes all the way up,
   and once as `edgeIncidence`. One pass now serves both readings: incidence 1 is a boundary feature
   edge (self-edges excluded, as the segment builder did), incidence > 2 is non-manifold.

**Equivalence evidence:** remesh output was fingerprinted (SHA-256 over the full vertex and face arrays,
plus split/collapse/flip counts) across 12 target-length x iteration-count configurations on an
irregular sheet, before and after. All 12 are byte-identical. `IsotropicRemesherScratchReuseTests`
covers determinism, many-collapse-round and many-flip-sweep workloads, edge manifoldness, and the
boundary polygon; `FeaturePolylineGraphIncidenceTests` covers both readings of the shared incidence pass
including a mesh that is non-manifold and bounded at once.

Not done here: replacing the flip dictionary with a flat incidence representation. The sweep's flip
order is its enumeration order, so that is an algorithm change needing its own validation, and the
allocation cost it was carrying is now gone.

**Confirmed patterns:** [src/MoleHill.Core/Engine/IsotropicRemesher.cs:869](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Engine/IsotropicRemesher.cs:869) copies positions and rebuilds an edge-incidence dictionary for each quality-flip sweep; the maximum is 16 sweeps per outer iteration. [src/MoleHill.Core/Engine/IsotropicRemesher.cs:596](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Engine/IsotropicRemesher.cs:596) rebuilds reusable CSR adjacency and sorts short-edge candidates per collapse round. [src/MoleHill.Core/Engine/FeaturePolylineGraph.cs:84](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Engine/FeaturePolylineGraph.cs:84) extracts boundaries, then separately counts whole-mesh edge incidence.

The important edge comparers and CSR collapse adjacency are **already present**. This is about remaining rebuilds, sorting, capacity, and repeated passes, not the previously fixed bad hash.

**First task:** Record per-round F/E, candidates, accepted operations, allocations, and elapsed time. Compare dictionary rebuilds with a reusable flat incidence representation; separately consider sharing feature-setup incidence. Inspect the analogous [src/MoleHill.Core/Engine/LocalMeshRefiner.cs:394](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Engine/LocalMeshRefiner.cs:394).

**Guardrails:** Sequential flip/collapse decisions affect later eligibility. Naive parallelism or reordered iteration can change topology, features, walls, and quality. Preserve deterministic tie/order behavior or explicitly validate a changed algorithm.

**Done when:** Profile-backed improvements survive feature, non-manifold quarantine, wall, target-length, and determinism tests. Keep each phase change separately reviewable.

## O04 — Shared spatial structures can amplify memory despite being indexed

**Resolved 2026-09-10:** All three indexes now store cells as flat CSR — a `Dictionary<long, int>` from
cell key to slot, an `int[]` of slot start offsets, and one `int[]` of memberships — built by a count
pass then a fill pass over the same items in the same order. Each is immutable once built, so the
former `Dictionary<long, List<int>>` was paying a `List` object plus its backing array for every
occupied cell: millions of small objects on a large terrain. `TerrainFaceGrid` and `MeshHeightProjector`
also reserved their dictionary by **face** count while their default cell size targets about
`faceCount / 4` cells, a ~4x over-reservation; both now reserve by an occupied-cell estimate. Because
both passes visit items in index order, every cell's run is ascending — exactly what the per-cell lists
held, which point location's first-match rule depends on.

The retained scratch is gone: `TerrainFaceGrid`'s thread-static ray buffer no longer carries a
face-sized stamp array (tens of megabytes per thread-pool worker, held for the process lifetime, long
after the build). Its candidates were already sorted back into source face order before use, so the
same sort now removes the duplicates a face spanning several traversed cells contributes. The buffer
retains only as much as the widest ray corridor.

`SpatialIndexEquivalenceTests` checks each index against a brute-force answer on a uniform sheet, a
coarse/fine transition, a narrow corridor, and a domain-spanning item among tiny ones, plus the ray
candidate path against `TryFindRayDaylightReachLinearForDiagnostics`.

Not done here: bounding index *membership* growth itself. A long diagonal still registers in every cell
of its bounding box — the tests confirm it stays correct, and the per-membership cost is now 4 bytes
rather than a list slot, but an oversized-item tier or a hierarchy still needs the measurements this
item asks for before it is worth its complexity.

**Confirmed patterns:** [src/MoleHill.Core/Grading/TerrainFaceGrid.cs:89](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Grading/TerrainFaceGrid.cs:89) preallocates a dictionary by face count and inserts each triangle into every cell of its bounding rectangle. [src/MoleHill.Core/Analysis/MeshHeightProjector.cs:29](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Analysis/MeshHeightProjector.cs:29) also reserves by face count. [src/MoleHill.Core/Engine/SpatialHashGrid2D.cs:170](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Engine/SpatialHashGrid2D.cs:170) fills bounding-box cells.

A long diagonal or a large triangle among tiny faces can occupy many cells. Clamping query extents fixed empty-space traversal; it does not bound index membership growth. TerrainFaceGrid also retains a face-sized mark array in thread-static ray scratch (lines 8–43), which can outlive the build.

**First task:** Measure occupied cells, total memberships, max/p95 bucket length, reserved capacity, and retained scratch across coarse/fine transitions, narrow corridors, and long diagonals. Evaluate flat cell storage, capacity based on occupied cells, oversized-item handling, or a hierarchy. Measure scratch retained across thread-pool workers.

**Guardrails:** Keep point-location tie rules, near-vertical fallback, tolerances, and candidate completeness. Never substitute a lossy spatial simplification.

**Done when:** Adversarial geometry has bounded practical memory, query equivalence tests pass, and the chosen structure improves total build-plus-query cost.

## O05 — A small sculpt commit performs whole-terrain work

**Resolved 2026-09-10:** `SculptFieldRasterizer.Rasterize` no longer allocates a whole-mesh XYZ delta
copy or builds a whole-mesh `TerrainFaceGrid`. It selects the faces whose XY bounds meet the sampled
rectangle (the dirty bounds padded by one field cell, which is what the sampling loop already used),
pulls their vertices in on first use, and grids only that. A face outside the rectangle cannot contain
a sample in it, so no interpolation result changes; selection is one arithmetic pass over the faces
with no per-face allocation, and the delta copy, the grid and the per-vertex constraint evaluations are
all sized by the stroke instead of the terrain. When no face meets the rectangle the commit returns
without writing, which is what the sampling loop did.

`SculptConstraintMask` now stores each region's XY bounds. A region whose distance-to-bounds lower
bound is positive and at or beyond the feather distance can neither pin the point nor pull the nearest
distance below the feather threshold, so its edges are skipped; the polygon ray cast is likewise
skipped for a point outside the bounds. Both are exact — the rejection only removes regions that cannot
change the answer.

`SculptFieldRasterizerLocalityTests` shows the same stroke writes an identical field on a 10-unit and a
40-unit terrain, that samples outside the dirty rectangle are untouched, that an off-mesh rectangle
writes nothing, and that the feather divide still records the unmasked displacement.
`SculptConstraintMaskBoundsRejectionTests` checks 3,000 points against a per-region reference over 25
scattered regions at three feather distances, plus the pinned-far-polygon and zero-feather boundary
cases.

**Confirmed pattern:** [src/MoleHill.Core/Sculpting/SculptFieldRasterizer.cs:35](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Sculpting/SculptFieldRasterizer.cs:35) allocates and fills XYZ delta data for every vertex and builds a fresh TerrainFaceGrid before sampling the dirty rectangle. [src/MoleHill.Rhino/Services/SculptSessionController.cs:460](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/SculptSessionController.cs:460) calls the rasterizer. [src/MoleHill.Core/Sculpting/SculptConstraintMask.cs:57](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Sculpting/SculptConstraintMask.cs:57) scans protection regions; evaluation includes polygon containment and segment distances.

**First task:** Profile mouse-up/commit separately from brush dabs. Hold the stroke rectangle fixed while growing the terrain. Investigate persistent XY topology lookup with a delta-Z accessor, or a dirty-face subset including all interpolation support. Index region bounds and relevant protection edges.

**Guardrails:** Preserve feathering exactly once, protected samples, undo/redo, dynamic-topology invalidation, and the hole-fill safeguards against spikes.

**Done when:** Small-stroke commit cost depends primarily on affected geometry, with rasterized field/undo regressions and native interaction validation.

## O06 — Scatter has a dense domain allocation before the sample cap helps

**Resolved 2026-09-10:** `SamplePoisson`'s backing grid is now a cell-keyed `Dictionary<long, int>`
instead of a dense `int[gridWidth * gridHeight]`. Bridson's grid holds at most one sample per cell, so
sparse occupancy costs one dictionary entry per accepted sample and no object per entry; the dense form
allocated on bounding-box area over spacing squared regardless of the cap, and its `int` product
overflowed outright on a large region with a small spacing. Grid dimensions in both `SamplePoisson` and
`SampleGrid` are now counted in doubles and clamped (Poisson to int range, so the packed cell key stays
injective) rather than cast through an `int` that wrapped negative and silently produced nothing. The
5x5 neighbourhood is visited in the same order and returns on the first violation, and the RNG stream
is untouched, so seeded output is unchanged.

Containment is prepared with per-loop bounding boxes computed once: a candidate outside a loop's box is
outside that loop, so its edges are never walked. Loop semantics (inside **any** loop) are unchanged.

`ScatterSamplerSparseDomainTests` covers a 200,000-unit region at 0.5 spacing with a 500 cap (dense
occupancy there would be hundreds of terabytes), seed determinism, minimum spacing, disconnected
regions, a narrow corridor, cancellation, and the grid pattern on a 1e9 extent.

**Confirmed pattern:** [src/MoleHill.Core/Scattering/ScatterSampler.cs:208](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Scattering/ScatterSampler.cs:208) allocates an int grid with width × height derived from bounding-box dimensions and radius, independently of the requested output cap. Large extents or tiny spacing can exhaust memory; integer dimension/product limits also deserve validation. Containment at [src/MoleHill.Core/Scattering/ScatterSampler.cs:74](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Scattering/ScatterSampler.cs:74) scans polygon loops/edges for candidate samples.

**First task:** Benchmark sparse/disconnected/narrow domains, small radius, and a low cap. Investigate sparse occupied-cell storage and safe dimension arithmetic; prepare polygon containment queries.

**Guardrails:** Preserve seed determinism, candidate acceptance order, odd/even holes, minimum spacing, cap behavior, and cancellation. A dictionary replacement is only helpful if it does not introduce another object per occupied sample.

**Done when:** A modest cap does not require a domain-sized dense allocation and seeded regression outputs or explicitly accepted invariants remain stable.

## O07 — Reuse reference projection contexts, not current-mesh statistics

**Resolved 2026-09-10:** The projection cache is now created once in the build pipeline and passed to
both `BuildAnalyses` and `BuildTerrainZones`. Each previously made its own, so a reference used by a
terrain-level analysis and by a zone comparison was indexed into a `MeshHeightProjector` twice per
build. The key (C01's) contains reference identity only — reference fingerprint, reference-terrain
fingerprint, and the fallback-base-mesh flag — so the wider scope cannot mix references. The statistics
caches stay pass-local because their key carries the current vertex and face arrays, which is what
keeps two zone pieces from sharing a volume.

`ReferenceComparisonCacheTests` gains two always-runnable key tests: the projection key varies only
with reference identity, and two pieces with identical reference inputs but different current geometry
still get separate statistics entries. The native two-mesh test already asserts one projector with two
distinct fill volumes; it remains skipped where Rhino's native runtime is unavailable.

**Confirmed pattern:** [src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:680](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:680) constructs a projection context on a comparison-cache miss; [src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:703](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:703) builds a MeshHeightProjector over the reference. Zone comparison caches are recreated per zone. Thus a common reference can be re-indexed repeatedly.

**First task:** After C01, count projector construction for multiple zones and analyses. Consider a build-local reference context keyed by resolved geometry identity/fingerprint, while statistics stay specific to current geometry and clipping inputs.

**Guardrails:** Source reference precedence, base versus finished terrain, changed reference Z/topology, fallback status, cancellation, and native mesh lifetime.

**Done when:** One unchanged reference is indexed once per appropriate scope and different current meshes still produce independent statistics.

## O08 — Prepared polygon queries have other high-volume consumers

**Resolved 2026-09-10:** `PreparedPolygon` (Core/Grading) preprocesses a loop for repeated queries and
answers exactly what the linear walks answered. Two exact accelerations: loop bounds (a point outside
them is outside the loop; a point further than `margin` from them is further than `margin` from every
edge), and — only above `IndexThreshold` (32) vertices — a Y-bucketed edge index for containment. The
crossing test toggles only for edges straddling the query's Y and parity does not depend on the order
those edges are visited in, so walking one bucket is exact. Below the threshold no index is built and
the linear walk is kept, which is the small-polygon fast path the review asked for; a loop carrying a
non-finite coordinate has no trustworthy bounds and keeps the linear walk entirely.

Wired into both named consumers: `RegionInputFilter.KeepPointsInside` (loops prepared once, then every
point rejected on bounds first — the margin test uses a threshold query rather than a full distance)
and `GradedRegionAssembler`'s centroid classification (terrain outline and every clipped loop prepared
once, before the per-face loop).

`PreparedPolygonTests` compares against `GradingGeometry2D` as the oracle: 20,000 random points per
loop size on a re-entrant star both below and above the index threshold, every vertex and five points
along every edge, a 1e6-offset coordinate range, a zero-height sliver loop, four margins for the
proximity query, and a loop with a NaN vertex.

Not done here: an accelerated **exact minimum** distance. `IsWithin` is a threshold query, which is
what both consumers need; a true nearest-distance index belongs with O13, which needs it for a
different reason.

**Confirmed patterns:** [src/MoleHill.Core/Processing/RegionInputFilter.cs:33](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Processing/RegionInputFilter.cs:33) tests every input point against polygon edges, and for outside points can also scan distance-to-polygon. [src/MoleHill.Core/Grading/GradedRegionAssembler.cs:284](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Grading/GradedRegionAssembler.cs:284) classifies face centroids against terrain and clipped loops.

**First task:** Benchmark detailed boundaries with 100k/1M input points and growing loop vertex counts. Add bounding rejection and prepared containment/distance queries where useful. Establish a small-polygon fast path rather than always paying index overhead.

**Guardrails:** Work-region margins, boundary inclusion, holes, tolerance and large-coordinate behavior. Share mechanics only where containment semantics agree.

**Done when:** Oracle comparisons at edges/vertices pass and preprocessing no longer dominates the triangulation it is intended to reduce.

## O09 — Cancellation needs to penetrate heavy Core work

**Resolved 2026-09-10:** `CancellationProbe` (Core/Engine) gives the heavy stages a bounded-interval
cancellation check. `ThrowIfCancelled` runs at phase and round boundaries; `ThrowIfCancelledOften`
sits inside the per-face and per-vertex loops and consults the callback every 4,096 iterations, so it
costs a decrement on the hot path.

`IsotropicRemesher.Options.ShouldCancel` is checked before the feature graph, between all four phases
of every outer iteration, at the top of each split round, collapse round and flip sweep, and inside the
split face scan, the collapse candidate scan and acceptance loop, the flip enumeration, and the relax
vertex loop. `MeshAreaTopologySplitter.Split` takes an optional callback checked between its phases
(boundary segments, face mapping, shared-edge registry, classification) and inside the per-face
triangulation loop; `MeshAreaSplitter.SplitPreservingTopology` passes it through.

Cancelling **throws** `OperationCanceledException` rather than returning a partial result. That is
already the Rhino pipeline's contract (`ThrowIfCancellationRequested`, caught in
`TerrainController.Build`), and it directly satisfies the guardrail: a stage abandoned part-way has no
valid output, so there is nothing a caller could mistakenly publish to a cache.

Wired into the Rhino callers: the Remesh modifier stage passes `ModifierBuildContext.ShouldCancel`
into the isotropic remesh, and the zones stage passes its `shouldCancel` into the topology splitter.

`CoreStageCancellationTests` covers the probe itself (never-cancelling shared instance, per-call versus
interval checking), immediate and part-way cancellation of both stages, and — importantly — that a
non-cancelling probe leaves both stages producing exactly the output they produced without one.

Not measured here: cancel-to-stop latency under real load, active worker counts, and retained bytes.
Those need the native-Rhino harness the benchmark plan describes; this item adds the mechanism the
measurement requires.

**Evidence:** [src/MoleHill.Rhino/Services/TerrainController.Build.cs:194](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainController.Build.cs:194) creates cancellation for background work, but the inspected MeshAreaTopologySplitter and IsotropicRemesher entry paths do not expose cancellation checks in their heavy loops.

**Hypothesis:** Superseded builds can continue consuming CPU and holding large buffers until a stage returns. Measure this; do not infer simultaneous active computation merely from retired-task bookkeeping.

**First task:** Trigger a new edit during remesh and zone mapping. Measure cancel-to-stop latency, active workers, and retained bytes. Consider optional cancellation at bounded batch/round intervals and parallel-loop integration.

**Guardrails:** No partial cache publication, corrupted shared TinEngine state, or premature disposal of meshes still read by workers.

**Done when:** Cancellation is timely under load and rapid edits leave only the latest valid result.

## O10 — Measure geometry lifetimes across cache, display, and snapshots

**Resolved 2026-09-10 (scoped):** The `Clone` callers were traced. One redundant copy is *provable*
from the code without a memory profiler, and it is the one on a hot path; it has been removed. The
rest of this item's measurement work is explicitly **not** done — see below.

`GradingTopologyCacheEntry` holds the retained grading topology: whole-mesh `Vertices` (3 doubles per
vertex) and `Faces`. Every member is `init`-only, and the codebase already treats entries as immutable
— `CreateWorkerCopy` and `ReplaceBuildCachesFrom` pass them between the UI-owned cache and worker
copies **by reference**. The Grade Pad stage was the outlier, deep-copying the whole topology twice:
once on every hot-cache hit, and once again when storing an entry it had just built from arrays it
exclusively owned. Its consumers only read — `PadGrader.ApplyGradingZ` writes into its own fresh clone
in every overload, and mesh building reads — so both copies are gone and the invariant is documented
on the type.

`GradingTopologyImmutabilityTests` pins what the sharing rests on: every `ApplyGradingZ` overload
leaves its input vertices and faces byte-identical, returns a distinct array, and repeated calls on the
same retained arrays return the same result (drift here is exactly what reusing one topology across
builds would expose).

**Deliberately not done:** cold-build / cache-hit / repeated-edit / undo / retired-worker memory
snapshots, the managed-versus-native split, and the simultaneously-live-copy census. Those need the
native Rhino harness (`docs/rhino-live-testing.md`) and a profiler; nothing here should be read as
having measured peak memory. `TerrainDisplayState.Clone` and the stage mesh clones were inspected and
left alone: each has a live consumer with a distinct lifetime, and the review's own guardrail is that
the deliberate mesh sharing in stage worker copies must not be "fixed" into deep copies.

**Evidence:** [src/MoleHill.Rhino/Services/TerrainDisplayState.cs:307](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainDisplayState.cs:307) clones terrain/base/preview meshes and generated objects. [src/MoleHill.Rhino/Services/TerrainRuntimeCache.cs:600](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainRuntimeCache.cs:600) clones retained grading topology arrays. Existing stage worker copies deliberately share mesh outputs; that optimization must not be “fixed” back into deep copies.

**First task:** Trace actual Clone callers and take cold-build, cache-hit, repeated-edit, undo, and retired-worker memory snapshots. Separate managed arrays from native Rhino mesh/GPU memory. Identify simultaneously live copies and ownership before changing anything.

**Guardrails:** Immutable sharing needs explicit lifetimes. Preserve worker retirement, undo isolation, native disposal, and cache invalidation.

**Done when:** A measured redundant copy or retention path is removed with lifecycle tests; avoid a speculative global cache redesign.

## O11 — Waterflow setup and path traces can be separated

**Resolved 2026-09-10:** Setup and tracing are now separated, and the traces run together.

`FaceSpatialIndex` had mutable candidate scratch on the index itself, which the review correctly noted
made it unshareable. Those buffers moved into a per-caller `QueryState`, leaving the index immutable
and shareable; `FindContainingFace` takes the state instead of allocating a candidate list per start.
With that, the start loop parallelises above a threshold (at least 8 starts and enough starts x faces
to cover partition overhead — a handful of starts on a large mesh stays serial). Each worker gets its
own `QueryState`; every trace is a pure function of read-only vertices, faces, neighbours and its own
start, so results are written by start index and compacted in order afterwards. Path order, path
content and the rejected count are therefore identical to the serial loop. Cancellation raised inside
the parallel body is unwrapped from `AggregateException`, so callers still see
`OperationCanceledException`.

Setup allocation also dropped: the neighbour array is filled with `Array.Fill` instead of a LINQ
`Enumerable.Repeat(...).ToArray()`, and the edge dictionary is presized to about the edge count instead
of rehashing up from empty on every call.

`WaterflowTracerParallelStartTests` uses the serial path as the oracle — 400 starts traced together
versus the same starts traced one per call — plus run-to-run identity, rejected starts not shifting the
surviving paths, cancellation on the parallel path, and the empty-start case.

**Confirmed patterns:** [src/MoleHill.Core/Analysis/WaterflowTracer.cs:77](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Analysis/WaterflowTracer.cs:77) builds adjacency and a spatial index per call, then traces starts serially. [src/MoleHill.Core/Analysis/WaterflowTracer.cs:226](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Analysis/WaterflowTracer.cs:226) retains an edge dictionary while constructing a flat neighbor array. Per-path visited-face sets are allocated.

**First task:** Separate setup from trace timing, varying faces and start count independently. Investigate reusable prepared topology and deterministic parallel starts with worker-local query scratch. Current FaceSpatialIndex has mutable candidate scratch, so it cannot simply be shared across parallel calls.

**Guardrails:** Stable start/output order, sinks, non-manifold neighbor semantics, cycle termination, maximum path length, cancellation.

**Done when:** Multi-start workloads improve without multiplying full-mesh scratch per start or altering path results.

## O12 — Contour stitching allocates per node and retains raw segments

**Resolved 2026-09-10:** The per-node allocation is gone. `StitchSegments` kept node incidence in a
`Dictionary<int, List<int>>` — a list object plus its backing array for every welded node, and a
segment-heavy job has roughly as many nodes as segments. It is now flat CSR (degree count, prefix sum,
fill in segment order), which also makes `Degree` an O(1) subtraction instead of a dictionary lookup
per sort comparison. Chain walking uses two reusable `List<int>` buffers instead of a `LinkedList<int>`
node per point: `forward` grows from the seed's second node, `backward` from its first, and the emitted
order is `backward` reversed then `forward` — exactly what `AddFirst`/`AddLast` produced. Seed ordering
replaces a LINQ `OrderBy` with an index-array sort keyed on precomputed degrees plus an explicit index
tiebreak, which reproduces `OrderBy`'s stability.

**Equivalence evidence:** contour output was fingerprinted (SHA-256 over every polyline's closed flag,
point count and full coordinate array, plus level and polyline counts) across six mesh-size x
level-interval configurations, up to 1,421 polylines. All six are byte-identical before and after.
`ContourStitchingTests` covers a closed ring not repeating its first point, an open chain running edge
to edge without splitting, every emitted point sitting at its level, run-to-run identity on a
segment-heavy job, and levels outside the mesh emitting nothing.

Not done: releasing or processing levels in bounded batches. Peak live segment buffers are unchanged —
that trade costs extra mesh scans, which the review itself flags, and needs the measurement of emitted
segment counts it asks for first.

**Confirmed patterns:** [src/MoleHill.Core/Analysis/ContourGenerator.cs:30](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Analysis/ContourGenerator.cs:30) collects segment coordinates for levels; [src/MoleHill.Core/Analysis/ContourGenerator.cs:146](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Analysis/ContourGenerator.cs:146) allocates endpoint arrays plus a dictionary of adjacency lists. The marching pass already skips levels outside each face's Z range.

**First task:** Profile emitted segment count, level count, stitching time, and peak live buffers. Compare compact node adjacency and releasing/processing levels in bounded batches. Batching must account for extra mesh scans.

**Guardrails:** Quantized weld behavior, open versus closed contours, branch handling, stable chain order, and level elevation.

**Done when:** Segment-heavy contour jobs use less peak memory without regressing the existing single-pass advantage.

## O13 — Seam deviation is a direct all-pairs nearest-segment scan

**Resolved 2026-09-10:** `SeamValidator.ComputeLoopDeviation` now measures through
`SegmentProximityIndex` once the target loop passes `IndexThreshold` (64) segments; smaller loops keep
the linear scan, so a short loop pays no index.

The index is **exact**, which the guardrail requires: a fixed-radius query cannot report the true
distance for a point that misses everything by a long way. It grows a query box and stops only when
the best distance found inside is no larger than the box half-width — at that point every unexamined
segment is outside the box, so its Chebyshev and therefore Euclidean distance exceeds the best found.
A box that outgrows the whole indexed extent falls back to scanning every segment, so distant misses
report their real distance. Distances come from `SeamValidator.DistancePointToSegment` itself rather
than a second implementation, so an indexed answer is bit-identical to the scan's — nearest distance,
max deviation, miss count and tolerance behaviour are all unchanged, and the closing segment of the
loop is indexed like any other.

`SeamLoopDeviationIndexTests` uses the all-pairs scan as the oracle: a detailed loop pair both below
and above the threshold, a source 5,000 units away from its target, 500 mixed near/far sources against
a 400-vertex star, a source lying exactly on its target, the closing segment specifically, and a
degenerate target.

**Confirmed pattern:** [src/MoleHill.Core/Grading/SeamValidator.cs:101](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Grading/SeamValidator.cs:101) checks each source-loop vertex against every target-loop edge. That is O(S × T); it becomes quadratic when both loop sizes grow together.

**First task:** Establish call frequency and loop sizes on real grading jobs, then compare an exact nearest-segment index against the current oracle.

**Guardrails:** A fixed-radius query alone cannot return the true maximum deviation for distant misses. Preserve nearest distance, miss count, closed-loop handling, and tolerances.

**Done when:** Detailed-loop scaling improves while deviation metrics match within a justified numerical bound.

## O14 — Cross-field diffusion can run 2,000 full passes

**Resolved 2026-09-10:** `Options.Iterations` is now a maximum. Each Gauss-Seidel sweep tracks the
largest per-vertex change of the 4-RoSy representative and exits once it falls below
`Options.ConvergenceTolerance` (default 1e-7; the representative is a unit vector, so that is about
2.5e-8 rad in θ — far below anything downstream resolves). The residual is accumulated in the same
fixed order as the in-place updates, so the stopping point is deterministic. Traversal order,
in-place updates and pinned vertices are untouched; no Jacobi, no parallelism — the review is right
that those would be algorithm changes.

**Residual versus iteration, measured.** Two families, comparing a converged run against the same run
with the tolerance disabled:

| Case | Vertices | Budget | Sweeps run | Final residual | Max θ difference |
|---|---|---|---|---|---|
| Regular sheet | 400 – 14,400 | 80 – 480 | **1** | 9.8e-17 | **0** |
| Disc (boundary tangent sweeps all directions) | 1,201 – 19,201 | 138 – 554 | all | 3e-5 – 8e-4 | **0** |

The useful finding is the second row: on a shape whose field genuinely has to diffuse, the field has
**not** converged when the budget expires — the residual is still ~1e-4. So on those meshes the
iteration budget, not the stopping rule, is what binds, and the rule correctly does not fire. The
saving is real on the axis-aligned sheets that terrain meshes usually are (one sweep instead of
hundreds, θ bit-identical), and the cost elsewhere is one subtraction per free vertex per sweep. Any
future work on the disc-like cases is a **quality** question — whether the budget is large enough —
not a scheduling one.

`CrossFieldConvergenceTests` covers: stopping short of the budget, converged output matching the
exhausted run on a regular and an irregular mesh, the disc case running its whole budget with
identical output, determinism (sweeps, residual and θ all reproducible), pinned vertices unaffected, a
low explicit iteration count still honoured as a maximum, and θ staying in [0, π/2).

**Confirmed pattern:** [src/MoleHill.Core/Retopo/CrossFieldSolver.cs:99](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Retopo/CrossFieldSolver.cs:99) derives the iteration budget from vertex count, clamped to 50–2,000, and performs in-place Gauss–Seidel updates without a convergence exit in the inspected loop.

**First task:** Record residual/change versus iteration and downstream quad quality. Investigate a convergence criterion or accelerated solver before simply increasing threads.

**Guardrails:** In-place updates depend on traversal order. Parallel Jacobi is an algorithm change, not an equivalent scheduling change. Preserve pinned features and evaluate field singularities and resulting mesh quality.

**Done when:** A justified stopping rule or solver reduces work on representative cases with quality and determinism validation.

## O15 — Scatter preview may become draw-call bound

**Not implemented 2026-09-10 — deliberately.** This is the one item in the batch whose "done when" is a
*measured frame-time improvement with real Rhino viewport verification*, and that measurement has not
been made. Changing the draw path without it would be exactly the speculative change the review warns
against, so nothing here was touched.

**What the code inspection found**, for whoever picks this up with a viewport attached:

- `TerrainDisplayConduit.DrawScatterObjects` submits **one draw call per drawn item** —
  `DrawPoint` per instance in Points mode, `DrawPoint` per shape point in ShapePoints mode (so an
  instance with a 200-point shape is 200 submissions), `DrawBox` per instance, and instance geometry
  otherwise. Batching Points and ShapePoints into one `DrawPoints` call per resolved colour is the
  obvious candidate and would preserve the drawn set exactly.
- The blocker on doing it blind is appearance, not correctness: `DrawPoint(point, color)` uses
  RhinoCommon's default point style and radius, and a batched `DrawPoints` overload has to be given
  those explicitly. Guessing them changes how the preview looks, which is a user-visible regression
  that cannot be checked without a viewport.
- The per-frame CPU work outside submission is already small: the colour, instance-definition, box and
  shape-point lookups are all cached in `ScatterPreviewDrawFrame` for the frame, and preview caps are
  applied before drawing.

**Required before changing anything:** native viewport frame time at increasing visible counts,
separately for point, shape, box and instance modes (`docs/rhino-live-testing.md`), to establish
whether draw submission actually dominates. Do not remove the existing caps to demonstrate throughput.

**Evidence:** [src/MoleHill.Rhino/Services/TerrainDisplayConduit.cs:177](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Rhino/Services/TerrainDisplayConduit.cs:177) builds frame state and iterates capped instances; shape-point mode transforms and draws individual points. Preview caps and frame helpers already exist.

**First task:** Native profiling of viewport frame time at increasing visible count, separately for point, shape, box, and instance modes. Evaluate batched drawing and cached immutable transformed data only if draw submission dominates.

**Guardrails:** Colour/layer overrides, visibility, block edits, transforms, cap semantics, and memory ownership. Do not remove existing caps to demonstrate throughput.

**Done when:** Measured frame-time improvement with real Rhino viewport verification, not just a faster Core loop.

## O16 — Finish measuring the improved zone splitter

**Evidence:** [src/MoleHill.Core/Grading/MeshAreaTopologySplitter.cs:1](C:/Users/hbxma/Dropbox/TopoTest/src/MoleHill.Core/Grading/MeshAreaTopologySplitter.cs:1) still needs mesh-sized cut slots, a global vertex lookup, vertex storage, output chunks plus a final contiguous face array, and a serial output-emission/registry pass. The on-demand FaceData change only made that setup constant-space. Indexed boundary pair discovery can still have high candidate counts for heavily overlapping boxes.

**First task:** Reuse its PerformanceTimings instrumentation on the actual workload and synthetic families. Measure phase allocations, peak live memory, touched-face ratio, candidates, and output growth. Evaluate sparse cut storage or more selective processing only after knowing their density and lookup cost.

**Guardrails:** Sparse dictionaries can lose to flat arrays; extra parallelism can increase memory. Preserve exact shared-edge subdivisions and insertion-order welding. Do not describe the entire algorithm as constant-memory.

**Done when:** A full-scale baseline exists and the next change addresses the measured dominant phase rather than the previously dominant phase.

## Benchmark and acceptance plan

Use independent scale axes so unrelated growth does not hide complexity:

| Axis | Suggested progression | Purpose |
|---|---|---|
| Terrain faces | 100k → 1M → 5M; actual 15M only within available memory | Setup, copies, topology structures |
| Boundary segments | 100 → 1k → 10k → 50k at fixed terrain | Pair discovery and polygon queries |
| Zone pieces | 1 → 10 → 100 at fixed result mesh | Extraction and reference reuse |
| Spatial distribution | Uniform, coarse/fine, long diagonal, tiny zone in large face | Grid pathologies |
| Local edit | Fixed stroke footprint, growing terrain | Local versus global work |
| Generated output | Levels, waterflow starts, scatter caps varied independently | Output-sensitive costs |

For each task, record Release configuration/runtime, machine/worker count, input counts, cold and warm timings, process-wide managed allocations, peak managed/native memory, and the relevant candidate/index counts. Use warmups and repeated runs; report median and spread. Current-thread allocation alone misses parallel allocations. Total allocated bytes are not peak live memory.

Begin with existing Core tests and benchmark fixtures (LargeTerrainPerformanceBenchmarkTests, LargeDatasetBenchmarkTests, plus the feature-specific tests). Confirm opt-in performance tests actually execute: some return early unless MOLEHILL_PERF=1. Add meaningful geometry or cache regressions before implementation. Use native Rhino tests for host ownership, normalization, cancellation, and preview; a skipped native test is not runtime evidence. Follow docs/rhino-live-testing.md for those runs.

A completed deep dive should leave: a reproducer, before/after measurements, a bounded change, relevant correctness tests, and an updated status in this backlog. No fixed speedup target should be promised before the baseline.

## Coverage limits and work not recommended yet

The survey covered input filtering, remesh/refine, grading helpers, zones and host extraction, reference analysis, contours, waterflow, sculpting, scatter, retopo, and selected cache/display paths. Snapshot construction was sampled; serialization, persistence, GeoTIFF/LandXML I/O, the vendored Triangle.NET engine, and every individual annotation/Grasshopper component were not exhaustively audited.

Existing protections worth preserving include indexed path/daylight search, sorted topology analysis, correct packed-edge comparers in major mesh loops, CSR collapse adjacency, native TIN adjacency, bulk mesh fingerprints, normalized cache restores, and contour marching restricted by face Z range. No evidence from this review justifies replacing those wholesale.

Keep reusable algorithms in Core and native operations in hosts. Prefer focused fixes with measured benefit over a universal spatial-index or topology framework.
