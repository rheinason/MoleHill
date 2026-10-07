# MoleHill.Revit

Grasshopper components for Rhino.Inside.Revit, built as a second assembly, `MoleHill.Revit.gha`, and
shipped in the same Yak package as `MoleHill.gha`. They replace the three Python 3 adapters that used to
live in `examples/RhinoInside.Revit/`. Those needed every input created, typed and wired by hand.

## Why a separate assembly

It is the only MoleHill assembly that references `RevitAPI`. `MoleHillRevitPriority` aborts its load in
any Grasshopper session where `RevitAPI` is not already loaded in the process. In plain Rhino, the
components never reach the ribbon. Set `MOLEHILL_LOAD_REVIT_COMPONENTS=1` to load them anyway, for
checking registration in a plain Rhino; solving them there reports that Revit is unavailable.

- **Build reference only.** `Nice3point.Revit.Api.RevitAPI` 2025 provides reference assemblies.
  Nothing of Autodesk's is copied or shipped, and Revit supplies the real API at runtime.
- **No Rhino.Inside.Revit reference.** Element inputs are unwrapped through `IGH_Goo.ScriptVariable()`,
  the same route Rhino.Inside.Revit's own Python components use. The active document is read by
  reflection from `RhinoInside.Revit.Revit.ActiveDBDocument`.
- **Revit 2025 or later.** The assembly targets `net8.0-windows` because Revit 2025's API is .NET 8.
  Rhino.Inside on Revit 2024 and earlier runs on .NET Framework, where no MoleHill assembly loads. A
  plain Rhino forced onto .NET 7 cannot load this file and reports it as a loading error. That does not
  affect `MoleHill.gha`.
- **Shared contract.** The preparation package crosses from `MoleHill.gha` to here through
  `MoleHill.Interop.IToposolidPreparation`. A type in either `.gha` would be invisible to the other,
  because each is a separate assembly.
- **Component signatures carry no Revit type.** Revit calls sit behind `RevitHost/RevitSession`, so a
  missing Revit surfaces as a component error instead of a type-load failure.

## Workflow

```text
MoleHill Terrain Snapshot / Construct Terrain
  -> Partition Terrain                (optional; one Toposolid per branch)
  -> standard Grasshopper transforms  (project/shared coordinates)
  -> Prepare Toposolid                (validation, point budget, measured error; compiled in MoleHill.gha)
  -> Write Toposolids                 (one wire: the Preparation output)
```

### Write Toposolids

Inputs:

- `Run`: while off, the packages are only validated.
- `Preparation`: a list of packages; one Toposolid per package.
- `Toposolid Type` and `Level`: needed only when a key is new.
- `Subdivisions`: on by default.
- `Subdivision Type`: optional.
- `Preserve Parameters`: instance parameters copied onto a replacement.

Outputs: `Toposolids`, `Subdivisions` (one branch per Toposolid) and `Report`.

- **Identity.** Each element stores the package's terrain key and geometry fingerprint in Extensible
  Storage, under the schema GUID the Python adapter used. Toposolids it wrote are therefore still
  matched.
- **Unchanged** fingerprint: no-op.
- **Changed** fingerprint: the replacement is created first, with its predecessor's type and level and
  any preserved parameters. The old element is deleted last, and all of this happens in one
  transaction group, so it is a single undo step.
- **Subdivisions** are matched by `hostKey::zoneKey`, so two partitions of one terrain can share zone
  keys. A replaced host gets new subdivisions, and the old ones go with the old host.
  If the predecessor carries a subdivision the run does not recreate (a removed zone, one drawn by hand, or
  any when `Subdivisions` is off), the write is refused before anything changes and the report names it.
- **Inputs are checked against the target document.** A type or level from another Revit project, or one
  of the wrong kind, is an error before any transaction opens.
- **Absence never deletes.** A key missing from the run leaves its element alone. A subdivision whose
  zone was removed is reported, not deleted (unless its host is being replaced; see above).
- **Units.** The package's `Meters Per Unit` converts to Revit's internal feet exactly once. Prepare
  Toposolid never moves coordinates, so apply placement transforms before it.

### Inspect Toposolids

Inputs: `Toposolids`, and optional `Meters Per Unit`; empty uses the Rhino document's units.

Outputs:

- `Keys` and `Fingerprints`
- `Is Subdivision`
- `Type Ids`, `Level Ids` and `Host Ids`
- `Profiles` (sketch edges, as lines) and `Shape Points`, one branch per input
- `Report`

## Limits

- **Unverified in Revit.** No Revit host is available to this project, and `rhino-mcp` cannot drive one.
  Only the Revit-free planner (`Planning/`) is tested: validation, unit conversion and the
  keep/create/replace decisions, in `MoleHill.Grasshopper.Tests`. Everything in `RevitHost/` compiles
  against the 2025 API but has not run. Reports from real Revit hosts are welcome; record the host
  versions with them.
- **No exact Revit TIN edges.** Revit re-triangulates the points, so MoleHill breaklines are sampled
  densely but are not guaranteed as Revit TIN edges.
- **Typed subdivision overload.** Revit 2025 has no typed `CreateSubDivision`, so the type is applied
  afterwards with `ChangeTypeId`.
- **Old Python subdivisions.** Subdivisions written by the old Python subdivision adapter are keyed
  differently and will not be matched. Delete them once, and the component will recreate them.
