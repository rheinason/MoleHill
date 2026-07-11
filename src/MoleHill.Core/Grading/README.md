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
- `GradingGeometry2D.cs` - **the** 2D primitives (point-in-polygon, distance-to-polygon,
  interior point, segment intersection, Z interpolation). `PadGrader.Spatial.cs` are thin public compat
  wrappers over it.
- `BatterStripBuilder.cs` - daylight loop + ruled batter strip (pad and path). `BuildDecimatedCarveXy`
  collapses a lock-curve-clamped daylight run back onto the wall breakline's own (terrain) vertices, so
  the carve loop rides a retaining wall instead of re-slivering its face (the strip seeds stay dense).
- `GradedRegionAssembler.cs` - `SplitOutside`/`SplitConform` (terrain split) + `WeldGradedRegion`
  (identity weld of fills into terrain) + `AssembledMesh`.
- `MeshAreaTopologySplitter.cs` / `MeshAreaSplitter.cs` - conforming terrain subdivision along loops.
- `MeshConstraintTopologyInserter.cs` - local constraint insertion (terrain-preserving).
- `ClipperGeometry.cs` - polygon boolean/offset (Clipper) for daylight unions and offsets.
- `GradingDiagnostic.cs` / `MeshTopologyOperations.cs` - diagnostics + watertight repair helpers.
