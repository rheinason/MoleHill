# MoleHill

MoleHill is a Rhino 8 terrain modeling toolkit centered on a document-backed Rhino plugin, with optional Grasshopper components for procedural workflows.

It uses [Triangle.NET](https://github.com/wo80/Triangle.NET) for constrained Delaunay triangulation and targets .NET 7-based Rhino 8 plugins.

**Status: 1.2.1.** Install it from Rhino's Package Manager; see [CHANGELOG.md](CHANGELOG.md) for what changed.
Bug reports with a repro case are very welcome: see [CONTRIBUTING.md](CONTRIBUTING.md). MoleHill is free,
and because its triangulator derives from Triangle it may not be sold or bundled in a commercial product;
see [License](#license).

## User Guide

New to MoleHill? The [User Guide](docs/user-guide/README.md) covers the product overview, a typical
workflow, the panel, every command, and a page for each modifier, object, zone, analysis and annotation card.

## Release Scope

- `MoleHill.Rhino.rhp` is the primary release artifact.
- `MoleHill.gha` is an optional companion plugin for Grasshopper users who want the same terrain operations in a graph workflow.

## Rhino Plugin

The Rhino plugin provides the panel-driven workflow for creating, editing, analyzing, and converting managed terrain definitions inside a Rhino document.

### Commands

| Command | Description |
|---------|-------------|
| `mhPanel` | Open the MoleHill panel |
| `mhCreateTerrain` | Create a terrain from the current selection and open the panel |
| `mhConvertToRhino` | Convert the selected managed terrain into standard Rhino objects |
| `mhValidateTerrainInputs` | Clean selected terrain points and curves, including duplicate joined segments |
| `mhSplitAtIntersections` | Split selected curves at their pairwise intersections |
| `mhDrapeCurve` | Sample selected curves onto a selected mesh or surface along World Z |
| `mhInspectCurve` | Open the read-only plan-station profile inspector: an elevation profile coloured by grade, elevation, cut/fill or plan radius, with checks (grade, radius, vertical break, terrain coverage), measurements and an event list; the same colour ramp drives a live viewport overlay, and hovering the profile scrubs a marker along the curve. `Label` drops elevation / grade / station / cut-fill text dots along the curve |
| `mhSlopeCurveSection` | Set or interpolate grade over a selected curve section, including blending to active terrain. Previewed live; `Anchor` picks which end holds its elevation, and `Transition` eases the moved end back into the rest of the curve instead of stepping. Reports how far the result strays from the prescribed grade when control-point editing cannot express it exactly |
| `mhCreateWall` | Draw a wall rail and generate its parallel, vertically offset companion rail |

## Grasshopper Components

### Terrain exchange
| Component | Description |
|-----------|-------------|
| **MoleHill Terrain Snapshot** | Read the latest completed final terrain, breaklines, and zones from the MoleHill Rhino panel |
| **Construct Terrain** | Package an ordinary mesh, breaklines, and a zone tree as open MoleHill Terrain data |
| **Deconstruct Terrain** | Expose MoleHill Terrain as ordinary Grasshopper mesh, curves, zone branches, and metadata |
| **Partition Terrain** | Insert zone boundaries once and output separate terrain meshes with exact shared seam vertices |
| **Prepare Toposolid** | Validate profiles and emit a point-budgeted, error-measured, fingerprinted Revit-neutral terrain package |

`MoleHill Terrain` is deliberately not a closed editing system. Deconstruct it, edit or join the
ordinary Rhino geometry with standard Grasshopper tools, then construct it again. Zone branches are
region data only; `Partition Terrain` is the explicit operation that turns them into separate terrain
pieces. Terrain Snapshot requires the MoleHill Rhino plugin from the combined package and rejects
preview/deferred builds so downstream geometry never silently changes from approximate to final.
`Prepare Toposolid` leaves coordinates in the incoming Rhino model space, retains explicit unit metadata,
and performs adaptive point reduction in compiled Core code. Inside Rhino.Inside.Revit
(Revit 2025+), a MoleHill > Revit tab adds **Write Toposolids** (create/update Toposolids and their
subdivisions from the Prepare Toposolid package, matched by stable key) and **Inspect Toposolids**. They
ship in the same package as `MoleHill.Revit.gha` and stay hidden in plain Rhino; Revit's API is never
copied into the package. The Revit step is not yet verified in a Revit host.

### Surface
| Component | Description |
|-----------|-------------|
| **TIN Surface** | Generate a TIN mesh from points and breaklines |
| **Remesh** | Refine a TIN mesh with quality constraints and max edge length |
| **Mesh Smooth** | Apply Laplacian Z-smoothing within boundary curves |

### Grading
| Component | Description |
|-----------|-------------|
| **Grade Pad** | Flatten terrain at pad elevations with slope transitions and cut/fill reporting |
| **Grade Path** | Grade roads and paths from centerline elevations, with constant fallback width or automatically matched variable-width edge curves |
| **Retaining Wall** | Pair open wall rails, generate wall solids, and grade terrain between toe and top rails |
| **In Situ Stair** | Generate stair geometry and terrain transitions from stair references |

### Analysis
| Component | Description |
|-----------|-------------|
| **Slope Analysis** | Apply per-face slope coloring with a configurable legend range |
| **Waterflow from Points** | Trace terrain-conforming downhill paths from point sources to boundaries or local sinks |
| **Terrain Sections** | Overlay multiple terrain profiles and shade proposed-versus-reference cut/fill regions |
| **Mesh Areas** | Split a mesh by closed boundary curves |
| **Mesh Collage** | Combine meshes in 2D planning mode or as 3D colored terrain |

## Install

### Rhino Package Manager

1. In Rhino 8, run `_PackageManager`.
2. Search for `MoleHill`.
3. Install the package to get the Rhino plugin and the optional Grasshopper companion together.

After installing, run `mhPanel` to open the MoleHill panel, or `mhCreateTerrain` to start a terrain
from the current selection.

### From source

Build it yourself (see [Build From Source](#build-from-source)) and load
`src/MoleHill.Rhino/bin/Release/net7.0/MoleHill.Rhino.rhp` in Rhino 8 with `_PlugInManager`. Copy
`MoleHill.gha` to `%AppData%\Grasshopper\Libraries\` for the Grasshopper components. In Windows file
properties, unblock the files if Rhino refuses to load them.

## Build From Source

Requires Windows, Rhino 8, and the .NET 8 SDK or newer. See [CONTRIBUTING.md](CONTRIBUTING.md) for
prerequisites, validation and conventions.

```bash
dotnet build MoleHill.sln
dotnet build src/MoleHill.Rhino/MoleHill.Rhino.csproj -c Release
dotnet build src/MoleHill.Grasshopper/MoleHill.Grasshopper.csproj -c Release -f net7.0-windows
pwsh ./build-yak-package.ps1
```

Release outputs:

- `src/MoleHill.Rhino/bin/Release/net7.0/MoleHill.Rhino.rhp`
- `src/MoleHill.Grasshopper/bin/Release/net7.0-windows/MoleHill.gha`
- `.artifacts/yak/MoleHill-<version>/molehill-<version>-rh8_9-win.yak`

`build-yak-package.ps1` stages a Rhino 8 Windows Yak package that includes both the Rhino plugin and the optional Grasshopper companion under `net7.0/`.

## Test

```bash
dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj
dotnet test tests/MoleHill.Grasshopper.Tests/MoleHill.Grasshopper.Tests.csproj
```

## License

This repository is mixed-license.

- MoleHill-authored code outside `src/TriangleNet/` is licensed under `GPL-3.0-only`, with an
  additional permission to combine it with the TriangleNet subtree.
- `src/TriangleNet/` is excluded and retains its upstream notices and terms. It derives from Jonathan
  Richard Shewchuk's Triangle, which may be freely redistributed without charge but **may not be sold or
  included in a commercial product without his license**. Using MoleHill in your own work, professional
  work included, is unaffected; selling MoleHill or shipping it inside a paid product is not allowed
  without that license.
- `Clipper2` (Boost 1.0) and `BitMiracle.LibTiff.NET` (BSD-style), shipped with the plug-ins, retain
  their own terms and notices.
- The MoleHill code is intended to be reciprocal: if you distribute modified versions, you need to provide the corresponding source under GPL terms.

See `LICENSE` and the files under `LICENSES/` for the details that apply to each part of the repository.
