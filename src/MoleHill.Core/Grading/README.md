# MoleHill.Core/Grading

Pad and path grading. **Invariant: grading output is always a watertight 2.5D mesh - never holes or
spikes.** Pure, unit-tested. See `docs/architecture.md` for the tier cascade overview.

## Tier cascade (Pad - `PadGrader.Grade`, takes the first watertight result)
1. **explicit batter** - `PadGrader.Explicit.cs` (exact ruled side-slopes; crispest).
2. **split-keep** - `GradeWithSplitKeep` + `GradedRegionAssembler.SplitConform` (conform terrain to
   daylight/footprint loops, keep whole mesh, assign Z by section; watertight by construction; CDT
   conform fallback when the hand-rolled splitter goes non-manifold).
3. **region-remesh** - `PadGrader.RegionRemesh.cs` (fresh dense patch graded by distance field; soft).

If all three tiers defer, `PadGrader.Grade` fails cleanly with a structured diagnostic instead of
emitting a non-watertight result.

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
   last resort, may emit unhealthy topology - diagnostics record why the upper tiers deferred).

The optional `preferSplitKeep` performance flag reverses the first two attempts for large constrained
meshes that are likely to reject explicit carve/weld assembly. Rhino final builds enable it only for
meshes with at least 10,000 faces and persistent hard constraints; the default Core contract remains
explicit-first.

`GradedRegionAssembler.SplitOutside` (used by both explicit tiers) repairs pinched/branched hole
boundaries by pulling outside faces at irregular vertices into the carve region, and falls back to a
single-CDT re-conform (`SplitConformViaCdt`) when the hand-rolled splitter emits an untraceable
(overlapping-sliver) boundary — as happens when the daylight loop reaches the terrain edge. That
re-conform re-triangulates the WHOLE terrain, so it must re-insert the caller's hard-constraint
breaklines (retaining walls) as exact constraint edges; otherwise the CDT flips away the near-vertical
wall-face edges and orphans wall-top vertices into tent-pole spikes. `SplitOutside`/`SplitConform`
therefore take the `hardConstraints` list and thread it through (Path passes them; Pad's public entry
does not carry them yet). Path shoulder sections whose daylight ray runs off the surveyed terrain cap
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
- `MeshConstraintTopologyInserter.cs` - local constraint insertion (terrain-preserving); intersection
  results are value types in its allocation-sensitive inner loops. Face geometry is a `readonly struct`
  built on demand for candidate faces only (never an object per terrain face), constraint-segment pairs
  are discovered through a `SpatialHashGrid2D` rather than an all-pairs sweep, and the input arrays are
  cloned only on the paths that return them unchanged.
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
