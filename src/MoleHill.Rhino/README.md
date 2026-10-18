# MoleHill.Rhino

The native Rhino plugin (`.rhp`): the dockable panel, the commands, the terrain definition model, and
the controller/build-service pipeline that turns a terrain definition into geometry in a document.

All Rhino API calls belong here (or in `MoleHill.Grasshopper`); reusable computation belongs in
`MoleHill.Core`. The rule is load-bearing rather than stylistic — Core is what the test suite can
exercise without a host.

Standalone Python experiments live in [`examples/Rhino/`](../../examples/Rhino/README.md),
including moving a curve control point's elevation relative to a reference point and slope.
They run independently of the compiled plugin.

`mhSlopeControlPoints` is the compiled control-point workflow: turn PointsOn and select control points,
or Ctrl+Shift-select polyline/polycurve segments, before or after starting the command. Pick a reference,
then move vertically to preview/pick the
slope or set `Slope` / `Units` and press Enter. It supports multiple grips on NURBS curves, polylines
and lines, or multiple sections, shows the edited curves and signed slopes live, and changes only
elevations. A selected section moves all its control points; neighbouring
endpoints follow shared vertices so the curve stays connected. A selected arc section becomes NURBS;
an adjoining section that cannot move its endpoint must be converted to NURBS first. Esc cancels;
Undo restores the edit. Closed/periodic seam points remain coupled. The slope is between control
point locations, not a tangent slope along the curve.

`mhMergeControlPoints` accepts preselected curve grips or individual picks in order, including
grips on separate curves. Choose `Mode` (`AtCenter`, `ByDistance`, `First`, `Last`), adjust `Distance`
in model units for ByDistance, inspect the green curve/movement preview, and press Enter to apply.
Esc cancels. ByDistance clusters selected points transitively in full 3D; First/Last use individual
pick order or Rhino's returned preselection order (which is not historical click order). This merges
locations without removing control points or joining curves. Invalid collapses cannot be accepted.
Supports NURBS curves, polylines and lines; convert other curve types to NURBS first.

## Folders

`mhScanToTerrain` (Phase 1) takes a Z-up photogrammetry mesh and infers ground beneath raised objects,
then outputs editable borders, contours and spot points on separate layers under a fresh `Scan {source}`
root. Enter starts with command-line settings; Esc cancels ground extraction or stops contour recovery
and keeps its partial result. `AddGroundMesh=Yes` adds the internal ground mesh for checking. Defaults
are physical lengths converted to model units. The reported tolerance is against the inferred ground;
the command reports when its contour budget is exhausted. Walls, road edges, buildings, point clouds
and the floating preview/Keep panel remain later phases. See the [plan](../../docs/scan-to-terrain-plan-2026-10-18.md).
On the reference scan, the default 300-contour budget leaves about 0.51 m residual. A Core run with
`Tolerance=0.25`, `PlanTolerance=0.1`, `MaxContours=1000` and `RidgesValleys=No` met 0.25 m against
inferred ground using 665 contours and 633 spots; use the larger budget when accuracy takes priority.

| Folder | What lives there |
|---|---|
| [`Model/`](Model/README.md) | The persisted terrain definition: modifiers, zones, markers, objects, analyses, annotations |
| [`Registry/`](Registry/README.md) | Type descriptors and the parameter schema that drives every card |
| [`Services/`](Services/README.md) | `TerrainController` / `TerrainBuildService`, the runtime cache, output and layer routing |
| [`UI/`](UI/README.md) | The Eto.Forms panel, split across partials, and its reusable card/editor primitives |
| `Commands/` | `MoleHillPanel`, `MoleHillCreateTerrain`, `MoleHillConvertToRhino` and the command services |
| `Toolbars/` | The `.rui` toolbar file; its button bitmaps are drawn by `generate-toolbar-icons.ps1` |
| `Resources/` | 16x16 panel tab and modifier badge icons, loaded via `PanelIcons.Load()` |

## Before changing the build pipeline

- [docs/build-result-ownership.md](../../docs/build-result-ownership.md) — who owns which geometry, and
  which disposal paths currently have no endpoint. A worker cache borrows most of what it holds.
- [docs/architecture.md](../../docs/architecture.md) — output layer roles, preview vs bake, analysis vs
  annotation, document schema compatibility.
- [docs/rhino-live-testing.md](../../docs/rhino-live-testing.md) — how to test the panel for real. Close
  Rhino before rebuilding: it locks the `.rhp`.

State is persisted as JSON in the `.3dm` via WriteDocument/ReadDocument. A document written by a newer
build is refused rather than silently rewritten — see the schema-compatibility section of the
architecture doc.
