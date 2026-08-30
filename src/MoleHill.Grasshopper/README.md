# MoleHill.Grasshopper

The Grasshopper project is a thin Rhino/GH host over reusable algorithms in `MoleHill.Core`. The
registry components cover conventional flat item/list ports; hand-written components remain appropriate
for stateful solvers, exotic data, or tree-shaped terrain exchange.

## Terrain exchange

- `MoleHill Terrain Snapshot` reads the latest completed final result from the MoleHill Rhino plugin
  through a reflection bridge. There is intentionally no project reference from the GHA to the RHP.
- `MoleHillTerrainData` / `MoleHillTerrainGoo` package a final mesh, hard constraints, named region
  boundaries, stable terrain/region keys, lossless source revision, diagnostics, Rhino units, and the
  optional saved Project Base transform. The custom goo is versioned and persistent.
- `Construct Terrain` and `Deconstruct Terrain` keep the type open to standard Grasshopper mesh and
  curve editing. Each zone is represented by one curve-tree branch; multiple outlines in a branch are
  classified by odd/even containment, allowing both disjoint zone parts and nested holes.
- `Partition Terrain` accepts either an explicit curve tree or the embedded regions. All outlines are
  inserted into the source TIN in one Core split, and every piece is extracted from that common result,
  so adjacent pieces share exact seam vertices. Later branches win where zones overlap. The optional
  remainder contains faces outside all zones and within zone holes. Breakline metadata is projected and
  clipped to each output mesh.
- `Prepare Toposolid` validates one heightfield terrain and its horizontal boundary/subdivision profiles,
  retains boundary and breakline-critical samples, and calls Core `ToposolidPointReducer` for deterministic
  adaptive reduction against a point budget and measured vertical error. It emits a stable SHA-256 geometry
  fingerprint for idempotent downstream updates. Coordinates remain unchanged; apply coordinate transforms
  upstream and use the emitted metres-per-unit value for one unit conversion in the adapter.

Zones never cause implicit partitioning. Keeping region metadata separate from terrain pieces lets a
definition pass through untouched, be partitioned into one-terrain-per-surface workflows, or be manually
cut/joined and reconstructed with ordinary Grasshopper tools.
The default Revit flow is `Partition Terrain -> standard GH editing/transforms -> Prepare Toposolid`, with
one independent Toposolid per branch. Optional Rhino.Inside.Revit Python adapters are kept outside the GHA
under `examples/RhinoInside.Revit/`; subdivisions are a separate opt-in host-following workflow.

The `Grade Path` component appends optional **Width Edges** and **Max Edge Distance** inputs after its
legacy ports. Leaving Width Edges empty is the constant-width path (there is no separate toggle port —
the empty input is the off state; the Rhino panel modifier uses an explicit `UseVariableWidth` checkbox
instead). When edges are wired they are matched globally and uniquely to centerline sides in plan, their
Z is ignored, and uncovered portions blend back to the constant Width input. A clean match reports as a
Remark; only unmatched, ambiguous, or partial edges raise Warnings.

## Tests

`tests/MoleHill.Grasshopper.Tests` links the component, type, utility, and registry sources directly.
Keep component registration smoke tests there. Rhino geometry execution tests use `RhinoNativeFact` so
they skip cleanly when the native Rhino runtime is unavailable; topology behavior belongs in Core tests.

See `Registry/README.md` for the spec-driven component pattern.
