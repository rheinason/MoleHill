# Rhino.Inside.Revit Toposolid adapters

These optional Python 3 components consume the ordinary Grasshopper outputs from MoleHill's compiled
`Prepare Toposolid` component. They are examples, not part of `MoleHill.gha`, and therefore do not add a
Rhino.Inside.Revit or Revit API dependency to the MoleHill package.

## Recommended flow

```text
MoleHill Terrain Snapshot / Construct Terrain
  -> Partition Terrain                         (one independent Revit surface per branch)
  -> optional standard Grasshopper transforms
  -> Prepare Toposolid                         (compiled validation, sampling, error analysis)
  -> CreateOrUpdateToposolids.py               (small Revit transaction only)
```

Coordinates remain unchanged in `Prepare Toposolid`. Apply project/shared-coordinate transforms before
it. The Python writer converts the component's declared `Meters Per Unit` into Revit internal feet exactly
once.

## Components

### CreateOrUpdateToposolids.py

Create a Python 3 Script component in a Rhino.Inside.Revit Grasshopper definition and paste in the script.
Use these inputs:

- `Run` — item, Boolean.
- `Profiles` — tree, Curve; connect `Prepare Toposolid -> Profiles`.
- `Points` — tree, Point; connect `Elevation Points`.
- `Keys` — list, Text; connect `Terrain Key`.
- `Fingerprints` — list, Text; connect `Geometry Fingerprint`.
- `MetersPerUnit` — list or item, Number; connect `Meters Per Unit`.
- `ToposolidType` — item, Revit ToposolidType or ElementId.
- `Level` — item, Revit Level or ElementId.
- `PreserveParameters` — optional list of writable instance-parameter names to copy when replacement is
  required.

Outputs are `Elements` and `Report`. The writer stores the MoleHill key and fingerprint in Revit Extensible
Storage. An unchanged fingerprint is a no-op; changed geometry is created first and then replaces the old
element in one transaction group.

### InspectToposolid.py

Inputs: `Toposolids` (list), `MetersPerUnit` (optional number, defaults to metres). Outputs: `Keys`,
`Fingerprints`, `TypeIds`, `LevelIds`, `Profiles`, `ShapePoints`, and `Report`.

### CreateSubdivisions.py

Subdivisions are optional. Inputs: `Run`, `Hosts` (list), `Profiles` (tree), `Keys` (list),
`Fingerprints` (list), `MetersPerUnit` (list or item), and optional `SubdivisionType`. Each profile branch
becomes one subdivision on the matching host. The same stable-key/fingerprint no-op and replace rules are
used.

## Important behaviour

- The default workflow is one independent Toposolid per `Partition Terrain` branch. A subdivision follows
  its host and is not an independently editable terrain surface.
- Revit regenerates its own triangulation. MoleHill preserves critical breakline samples, but the writer
  cannot promise exact Revit TIN edges along those breaklines.
- `Prepare Toposolid` enforces the point budget and measures source-vertex error in compiled code. Do not
  move partitioning, simplification, triangulation, or error analysis into these scripts.
- These adapters require Revit 2024 or later and must run inside a Rhino.Inside.Revit Grasshopper session.
  They cannot be executed or transaction-tested in an ordinary Rhino session.

References:

- [Autodesk Revit API: Toposolid.Create](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/7516b682-d489-a462-47ab-192c63d1d9e4.htm)
- [Autodesk Revit subdivisions](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-Model/files/GUID-BA7C38A7-7E8B-45CC-B4A3-950D19B48C66.htm)
- [Rhino.Inside.Revit Grasshopper components](https://www.rhino3d.com/inside/revit/1.0/reference/gh-components)
