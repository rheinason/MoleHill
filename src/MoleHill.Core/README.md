# MoleHill.Core

Pure terrain logic: triangulation, grading, analysis, scattering, sculpting, remeshing. **No Rhino or
Grasshopper dependency** — anything here is testable without a host, and everything reusable belongs
here rather than in a host project.

Two things about this project are not obvious from its file list:

- It **compiles the vendored TriangleNet sources directly** (`Compile Include="..\TriangleNet\**"`),
  rather than referencing `TriangleNet.csproj`. That is why ILRepack merges only `MoleHill.Core.dll`
  into the `.gha`, and why the vendored files' nullability warnings are exempted in `.editorconfig`
  rather than by a project-wide `NoWarn`. See [docs/validation-lanes.md](../../docs/validation-lanes.md).
- All pipeline data is **flat arrays** (`[x0,y0,z0,x1,…]`, `[i0,i1,i2,…]`), for cache friendliness.
  The layouts are tabulated in [docs/architecture.md](../../docs/architecture.md).

## Sub-namespaces

| Folder | What lives there |
|---|---|
| [`Engine/`](Engine/README.md) | Triangulation, the TIN cache, remeshing, the shared spatial index and cancellation probe |
| [`Processing/`](Processing/README.md) | Input cleaning, simplification, constraint resolution |
| [`Grading/`](Grading/README.md) | Pads, paths, walls, zone/area splitting — the watertight 2.5D invariant |
| [`Analysis/`](Analysis/README.md) | Slope, aspect, elevation, cut/fill, contours, waterflow, drainage |
| [`IO/`](IO/README.md) | Host-free file readers: classic TIFF / GeoTIFF tags and raster georeferencing |
| [`Reporting/`](Reporting/README.md) | Quantity tables and CSV export |
| [`Scattering/`](Scattering/README.md) | Deterministic weighted block scatter |
| [`Sculpting/`](Sculpting/README.md) | The sparse world-XY displacement field and brush engine |
| [`Retopo/`](Retopo/README.md) | Cross-field solve and quad retopology |
| [`Interop/`](Interop/README.md) | The versioned host-exchange contract |

Start from [docs/architecture.md](../../docs/architecture.md) for the pipeline, and
[docs/file-index.md](../../docs/file-index.md) for a one-line summary of every file.
