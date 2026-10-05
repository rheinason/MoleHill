# Grasshopper examples

`TerrainSnapshotBranches.gh` is a small, validated starting graph with two independent MoleHill Terrain
Snapshot branches feeding Deconstruct Terrain. Open it beside a Rhino document containing completed
MoleHill terrains, then use each Snapshot component's menu to choose a terrain if the saved source
identity is not present in the current session. The two branches are intentionally independent so a
panel selection change does not silently rebind either one.

[`ModifierRoutesInventory.gh`](ModifierRoutesInventory.gh) is a live-saved inventory graph containing the
currently shipped grading, surface, wall, stair, balance, and Retopo routes. It is intentionally an
unwired inspection graph; connect terrain and design inputs for a working study.

The open workflow components now include typed Terrain migration for Grade Pad, Balance Grade Pad,
Grade Path, In-Situ Stair, Retaining Wall, Remesh, Smooth, Add Geometry, Project To, and Mesh Simplify.
Retopo is available
as a terminal quad Mesh route; it intentionally does not emit typed Terrain because downstream terrain
components require triangular 2.5D topology. These routes are covered by registration smoke tests; a
saved modifier graph with matched native output remains part of the acceptance backlog.

For the Revit boundary workflow, wire `Partition Terrain -> Prepare Toposolid -> Write Toposolids`; the
last is on the MoleHill > Revit tab, which appears only inside Rhino.Inside.Revit. The Revit step
remains labelled unverified because no Revit host is available in this environment
(see `src/MoleHill.Revit/README.md`).
