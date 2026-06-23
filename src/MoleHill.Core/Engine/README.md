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
- `SurfaceRemesher.cs` — quality remesh with constraints (the Remesh component / grading rebuilds).
- `MeshTopologyValidator.cs` — boundary-graph analysis (non-manifold edges, open chains, loop count) —
  the watertight gate used everywhere.
- `MeshConstraintTools.cs`, `TriangleBoundaryCuller.cs`, `BoundaryTrianglePeelSettings.cs`,
  `SpatialHashGrid2D.cs`, `IndexedMeshTools.cs`, `QualitySettings.cs` — supporting utilities.
