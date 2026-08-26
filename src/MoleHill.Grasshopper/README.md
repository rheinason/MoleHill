# MoleHill.Grasshopper

The Grasshopper project is a thin Rhino/GH host over reusable algorithms in `MoleHill.Core`. The
registry components cover conventional flat item/list ports; hand-written components remain appropriate
for stateful solvers, exotic data, or tree-shaped terrain exchange.

## Terrain exchange

- `MoleHill Terrain Snapshot` reads the latest completed final result from the MoleHill Rhino plugin
  through a reflection bridge. There is intentionally no project reference from the GHA to the RHP.
- `MoleHillTerrainData` / `MoleHillTerrainGoo` package a final mesh, hard constraints, named region
  boundaries, a stable key, source revision, and diagnostics. Geometry is duplicated at the wrapper
  boundary.
- `Construct Terrain` and `Deconstruct Terrain` keep the type open to standard Grasshopper mesh and
  curve editing. Each zone is represented by one curve-tree branch; multiple outlines in a branch are
  treated as disjoint parts of the same named zone.
- `Partition Terrain` accepts either an explicit curve tree or the embedded regions. All outlines are
  inserted into the source TIN in one Core split, and every piece is extracted from that common result,
  so adjacent pieces share exact seam vertices. Later branches win where zones overlap. The optional
  remainder contains faces outside all zones.

Zones never cause implicit partitioning. Keeping region metadata separate from terrain pieces lets a
definition pass through untouched, be partitioned into one-terrain-per-surface workflows, or be manually
cut/joined and reconstructed with ordinary Grasshopper tools.

## Tests

`tests/MoleHill.Grasshopper.Tests` links the component, type, utility, and registry sources directly.
Keep component registration smoke tests there. Rhino geometry execution tests use `RhinoNativeFact` so
they skip cleanly when the native Rhino runtime is unavailable; topology behavior belongs in Core tests.

See `Registry/README.md` for the spec-driven component pattern.
