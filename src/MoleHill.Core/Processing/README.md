# MoleHill.Core/Processing

Input preparation before triangulation — cleaning, deduplicating, and discretizing raw points/curves.
Pure, unit-tested.

- `PointCloudProcessor.cs` — merges spot points + breakline vertices with Z-aware spatial-hash dedup
  (breakline-to-breakline merges require XY **and** Z proximity, preserving parallel retaining walls).
- `BreaklineDiscretizer.cs` — polylines → flat vertex array + segment index pairs.
- `TerrainConstraintPreprocessor.cs` — conditions breakline and contour spacing before meshing using
  observed source-segment medians. This preserves the panel's straight-run normalization and long
  breakline stations without turning document tolerance into a large-site density target.
- `TinInputCleaner.cs` — removes degenerate/duplicate input geometry.
- `RegionInputFilter.cs` — keeps only points inside a work-region boundary (+margin); backs the
  Triangulate "work boundary" fast sub-area crop (`TerrainBuildService` calls it). Reuses
  `Grading/GradingGeometry2D`.
