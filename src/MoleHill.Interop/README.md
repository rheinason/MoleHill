# MoleHill.Interop

This net7.0, RhinoCommon-only assembly is the typed bridge between the Rhino plugin and the
Grasshopper plugin. Both host projects reference it, and Yak ships one `MoleHill.Interop.dll` beside
the `.rhp` and `.gha`. Grasshopper locates `TerrainGrasshopperBridge.Instance` in the loaded Rhino
assembly and checks `ContractVersion` before reading snapshots through `ITerrainSnapshotBridge`.

`IToposolidPreparation` / `IToposolidSubdivision` are the second contract: Grasshopper's
`ToposolidPreparationData` implements them, and `MoleHill.Revit.gha` reads the `Preparation` output
through them. A type defined in either `.gha` would be a different type in the other.

The snapshot contract currently carries completed mesh geometry, constraints, resolved region outlines and
zone display/priority metadata,
diagnostics, units, coordinate metadata, source status, terrain picker references, persistent Rhino
document identity, and a content fingerprint. The bridge contract is version 4. The wider B7
contract for independently typed constraint kinds and raw zone semantics is still planned.
