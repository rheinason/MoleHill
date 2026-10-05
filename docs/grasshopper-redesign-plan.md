# B7 — Grasshopper terrain workflow redesign

Status: **implementation substantially complete; acceptance gates remain**. Updated 2026-09-16. The separately shipped typed Interop bridge,
GUID picker/follow mode, persistent source-document binding, duplicate-name rejection, same-session
stale holding, content fingerprint, and saved freeze/refresh workflow have landed in code. These
behaviours have now been solved and traced in a real GH canvas. The normal file-open path for legacy
graphs remains open. Mesh Simplify now exposes the Core
deviation/count/percentage simplifier and mandatory edge preservation, and Project To exposes Core
surface conforming with strength, feather, and boundary loops, Add Geometry now rebuilds merged
points/breaklines through Core TIN, and Grade Pad, Project To, Mesh Simplify, Add Geometry, and Mesh
Smooth and Balance Grade Pad now carry
Terrain metadata through appended typed outputs; Mesh Smooth, Remesh, Retaining Wall, Grade Path, and In-Situ Stair now follow the same migration
pattern. The remaining gaps are native parity fixtures and terminal/host-specific workflows rather than
the typed Terrain contract. The Core
bracketed balance search is exposed by a new one-pad GH component, whose typed Terrain output preserves
source metadata. See the
[seeded parity inventory](gh-modifier-parity-matrix.md).
The validated two-branch starting graph is shipped at
[`examples/Grasshopper/TerrainSnapshotBranches.gh`](../examples/Grasshopper/TerrainSnapshotBranches.gh).
The partition classifier now has automated coverage for nested holes and later-branch overlap priority;
native breakline clipping remains covered by a Rhino-native test when available.
Partition also accepts explicit stable zone keys after the legacy inputs, so branch reordering does not
reassign child terrain identities; duplicate keys are rejected before partitioning to keep identities
unambiguous.

The current full suite is green: Core 858/858, Rhino 639 passed (113 native skips), and Grasshopper
36 passed (15 native skips). The installed Rhino 8 Grasshopper library was also enumerated live and
reported all 18 MoleHill components. A fresh live canvas placement and solve of Retopo also completed;
with no mesh wired, the component reported the expected `Input parameter M failed to collect data`
warning and produced no runtime exception. After refreshing the installed library, a live Balance Grade
Pad discovery and canvas placement confirmed its appended Terrain input and output (12 inputs, 13
outputs) and the expected missing-boundary/elevation warnings.

### Delivery gate audit — 2026-09-16

| Gate | Evidence now present | Remaining acceptance |
| --- | --- | --- |
| Phase 1 contracts and references | Interop bridge, identity/fingerprint contract, live/frozen reference lifecycle, current-graph save/reopen, direct legacy component/wire migration | Normal legacy `GH_DocumentIO.Open` completion path; native build-exception fixture |
| Phase 2 first workflow | Grade Pad, balance helper (now typed Terrain-aware), typed Terrain migration, live multi-branch and stale/frozen tracing | Matched native Grade Pad fixture and terrain-aware chosen-input workflow |
| Phase 3 modifier coverage | Add Geometry, Project To, Mesh Simplify, Mesh Smooth, Remesh, Retaining Wall, Grade Path, In-Situ Stair, and Retopo routes with diagnostics and preserved terminal semantics; live Rhino 8 discovery enumerated all 18 installed MoleHill components, including every new route | Remaining modifier routes, constraint-aware parity fixtures, Retopo native comparison |
| Phase 4 zones/Revit | Partition holes, overlap priority, remainder, stable keys, the native zone boundary priority comparator moved to Core, Toposolid preparation; 2026-10-05: the Python adapters replaced by the compiled `MoleHill.Revit.gha` (Write/Inspect Toposolids, one-wire input, self-hiding outside Revit), its planner unit-tested; environment probe found no Revit or Rhino.Inside.Revit installation | GH priority metadata and resolver use, full multi-zone live acceptance graph; Revit transaction execution requires a Revit host |
| Phase 5 usability/release | Native artwork, validated Snapshot example, live-saved `ModifierRoutesInventory.gh`, performance baseline, Yak package | Working modifier study examples with matched outputs and final release review |
Backlog: [B7](backlog.md#b7--grasshopper-terrain-workflow-redesign) umbrella, tracked as sub-items
B7a–B7e (see [Delivery sequence](#delivery-sequence)).

## Purpose

Make MoleHill's Grasshopper surface a usable, coherent environment for terrain design: reliable live
references to Rhino terrains, modifier parity, dependable zones, recognisable icons, and practical
Rhino.Inside.Revit workflows. Grasshopper should also let a user explore a difficult grading problem,
compare alternatives, and carry the chosen result back into their normal Rhino workflow.

The typical starting point is a minimal MoleHill terrain in Rhino. The user references its completed
surface in Grasshopper and adds operations there. Creating a terrain directly from GH geometry remains
a supported workflow. This is a redesign of the user experience and terrain data flow, not simply a
list of missing components.

## Scope and boundaries

In scope:

1. Robust terrain selection and live updates in documents containing several terrains.
2. Modifier-to-component parity, including the parameters and constraint behaviour that affect results.
3. Zones that remain correct through editing, branching, partitioning, and export.
4. An approachable route into Rhino.Inside.Revit (configured examples; Revit-host verification is
   out of scope — see section 4).
5. Icon and naming parity with native MoleHill tools.
6. Exploratory grading, including cut/fill balancing and difficult slope conditions, with useful
   outputs for returning the chosen design to Rhino.

Out of scope:

- Converting a Rhino modifier stack into a GH graph, including generating and exploding clusters.
- Converting a GH graph into native MoleHill modifiers or baking a terrain with its modifier history.
- Synchronising editable design history between Rhino and Grasshopper.
- Requiring GH geometry to be baked before a GH modifier can use it.
- Verifying create/update behaviour inside an actual Rhino.Inside.Revit host (see section 4).
- A Grasshopper Sculpt component. Sculpt stays Rhino-only (see section 2).

The two return paths are deliberate:

- **Recreate the design:** bake the successful input geometry, then manually recreate the native
  modifier with the chosen settings. The graph must expose the actual geometry used and readable
  settings, so the user can reproduce the result without reverse-engineering it.
- **Continue from the surface:** bake the resulting mesh and assign it to a new terrain's
  **Exact TIN Mesh** input. This is a geometry checkpoint. It carries no modifier history and the
  existing mesh-input path does not restore GH zone or constraint semantics automatically.

No separate publishing system is required for either path.

## Existing foundation and gaps

The [GH project README](../src/MoleHill.Grasshopper/README.md) and
[architecture](architecture.md#rhino--grasshopper-terrain-exchange) describe the implemented exchange.

- `MoleHill Terrain Snapshot` now distinguishes bound and follow modes. Bound mode searches open
  documents by a saved MoleHill document identity and resolves its terrain GUID, while a non-empty
  text input still targets the active document. Duplicate names are rejected. Same-session stale
  holding, freeze, and fingerprint comparison are implemented. A disposable Rhino 8/GH canvas on
  2026-09-15 verified two independently bound terrains, unchanged bindings after panel selection,
  automatic name refresh after a source rename, and a completed snapshot changing to `Frozen`.
  A second disposable canvas on 2026-09-16 verified `.gh` save/reopen, source-changed freeze,
  Refresh/Resume, and selective branch updates; see the live evidence below. Fresh Rhino source-document
  A native build exception and the normal legacy file-open path remain open.
- `TerrainGrasshopperBridge` is discovered by type and accessed through `MoleHill.Interop`, a
  separately shipped typed contract. `Revision` remains a session hint and restarts at 0 on reopen;
  the content fingerprint is the provenance value.
- `MoleHill.Shared` is still linked into both plugins as source; cross-host Snapshot DTOs and the
  interface instead live in `MoleHill.Interop`.
- `MoleHillTerrainData` / `MoleHillTerrainGoo` already carry a mesh, breakline curves, regions, source
  key/revision, diagnostics, units, and Project Base metadata, with versioned persistence.
- `Construct Terrain`, `Deconstruct Terrain`, and `Partition Terrain` already provide an open boundary
  with standard GH geometry. Partitioning inserts shared boundaries in one split before extracting
  pieces, including an optional remainder.
- Existing grading/remeshing components mostly use mesh ports. A mesh cast discards the extra terrain
  data; passing through such a component is not sufficient to preserve the terrain contract.
- The snapshot currently combines hard and elevation constraints into one curve collection. Audit
  whether each downstream operation needs their distinction before promising constraint parity.
- `Prepare Toposolid` and the Write/Inspect Toposolids components exist. Treat these as a foundation to verify and
  improve; source code and documentation alone do not establish an end-to-end Revit workflow.

### Live verification — 2026-09-16

A disposable Rhino 8 slot loaded the exact Debug RHP and the rebuilt GHA. A four-component GH canvas
had two GUID-bound Snapshot components, each wired to Deconstruct Terrain. Assertions used Grasshopper
volatile output data, document solution history, and output object identity.

| Check | Observed result |
| --- | --- |
| Save/reopen | `GH_DocumentIO.SaveQuiet` wrote a `.gh`; `Open` restored four components and two wires. After adding the reopened document to Grasshopper's document server, both bound branches solved against the same Rhino source. A second saved file reopened Site A as `Frozen` and Site B as `Current`. |
| Frozen lifecycle | Editing and rebuilding Site A changed the frozen component to `FrozenSourceChanged` while its stored fingerprint stayed fixed. The badge was recomputed after another `.gh` reopen. Refresh captured the new fingerprint and returned to `Frozen`; Resume returned to `Current`. |
| Selective updates | Editing Site B added two GH document solutions; Site A Snapshot and Deconstruct retained their exact output objects, while Site B Snapshot and Deconstruct replaced theirs. |
| Same-content rebuild | Rebuilding Site B without source edits produced `Rebuilding` then `Current` with the same fingerprint. The second solution replaced Site B Snapshot and Deconstruct outputs. This is an extra downstream run to benchmark and, if costly, reduce. |
| Frozen badge cost | When Site A's source changed, the `FrozenSourceChanged` transition replaced both its Snapshot and Deconstruct output objects even though the frozen fingerprint stayed fixed. |
| Missing exact-TIN source | Removing Site B's mesh produced `Rebuilding` then `Unavailable`; the terrain and downstream mesh outputs cleared. Restoring the same object GUID and reattaching the source reference returned the branch to `Current`. |
| Failed-status contract | A controlled `LastBuildMessage` injection produced `Failed`. Snapshot held the last completed terrain and fingerprint, emitted a warning, and Deconstruct kept its mesh. This checked the GH status path, not a native build exception. |
| Rhino source reopen | A saved `.3dm` reopened through `RhinoDoc.OpenHeadless` with the same MoleHill document identity, terrain GUID/name, and exact-TIN source object GUID. A fresh final rebuild returned `Current` and an eight-face Snapshot. The headless document was disposed without replacing or detaching the managed active document. |
| Large terrain baseline | A generated 90,601-vertex/180,000-face exact TIN built in 0.30 s after a 0.07 s source snapshot. Two bound Snapshot → Deconstruct branches took 218.4 ms wall time for a forced solve (103.6 ms GH solution span). Managed memory settled at 74.7 MB, about 9.4 MB over the post-build Rhino baseline, and remained flat across a second forced solve. These are one-machine Debug-build measurements, not release targets. |
| Legacy graph upgrade | [`tests/MoleHill.Grasshopper.Tests/Fixtures/legacy-snapshot-20260914.gh`](../tests/MoleHill.Grasshopper.Tests/Fixtures/legacy-snapshot-20260914.gh), saved by the 2026-09-14 GHA, contains Snapshot → Deconstruct with the original ten Snapshot outputs and one wire. In a disposable Rhino 8 slot, `GH_Archive.ReadFromFile` and `GH_Document.Read` restored two components, upgraded Snapshot to 12 outputs, and kept the Deconstruct input connected. After canvas attachment, `g1_solve_graph` produced `Current`, the selected terrain name, and a fingerprint with no component errors. The migration is covered by `LegacySnapshotArchive_DeserializesAndPreservesWire` (native test; skipped when Rhino is unavailable). A fresh 2026-09-16 `GH_DocumentIO.Open` probe on the same file, including a copy without `GHALibraries`, still populated in-memory objects but remained `Running` after a ten-second worker-thread timeout; the exact managed slot was then closed. Legacy component/wire migration is verified through direct document deserialization; the normal file-open completion path remains unverified. |

The lifecycle fixtures are small (nine vertices per terrain). Actual exception handling during a failed native
build and the normal file-open path for saved pre-upgrade `.gh` definitions remain acceptance work. The large-terrain baseline above
uses regular grid topology; captured production terrain cases should supplement it when available.

## 1. References that users can trust

**Proposed interaction:** choose a terrain by its readable name from the source document; retain its
stable terrain identity behind that selection. Names are labels and a convenient lookup mechanism,
but a saved reference must survive a rename. Duplicate names need disambiguation; never select the
first matching terrain silently.

Specify and implement these behaviours:

- Several references in one graph can independently target different terrains.
- A bound reference does not change when the user selects another terrain in the panel.
- If retained, “follow panel selection” is an explicit mode, visibly different from a bound reference.
- Deletion, missing plugins, unavailable documents, and ambiguous names produce actionable messages.
  A missing reference never rebinds to another same-named terrain without a deliberate user action.
- Save/reopen, rename, Save As, copied documents, and switching between Rhino documents have a stated
  binding policy. Choose the persistent document identity/rebinding mechanism during phase 1; a file
  path or active-document assumption alone is insufficient.
- Only completed, build-consistent snapshots feed downstream work. Show whether the source is
  rebuilding, unavailable, failed, current, or explicitly frozen; never label old data as current.
- **Rebuilding holds the last result, marked stale (decided).** While the source is rebuilding or has
  deferred outputs, the component keeps emitting its last completed snapshot. It adds a warning and
  sets a `Status` output to `Rebuilding`. A status change requires a GH solve and may expire downstream
  components even when the terrain fingerprint is unchanged. Coalesce source events and avoid repeated
  solves for the same status/fingerprint pair; verify the actual downstream solve count in GH. A newly
  completed snapshot refreshes the graph when its fingerprint differs. With no previous completed
  snapshot, it outputs nothing and reports `Rebuilding`. A failed build keeps the last snapshot with
  `Status = Failed` and the build diagnostics; it never reports `Current`.
- **Provenance is a content fingerprint (decided).** The snapshot carries a deterministic hash of the
  completed mesh, constraints (by kind), zones (keys, names, boundaries, semantics), and unit/coordinate
  metadata. Refresh compares fingerprints to decide whether anything changed, and a frozen snapshot
  compares its fingerprint with the live source to show "source has changed". The session revision
  number may be displayed as a hint, but never used for equality or provenance.
- Live updates follow relevant completed geometry/metadata changes. Coalesce rapid events and avoid
  rebuilding expensive downstream graphs merely because panel selection or unrelated UI state changed.
- Provide an explicit freeze/internalise and refresh workflow for exploration. A frozen snapshot saves
  with the GH file and clearly shows its origin and fingerprint. It must never silently resume live updates.
- **Live auto-refresh is the default (decided).** A new or bound reference updates automatically.
  Freezing is always opt-in and never a default or a side effect.
- **Picker: right-click menu plus text input (decided).**
  - The right-click menu lists the source document's terrains by name. Duplicate names get a short
    id suffix, e.g. `Site B (3f2a)`.
  - Menu actions: `Follow panel selection`, `Freeze`, and `Resume live` (shown while frozen). Choosing a
    terrain stores its GUID, never its name.
  - The message bar always shows the mode: `Live: Site A`, `Following: Site A`, `Frozen: Site A`, or
    `Frozen · source changed`.
  - The Terrain text input stays for scripted graphs. A non-empty input overrides the menu choice, and
    the message bar says so (`Input: Site A`). An ambiguous name is an error (D3).

**Existing component is upgraded in place (decided).** `MoleHill Terrain Snapshot` keeps its
`ComponentGuid` and its existing port indices. New outputs (`Status`, fingerprint) are appended. A
persisted reference mode (`Bound` / `Follow panel selection`) is written with the component. Files saved
without that value open in `Follow panel selection` if their input was empty, and `Bound` otherwise, so
old graphs keep their behaviour. New instances default to `Bound`. The duplicate-name defect is fixed
as part of the upgrade: an ambiguous name produces an error that lists the candidates, never the
first match.

Acceptance: two same-named terrains can be distinguished; renaming the chosen one preserves binding;
editing it updates the correct graph branch; panel selection and document switching cannot redirect it;
an old GH file with an empty input still follows the panel selection after reopening; rebuilding keeps
the last completed snapshot marked stale, and a changed completed fingerprint refreshes the correct
branch without duplicate source-event solves. Record downstream solves caused by status updates.

## 2. A consistent terrain contract and modifier parity

The proposed primary chain is **Terrain → modifier → Terrain**. Keep ordinary mesh and curve access
through construct/deconstruct so users can combine MoleHill with the rest of Grasshopper.

**Migration: widen existing components, keep GUIDs (decided).** Existing modifier components keep
their `ComponentGuid` and port indices:

- The terrain input accepts either a MoleHill Terrain or a plain Mesh. A mesh is wrapped as a terrain
  that visibly has no constraints, zones, or source identity.
- The primary output becomes a MoleHill Terrain. `MoleHillTerrainGoo` already casts to `Mesh`, so the
  intended result is that existing mesh consumers keep receiving geometry and a terrain can be passed
  to ordinary GH mesh parameters. Verify that saved wires reconnect and cast correctly after changing
  the output parameter type; the cast alone does not prove serialized-wire compatibility.
- When the input was a terrain, the output carries the propagated metadata described below. When it was
  a mesh, the output carries none and says so in its diagnostics.
- The reverse cast from Mesh to Terrain carries no metadata. Rules for inferring constraints or zones
  from a plain mesh are not part of this plan.

The upgrade must not reinterpret old wires. Keeping component GUIDs and port order is necessary but
does not establish compatibility when parameter types change. Reopen and solve saved legacy graphs,
checking wire connections, input/output data, and downstream mesh consumers. If an in-place change
fails that check, record the incompatibility and choose a compatible migration before shipping it.

**Bridge contract: a shared interop assembly (decided).** Replace reflection by property name with a
small `MoleHill.Interop` assembly (net7.0, RhinoCommon only). It holds the snapshot/status/region DTOs
and the bridge interface, and both the RHP and the GHA reference it. Constraints:

- It is a separately shipped DLL, loaded once. ILRepack must **not** merge it into the `.gha`, and it must
  not be source-linked like `MoleHill.Shared`, otherwise each side gets its own copy of the types.
- Add `MoleHill.Interop.dll` to the Rhino and Grasshopper build outputs, the Grasshopper Libraries copy
  step, and the explicit `build-yak-package.ps1` staging list. Check the staged Yak package contains one
  copy beside the `.rhp` and `.gha`, and test loading that package in disposable Rhino/GH; successful
  compilation alone does not verify shared runtime type identity.
- The GHA still has no project dependency on the RHP. It finds the bridge through the interop interface.
- The contract carries a version number. A GHA older or newer than the RHP reports a clear
  mismatch message instead of failing silently.
- A contract test builds a snapshot on the Rhino side and reads it back through the interface.

**Terrain data persistence.** Splitting constraints by kind, adding zone semantics, and adding the
fingerprint all change what `MoleHillTerrainGoo` serializes. Increase its persistence version and keep
a reader for the current format, so internalised terrains in existing GH files still load. A legacy
curve list is read as constraints of unknown kind and marked as such.

Before migrating components, define:

- Geometry ownership: one branch cannot mutate the input or a sibling branch. Measure mesh-copy and
  cache costs on realistic terrains rather than multiplying full copies unnecessarily.
- Constraint propagation: preserve, add, clip, or invalidate constraints according to the actual
  operation. Distinguish constraint types when required by Core; do not just reattach stale curves.
- Region propagation: define what happens when extent, topology, or elevation changes (see section 3).
- Identity: source identity/revision records provenance; a GH derivative also needs its own stable
  output identity and a content fingerprint. Two alternatives must not accidentally target the same
  Revit element, and changing a slider must not create a new identity on every solve.
- Units, tolerances, slope interpretation, optional values, list matching, and tree access. Distinguish
  an absent optional value from zero. Show slope units and support the shared `SlopeInput` vocabulary.
- Failure behaviour: report invalid inputs, failed operations, and fallbacks explicitly. An unchanged
  fallback mesh must not look like a successfully graded solution.
- Standard GH interoperability: mesh-only processing loses metadata. Reconstruction must make missing
  or invalidated constraints/zones apparent. Transforms must keep mesh, constraints, regions, units,
  and coordinate metadata consistent; reject shapes that cease to be valid terrain where required.

Seed a parity matrix in phase 1 covering every active native modifier and mode: Triangulate/boundary
roles, Add Geometry, Grade Pad, Grade Path, Retaining Wall, In-Situ Stair, Remesh, Smooth, Simplify,
Retopo, Sculpt, and Project To. Refresh this inventory against the registry when implementation starts.
Include B10's swale mode when that feature is available. Phase 1 completes only the Grade Pad entry.
The other entries are completed as their components land in phase 3.

For each entry record inputs, defaults, units, optionality, constraint effects, outputs, icons,
implementation status, and a matched Rhino/GH fixture.

Host-specific entries are pre-classified (decided):

| Native modifier | GH treatment |
| --- | --- |
| Triangulate (incl. boundary roles), Add Geometry | Covered by `Construct Terrain` and an Add Geometry equivalent, not 1:1 components. Boundary roles become explicit inputs. |
| Project To | Takes its target geometry from GH inputs. It never picks objects from the Rhino document. |
| Retopo | Available, but **terminal**: it outputs a quad Mesh, not a Terrain, so it cannot feed components that need a triangular 2.5D surface. |
| Sculpt | **Rhino-only.** No GH component. A sculpted Rhino terrain enters GH through its completed snapshot. |

Parity means equivalent supported operations on equivalent inputs. A GH component need not reproduce
the panel layout or Rhino picking interactions. Geometry produced entirely in GH is a first-class input.

Add the analysis components needed to evaluate designs, especially cut/fill quantities and slope, with
contours and other useful outputs sequenced alongside the workflows that need them. Track analyses and
annotations separately from modifier parity; their native families remain separate. Full parity for
all drawing, object-placement, and scatter workflows is not implicitly included in this milestone.

## 3. Zones throughout Grasshopper

Users must be able to inspect, select, create, replace, and remove named zones from GH geometry, then
carry them through modifiers or explicitly partition them into surfaces.

- Preserve stable zone keys separately from names and GH tree paths. Renaming/reordering cannot assign
  an existing downstream identity to a different zone.
- Specify how GH-authored zones and referenced zones are combined, and make overlap priority visible.
  Match native resolved-zone behaviour on parity fixtures.
- **First supported zone set (decided).** Each zone carries:
  - key, name, and its original boundary outlines;
  - stack index and `priority by elevation`;
  - enabled flag (disabled zones travel marked; they are never silently dropped);
  - colour and colour-override flag;
  - layer name, material name, and `split to separate mesh`.
  Layer, material, and split are output hints only. In GH, splitting into separate meshes still
  happens only through an explicit Partition, and a disabled zone takes no part in resolution.
- **Priority carries both raw and resolved data (decided).** The terrain carries each zone's raw
  outlines and priority inputs *and* the resolved, non-overlapping regions. Any GH zone edit
  re-resolves with the same resolver the native build uses, so overlaps stay editable and results
  match native. The native boundary ordering now lives in Core `ZonePriorityResolver`; carrying raw and
  resolved zones through GH and reusing that resolver there remains B7c work.
- Define plan-boundary versus surface-elevation semantics. Reproject/clip boundaries when appropriate;
  a modifier that changes terrain extent must not leave apparently valid regions outside the surface.
- Support multiple outlines, disjoint regions, nested holes, overlaps, empty zones, and remainder output.
- Partitioned pieces must share exact seam coordinates and carry only applicable constraints. Identify
  and report non-partitioning/disconnected cases rather than corrupting branch correspondence.
- Preserve names, keys, and branch relationships through construct/deconstruct and ordinary tree work.
- Provide clear zone preview and selection feedback. Regions do not become separate terrain meshes or
  Revit subdivisions merely because they exist.

Acceptance: one multi-zone fixture survives reference → grading → zone edit → partition → preparation,
including holes, overlap priority, remainder, rename, and reorder. Check geometry and identities.

## 4. Ready for Rhino.Inside.Revit

Use the existing [Revit components](../src/MoleHill.Revit/README.md) as the starting point:

```text
Terrain Reference / Construct Terrain
  → optional GH modifiers and zone editing
  → explicit partitioning when separate surfaces are wanted
  → coordinate placement → Prepare Toposolid → Revit create/update
```

**Delivery: compiled components in their own assembly (revised 2026-10-05).** The earlier decision was
pasted Python adapters in configured `.gh` examples. In practice, each script meant creating up to nine
typed inputs by hand. Write Toposolids and Inspect Toposolids now live in `MoleHill.Revit.gha`
([README](../src/MoleHill.Revit/README.md)).

- **Wiring.** Write Toposolids takes Prepare Toposolid's `Preparation` package list as a single wire and
  writes subdivisions from the same package.
- **Packaging.** It ships in the Yak package and aborts its own load outside Revit, so plain Rhino
  never shows it.
- **No Revit dependency elsewhere.** It references Revit's API only as a build-time reference assembly,
  so neither `MoleHill.gha` nor the `.rhp` gains one.
- **Separation.** Core geometry work and Revit transactions stay separate: everything that can be
  decided without Revit is in a tested planner.

**Revit-host verification is descoped (decided).** MoleHill verifies everything up to the Revit
boundary. The Revit create/update step ships as compiled components, labelled
**unverified in Revit**. No environment is available to test them in a Revit host, and `rhino-mcp`
cannot drive one.

Verify in Rhino/GH (automated where possible) the data handed to the adapters:

- One terrain, multiple terrains, partitioned zones, and two design alternatives.
- Explicit shared/project-coordinate placement and exactly one unit conversion.
- Stable output identities: an unchanged design keeps the same identities and fingerprints; an edited
  design keeps its identity with a new fingerprint; renamed or reordered zones do not swap identities.
- Point budgets, holes, disconnected footprints, and invalid terrain diagnostics.

Document, without claiming Revit verification:

- The intended create/update policy (unchanged → no-op, edited → update the intended element), and a
  stated policy for missing branches and removed zones. Absence must not silently authorise deletion.
- Independent Toposolids versus optional host-following subdivisions, explained with examples.
- Approximation limits: Revit rebuilds its triangulation, so exact imported TIN edges are not promised.
  Distinguish sampled error measurements from a verified whole-surface error bound.

Community or user reports from real Revit hosts may be recorded here with host versions. They do not
count as MoleHill evidence.

## 5. Icons, discovery, and preview

Use matching native MoleHill artwork for equivalent operations and a consistent related set for
reference, terrain-data, zone, and Revit-preparation tools. Reuse existing icon assets/generation routes;
do not introduce an unrelated visual language. Check readability at GH icon size and in its actual UI.

Agree component names, search terms, categories, port labels, help text, and default visibility together.
Make terrain/zone preview usable without unpacking everything merely to see the result. Establish
predictable preview toggles for the source and alternatives so overlapping Rhino and GH displays do
not obscure the design being evaluated. Supply example graphs as part of the feature, not a later task.

## 6. Exploration and returning a chosen design

First complete example: **snapshot → Grade Pad alternatives → cut/fill comparison → chosen inputs**.
Second: **snapshot → Grade Path/swale alternatives → slope and ponding checks → chosen inputs**.
The second can start with existing Grade Path; B10's new mode is a follow-on dependency.

- Keep the reference surface fixed and explicit during comparisons. A final Rhino snapshot does not
  automatically include the original baseline needed for earthworks. Allow another terrain/snapshot
  as reference, and report unmeasured areas rather than treating them as zero difference.
- A balance helper takes a bounded elevation search, a target net volume, and a stated tolerance.
  Return the actual adjusted input geometry, chosen elevation/settings, achieved cut/fill/net values,
  convergence status, and diagnostics. Verify bracketing and monotonic assumptions on the supported
  grading cases; finite iteration limits and unattainable targets must have clear outcomes.
- **Algorithm: bracketed bisection (decided).**
  1. The user supplies minimum and maximum elevations. Grade and measure at both ends.
  2. Require `net − target` to change sign between them. If it does not, return `NoBracket` with
     both samples and the nearer end. Never extend the range silently.
  3. Bisect until `|net − target| ≤ tolerance` (`Converged`) or the iteration cap is reached
     (`IterationCap`, returning the best sample so far).
  4. Record every sample. If the net volumes are not monotonic in elevation, or a grading fallback
     happened, return `NonMonotone` or `GradingFallback` alongside the best result. Never return
     plain `Converged` in those cases.
  5. Output all samples (elevation, cut, fill, net) so users can plot the curve.
  Put the search in Core, grading and measuring through existing `PadGrader` and volume code, so it is
  testable without Grasshopper.
- Users can freeze the starting snapshot, branch alternatives, and compare the same reference without
  repeated upstream work. Benchmark response time; isolate/cache work independent of changing inputs.
- No solve silently modifies Rhino source objects. Exploratory geometry remains GH geometry until baked.
- For recreation, show which native modifier and settings to use, including units and optional values.
  Verify an example by baking inputs and comparing the reconstructed native result with the GH result.
- For a checkpoint, demonstrate baking a mesh and selecting it as **Exact TIN Mesh** in a new terrain.
  Explain that this bypasses initial point triangulation, ignores the other point/curve sources, and
  does not restore semantic constraints or zones. Downstream native edits may therefore behave
  differently from edits to the original fully described terrain.

## Delivery sequence

B7 is an umbrella. Each backlog sub-item closes on its own evidence; B7 closes when all of them have.

| Phase | Backlog | Deliverable | Completion evidence |
| --- | --- | --- | --- |
| 1 — thin contracts | B7a | `MoleHill.Interop` bridge contract and shipment; reference modes, status, and fingerprint; the in-place upgrade rule for Snapshot and modifier components; terrain/identity contract and goo version for Grade Pad; parity matrix seeded with the host-specific classifications | Contract test passes across RHP/GHA; staged Yak contains one interop DLL and loads in Rhino/GH; the duplicate-name defect is fixed; legacy GH files reopen and solve with wires intact |
| 2 — first workflow | B7a | Robust reference + terrain-aware Grade Pad + volume comparison + bracketed balance helper + chosen-input outputs | Multi-terrain updates, stale-while-rebuilding, freeze, independent alternatives, and manual native recreation demonstrated |
| 3 — modifier coverage | B7b | Remaining modifier equivalents (per the matrix) and evaluation outputs in coherent groups | Matched fixtures, preserved constraints/metadata, and actionable failures for each completed matrix entry |
| 4 — zones and Revit preparation | B7c, B7d | Zone resolver moved to Core; full zone workflow; configured Rhino.Inside.Revit example `.gh` files, labelled unverified in Revit | Multi-zone fixture passes the section 3 acceptance in Rhino/GH; prepared data has stable identities and a single unit conversion |
| 5 — usability and release | B7e | Icons, discoverability, previews, performance, help and examples | Matching artwork is embedded for the new modifier routes, and `examples/Grasshopper/TerrainSnapshotBranches.gh` provides a validated two-branch starting graph. Existing GH files reopen; representative new workflows and final release review remain. |

Zone/identity correctness starts in phase 1 and is tested throughout; phase 4 completes the full workflow.
Icon/help requirements apply to each component as it lands. Each phase must leave a usable example.

## Implementation and verification guardrails

- Reusable computation belongs in Core, host calls in Rhino/GH. Avoid a direct GHA-to-RHP project
  dependency or moving the document-bound build service wholesale into Grasshopper. Extract only the
  shared behaviour required for equivalent results. Panel schemas alone cannot generate GH contracts.
- Preserve existing `ComponentGuid`s and port order. Terrain-awareness is added by widening parameter
  types in place, as described in section 2. Never reinterpret an old graph's wires silently; if a
  component cannot be widened compatibly, stop and record why before introducing a new identity.
- Use the existing registry where appropriate, retaining bespoke components for stateful or tree-shaped
  behaviour. Follow its documented constructor-order constraint.
- Test pure geometry/constraint/zone invariants in Core, component and serialization contracts in GH,
  and reference lifecycle plus native parity in disposable Rhino per
  [live-testing guidance](rhino-live-testing.md). Record skipped native tests explicitly.
- Build and solve the acceptance graphs automatically in a disposable slot with the `rhino-mcp`
  Grasshopper tools (`g1_apply_graph`, `g1_solve_graph`, `g1_get_canvas_graph`). Assert on output data,
  not on screenshots. Include saved legacy graphs in these runs to prove in-place upgrades. Measure
  downstream solve counts during rebuilding, failed builds, and unchanged-fingerprint status changes.
- Validate icon and graph usability in real GH. Benchmark realistic large terrains and multiple
  branches; record timing and memory baselines before setting performance targets.
- Update architecture, folder READMEs, and file index as implementation changes them. This plan records
  intended behaviour and must not be mistaken for shipped architecture.

## Decisions

Settled in the 2026-09-15 plan review:

| # | Question | Decision | Where |
| --- | --- | --- | --- |
| D1 | Output while the source rebuilds | Keep the last completed snapshot, mark it stale with a `Status` output, and re-solve downstream once when the build completes | Section 1 |
| D2 | Snapshot provenance | Deterministic content fingerprint; the session revision is only a display hint | Section 1 |
| D3 | Existing Snapshot component | Upgrade in place (same GUID). Old files with an empty input keep following the panel selection; new instances bind. An ambiguous name is an error | Section 1 |
| D4 | Modifier component migration | Keep GUIDs; the input accepts Terrain or Mesh; the output is a Terrain that casts to Mesh | Section 2 |
| D5 | Rhino↔GH bridge | Separate, non-merged `MoleHill.Interop` assembly with a versioned contract | Section 2 |
| D6 | Host-specific modifiers | Triangulate/Add Geometry via Construct Terrain; Project To from GH geometry; Retopo terminal (quad Mesh output); Sculpt Rhino-only | Section 2 |
| D7 | Balance helper | Bracketed bisection over a user-given range, with distinct `NoBracket` / `IterationCap` / `NonMonotone` / `GradingFallback` statuses and all samples output | Section 6 |
| D8 | Revit verification | Descoped: configured examples labelled unverified in Revit; MoleHill verifies up to the adapter boundary | Section 4 |
| D9 | Backlog tracking | B7 umbrella with sub-items B7a–B7e | Delivery sequence |
| D10 | Phase 1 weight | Thin: only the contracts the first workflow needs, plus a seeded parity matrix | Delivery sequence |

Settled in the follow-up review:

| # | Question | Decision | Where |
| --- | --- | --- | --- |
| D11 | Reference picker and freeze UX | Live auto-refresh by default; right-click menu of terrains (stored by GUID) with Follow / Freeze / Resume live; mode always shown in the message bar; a non-empty text input overrides the menu | Section 1 |
| D12 | Zone fidelity | Key, name, outlines, stack index, priority-by-elevation, enabled, colour (+ override), layer, material, split hint | Section 3 |
| D13 | Zone priority in GH | Carry raw and resolved zones; re-resolve with the native resolver after moving it to Core | Section 3 |
| D14 | Revit delivery form | **Revised 2026-10-05:** compiled components in `MoleHill.Revit.gha`, shipped in the Yak package and self-hiding outside Revit. The pasted Python adapters were too cumbersome to set up. Still unverified in Revit (D8) | Section 4 |

The source document identity is stored in Rhino document strings and minted when a terrain is saved
or explicitly chosen for binding. Save the Rhino document after choosing a terrain for durable
reopening. Save As and copies keep the same lineage; when only one copy is open, the binding resolves
to it. When several copies are open, the in-session selected runtime document wins, and a reopened
Grasshopper file requires a new selection to disambiguate. Missing documents and missing identities
are reported without rebinding by name. Phase 1 still needs the terrain ownership, propagation, and derivative-identity rules
(section 2); phase 4 needs boundary/elevation and removed-zone policies (sections 3–4). Record those
bounded implementation decisions here before building the behaviour that depends on them. Add any
other choices found during implementation in the same way.

These decisions affect implementation, but do not change the agreed workflows or introduce graph/stack
conversion. B7 closes when sub-items B7a–B7e have delivered their evidence and documented exceptions,
including both manual return paths. Revit-host behaviour is documented, not verified.
