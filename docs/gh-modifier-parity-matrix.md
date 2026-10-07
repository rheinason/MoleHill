# B7 modifier parity inventory

Seeded from `src/MoleHill.Rhino/Registry/*ModifierDescriptor.cs` and the active GH components on
2026-09-15; the installed Rhino 8 Grasshopper library was enumerated live on 2026-09-16 and contains
all 18 MoleHill components listed by the router. "Existing mesh tool" means a component exists, but its mesh port drops MoleHill terrain
metadata. It is not a completed B7 parity entry. Every fixture below still needs a matched Rhino/GH
result, including constraint and zone checks.

Mesh input handling is now shared: every GH mesh component extracts through
`RhinoGeometryConversions` (via `GhSolveContext.TryExtractMesh`), so quad faces are accepted and normalized
and duplicate vertices are combined exactly as in the panel. Slope Analysis keeps the raw triangle-only read
because its per-face output is index-aligned with the input faces.

| Native modifier | GH route | Current state | First matched fixture / gap |
| --- | --- | --- | --- |
| Triangulate, including boundary roles | Construct Terrain | Partial | Same point/curve/boundary roles; current Construct does not expose native boundary roles. |
| Add Geometry | Add Geometry | Partial | Additional points and breaklines are merged with an existing mesh and rebuilt through Core `TinEngine`; optional Boundary curves constrain the rebuilt footprint, while Terrain input/output carries source metadata and the component embeds matching native artwork. Boundary role details and matched fixtures remain. |
| Grade Pad | Grade Pad | Partial terrain migration | Legacy mesh ports remain in place; an optional Terrain input and appended Terrain output carry source constraints, zones, units, and identity metadata through the graded mesh. Source breaklines become default lock curves when no explicit locks are wired. Native matched fixture and constraint projection semantics remain. GH port 2 is cut slope; native `SlopeAngle` is the fill/main slope and `CutSlopeAngle` is optional. GH port 6 is fill override. |
| Grade Path | Grade Path | Partial terrain migration | Legacy mesh route now accepts optional Terrain input/output and preserves source metadata through grading; constant and variable width, cut/fill, center/edge constraints, and a matched native fixture remain. |
| Grade Line | Grade Line | Partial terrain migration | Corridor grader at width zero: design lines in, curve Z is the finished elevation, batters daylight both sides. Shared Fill/Cut slopes plus four optional per-side overrides. Accepts optional Terrain input/output like Grade Path; a matched native fixture remains. |
| Retaining Wall | Retaining Wall | Partial terrain migration | Legacy mesh route now accepts optional Terrain input/output and carries source breaklines plus accepted wall rails into typed metadata; rail pairing, wall-strip geometry, remesh constraints, and a matched native fixture remain. The `Grade Terrain` mode and its slope inputs are appended after the Terrain port so saved definitions keep their port indices. |
| In-Situ Stair | In-Situ Stair | Partial terrain migration | Legacy mesh route now accepts optional Terrain input/output and preserves source metadata through support grading; stair plan/elevation profile, constrained seams, and a matched native fixture remain. |
| Remesh | Remesh | Partial terrain migration | Legacy mesh route accepts optional Terrain input/output and source breaklines become default constraints; density, edge survival, and matched native parity remain. The component runs `SurfaceRemesher`, i.e. the card's **Full Rebuild** mode only; the card's default isotropic engine (`IsotropicRemesher`/`TiledIsotropicRemesher`) has no GH route, so a GH Remesh and a default Remesh card differ by design until a Mode input is added. |
| Smooth | Smooth | Partial terrain migration | Legacy mesh route now accepts an optional Terrain input and emits typed Terrain metadata; source breaklines become default fixed curves when explicit breaklines are absent. Algorithm, crease semantics, and a matched native fixture remain. |
| Simplify | Mesh Simplify | Partial | Deviation, target-count, and retained-percentage modes now call Core `SurfaceSimplifier`; straight Required Edges preserve mandatory endpoint pairs. An optional Terrain input/output carries source metadata and the component embeds matching native artwork. A matched native fixture and constraint-aware propagation still need completion. |
| Retopo | Retopo | Partial terminal route | New GH component calls Core `QuadRemesher` and emits quad-dominant Mesh plus counts/diagnostics. Core coverage now verifies a constrained breakline's endpoints and hole-free topology. It deliberately remains terminal and does not produce typed Terrain; matched native fixture and field/constraint comparison remain. |
| Sculpt | Rhino snapshot only | Rhino-only by design | Completed sculpted surface enters through Snapshot. |
| Project To | Project To | Partial | Mesh target, strength, feather, and closed XY boundaries now call Core `SurfaceConformer`; an optional Terrain input/output carries source metadata and the component embeds matching native artwork. A matched native fixture and constraint-aware propagation remain. |

For each entry before completion, record port names and order, defaults, units, optionality, constraint
effects, output type, icon, fixture geometry, and result comparison. The Grade Pad entry is the first
target for a full record. Native analyses and annotations are tracked separately from this matrix.
