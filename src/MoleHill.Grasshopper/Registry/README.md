# MoleHill.Grasshopper/Registry

Spec-driven Grasshopper components: a component is declared as data + a Core-call body, and a single
generic base turns it into a working `GH_Component`. This dedupes the param-registration boilerplate and
the mesh/curve conversion plumbing the hand-written components each used to copy.

## Pieces
- `GhComponentSpec.cs` — `GhPort` (declarative input/output port: type, access, optional, default) +
  `GhComponentSpec` (catalog metadata, `Inputs`/`Outputs`, and a `Solve(GhSolveContext)` body).
  Port types: Mesh, Curve, Number, Integer, Boolean, Brep, Line, Text, Geometry.
- `GhSolveContext.cs` — per-solve `IGH_DataAccess` wrapper: typed getters/setters
  (`TryGetMesh`/`GetCurves`/`GetNumber(s)`/`GetInt(s)`/`GetGeometry`/`SetData`/`SetDataList`), messages
  (`Warn`/`Error`/`Remark`), and shared geometry plumbing (`ToFlatVertices`, `TryToFlatFaces`,
  `BuildMesh`, repeat-last `ListValue`). It also exposes the shared `ModelUnitContext`, model tolerance,
  and metre-to-document conversion used by physical defaults.
- `RegistryTerrainComponent.cs` — the generic base. `RegisterInputParams`/`RegisterOutputParams` walk the
  spec's ports; `SolveInstance` rejects `None`/`Unset` document units before calling `spec.Solve`.

## Adding / migrating a component
A component is a thin subclass: a `static readonly GhComponentSpec` built in `BuildSpec()`, the
`ComponentGuid` (**keep it stable** — existing `.gh` files reference it), the icon, and the `Solve` body.

**GH gotcha:** the spec must be exposed via `protected override GhComponentSpec Spec => ComponentSpec;`
(virtual property backed by a `static` field), **not** an instance field. `GH_Component`'s base
constructor runs `PostConstructor → RegisterInputParams` before the derived constructor body, so a field
assigned after `base(...)` is still null at registration time.

## Escape hatch
Components that don't fit (per-instance state, exotic Colour/Hatch I/O, custom goo, or tree-shaped
ports) stay hand-written `GH_Component`s. This includes `TinFromPointsAndBreaklines` (instance caching),
`MeshCollageComponent` (Colour + `GH_Hatch`, dual-mode), and the terrain exchange components (custom
`MoleHillTerrainGoo` plus zone trees and live Rhino-host invalidation). That's expected; don't force
them through the spec.
