# MoleHill.Core/Processing

Input preparation before triangulation — cleaning, deduplicating, and discretizing raw points/curves.
Pure, unit-tested.

- `PointCloudProcessor.cs` — merges spot points + breakline vertices with Z-aware spatial-hash dedup
  (breakline-to-breakline merges require XY **and** Z proximity, preserving parallel retaining walls).
  Occupied cells use one shared linked-index store instead of one allocated list per cell, keeping sparse
  multi-million-point surveys fast and memory-bounded.
- `BreaklineDiscretizer.cs` — polylines → flat vertex array + segment index pairs.
- `TerrainConstraintPreprocessor.cs` — conditions breakline and contour spacing before meshing using
  observed source-segment medians. Segment lengths are collected once per source class and straight
  runs are detected in linear time, so multi-million-station polylines retain the panel's spacing
  normalization without a quadratic preprocessing stall.
- `TinInputCleaner.cs` — removes degenerate/duplicate input geometry.
- `RegionInputClipper.cs` — exact union clipping for Data Clip: keeps points inside/on closed World-XY
  loops and splits crossing polylines while linearly interpolating Z. The Rhino host applies it to raw
  Triangulate and Add Geometry sources, never to the carried mesh.
- `RegionInputFilter.cs` — legacy margin-capable point-region helper retained for compiled callers.
- `SurfaceConformer.cs` — Z-only blend from an incoming terrain to a target 2.5D mesh. Optional closed
  loops use even-odd nesting for islands and donut holes, with a smooth feather contained inside every
  boundary edge.
