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
