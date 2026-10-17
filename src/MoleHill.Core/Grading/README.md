# MoleHill.Core/Grading

Pad and path grading. **Invariant: grading output is always a watertight 2.5D mesh - never holes or
spikes.** Pure, unit-tested. See `docs/architecture.md` for the tier cascade overview.

`WallRailStationing` synchronizes paired wall rails for solid generation. Corresponding plan bends
anchor the mapping; stations between them use plan length and interpolate each rail's own elevation.
A bend is a run of same-direction turns, so a sharp corner, a chamfer and a tessellated fillet (one
rail of a round-cornered Offset) match each other; a run that matches nothing is split and retried.
Bends anchor at their turn-weighted middle, and two arcs also anchor start to start and end to end.
It retains authored vertices with unequal sampling counts, merges stations closer than the tolerance
on both rails, and aligns closed-ring seams at bends, avoiding corner folds caused by matching
whole-rail 3D length fractions. `WallRailStationingSweepTests` checks over 1,000 random offset walls.

`BracketedVolumeSearch` implements the B7 bounded net cut/fill search: both endpoints are measured,
an unbracketed target returns the nearer end, and bisection records every sample with distinct
`Converged`, `NoBracket`, `IterationCap`, `NonMonotone`, and `GradingFallback` outcomes.
`PadElevationBalancer` translates one planar pad boundary in Z and evaluates each candidate through
`PadGrader.Grade`, returning the actual best graded mesh and adjusted input boundary. GH integration
and the first example graph remain B7 work. `PadElevationBalancer.TrySuggestRange` brackets the search
from the terrain under the footprint (all cut at the bottom, all fill at the top, widened for the
batters), so Rhino's `mhBalancePad` needs no bounds from the user. `PadSetBalancer` balances several
pads at once (`Together`: one shared offset; `Each`: every pad to its own net, sweeping until all hold),
grading them together through `GradeWindowed`, the entry point the Grade Pad card builds with, and giving
each graded face's volume to the nearest pad footprint.

**Every volume is exact: `SurfaceDifferenceVolume`.** It overlays the graded mesh on the original
(`ReferenceSurface`, a CSR plan grid of the original's triangles) and integrates the linear height difference
on each clipped piece, cutting a sign change exactly, so the figure depends on the two surfaces and never on
how either is triangulated. A face copied unchanged is skipped; overlapping reference triangles (an overhang)
fall back to sampling against the nearest sheet. `GradingResult.CutVolume`/`FillVolume` are exact against the
terrain the public entry points were given (`MeasuredAgainst`, measured lazily on first read, so the Rhino
build, which never reads them, pays nothing), and Rhino's Earthworks, Cut / Fill and zone volumes use the
same routine. The per-face rules it replaced (corner average in the graders, centroid in Earthworks) were
exact only where the original was flat across a face; on a coarse contour terrain a pad's figures were 9-16%
off and jumped whenever a pad-top diagonal flipped.

**Corner sections follow the fan.** `ApplyGradingToVerticesWithSections` grades a point in a pad's corner
sector along its own direction from the corner, interpolated across the corner fan
(`TryResolveCornerFanSection`). The fan's copies of the corner are joined by zero-length segments the
closest-location search skips, so such a point used to reach the corner through one of the two real edges,
tied in distance, and noise chose the incoming edge's shoulder or the outgoing one's. A point past the shorter
reach was then left at the original ground, a lump inside the cut that came and went as the pad moved by
centimetres (`PadGraderCornerFanTests`).

`GradingWindows` + `PadGrader.Windowed.cs` / `PathGrader.Windowed.cs` (`GradeWindowed`) grade each group
of pads or paths on the faces within their reach (a pad's filled outline, a path's centreline, grown by how
far it can change the terrain) and stitch the patches back, memoizing each window by a key of its canonical
input. Small enclosed pockets join their window, and a patch must keep the edges it shares with the rest of the terrain,
or the call grades the whole mesh instead. A window takes the faces its items reach whole, so they stick out past the
reach box; the box that picks the window's locks and breaklines is grown to cover them (`CoveringBox`), or a batter
runs across a breakline lying on a window face just past the reach (`GradingWindowsBreaklineCoverageTests`). See `docs/architecture.md` -> "Grading windows".

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
Such a rail keeps every authored vertex, because the normals are supplied one per authored vertex, and
`BuildOneSidedRailStations` adds stations between them (a third of the batter reach apart, never finer
than the terrain under the rail), each facing along its segment's perpendicular. A closed one-sided rail
conforms as two rings, rail and daylight (`PathCorridor.RailLoopXy`). See architecture.md, "Retaining
walls: getting the rails in".

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

**Paths stop at hard constraints; they are not refused for crossing one.** Before the cascade,
`PathGrader.StopPathsAtHardConstraints` (`PathGrader.BarrierStops.cs`) splits each centerline where it
crosses a preserved-elevation hard constraint. A piece inside a closed one (a graded pad or lawn) is
left to it, and every other piece is graded, ending short of the constraint by as much as its road edges
need at that crossing angle. A crossing used to be refused, and the refusal took every path in the
modifier with it: on the park-scale probe, 22 crossings among 24 paths left nothing graded while the build
reported success. Variable-width and one-sided paths are passed through unchanged. The Rhino stage
applies the same stop before it makes the path's elevation constraints, so the constraints it persists
describe the pieces that were graded.

**A closed rail whose inward batter cannot reach daylight grades without a daylight ring.** On a ring too
small for its batter the daylight rays cross before they meet ground, and the envelope self-intersects.
The explicit tier used to defer the whole grade to constraint insertion, which does not put the rails in
the mesh, so a terraced ring wall lost its walls. It now conforms the rail ring alone and lets the section
pass grade the interior (`OneSidedRailRingTests`, on a captured terrace case).

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

## The grading envelope (every tier, both graders)

Outside the road or pad, a batter runs from the design edge to the ground, so every point a grade moves
lies between the original ground and the design height at its nearest edge. A point above both or below
both is a fault: a batter that never met the ground, or a spike.

- **Choose the side by `BatterBranch`:** the side the ground *starts* on — the first difference from the
  design, outward from the edge, beyond the at-grade tolerance. A batter can only daylight there. Taking
  the side of the largest difference instead stood a batter 5 m proud of a terrace behind a low verge.
- **Keep each batter point between its edge and its own ground.** The section and edge tiers interpolate
  or extrapolate a straight batter; the ground under a given point need not follow it. Where the batter
  passes the ground, the ground stays (`ClampBetween(candidate, edgeZ, originalZ)` in
  `TryComputePathSectionInfluence`, `TryComputePathInfluence` and Pad's `ApplyGradingToVerticesWithSections`).
  Consequence, deliberately: in a cut, at-grade dips are left alone rather than filled into a band.
- **Check, clamp, report:** `GradingEnvelope` checks the final result independently of the tiers (own
  closest-point search; ground as a band of every input face covering a point, because a retaining wall
  has two heights at one plan position). Violators are clamped and the result carries
  `*.envelope.clamped`; `GradingResult.HasEnvelopeFault` reports it and `FindEnvelopeViolations` checks
  any result. Tests and Copy Case output assert it is false. `GradingEnvelopeSweepTests` grades seeded
  random scenes against it (`MOLEHILL_GRADING_SWEEP` for a deeper run).
- A hard-constraint cap still extends the slope to the constraint: the clamp bounds it, and the step left
  against a wall or kerb is what the constraint is for.

## Smoothing rules (`MeshSmoother`)

Smooth has no design to grade toward, so its rules are its own: a held point — the mesh border, outside
every region, at zero strength, or on a fully held breakline — does not move, and every other point stays
within the input range of the points it could have mixed with (its k-ring after k passes): smoothing makes
no new peak or pit. Each pass's target is a plane fitted through the one-ring, which keeps a uniform slope
exactly planar, then clamped to the ring's current range (`ClampToRing`), because on an irregular ring a
fitted plane can land outside it. `MeshSmoother.FindViolations` checks either entry point's result;
`MeshSmootherSweepTests` runs random irregular meshes through both.

## Crease rounding (`CreaseRounder`)

Smooth's **Round Creases** mode. It rounds batter crests, toes and slope breaks with a parabolic vertical
curve of horizontal length L. A crease whose grade changes by κ moves a point at plan distance d by
κ(L/2 − d)²/2L: κL/8 on the crease and nothing at L/2. That is exactly a box blur of width L across the
kink. Crests only drop, toes only rise, the plan never moves, and detail inside the band rides on top.

It is computed as a blur, not crease by crease. Every crease edge pulls each nearby point by its κ,
integrated along the edge against a radial kernel. The kernel solves ∇²Φ = G − δ, where G is the 2D
kernel whose every line projection is that 1D box, so a straight crease gets exactly the parabola. The
mesh splits a pad corner or a coned batter end into many short folds. The first version summed the
nearest point of each crease chain and counted such a corner up to four times. The line integral is
linear, so those folds add up as one surface and no crease is counted twice.

What it never touches:
- Wall faces (70° or steeper) and their edges, so walls are never buried.
- Creases along Protect curves.
- Networks of creases shorter than L, which are surface detail.

The offset fades to zero over L/2 approaching a Protect curve, a wall, the mesh border or a boundary
edge. Constraint creases (pad edges, road edges) round like any other, and Protect is how a sharp one is
kept. A crease with nothing beside it to round into would move only its own vertices, and a wide face (a
pad top meshed from its outline) would tilt whole. So `DensifyBand` first midpoint-splits the band's
triangles longer than L/4 through `LocalMeshRefiner`'s whole-triangle gate. The surface doesn't change.
A wall face never asks for a split, but it is bisected along an edge a band triangle splits, to keep the
mesh conforming. The split costs a whole-mesh refine and a fresh `Prepare`, so it runs on final builds
only: a preview (scrubbing the length) rounds at the mesh's own resolution. It is refused, with a
diagnostic, when the band could need more than 500k vertices (or twice the mesh), the case of a short
length over long creases. `CoarseCreaseLength` reports any chamfer left. `Prepare` holds the incoming
mesh's creases, which do not depend on the length, and is cached. `CreaseRounderTests` pins the parabola, the
superposition of crest and toe, and the pad corner on a symmetric triangulation.

## Shared building blocks
- `HeldLines` - what a height-only edit holds and how it fades out of it: plan segments in a spatial index,
  `IsOn` (a vertex on the line within tolerance), `Weight` (smoothstep from 0 on the line to 1 at the reach),
  plus `InsideBoundaries` and `HoldWallFaces`. Round Creases and Displace both use it, so the two fade the same
  way; a third height edit should too, not write its own.
- `BreaklineHeightConflicts` - where two sets of breaklines cross in plan at different heights (a breakline-mode
  wall rail over a constrained contour). No 2.5D terrain can honour both lines, so the Retaining Wall stage
  reports these crossings instead of attempting a rebuild.
- `RegradedConstraintTrimmer` - cuts persisted breaklines and contours down to the runs that still lie on a
  graded surface. A card with "Grade Through Breaklines" on uses it so later stages do not restore the old ground.
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
- The 2D primitives (point-in-polygon, distance-to-polygon, interior point, segment tests, Z
  interpolation) live in `Core/Geometry/Geometry2D.cs`, not here; `PadGrader` has no wrappers for them.
- `SegmentProximityIndex.cs` - **exact** nearest-segment distance for a closed loop queried many
  times (seam deviation). Grows a query box until the best distance found inside it is no larger than
  the box half-width, at which point everything unexamined is provably further; a box that outgrows the
  whole extent falls back to a full scan, so a distant miss still reports its true distance rather than
  a clamped radius. It calls `SeamValidator.DistancePointToSegment` deliberately, so an indexed answer
  is bit-identical to that caller's scan. Loops under `IndexThreshold` segments keep the scan.
- `PreparedPolygon.cs` - the same two answers as `Geometry2D.PointInPolygon` /
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
- `PadGradeRequest.cs` / `PathGradeRequest.cs` / `GradeOutcome.cs` - the graders' only entry points:
  `PadGrader.Grade(request)`, `PathGrader.Grade(request)` and their `GradeWindowed` forms. The terrain
  travels as an `IndexedTriMesh` (Engine), options are named properties, and both graders report failure
  through one `GradeOutcome` (result, message, failure polylines, structured diagnostics).
- `FaceCut/` - the face-cutting kernel the area splitter and the constraint inserter share
  (`FaceCutGeometry`, `FaceData`, `LocalPointBuilder`, `GlobalPointLookup` and the small value types):
  splitting a segment network at its own crossings, clipping a segment to a face, edge-point and piece
  bookkeeping, per-edge split chains. The two engines were once copies and had drifted: the inserter now
  also keeps a clockwise face's winding, never merges two corners, and emits a face with no triangulable
  area unchanged (`MeshConstraintInserterFaceCutParityTests`). What still differs is policy, named rather
  than copied: `EdgePointMerge` (the inserter merges same-edge points only) and the splitter's 8x conform
  snaps, which line insertion must never use. The splitter snaps per `MeshAreaSplitter.BoundarySnap`:
  `ConformToTerrain` (8x, grading and trims) or `FollowDrawnLine` (within tolerance, zones), where faces
  narrower in plan than the conform radius - walls - take no cut of their own and only follow their
  neighbours' edge splits. A face narrower in plan than the tolerance (a vertical or near-vertical wall face)
  that a neighbour split is fanned over the split in 3D rather than triangulated in plan, where its pieces
  fail the plan-area checks, or emitted whole (`MeshAreaSplitterDrawnLineTests`).
- `MeshAreaTopologySplitter.cs` / `MeshAreaSplitter.cs` - conforming terrain subdivision along loops.
  Splits resolve per edge (`MeshEdgeSplitRegistry.cs`, `LocalPointIdentities.cs`, shared with the
  constraint inserter), caps are split before and near-twin vertices fused after
  (`NearVertexCollapser.cs`), so the result tiles the input exactly; see `docs/architecture.md`.
- `MeshEdgeSplitRegistry.cs` - the split vertices of each mesh edge, shared by the two faces on it; a new
  split lies exactly on its edge.
- `LocalPointIdentities.cs` - what each local point of a face re-triangulation stands for (corner, edge
  split, other).
- `FillSlopeFlipper.cs` - flips free interior edges of a graded hole fill to the flatter diagonal once
  heights are assigned (Grade Path and Grade Pad explicit fills).
- `NearVertexCollapser.cs` - fuses vertices a re-triangulation created into a neighbour closer than
  tolerance, by manifold-safe edge collapse; input vertices never move.
  Segment/triangle mapping uses one fixed-buffer three-edge pass instead of per-candidate lists and
  duplicate intersection calls; opt-in diagnostics split allocation by preparation, mapping, touched-face
  triangulation, and classification.
- `TerrainBoundaryTrimmer.cs` - inserts Outer/Hide/Show edges in one conforming split, applies fixed
  `Outer && (!Hide || Show)` precedence, compacts every surviving island into one mesh, and rejects open
  or non-manifold output.
- `TerrainDetailInserter.cs` - Add Geometry's local path: inserts spot points, breaklines and contours
  into a finished terrain without re-triangulating it, gives them their data elevations, and reconnects
  them with `LawsonFlipper.cs` (incremental-Delaunay flips that start only from the new vertices). A
  vertex on an existing hard constraint or on a face ≥ 70° keeps its elevation, and constraint, boundary
  and pre-existing wall edges never flip — so walls and every upstream edge survive, which a rebuild from
  the mesh's own vertices (XY-merged) did not guarantee.
- `InsertedConstraintTracer.cs` - recovers a line as it was actually inserted: the edge-connected chain
  of mesh vertices along it, including the splits and snaps insertion made. Add Geometry persists this
  form (falling back to the drawn line when the trace does not complete), because a later constrained
  rebuild given the drawn line leaves a zero-area cap at every split vertex. Retaining Wall's grade mode
  traces its rails through the graded mesh at the conform's snap radius to adopt them. The walk starts a
  closed line across its seam and backtracks out of dead ends.
- `MeshConstraintTopologyInserter.LocalTriangulation.cs` - constraint insertion by re-triangulating the
  crossed faces and their vertex ring as one CDT, keeping every existing vertex and the patch boundary; the
  fallback when face-by-face insertion disagrees with a neighbour about a shared edge. `TryInsert` now runs
  it itself when its own result fails the self-check (plan area, edge multiplicity, single-use edges only
  on the input border), and declines if it also fails — callers never receive a folded mesh. Face-by-face
  splits resolve by identity (corner → own vertex, edge split → per-edge registry, exactly on the edge),
  not by nearest XY; see `docs/architecture.md`.
- `MeshConstraintTopologyInserter.cs` - local constraint insertion (terrain-preserving); intersection
  results are value types in its allocation-sensitive inner loops. Face geometry is a `readonly struct`
  built on demand for candidate faces only (never an object per terrain face), constraint-segment pairs
  are discovered through a `SpatialHashGrid2D` rather than an all-pairs sweep, and the input arrays are
  cloned only on the paths that return them unchanged.
  Its `.WallPatch.cs` and `.WallQuality.cs` partials are the retaining-wall quality path: the patch is
  re-triangulated as a whole against its own perimeter, adjacent untouched triangles are bisected to stay
  conforming, and elevations are sampled from a lifted wall reference. `.WallQuality.cs` expands the
  patch ring by ring and validates achieved quality, area, perimeter and vertex retention before
  publishing; the per-face path above is the fallback when it declines. The point-aware `TryInsert`
  overload also takes isolated points: one on an existing vertex is left out, one on an edge splits it
  in both neighbours, and one inside a face becomes a free vertex of that face's local triangulation.
  See
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
