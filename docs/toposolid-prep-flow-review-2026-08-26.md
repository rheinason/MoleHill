# Toposolid Preparation Flow Review — 2026-08-26

## Verdict

Commit `c446c44` (`Add Grasshopper terrain exchange components`) adds a useful, well-separated
Rhino-to-Grasshopper terrain **exchange** flow. It does **not** yet implement a Toposolid preparation
or Revit import flow.

The current path is:

```text
MoleHill final display state
  -> TerrainGrasshopperBridge
  -> MoleHill Terrain Snapshot
  -> MoleHillTerrainData
  -> Deconstruct Terrain / Partition Terrain
  -> Rhino meshes, 3D curves, names and keys
                                      |
                                      X  no Toposolid profiles/points contract
```

That boundary is intentional in the current architecture: `docs/architecture.md:69-88` explicitly
says the mesh is not treated as Revit-ready and describes Revit-specific preparation as future work.
The flow should therefore not be presented as Toposolid-ready until the high-priority gaps below are
implemented.

## Resolution status

All repository-side findings were addressed after this review:

- Added compiled `Prepare Toposolid` validation and a Revit-neutral typed/persistent preparation package.
- Added Core `ToposolidPointReducer` with a hard point budget, protected boundary/breakline samples,
  deterministic spatial-extrema seeding, iterative vertical-error refinement, and measured diagnostics.
- Added stable SHA-256 base and subdivision geometry fingerprints.
- Kept coordinates unchanged and user-controlled upstream; carried explicit Rhino unit metadata so the
  optional adapter converts to Revit internal feet exactly once.
- Added strict heightfield, vertical-face, profile closure/planarity/area/intersection/nesting, and atomic
  region validation.
- Made Construct/Deconstruct preserve region keys, 64-bit revision, diagnostics, units and Project Base
  metadata; added versioned goo persistence.
- Made partition boundary failure atomic and visible, retained stable region-derived child keys, reduced redundant
  terrain copying, made snapshot reflection signature-specific, and deterministically disposed reflection
  DTO geometry.
- Added optional `CreateOrUpdateToposolids.py`, `InspectToposolid.py`, and `CreateSubdivisions.py` adapters
  under `examples/RhinoInside.Revit/`. Revit and Rhino.Inside.Revit remain outside the MoleHill package.

The compiled preparation path is covered by managed/Core tests and Rhino-native-gated Grasshopper tests.
It was also loaded from the newly built `.gha` in Rhino 8: a 961-vertex terrain produced one valid profile,
a stable fingerprint and exactly the requested 180-point hard budget while preserving unit metadata. The
actual `Prepare Toposolid` component registered the expected four-input/fifteen-output contract. The example
Revit transactions still require an actual Rhino.Inside.Revit/Revit session for runtime validation; this
repository and current automation environment do not provide a Revit host.

## Scope and evidence

- Reviewed commit `c446c4446c12a14cb85fc34fa24f2c6a30683088` and the current working-tree version
  of its components.
- Reviewed `Construct Terrain`, `Deconstruct Terrain`, `MoleHill Terrain Snapshot`, `Partition Terrain`,
  the terrain goo/data types, the Rhino reflection bridge, and current tests.
- No `.gh` or `.ghx` Toposolid definition and no Rhino.Inside.Revit/Revit API integration are present
  in the repository.
- `Python Commands Source/CopyAndOpenTopo.py:13` refers to an external `Topo - Path.gh` on an `F:` drive;
  that file is unavailable here. This review cannot cover its internal Grasshopper graph.
- The review reflects the fixes made after the commit: final-state snapshot gating, odd/even zone holes,
  and clipped per-piece breaklines are working and are not listed as open defects.

## Findings

### H1 — There is no Toposolid-ready output contract

`Deconstruct Terrain` exposes a Rhino mesh, 3D breaklines and 3D zone curves
(`DeconstructTerrainComponent.cs:31-40`). `Partition Terrain` exposes more Rhino meshes
(`PartitionTerrainComponent.cs:59-63`). None of the components produces the data contract consumed by
Revit's Toposolid creation API:

- horizontal, planar, closed, mutually valid profile loops;
- elevation points for the top face;
- a declared coordinate mode and unit conversion;
- a mapping of named regions to Toposolid subdivisions or separate Toposolids;
- creation/update identity for downstream Revit elements.

Revit's `Toposolid.Create` accepts profile `CurveLoop`s and points rather than a Rhino mesh. Its profiles
must satisfy Revit's planarity, closure and intersection rules. `Toposolid.CreateSubDivision` likewise
expects valid horizontal profile loops. The current Rhino meshes and curves are useful source data, but
they are not directly consumable as that contract.

Impact: a downstream graph has to invent every important conversion rule. Different definitions can
produce different placement, boundaries, holes and triangulation from the same MoleHill terrain.

Recommendation: add an explicit `Prepare Toposolid` component with documented, ordinary Grasshopper
outputs: outer/hole profile trees, sampled elevation-point trees, subdivision profile trees, stable
keys/names, coordinate/unit metadata, and a validation report. Keep actual Revit type/level selection and
transactions in a separate Rhino.Inside.Revit-facing component.

References:

- [Autodesk Revit API: Toposolid.Create](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/0d4cd6ef-eadd-ace6-2999-2270c1317fb1.htm)
- [Autodesk Revit API: Toposolid.CreateSubDivision](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/6b4bcf67-0432-9549-dafb-464f8ca07eec.htm)

### H2 — Units and georeferencing are not carried across the exchange boundary

`MoleHillTerrainData` contains geometry, name, key, revision and diagnostics only
(`MoleHillTerrainData.cs:8-46`). The snapshot bridge duplicates the final document-space mesh and curves
but exports neither the Rhino document unit nor the saved MoleHill Project Base transform
(`TerrainController.Grasshopper.cs:52-70`).

This is especially important after georeferenced LandXML import. MoleHill can convert LandXML coordinates
into project-local Rhino coordinates, but the terrain goo does not say whether its coordinates are local,
project-base/georeferenced, or already converted for a Revit site. It is therefore not self-describing,
and the downstream graph cannot reliably reconstruct the intended real-world placement.

Impact: terrain can have the correct shape but the wrong scale, origin, rotation or elevation in Revit.

Recommendation: make coordinate handling explicit. At minimum export:

- Rhino unit system and metres-per-model-unit;
- coordinate mode (`Rhino local`, `MoleHill project base`, or an explicitly supplied Revit transform);
- the resolved local-to-real-world transform and enough provenance to diagnose it;
- transformed profiles and points from one well-defined boundary, rather than relying on ad-hoc GH
  transforms downstream.

### H3 — Full mesh vertices are not a safe Toposolid point strategy

The current flow preserves the complete source/partition mesh. There is no point budget, terrain-aware
decimation, error tolerance or prioritisation of boundary/breakline vertices. Large MoleHill terrain meshes
can therefore hand tens or hundreds of thousands of candidate points to a downstream definition.

Revit exposes a configurable point threshold for Toposolid creation (20,000 by default, with a documented
10,000-50,000 range) and warns that increasing it affects performance. Revit also downsamples large point
imports. Blindly forwarding every mesh vertex is therefore both a performance risk and a geometry-fidelity
risk.

Impact: slow or failed Revit creation, unpredictable downsampling, oversized models, or loss of locally
important terrain features.

Recommendation: implement constrained sampling with a configurable hard cap. Always retain boundary
corners, region-loop vertices, breakline samples and local elevation extrema; simplify the remaining
surface against a vertical-error tolerance. Report source count, output count and maximum measured error.

References:

- [Autodesk Revit: Toposolid Settings](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-Model/files/GUID-E2E75A78-B698-4518-A5AE-29BFF2BC2688.htm)
- [Autodesk Revit: Create a Toposolid from Imported Data](https://help.autodesk.com/cloudhelp/2025/ENU/Revit-Model/files/GUID-A1E1DE84-29AE-4B99-B377-091C0AC45249.htm)

### H4 — Breaklines do not map to guaranteed Toposolid edges

MoleHill carries hard/elevation constraints as curves (`TerrainController.Grasshopper.cs:58-63`), and
partitioning clips those curves to each piece. Revit's Toposolid creation signature accepts profiles and
points, not terrain breakline constraints. Merely sampling a breakline into points does not guarantee that
Revit's regenerated triangulation will retain the same edge chain or crease.

Impact: retaining-wall rails, kerbs, ridges and other hard terrain features can soften, bridge, or
triangulate across the intended constraint after conversion.

Recommendation: define the intended approximation explicitly. Sample breaklines by chord/vertical error,
retain their ordered identity in the output, and report that exact TIN edges are not guaranteed. Where
the Revit API offers a suitable post-creation shape-edit operation, apply it in the Revit execution layer
and verify the resulting surface rather than assuming mesh topology was preserved.

### M1 — `Partition Terrain` is not equivalent to Revit subdivisions

`Partition Terrain` creates separate terrain payloads and an optional remainder
(`PartitionTerrainComponent.cs:150-217`). It intentionally inserts all curves into a shared Rhino TIN so
adjacent output meshes have identical seam coordinates. Revit subdivisions, however, are profiles hosted
by one Toposolid and Revit regenerates the host surface.

The exact shared-triangle/seam guarantee is valuable for Rhino mesh work, but it does not survive as a
guarantee when each piece is separately converted into a Revit Toposolid. Separate Revit surfaces may
retriangulate differently along the same nominal edge.

Impact: users may choose the partition output believing it is the correct path to Toposolid subdivisions
or that it guarantees crack-free separate Toposolids.

Recommendation: provide two explicit preparation modes:

1. one base Toposolid plus subdivision profile trees; and
2. separate Toposolids, with a clear warning that Revit retriangulation does not preserve the shared Rhino
   mesh topology.

### M2 — Construct/deconstruct is not fully reversible despite the documentation

`docs/architecture.md:78-80` says the wrapper is reversible including stable key, revision and diagnostics.
The actual ports do not preserve all of that data:

- `Deconstruct Terrain` outputs revision and diagnostics (`DeconstructTerrainComponent.cs:39-40`), but
  `Construct Terrain` has no inputs for either and creates revision `0` with empty diagnostics
  (`ConstructTerrainComponent.cs:26-38,85`).
- Region keys are not output. Deconstruction replaces region paths with sequential `{0}`, `{1}`, ... paths
  (`DeconstructTerrainComponent.cs:54-61`), while construction derives new region keys from those paths
  (`ConstructTerrainComponent.cs:67-81`). Original region keys are lost.

Impact: reconstructing a terrain loses revision/provenance and stable zone identity. Stable identity is
particularly important for idempotent updates of downstream Revit elements or subdivisions.

Recommendation: add zone-key, revision and diagnostic ports to both sides, preserving the original path/key
mapping, or narrow the documentation to say that only geometry and display metadata round-trip.

### M3 — Toposolid-specific geometry validation is absent

`MoleHillTerrainData.IsValid` only checks Rhino mesh validity and non-empty vertex/face counts
(`MoleHillTerrainData.cs:48`). Region construction duplicates curves without requiring closed, planar,
non-self-intersecting boundaries (`MoleHillTerrainRegion.cs:8-15`). No stage validates:

- finite coordinates and a single meaningful elevation for each XY sample;
- surface suitability as a Revit top face, including near-vertical retaining-wall geometry;
- profile closure, horizontality after projection, self-intersection or mutual intersection;
- loop nesting and hole semantics after tolerance cleanup;
- points lying within the intended profiles.

Impact: failures are deferred to Revit, where diagnostics are less connected to the source branch and can
be difficult to repair deterministically.

Recommendation: make Toposolid validation a first-class preparation result. Reject incompatible branches
before the Revit transaction and attach branch/key-specific errors. Do not use the generic
`MoleHillTerrainData.IsValid` check as evidence that a terrain is Toposolid-compatible.

### M4 — Invalid partition outlines can produce a partial, misleading result

`Partition Terrain` skips each invalid curve independently, adds text to its `Report`, and continues with
the other curves (`PartitionTerrainComponent.cs:107-127`). It does not raise a Grasshopper runtime warning
for those skipped profiles. If an outer loop is valid but its intended hole is invalid, the output can fill
the hole and still look like a successful partition.

Impact: a Toposolid or subdivision can cover an area the user explicitly intended to exclude.

Recommendation: for preparation/export, validate each branch atomically. If any loop in a region is
invalid, fail that region and emit a visible runtime error or warning. Partial processing can remain an
opt-in mode for general mesh partition work.

### M5 — Live solves create substantial native-geometry copy pressure

Geometry ownership is safe in intent but expensive in practice:

- the Rhino bridge duplicates mesh and curves (`TerrainController.Grasshopper.cs:52-69`);
- the reflection reader duplicates curves (`MoleHillTerrainSnapshotComponent.cs:183-208`);
- `MoleHillTerrainData` duplicates mesh, curves and regions again (`MoleHillTerrainData.cs:17-25`);
- every downstream generic terrain read duplicates the entire payload again
  (`TerrainDataAccess.cs:16-31`);
- deconstruction duplicates output geometry once more (`DeconstructTerrainComponent.cs:52-66`).

Several intermediate Rhino geometry objects are left to finalization rather than deterministic disposal.
With live snapshot invalidation (`MoleHillTerrainSnapshotComponent.cs:165-168`), large terrain graphs can
generate avoidable native-memory and GC pressure on every MoleHill state change.

Impact: UI pauses, transient memory spikes and poor scalability on exactly the large sites that need point
reduction before Revit.

Recommendation: define one ownership boundary and avoid full payload duplication at every component hop.
Dispose temporary reflection DTO geometry deterministically where possible, and benchmark repeated snapshot
updates with a large terrain.

### L1 — Reflection lookup is brittle if the bridge API evolves

`MoleHill Terrain Snapshot` requests `GetSnapshot` by name only
(`MoleHillTerrainSnapshotComponent.cs:83-85`). An overload can make this lookup ambiguous. The invocation
handler catches `TargetInvocationException` only (`MoleHillTerrainSnapshotComponent.cs:94-103`), not lookup,
argument, access or conversion failures.

Recommendation: resolve the exact parameter signature and convert all reflection failures into a concise
Grasshopper runtime error.

### L2 — Revision loses 64-bit identity at Grasshopper ports

Revision is a `long` in the payload but both Snapshot and Deconstruct expose a Grasshopper integer and clamp
outside the 32-bit range (`MoleHillTerrainSnapshotComponent.cs:224-227`,
`DeconstructTerrainComponent.cs:74-76`). This can make distinct future revisions appear identical.

Recommendation: expose revision as text or a 64-bit-safe generic value. If the integer output must remain
for compatibility, add a lossless output beside it.

### L3 — The custom terrain goo has no explicit persistence contract

`MoleHillTerrainGoo` implements duplication and casting but no explicit `Write`/`Read` serialization. This
is acceptable for transient computed wires, but internalised/persistent terrain values should not be
assumed to survive saving and reopening a Grasshopper definition.

Recommendation: either implement versioned serialization or explicitly document the goo as transient and
prevent workflows that imply persistence.

### L4 — Tests cover registration and geometry helpers, not the end-to-end preparation contract

Current coverage verifies port registration, input duplication, odd/even partition ownership and breakline
clipping. There is no solved-component test for:

- Snapshot -> Deconstruct -> Construct round-trip identity;
- unit/project-base conversion;
- profile validation and hole nesting;
- point-budget simplification and vertical-error bounds;
- repeated snapshot memory behaviour;
- creation/update/delete behaviour in Revit.

Recommendation: add pure Core tests for preparation math and a small Rhino.Inside.Revit integration fixture
for API creation and idempotent updates. Keep the Revit-dependent layer thin.

## Positive findings

- Snapshot now rejects preview and deferred display states, so downstream graphs cannot mistake unfinished
  terrain for a completed result.
- The reflection bridge keeps the GHA independent from the optional Rhino plugin assembly.
- Terrain and region snapshots have stable source GUID keys before deconstruct/reconstruct loses region keys.
- Partitioning inserts all zone boundaries once and derives outputs from one split result.
- Odd/even nested holes and clipped per-piece breaklines now have targeted tests.
- The current solution builds cleanly and all available managed tests pass; native-runtime-gated tests skip
  cleanly when Rhino is unavailable.

## Recommended implementation order

1. Define `ToposolidPreparationData`: profiles, points, subdivisions, units, stable keys, fingerprint and
   diagnostics.
2. Implement strict profile and 2.5D compatibility validation.
3. Add constrained point reduction with a configurable budget and measured error.
4. Keep coordinate transforms upstream and visible; carry document-unit metadata for exactly one conversion
   in the adapter.
5. Add a Grasshopper `Prepare Toposolid` component producing ordinary points/curves plus the typed package.
6. Add separate optional Rhino.Inside.Revit Python adapters with type/level inputs and idempotent element
   update semantics, outside the packaged GHA.
7. Correct the construct/deconstruct metadata round trip and reduce duplicate native geometry ownership.
8. Add contract and Revit integration tests before labelling the flow Toposolid-ready.

## Acceptance criteria for “Toposolid-ready”

- Upstream local/shared-coordinate transforms remain unchanged through preparation and land at the same
  documented Revit coordinate and elevation after adapter unit conversion.
- Rhino units are converted to Revit internal units exactly once.
- Every emitted profile is horizontal, closed and valid under Revit's profile rules.
- Nested holes and overlapping named regions resolve deterministically.
- Output stays within a declared point budget while reporting maximum vertical deviation.
- Boundary and breakline-critical points survive simplification, with any loss of exact breakline topology
  explicitly reported.
- Re-solving updates the same Revit Toposolid/subdivision elements without creating duplicates.
- Invalid branches fail before mutation and identify the responsible terrain/region key.
