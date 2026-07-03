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
- `SurfaceRemesher.cs` — global **constrained-Delaunay** remesh with constraints (the Remesh modifier's
  legacy/global mode and grading rebuilds). Rebuilds the region from scratch.
- `LocalMeshRefiner.cs` — the Remesh modifier's **"Local refine"** mode, now the default for new modifiers:
  connectivity-preserving retopology.
  Keeps every input edge, splits only coarse triangles in place at edge midpoints (Rivara longest-edge
  bisection → new vertices sit exactly on the input surface, no off-surface darts), and preserves flow lines.
  The Core refiner can also flip non-feature interior diagonals to raise triangle quality (Lawson
  max-min-angle), but Rhino Remesh leaves that off because regularizing flips can scramble graded topology.
  Features (boundary ∪ creases ∪ constraints) are
  pinned — never flipped, split midpoints stay on the line. No vertex moves; a clean base for Smooth.
  `Options.RegionFilter` gates refinement to an XY region (the Sculpt modifier's DynTopo densifies only
  under the displacement field / brush); `Result.MidpointParents` reports each added vertex's parent edge
  so callers can reconstruct per-vertex attributes (sculpt BaseZ) exactly.
- `MeshFlipGeometry.cs` — shared edge-flip / triangle-adjacency primitives (convexity, oriented-face write,
  min-angle, normal agreement, adjacency incidence) used by both `SurfaceRemesher` and `LocalMeshRefiner`.
- `MeshTopologyValidator.cs` — boundary-graph analysis (non-manifold edges, open chains, loop count) —
  the watertight gate used everywhere.
- `MeshConstraintTools.cs`, `TriangleBoundaryCuller.cs`, `BoundaryTrianglePeelSettings.cs`,
  `SpatialHashGrid2D.cs`, `IndexedMeshTools.cs`, `QualitySettings.cs` — supporting utilities.
