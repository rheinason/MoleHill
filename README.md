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

## Grasshopper Components

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
| **Grade Path** | Grade roads and paths with width, cross-slope, and slope transitions |
| **Retaining Wall** | Pair open wall rails, generate wall solids, and grade terrain between toe and top rails |
| **In Situ Stair** | Generate stair geometry and terrain transitions from stair references |

### Analysis
| Component | Description |
|-----------|-------------|
| **Slope Analysis** | Apply per-face slope coloring with a configurable legend range |
| **Mesh Areas** | Split a mesh by closed boundary curves |
| **Mesh Collage** | Combine meshes in 2D planning mode or as 3D colored terrain |

## Install

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
```

Release outputs:

- `src/MoleHill.Rhino/bin/Release/net7.0/MoleHill.Rhino.rhp`
- `src/MoleHill.Grasshopper/bin/Release/net7.0-windows/MoleHill.gha`

When Yak is installed, the Grasshopper build can also emit a `.yak` package from the release output directory.

## Test

```bash
dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj
dotnet test tests/MoleHill.Grasshopper.Tests/MoleHill.Grasshopper.Tests.csproj
```

## License

MIT
