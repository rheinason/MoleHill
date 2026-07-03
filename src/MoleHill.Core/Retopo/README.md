# MoleHill.Core/Retopo

Field-guided **quad retopology** for terrain — turning the analysis TIN into a quad-dominant, feature-aligned
render mesh that survives Catmull-Clark subdivision. Pure Core (no Rhino). See `docs/architecture.md` for how
it fits; driven from the Rhino **Retopo** modifier.

Terrain is 2.5D (single-valued height over XY), so the whole pipeline runs in the XY plane and lifts Z exactly
via `Grading/TerrainFaceGrid.InterpolateZ`.

Staged build:
- **Stage 1 (here now) — `CrossFieldSolver.cs`**: a 2-D 4-RoSy **cross-field**. Pins the field to feature
  tangents (boundary ∪ creases via `Engine/SurfaceRemesher.DetectCreaseEdges` ∪ constraint breaklines) and
  smooths the rest by matrix-free Gauss–Seidel diffusion on the vertex graph (no external solver). Output is
  per-vertex θ ∈ [0, π/2) — the local quad-edge direction. Features come from the whole modifier stack
  (boundary, creases, the Retopo constraint curves, and `PersistentHardConstraints` — grade-path road edges
  arrive that way). The Retopo modifier draws it as a flow-cross overlay so the flow can be validated before
  extraction is built.
- **Stage 2/3 (here now)** — `GuidedParametrizer.cs` + `QuadExtractor.cs` + `QuadRetopoCleanup.cs` +
  `QuadRemesher.cs`: BFS
  branch-resolve the field → guided parametrization (u,v) by a matrix-free Poisson/cotangent-Laplacian CG
  (no external solver) → read the integer (u,v) lattice into quads (dedup by (i,j)), Z lifted from the source
  triangle → practical cleanup that closes only small internal lattice holes with fan triangles and welds
  assembled quad sets. Large extraction gaps are left open rather than fan-filled, because those gaps usually
  indicate a feature corridor or parametrization defect where radial fill looks worse than a gap.
  `QuadRemesher.Remesh` is the entry point (returns the field too for the overlay). It is still non-seamless,
  but the Retopo modifier's `Quads` toggle now runs the practical cleanup path.
  - **Retaining walls** — near-vertical, so a sliver in plan: `QuadRemesher.Options.WallFaceMinSlopeDeg` masks
    steep faces out of the field (parametrizer + extractor), and `WallQuadStripBuilder.cs` rebuilds each wall
    as a clean quad strip between its top & toe rails (rails from `RetainingWallPlannerCore`, joined Rhino-side
    in `ApplyRetopoQuads`).
- **Roadmap**: stronger protecting/holding loops along features, singularity cleanup, optional seamless
  (integer) parametrization + SubD output.
