# MoleHill

A Grasshopper plugin for terrain modeling — TIN surfaces, grading, slope analysis, and more.

Built for **Rhino 8** (.NET 7+). Uses [Triangle.NET](https://github.com/wo80/Triangle.NET) for constrained Delaunay triangulation.

## Components

### Surface
| Component | Description |
|-----------|-------------|
| **TIN Surface** | Generate a TIN mesh from points and breaklines (constrained Delaunay) |
| **Remesh** | Refine a TIN mesh with quality constraints and max edge length |
| **Mesh Smooth** | Laplacian Z-smoothing within boundary curves |

### Grading
| Component | Description |
|-----------|-------------|
| **Grade Pad** | Flatten terrain at pad elevations with slope transitions and cut/fill reporting |
| **Grade Path** | Grade roads/paths with width, cross-slope, and slope transitions |

### Analysis
| Component | Description |
|-----------|-------------|
| **Slope Analysis** | Per-face slope coloring with configurable legend range |
| **Mesh Areas** | Split a mesh by closed boundary curves |
| **Mesh Collage** | Combine meshes — 2D planning mode or 3D colored terrain |

## Install

1. Download `MoleHill.gha` from the [latest release](https://github.com/rheinason/MoleHill/releases/latest)
2. Copy to your Grasshopper Libraries folder:
   ```
   %AppData%\Grasshopper\Libraries\
   ```
3. Right-click the file → Properties → **Unblock** (Windows security)
4. Restart Rhino

## Build from source

Requires .NET 8 SDK.

```
dotnet build MoleHill.sln
```

The output `MoleHill.gha` is in `src/MoleHill.Grasshopper/bin/Debug/net7.0/`. ILRepack merges all assemblies into the single `.gha` file.

## License

MIT
