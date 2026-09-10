# MoleHill.Core/Retopo

Field-guided **quad retopology** for terrain — turning the analysis TIN into a quad-dominant,
feature-aligned render mesh. Pure Core (no Rhino). See `docs/architecture.md` for how it fits; driven
from the Rhino **Retopo** modifier.

Terrain is 2.5D (single-valued height over XY), so the field runs in the XY plane and Z stays exact
throughout (the remesh back-projects onto the input surface via `Grading/TerrainFaceGrid`).

Pipeline (`QuadRemesher.Remesh` is the entry point; returns the field too for the overlay):

1. **`CrossFieldSolver.cs`** — a 2-D 4-RoSy **cross-field**. Pins the field to feature tangents
   (boundary ∪ creases via `Engine/SurfaceRemesher.DetectCreaseEdges` ∪ constraint breaklines) and
   smooths the rest by matrix-free Gauss–Seidel diffusion on the vertex graph (no external solver).
   Output is per-vertex θ ∈ [0, π/2) — the local quad-edge direction. Features come from the whole
   modifier stack (boundary, creases, the Retopo constraint curves, and `PersistentHardConstraints` —
   grade-path road edges arrive that way). The Retopo modifier draws it as a flow-cross overlay.
   `Options.Iterations` is a **maximum**: each sweep tracks the largest per-vertex change of the 4-RoSy
   representative and stops once it is below `ConvergenceTolerance` (default 1e-7, about 2.5e-8 rad in
   θ). The residual accumulates in the same fixed order as the in-place updates, so the stopping point
   is deterministic. Measured: an axis-aligned terrain sheet settles in **one** sweep of an 80-480
   sweep budget with θ identical to the exhausted run; a disc, whose boundary tangent takes every
   direction, is still at a ~1e-4 residual when its budget ends, so there the budget — not the
   tolerance — binds, and that run is untouched. `Result` reports `Iterations`, `FinalResidual` and
   `Converged`.
2. **`Engine/IsotropicRemesher` with `FieldTheta`** — the field-aligned isotropic remesh: split /
   collapse / flip / relax / back-project with feature polylines pinned. Tangential relaxation is
   damped ACROSS the local field direction so vertices slide along field lines, and the **flip
   objective becomes field-aligned** when a field is present: instead of Lawson max-min-angle (which
   drives toward 60° equilateral triangles that pair into 60/120° rhombi), it prefers the diagonal
   running at ±45° to the field — the hypotenuse of an axis-aligned quad — so `TriQuadPairer` merges
   pairs into clean 90° quads. A hard min-angle floor guards against slivers where the field is noisy,
   and the plain Remesh path (no field) keeps pure Lawson unchanged. Steep retaining-wall faces are
   frozen (never moved, split, or cut out).
3. **`TriQuadPairer.cs`** — merges adjacent triangle pairs into quads, scored by corner angles near
   90°, edge alignment to the field, and planarity across the removed diagonal; greedy best-first.
   Geometry never changes, every triangle appears exactly once as a tri or half a quad, so the output
   is ONE connected quad-dominant mesh — **watertight/hole-free by construction**. Feature edges are
   never removed (no quad straddles a crease/road edge); wall triangles pair only with each other,
   scored in their own best-fit plane.

The parametrization pipeline that preceded this (GuidedParametrizer / QuadExtractor /
QuadRetopoCleanup / WallQuadStripBuilder) was removed: a non-seamless parametrization structurally
produced lattice holes at singularities and boundaries, and the weld-only wall join could gap — see
`docs/comment.md` iteration 18.

**Roadmap**: Blossom perfect matching behind the same pair scoring (the current matcher is greedy
best-first, so parity/ordering leftovers remain), optional SubD output. The field-alignment flip
objective (step 2) is implemented.
