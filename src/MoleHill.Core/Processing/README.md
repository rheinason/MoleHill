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
- `SurfaceDeviationEvaluator.cs` — indexed, streaming XY-overlay comparison for two 2.5D triangle
  meshes. It certifies maximum vertical deviation at overlay vertices, verifies domain area in both
  directions (including holes/islands), records a worst-error witness, and supports cancellation.
  Degeneracy uses an adaptive robust orientation predicate rather than treating model tolerance as a
  minimum face width, so narrow but valid grading-fan triangles remain supported.
- `SurfaceConstraintEdgeResolver.cs` — maps persistent constraint polylines onto actual mesh edges
  with indexed XY lookup and elevation-coherence checks, keeping reusable geometry validation out of
  the Rhino host.
- `SurfaceSimplifier.cs` — constrained surface reduction by certified maximum deviation or deterministic
  target vertex count. It protects every boundary
  and required-chain vertex/edge, refines from certified overlay-error witnesses and source errors,
  and returns the unchanged input with an explicit reason when it cannot prove a smaller result.
- `ZonePriorityResolver.cs` — deterministic boundary ordering before a shared area split, using zone
  stack index, source boundary index, input elevation, and elevation-priority flag. Stack-priority zones
  separate elevation-sorted runs, avoiding the old mixed-mode comparator cycle. Rhino's zone stage calls
  this Core resolver; GH zone metadata and resolver use remain B7c work.
