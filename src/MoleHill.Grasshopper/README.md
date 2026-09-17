# MoleHill.Grasshopper

The Grasshopper project is a thin Rhino/GH host over reusable algorithms in `MoleHill.Core`. The
registry components cover conventional flat item/list ports; hand-written components remain appropriate
for stateful solvers, exotic data, or tree-shaped terrain exchange.

The [B7 redesign](../../docs/grasshopper-redesign-plan.md) is in progress. Snapshot now uses the
separately shipped `MoleHill.Interop` contract and supports GUID binding through its menu, an explicit
follow-panel mode, and a text-input override. Duplicate names require a GUID. During a rebuild, a
same-session reference holds its last completed result and marks it stale. A saved binding uses a
persistent MoleHill identity in the source Rhino document. Snapshot appends a content fingerprint
and can freeze a completed terrain into the GH file, explicitly refresh it, or resume live updates.
The live component refreshes its displayed name when the Rhino terrain is renamed, even if the
geometry fingerprint stays the same.
Terrain-aware modifier propagation and full zone semantics remain planned.

## Terrain exchange

- `MoleHill Terrain Snapshot` reads completed final results through a versioned typed bridge in
  `MoleHill.Interop`. The GHA discovers the bridge instance without a project reference to the RHP.
  Binding searches open Rhino documents by persistent document identity, then terrain GUID. Multiple
  open copies of the same source document must be disambiguated by a new menu selection.
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
An optional `Keys` list keeps explicit branch identities stable when the Grasshopper tree is reordered.
Duplicate keys are rejected before splitting so downstream identities remain unambiguous.
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

`Grade Line` grades away from a drawn line — the corridor grader at width zero, so a crest, a toe or a
swale invert needs no invented width. Beyond the shared Fill/Cut slopes it exposes four optional
per-side overrides (Left/Right Cut and Fill); zero on any of them inherits the shared pair, so the
symmetric case needs no wiring. There is no per-side enable, because a line at an authored elevation is
a discontinuity and both sides always resolve to some slope.

The `Retaining Wall` component takes a **Grade Terrain** boolean plus slope and reach inputs, appended
*after* its Terrain port rather than inserted before it — saved definitions bind ports by index, so the
four original inputs keep their positions. Off (the default) is the historical breakline-only result.

`Mesh Simplify` exposes the Core 2.5D simplifier with maximum-deviation and target-vertex-count modes.
Straight **Required Edges** whose endpoints coincide with mesh vertices are retained; the component
reports measured deviation, protected-vertex count, and the explicit Core termination diagnostic.
Retained-percentage mode converts the used-vertex percentage to a deterministic target count. Terrain
metadata propagation and a matched native fixture remain future parity work.

`Project To` uses the Core surface conformer to blend an input mesh toward a target 2.5D mesh. Strength,
feather distance, and nested even-odd boundary loops are supported; the output reports changed vertices.
The legacy route is mesh based; when a typed Terrain input is supplied, Project To also emits an
appended Terrain output carrying the source metadata through the projected mesh.

`Mesh Simplify` likewise accepts an optional typed Terrain input after its legacy mesh ports and emits
an appended Terrain output with the source metadata and the simplified mesh.

`Mesh Smooth` follows the same migration pattern and uses source breaklines as default fixed curves when
its explicit breakline input is empty.

`Remesh` accepts an optional typed Terrain input, uses its breaklines as default constraints, and emits an
appended Terrain output carrying the source metadata.

`Add Geometry` merges additional points and breaklines with an existing mesh and rebuilds one constrained
TIN through Core without baking Rhino objects. Optional Boundary curves constrain the rebuilt footprint,
and a typed Terrain input/output carries source metadata through the rebuild. Detailed boundary-role
parity remains future work.
The typed output also includes the newly supplied breaklines in its constraint collection.

The Add Geometry, Project To, and Simplify components embed the matching native modifier artwork so
their Grasshopper entries remain recognisable alongside the Rhino panel cards.

`Balance Grade Pad` is an exploratory mesh-output tool. It accepts one closed 3D boundary, bounded
elevations at its first vertex, a target net cut-minus-fill volume, and a tolerance; Core grades and
measures both endpoints, then bisects a bracket. It emits the best graded mesh, the actual adjusted
boundary for baking and native recreation, cut/fill/net, a distinct status, and every sampled value.
Cut/fill slope text uses `SlopeInput` (bare numbers mean degrees). The component does not yet preserve
MoleHill Terrain metadata or offer the planned saved example graph.

`Grade Pad` retains its original mesh ports for existing definitions and adds an optional Terrain input
plus appended Terrain output. When used, source constraints, zones, units, Project Base, and identity
metadata are carried through the graded mesh.
When no explicit lock curves are supplied, the Terrain input's breaklines are used as default locks.

## Tests

`tests/MoleHill.Grasshopper.Tests` links the component, type, utility, and registry sources directly.
Keep component registration smoke tests there. Rhino geometry execution tests use `RhinoNativeFact` so
they skip cleanly when the native Rhino runtime is unavailable; topology behavior belongs in Core tests.

See `Registry/README.md` for the spec-driven component pattern.
