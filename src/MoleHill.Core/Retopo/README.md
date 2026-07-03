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
2. **`Engine/IsotropicRemesher` with `FieldTheta`** — the field-aligned isotropic remesh: split /
   collapse / flip / relax / back-project with feature polylines pinned, and tangential relaxation
   damped ACROSS the local field direction so vertices slide along field lines and edges straighten
   into the quad flow. Steep retaining-wall faces are frozen (never moved, split, or cut out).
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

**Roadmap**: a field-alignment flip objective in the remesh (Lawson prefers 60° triangles; right
triangles pair into better quads), Blossom perfect matching behind the same pair scoring, optional
SubD output.
