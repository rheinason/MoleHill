# MoleHill

MoleHill is a Rhino 8 terrain modeling toolkit centered on a document-backed Rhino plugin, with optional Grasshopper components for procedural workflows.

It uses [Triangle.NET](https://github.com/wo80/Triangle.NET) for constrained Delaunay triangulation and targets .NET 7-based Rhino 8 plugins.

## Release Scope

- `MoleHill.Rhino.rhp` is the primary release artifact.
- `MoleHill.gha` is an optional companion plugin for Grasshopper users who want the same terrain operations in a graph workflow.

## Rhino Plugin

The Rhino plugin provides the panel-driven workflow for creating, editing, analyzing, and converting managed terrain definitions inside a Rhino document.

### Commands

| Command | Description |
|---------|-------------|
| `MoleHillPanel` | Open the MoleHill panel |
| `MoleHillCreateTerrain` | Create a terrain from the current selection and open the panel |
| `MoleHillConvertToRhino` | Convert the selected managed terrain into standard Rhino objects |
| `mhValidateTerrainInputs` | Clean selected terrain points and curves, including duplicate joined segments |
| `mhSplitAtIntersections` | Split selected curves at their pairwise intersections |
| `mhDrapeCurve` | Sample selected curves onto a selected mesh or surface along World Z |
| `mhInspectCurve` | Open the read-only plan-station profile inspector: an elevation profile coloured by grade, elevation, cut/fill or plan radius, with checks (grade, radius, vertical break, terrain coverage), measurements and an event list; the same colour ramp drives a live viewport overlay, and hovering the profile scrubs a marker along the curve. `Label` drops elevation / grade / station / cut-fill text dots along the curve |
| `mhSlopeCurveSection` | Set or interpolate grade over a selected curve section, including blending to active terrain |
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
and performs adaptive point reduction in compiled Core code. Optional Rhino.Inside.Revit Python 3
create/update, inspect, and subdivision adapters live under `examples/RhinoInside.Revit/`; Revit assemblies
are not dependencies of the MoleHill package.

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
4. Enable prerelease packages when testing Yak prerelease builds.

### Rhino Plugin

1. Download `MoleHill.Rhino.rhp` from the [latest release](https://github.com/rheinason/MoleHill/releases/latest).
2. Install or load the plugin in Rhino 8.
3. Run `MoleHillPanel` or `MoleHillCreateTerrain` to start the workflow.

### Optional Grasshopper Plugin

1. Download `MoleHill.gha` from the same release.
2. Copy it to:
   ```
   %AppData%\Grasshopper\Libraries\
   ```
3. In Windows file properties, unblock the file if needed.
4. Restart Rhino and Grasshopper.

## Build From Source

Requires .NET 8 SDK or newer and access to Rhino and Grasshopper NuGet packages.

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

- MoleHill-authored code outside `src/TriangleNet/` is licensed under `GPL-3.0-only`.
- `src/TriangleNet/` is excluded and retains its upstream notices and original terms.
- `BitMiracle.LibTiff.NET`, shipped with the Rhino plugin for numeric DEM decoding, retains its
  BSD-style terms and upstream notices.
- The MoleHill code is intended to be reciprocal: if you distribute modified versions, you need to provide the corresponding source under GPL terms.

See `LICENSE` and the files under `LICENSES/` for the details that apply to each part of the repository.
