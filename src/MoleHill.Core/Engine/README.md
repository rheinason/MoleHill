# MoleHill.Core/Engine

Triangulation core — turning a point cloud + constraints into a TIN mesh. Pure (no Rhino), unit-tested.
See `docs/architecture.md` for how this fits the pipeline; `docs/file-index.md` for every file.

**Entry point:** `TinEngine.Build` — persistent across solves; `InputSnapshot` fingerprints XY topology
and Z separately so it can take the cheapest path (Z-only update → incremental edit → full rebuild).
Callers that only need vertices/faces can disable edge output; the Rhino terrain panel does this so a
multi-million-face TIN does not build and sort unused edge topology. Boundary peeling retains and validates
Triangle.NET's native face adjacency during extraction, then uses it for exact-median edge traversal and
incremental exposure; invalid/native-incompatible meshes fall back to the generic edge-map culler.
Incremental-edit dictionaries are retained only through 250,000 vertices; larger solves keep exact and Z-only
caching without their memory cost.

Key files:
- `FeaturePolylineGraph.cs` protects full subdivided constraint chains through actual CSR mesh adjacency,
  not consecutive vertices in a proximity-sorted list. Constraint queries use mesh-sized spatial cells
  and switch to a bounded vertex scan for long off-mesh runs.
- `SpatialHashGrid2D.cs` - read-only spatial queries with caller-owned scratch; query cell ranges are
  clamped to the index extent so oversized overlapping queries do not traverse empty space outside it.
  Cells are **flat CSR** (key -> slot, slot -> a run of item indices), stored in `CellMembershipIndex`,
  which `TerrainFaceGrid` and `MeshHeightProjector` share, and built by its count pass then fill pass.
  The index is immutable once built, so a `List<int>` per occupied cell only bought a small object
  plus a backing array for each of millions of cells. Both passes visit items in index order, so a
  cell's run is ascending, exactly what the per-cell lists held. Query scratch supports dense stamps
  or a reusable sparse visited set; localized parallel waterflow queries use the latter to avoid
  allocating a whole-index stamp array per worker. Both modes preserve candidate visitation order.
- `CoordinatePrecision.cs` - how far from the origin a terrain can sit before single-precision rounding
  (every stage hands its mesh on as a Rhino mesh) reaches a tenth of the model tolerance: ~8 km at
  0.01 m. The build warns beyond it and points at `mhOrientToOrigin`; measured at +500 km, the Glyvra
  terrain went from 1 boundary to 1,018 holes.
- `CancellationProbe.cs` - cooperative cancellation for the heavy Core stages. `ThrowIfCancelled` at
  phase and round boundaries; `ThrowIfCancelledOften` inside per-face / per-vertex loops, which
  consults the callback only every `DefaultInterval` iterations (its counter is deliberately
  unsynchronised - sharing it across workers changes check frequency, never correctness). Cancelling
  **throws** `OperationCanceledException`, matching the Rhino pipeline's contract: a stage abandoned
  part-way has no valid output, and throwing stops a caller publishing one to a cache.
- `ScaleAwareTolerance.cs` - numerical length floors derived from geometry scale/caller tolerance,
  never an assumed metre/mm model. Spatial grids and projection use it so uniform unit scaling does not
  change lookup topology.
- `TinEngine.cs` — the caching triangulation engine; `TinResult.cs` its output (flat XYZ + faces + edges).
- `XxHash64Builder.cs` — deterministic 64-bit streaming fingerprints for `InputSnapshot` and staged
  Rhino cache keys.
- `TriangulationHelper.cs` — shared 5-tier CDT fallback chain (used here, PadGrader, Remesh, splitters).
  Plain Triangle.NET triangulations defer creation of the large quality-refinement queues/workspace until
  quality, conforming Delaunay, or an incremental mutation requests it; a later `IMesh.Refine` still
  creates it lazily.
- `TinBoundaryPreparer.cs` — turns an optional explicit boundary into a constraint loop; without one,
  Triangle.NET uses its ordinary convex hull even when open contour or breakline segments are present.
- `TriangleNetExtractor.cs` — Triangle.NET `IMesh` → flat arrays, preserving `Vertex.ID` as `SourceId`;
  optionally retains a dense-id/native-reference adjacency view and validates reciprocal shared edges.
- `SurfaceRemesher.cs` — global **constrained-Delaunay** rebuild with constraints. Rebuilds the region
  from scratch (every constraint incl. wall rails becomes a hard edge — structurally cannot cross a
  wall); also home of the shared `DetectCreaseEdges`. Used by grading rebuilds, the GH Remesh component,
  and the Remesh modifier's **Full Rebuild** mode (`RemeshModifierDefinition.Mode == "rebuild"`, classic/
  wall-safe, coarser triangle shapes).
- `IsotropicRemesher.cs` — the Remesh modifier's default engine (`Mode == "isotropic"`): full incremental
  **isotropic remeshing** (split long / collapse short / Lawson flips / tangential relax /
  back-project). 2.5D makes the loop safe: every moved or added vertex re-samples Z from the ORIGINAL
  mesh at its new XY, so the output sits exactly on the input surface. Feature polylines are pinned
  (vertices slide 1-D along them, corners fixed, no flips or cross-feature collapses); steep
  retaining-wall faces and faces touching non-manifold edges are frozen outright (walls can't be
  buried; upstream weld defects are quarantined, and the acceptance gate only requires the output to be
  no worse than the input). Best overall quality; may occasionally cross a wall on a shallow wall
  angle. With `Options.FieldTheta` the relaxation is field-aligned — the base of the Retopo quad
  pipeline. Phase scratch is allocated once per call, not per sweep or round: flips size their edge
  incidence, touched flags and created-edge set from the face count (flips rewrite faces, never add or
  remove them) and clear them between sweeps, and the collapse phase reuses one candidate list and a
  stamped one-ring lock array across its rounds. Clearing preserves refill order, so the sweep considers
  edges in the same order — remesh output is byte-identical to the per-sweep-allocation form.
- `FeaturePolylineGraph.cs` — feature topology for the isotropic remesh: chains boundary ∪ creases ∪
  constraint edges into polylines with arc-length parameters and classifies vertices
  Free/Feature/Corner/Frozen. Short crease-only chains (fold noise in badly triangulated fans) are not
  pinned. A steep face thinner than the tolerance (a zero-area cap along a straight line, whose normal
  is rounding noise) is a wall only when edge-connected through other thin faces to a genuine wall
  face; stray ones on open terrain froze whole breaklines and let wall bisection densify them.
  Boundary edges (incidence 1) and non-manifold edges (incidence > 2) are read off **one**
  whole-mesh edge-incidence pass — do not reintroduce a second whole-mesh pass via
  `AddBoundarySegments`.
- `LocalMeshRefiner.cs` — connectivity-preserving subdivision-only refiner: Sculpt's region-gated DynTopo
  (needs the cheap region early-out and the no-vertex-motion guarantee) and the Remesh modifier's
  **Local Refine** mode (`Mode == "local"` — fastest/safest on huge terrains and delicate wall/pad
  topology since it never re-triangulates, but coarsest quality). Splits coarse triangles at edge
  midpoints; features pinned; no vertex ever moves. `Result.MidpointParents` reports each added vertex's
  parent edge so sculpt can reconstruct BaseZ.
- `MeshVertexAdjacency.cs` — vertex → incident faces and vertex → neighbor vertices as flat CSR arrays,
  rebuilt (with buffer reuse) once per collapse round and relax sweep of the isotropic remesh. It
  replaced a `Dictionary<int, List<int>>` + `Dictionary<int, HashSet<int>>` pair whose per-vertex
  allocations dominated the operator loop on large terrains.
- `MeshFlipGeometry.cs` — shared edge-flip / triangle-adjacency primitives (convexity, oriented-face write,
  min-angle, normal agreement, adjacency incidence) used by the remeshers and refiner.
- `MeshTopologyValidator.cs` — low-allocation sorted-edge boundary analysis (non-manifold edges, open
  chains, loop count) and compressed flat adjacency for ordered boundary extraction. Storage scales
  with edge/boundary counts rather than maximum vertex id; it is the watertight gate used everywhere.
- `MeshConstraintTools.cs`, `TriangleBoundaryCuller.cs`, `BoundaryTrianglePeelSettings.cs`,
- `FaceAdjacency.cs` — face-to-face adjacency across shared edges, as a flat `faceCount * 3` array with
  -1 for a naked edge. Shared by `WaterflowTracer` and `DrainageBasinAnalyzer` deliberately: both walk
  the dual graph, and if the two disagreed about which edges are naked or what a non-manifold edge means,
  a traced flow path could cross a catchment boundary that claims water cannot go there.
- `EdgeLoopChainer.cs` — directed edges into closed loops (open chains where they do not close). Kept
  separate from `FeaturePolylineGraph`, which chains *undirected* feature edges and needs corner
  classification and arc-length parameters a boundary polygon has no use for. Two small correct things
  rather than one general one.
  `SpatialHashGrid2D.cs`, `IndexedMeshTools.cs`, `QualitySettings.cs` — supporting utilities.
  `IndexedMeshTools` owns the packed edge key, its comparers, and `CountFaceEdges` (edge-use counts in
  first-use order); use them rather than a local copy. `CellMembershipIndex.cs` — the flat CSR cell
  store and two-pass builder behind every spatial cell index.
