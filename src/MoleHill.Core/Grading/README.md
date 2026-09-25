# MoleHill.Core/Grading

Pad and path grading. **Invariant: grading output is always a watertight 2.5D mesh - never holes or
spikes.** Pure, unit-tested. See `docs/architecture.md` for the tier cascade overview.

`BracketedVolumeSearch` implements the B7 bounded net cut/fill search: both endpoints are measured,
an unbracketed target returns the nearer end, and bisection records every sample with distinct
`Converged`, `NoBracket`, `IterationCap`, `NonMonotone`, and `GradingFallback` outcomes.
`PadElevationBalancer` translates one planar pad boundary in Z and evaluates each candidate through
`PadGrader.Grade`, returning the actual best graded mesh and adjusted input boundary. GH integration
and the first example graph remain B7 work.

## Tier cascade (Pad - `PadGrader.Grade`, takes the first watertight result)
1. **explicit batter** - `PadGrader.Explicit.cs` (exact ruled side-slopes; crispest).
2. **split-keep** - `GradeWithSplitKeep` + `GradedRegionAssembler.SplitConform` (conform terrain to
   daylight/footprint loops, keep whole mesh, assign Z by section; watertight by construction; CDT
   conform fallback when the hand-rolled splitter goes non-manifold).
3. **region-remesh** - `PadGrader.RegionRemesh.cs` (fresh dense patch graded by distance field; soft).

If all three tiers defer, `PadGrader.Grade` fails cleanly with a structured diagnostic instead of
emitting a non-watertight result.

## Grade Line (the path cascade at width zero)

A `PathDefinition` with `Width == 0` is a **single line**: the drawn curve is the footprint and both
rails collapse onto it. `BuildCorridors` then walks the line out and back into one station ring — the
forward pass rays left, the backward pass right — and the whole cascade below runs unchanged. The
footprint has no area while the daylight loop does, so split-keep's footprint loop is skipped and the
corridor conforms to its daylight envelope alone. Spacing comes from the batter reach
(`ComputeSingleLineSegmentLength`) since there is no width to set it, and exactly one output polyline is
published: a line has one design line, not two road edges.

**Per-side batters.** `LeftCutSlopeAngleDeg` / `LeftFillSlopeAngleDeg` / `RightCutSlopeAngleDeg` /
`RightFillSlopeAngleDeg` are optional overrides; zero inherits the shared cut/fill pair, so a symmetric
definition is bit-identical to one without them. They must be honoured in **all three** places that
compute a batter, not just the daylight ray: `BatterStripBuilder.BuildDaylightLoop`'s optional
per-station angle array, `BuildPathSections`' per-side endpoint solve, and `PreparedPath.SlopeRatioFor`
during the elevation pass. Missing the last one flattens an asymmetric section back to symmetric with no
error anywhere.

There is no per-side *enable*, and adding one would be wrong: a line at an authored elevation is a
discontinuity, so a side with no batter would run from that elevation straight to the nearest existing
vertices. "No grading on this side" is instead what `DaylightStatus.Flat` already reports where the
terrain meets the line.

`OutwardNormals` supplies explicit per-vertex outward directions and makes the grade **one-sided** — the
retaining-wall case, where a rail batters away from its partner rather than along its own plan normal.
Such a rail keeps the stationing it arrived with (the planner's), because the normals are supplied one
per authored vertex and resampling would leave them misaligned.

**One-sidedness has to be honoured in the elevation pass too, not just the carve.**
`PathDefinition.OutwardSideSign()` derives which side (+1 left, -1 right, 0 both) from `OutwardNormals`,
and both elevation paths gate on it: `TryComputePathInfluence` and — the one that actually runs under
split-keep — `TryComputePathSectionInfluence`. Place the gate *after* each function's on-rail early
return, or the rail loses its own pin and drops to existing ground. Without the gate a wall's upper rail
also grades the ground below the wall: found live, where the toe side rose to 4.5 instead of falling to
0, while the top side measured perfectly. `Grade_WallRails_EachBatterStaysOnItsOwnSideOfTheWall` covers
it.

**A one-sided grade always defers the explicit tier to split-keep, deliberately.** Its rail lies *on*
the carve boundary rather than inside it, so the explicit fill carries that rail twice — once as a
boundary vertex at terrain elevation, once as a rail vertex at the authored elevation — and the weld
keeps the terrain one, silently flattening the rail to existing ground. Found live on a retaining wall,
and worth knowing how it presented: the batter above the rail was *correct*, computed from the authored
elevation, so only the rail row itself was wrong. Nothing threw, the build reported a clean watertight
result, and the cut direction was unaffected — it only showed in fill. Split-keep conforms the rail in
place and gets it right, so the explicit tier declines the case rather than welding a wrong elevation.

## Tier cascade (Path - `PathGrader.Grade`, takes the first result)
Optional plan edges are resolved by `VariablePathWidthResolver` before this cascade — only when the
caller opts in (the Rhino modifier's `UseVariableWidth`; in Grasshopper, by wiring Width Edges). It uniquely
matches edges to centerline sides using closure, distance, ordered station, side, and ambiguity checks;
valid partial runs blend into the constant fallback width. Edge Z is ignored. The resolver emits aligned
center/left/right rows carrying centerline-authored elevation, and every tier below consumes those same
rails for the path top, constraints, and daylight starts. Longitudinal station spacing remains controlled
by the configured Width rather than the widest transverse rail reach, preserving explicit ruled-batter
density when variable edges flare outward.

1. **explicit corridor** - `PathGrader.Explicit.cs` (ruled road surface + side batters welded into
   terrain via carve/fill; exact slopes, crispest). Hole fills are seeded with batter rows AND
   per-station rows against the actual conformed boundary (density-guarded) so barrier-clipped or
   envelope-simplified regions cannot span long steep slivers.
2. **split-keep** - `PathGrader.SplitKeep.cs` + `GradedRegionAssembler.SplitConform` (conform the
   corridor daylight envelope + road-edge footprint loops in place, keep the whole mesh, reassign Z
   via `ApplyGradingZ`; watertight by construction - no hole tracing, so it also covers corridors
   whose daylight reaches the terrain edge).
3. **constraint insertion** - `PathGrader.Patches.cs` (local topology insertion + Z-only grading;
   last resort - diagnostics record why the upper tiers deferred).

**Every tier has a floor, including the last one.** Tier 3 used to be allowed to emit unhealthy
topology, on the reasoning that a last resort is better than nothing. It is not: a torn mesh returned
with a null error is indistinguishable downstream from a clean grade, and the Rhino retaining-wall
stage then rebuilds from it and loses the terrain (measured: 2,828 faces to 249, taking the upstream
Remesh with it). Tier 3 now validates its own output through
`GradingTopologyDiagnostics.IsNotWorseThanInput` and returns null with a message instead.

**What it rejects is non-conforming connectivity, not missing terrain.** The failure that motivated
this reported "extra boundary loops" while total surface area was unchanged to the last digit and no
face was lost: constraint insertion had split an edge in one face and not in its neighbour, leaving a
vertex inside the neighbour's sub-edge. That sub-edge is used once, so boundary analysis counts it as
naked. `MeshConstraintTopologyInserter.ConformSharedEdgeSplits` now reconciles both incident faces to
the same ordered split set, so shared edges conform by construction.

The bar is **not worse than the input**, never absolute health: a terrain may legitimately carry an
interior hole, and such a terrain has more than one boundary component before any grading runs.
What is never legitimate is a grade that *adds* a loop, opens a naked-edge chain, or tears the mesh
non-manifold. `RetainingWallRailGradeContractTests` pins this across a swept slope angle on captured
wall rails; several angles still fail to grade, which is a known open geometry defect in the tiers
above - the contract is that they fail *visibly*.

The optional `preferSplitKeep` performance flag reverses the first two attempts for large constrained
meshes that are likely to reject explicit carve/weld assembly. Rhino final builds enable it only for
meshes with at least 10,000 faces and a persistent hard constraint that crosses or overlaps the
corridor's own constraints; the default Core contract remains explicit-first. A hard constraint merely
*existing* is not enough: a distant Grade Pad's boundary once flipped a path into split-keep, which
graded it as a near-vertical wall where explicit assembly succeeds
(`GradePathDistantHardConstraintCopiedCaseTests`).

**`SplitConform` accepts the hand-rolled split only when it leaves topology no worse than the input.**
It used to accept anything that was not non-manifold, but the same "cut point a hair off an existing
vertex" situation also yields a plainly NON-CONFORMING edge -- one face split, its neighbour not, a
vertex left inside the neighbour's sub-edge. Nothing non-manifold, no area lost, but the terrain gains
a boundary loop, split-keep's own gate rejects it, and the corridor drops to the constraint-insertion
tier, which does Z-only grading and no ruled batter at all. Falling through to `SplitConformViaCdt`
(valid by construction) costs terrain detail and is still far better than an invalid mesh.

`GradedRegionAssembler.SplitOutside` (used by both explicit tiers) repairs pinched/branched hole
boundaries by pulling outside faces at irregular vertices into the carve region, and falls back to a
single-CDT re-conform (`SplitConformViaCdt`) when the hand-rolled splitter emits an untraceable
(overlapping-sliver) boundary — as happens when the daylight loop reaches the terrain edge. That
re-conform re-triangulates the WHOLE terrain, so it must re-insert the caller's hard-constraint
breaklines (retaining walls) as exact constraint edges; otherwise the CDT flips away the near-vertical
wall-face edges and orphans wall-top vertices into tent-pole spikes. `SplitOutside`/`SplitConform`
therefore take the `hardConstraints` list and thread it through, and **both** graders pass it: a pad
whose daylight reaches the terrain edge re-conforms the whole terrain, so a wall on the far side of
the site is just as exposed as one beside a path. `PadGrader.Grade`'s widest overload takes the list
as a trailing optional argument and hands it to the explicit and split-keep tiers;
`GradePadRetainingWallRegressionTests` pins the re-conform with and without it. Path shoulder sections whose daylight ray runs off the surveyed terrain cap
AT the terrain boundary with the terrain's own elevation (`PathGrader.Sections.cs`) instead of
staying unresolved.

`PadGrader` is split across `PadGrader.*.cs` partials (`.Explicit`, `.RegionRemesh`, `.Surfaces`,
`.Daylighting`, `.Spatial`, `.Support`, `.Types`, ...). `PathGrader.*.cs` is the corridor analogue.

## Shared building blocks
- `MeshAreaTopologySplitter` supplies face geometry on demand and maps indexed boundary segments in
  parallel with face-local scratch and stable segment order. Grid queries clamp to the indexed extent,
  including when a large terrain face encloses a tiny zone. The shared-edge registry conforms neighbors;
  tests require every single-use edge to remain on the original terrain perimeter. Output face indices
  grow in fixed-size chunks, and global vertex lookup uses cell head/tail links instead of per-cell lists,
  preserving original insertion order and nearest-point ties. Mesh-sized buffers still require linear memory.
  **Measured (2026-09-10, `MeshAreaTopologySplitterScalingBenchmarkTests`, opt-in `MOLEHILL_PERF=1`):**
  the dominant phase at scale is now `OutputSetup` — 58% of a one-million-face split, and the only phase
  that grows with terrain size regardless of the workload. Its cost is `GlobalPointLookup` registering
  every vertex with a cell size equal to the model tolerance, so nearly every vertex occupies its own
  cell. Boundary-segment count is essentially free now, and touched-face triangulation scales with
  touched faces, as it should. See `docs/terrain-scalability-review-2026-09-09.md` O16 for the tables.
- `TerrainFaceGrid` stores cells as flat CSR (key -> slot, slot -> a run of face indices) built by a
  count pass then a fill pass, both in face order: the grid is immutable once built, and a `List<int>`
  per occupied cell cost millions of small objects on a large terrain. Its ray candidate buffer no
  longer carries a face-sized stamp array - that buffer is thread-static, so every worker that ever ran
  a ray query held tens of megabytes for the process lifetime. Candidates are already sorted into source
  face order before use, so the same sort removes the duplicates a multi-cell face contributes.
- `MeshAreaSplitter` classifies centroids with indexed rightward-ray queries, retaining the original
  point-in-polygon arithmetic and highest-area-index overlap priority. Its boundary proximity index is
  also used when callers request a nonzero tolerance.
- `ScaleAwareTolerance` (Engine) and the daylight solvers use relative convergence and fixed sample-count
  policies. Pad/path grading therefore follows the same branch decisions after uniform unit scaling.
- `GradingGeometry2D.cs` - **the** 2D primitives (point-in-polygon, distance-to-polygon,
  interior point, segment intersection, Z interpolation). `PadGrader.Spatial.cs` are thin public compat
  wrappers over it.
- `SegmentProximityIndex.cs` - **exact** nearest-segment distance for a closed loop queried many
  times (seam deviation). Grows a query box until the best distance found inside it is no larger than
  the box half-width, at which point everything unexamined is provably further; a box that outgrows the
  whole extent falls back to a full scan, so a distant miss still reports its true distance rather than
  a clamped radius. It calls `SeamValidator.DistancePointToSegment` deliberately, so an indexed answer
  is bit-identical to that caller's scan. Loops under `IndexThreshold` segments keep the scan.
- `PreparedPolygon.cs` - the same two answers as `GradingGeometry2D.PointInPolygon` /
  `DistanceToPolygon`, for loops queried many times. Rejects on loop bounds, and above
  `IndexThreshold` vertices walks only the edges bucketed at the query's Y (crossing parity is
  order-independent, so the restricted walk is exact). Below the threshold the linear walk wins and no
  index is built - keep that fast path. A loop with a non-finite coordinate has no trustworthy bounds
  and keeps the linear walk entirely.
- `BatterStripBuilder.cs` - daylight loop + ruled batter strip (pad and path). `BuildDecimatedCarveXy`
  collapses a lock-curve-clamped daylight run back onto the wall breakline's own (terrain) vertices, so
  the carve loop rides a retaining wall instead of re-slivering its face (the strip seeds stay dense).
  Its terrain-ray solve uses `TerrainFaceGrid`'s finite grid corridor, restores source face order before
  resolving hits, and retains a complete-scan fallback for invalid numeric bounds.
- `PathGrader.ApplyZ.cs` - closest-path evaluation. Resampled paths with at least 64 segments retain a
  segment-bounds grid and query only the maximum-influence neighbourhood; shorter paths keep the
  measured-faster linear loop. Candidate ids are sorted before evaluation to preserve tie behaviour.
- `GradedRegionAssembler.cs` - `SplitOutside`/`SplitConform` (terrain split) + `WeldGradedRegion`
  (identity weld of fills into terrain) + `AssembledMesh`.
- `MeshAreaTopologySplitter.cs` / `MeshAreaSplitter.cs` - conforming terrain subdivision along loops.
  Segment/triangle mapping uses one fixed-buffer three-edge pass instead of per-candidate lists and
  duplicate intersection calls; opt-in diagnostics split allocation by preparation, mapping, touched-face
  triangulation, and classification.
- `TerrainBoundaryTrimmer.cs` - inserts Outer/Hide/Show edges in one conforming split, applies fixed
  `Outer && (!Hide || Show)` precedence, compacts every surviving island into one mesh, and rejects open
  or non-manifold output.
- `MeshConstraintTopologyInserter.cs` - local constraint insertion (terrain-preserving); intersection
  results are value types in its allocation-sensitive inner loops. Face geometry is a `readonly struct`
  built on demand for candidate faces only (never an object per terrain face), constraint-segment pairs
  are discovered through a `SpatialHashGrid2D` rather than an all-pairs sweep, and the input arrays are
  cloned only on the paths that return them unchanged.
  Its `.WallPatch.cs` and `.WallQuality.cs` partials are the retaining-wall quality path: the patch is
  re-triangulated as a whole against its own perimeter, adjacent untouched triangles are bisected to stay
  conforming, and elevations are sampled from a lifted wall reference. `.WallQuality.cs` expands the
  patch ring by ring and validates achieved quality, area, perimeter and vertex retention before
  publishing; the per-face path above is the fallback when it declines. See
  `docs/wall-pinch-investigation.md` for background and the rejected alternatives.
- `FaceOwnerGroups.cs` / `SubMeshVertexRemap.cs` - sub-mesh extraction support for the hosts. Group a
  split result's faces by owner **once** (`FaceOwnerGroups`) and reuse one `SubMeshVertexRemap` across
  the extractions: rescanning every face per area is O(areas x faces), and a fresh hash set plus
  dictionary per area allocates two whole-vertex-set structures each time. Group face order is ascending
  (what a linear scan produced) and the remap claims vertices in first-touch order, so extracted topology
  and vertex ordering are unchanged.
- `MeshBoundaryLoopBuilder.cs` - ordered single/multi-loop extraction over the shared flat
  `MeshTopologyValidator` result. Split-keep carries its conformed analysis into Z application instead
  of rebuilding the boundary graph.
- `ClipperGeometry.cs` - polygon boolean/offset (Clipper) for daylight unions and offsets.
- `GradingDiagnostic.cs` / `MeshTopologyOperations.cs` - diagnostics + watertight repair helpers.
- `ConstraintCoincidenceSnapper.cs` - snaps constraint points onto nearby terrain vertices and edges
  before the constraints go into a triangulation. Build it **after** the constraint list exists, through
  `ForConstraints`, not at the top of the method: it then indexes only the terrain those constraints can
  reach instead of every edge in the mesh, which was measured at 0.91 s of a 1.00 s
  `PadGrader.CreateConstraints` on a 186k-edge terrain. The clipping is exact — a query whose tolerance
  box lies inside the region can only be won by a member that met the region — and a query that escapes
  the region discards it and rebuilds over the whole mesh, so a wrong region costs speed, never
  geometry.
