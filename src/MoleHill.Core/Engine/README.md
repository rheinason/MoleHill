# MoleHill.Core/Engine

Triangulation core — turning a point cloud + constraints into a TIN mesh. Pure (no Rhino), unit-tested.
See `docs/architecture.md` for how this fits the pipeline; `docs/file-index.md` for every file.

**Entry point:** `TinEngine.Build` — persistent across solves; `InputSnapshot` fingerprints XY topology
and Z separately so it can take the cheapest path (Z-only update → incremental edit → full rebuild).

Key files:
- `TinEngine.cs` — the caching triangulation engine; `TinResult.cs` its output (flat XYZ + faces + edges).
- `TriangulationHelper.cs` — shared 5-tier CDT fallback chain (used here, PadGrader, Remesh, splitters).
- `TinBoundaryPreparer.cs` — turns an optional boundary into an explicit constraint loop (vs convex hull).
- `TriangleNetExtractor.cs` — Triangle.NET `IMesh` → flat arrays, preserving `Vertex.ID` as `SourceId`.
- `SurfaceRemesher.cs` — global **constrained-Delaunay** rebuild with constraints (grading rebuilds and
  the GH Remesh component). Rebuilds the region from scratch; also home of the shared
  `DetectCreaseEdges`.
- `IsotropicRemesher.cs` — the Remesh modifier's engine: full incremental **isotropic remeshing**
  (split long / collapse short / Lawson flips / tangential relax / back-project). 2.5D makes the loop
  safe: every moved or added vertex re-samples Z from the ORIGINAL mesh at its new XY, so the output
  sits exactly on the input surface. Feature polylines are pinned (vertices slide 1-D along them,
  corners fixed, no flips or cross-feature collapses); steep retaining-wall faces and faces touching
  non-manifold edges are frozen outright (walls can't be buried; upstream weld defects are quarantined,
  and the acceptance gate only requires the output to be no worse than the input). With
  `Options.FieldTheta` the relaxation is field-aligned — the base of the Retopo quad pipeline.
- `FeaturePolylineGraph.cs` — feature topology for the isotropic remesh: chains boundary ∪ creases ∪
  constraint edges into polylines with arc-length parameters and classifies vertices
  Free/Feature/Corner/Frozen. Short crease-only chains (fold noise in badly triangulated fans) are not
  pinned.
- `LocalMeshRefiner.cs` — connectivity-preserving subdivision-only refiner, now used exclusively by the
  Sculpt modifier's region-gated DynTopo (needs the cheap region early-out and the no-vertex-motion
  guarantee). Splits coarse triangles at edge midpoints; features pinned; no vertex ever moves.
  `Result.MidpointParents` reports each added vertex's parent edge so sculpt can reconstruct BaseZ.
- `MeshFlipGeometry.cs` — shared edge-flip / triangle-adjacency primitives (convexity, oriented-face write,
  min-angle, normal agreement, adjacency incidence) used by the remeshers and refiner.
- `MeshTopologyValidator.cs` — boundary-graph analysis (non-manifold edges, open chains, loop count) —
  the watertight gate used everywhere.
- `MeshConstraintTools.cs`, `TriangleBoundaryCuller.cs`, `BoundaryTrianglePeelSettings.cs`,
  `SpatialHashGrid2D.cs`, `IndexedMeshTools.cs`, `QualitySettings.cs` — supporting utilities.
