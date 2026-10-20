# Scan terrain review runner

Runs the host-free terrain pipeline against a textured, Z-up OBJ in metres. It does not change the
source, invoke Rhino, publish a package or certify surveyed accuracy. Build separately from the solution:

```powershell
dotnet build tools/MoleHill.ScanReview/MoleHill.ScanReview.csproj
dotnet run --project tools/MoleHill.ScanReview --no-build -- "scan.obj" "texture.rgb" ".artifacts/scan-review"
```

The RGB file starts with little-endian signed Int32 width and height, followed by packed R,G,B bytes
in top-to-bottom row order (the same format used by the original reference capture). The OBJ must
have face-corner texture coordinates. Polygon faces are triangulated as fans; positive and relative
negative indices are supported. This diagnostic reader keeps degenerate faces; native Rhino import
may cull them, so compare runs using the same reader.

Options: `--no-recovery` exports geometry only; `--legacy-recovery` compares the legacy 300-contour
budget on the same prepared reference; `--max-inputs=N` changes the new combined cap (default 2,000).
Landform lines are disabled, and buildings are off unless `--buildings` asks for them with the native
command's defaults (`--roof-faces=N` changes the face budget, 0 lifts it). It then writes
`buildings.json` (kind, roof error and face counts per building), `buildings.obj`, the planar faces a
host joins into each solid as `building-faces.json`, and the scan's top surface as `surface.bin` (the
`ground.bin` header, then float32 heights) to compare roofs against. Every run also writes the prepared reference
mesh contours are recovered from as `reference.obj`, its fixed breaklines as `fixed.json`, and the gap-fill mask
as `fill.pgm` (white = filled). `--measure-fills` measures fills like the rest, as before. Physical settings are explicit
in `Program.cs`. `terrain.json` includes rational pieces, controls, sample points, observation/inference
metadata and filtering/rejection counts. `orthophoto.ppm` records raster colour placement.
`contours.json` records contours/spots, termination, filtered/original-reference residuals, the uncovered
area and the remaining miss regions (worst first; the run also prints the twelve worst).

The reference input is external and is not distributed with this repository. See
[terrain output review](../../docs/scan-terrain-output-review.md) for the baseline, measured changes
and unresolved classification/accuracy limitations. Runtime observations from this runner are not
hosted performance acceptance.
