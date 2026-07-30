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
- `RegionInputFilter.cs` — keeps only points inside a work-region boundary (+margin); backs the
  Triangulate "work boundary" fast sub-area crop (`TerrainBuildService` calls it). Reuses
  `Grading/GradingGeometry2D`.
