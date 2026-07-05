# MoleHill.Core/Analysis

Terrain analysis math. Pure, unit-tested.

- `ContourGenerator.cs` — single-pass marching-triangles contour extraction (one pass over faces, each
  triangle only contributes to the levels in its own Z-span — far faster than one mesh-plane per level).
  Returns `ContourLevel`s of `ContourPolyline`s. The Rhino side (`TerrainBuildService.Analysis.cs`)
  wraps these as curves.
- `ContourLevel.cs` / `ContourPolyline.cs` — result types.
- `SlopeAnalyzer.cs` — slope analysis. `Summarize` computes min/max/area-weighted average without
  allocating preview colors; `Analyze` keeps the per-face slope + palette mapping path for colored
  previews.
- `MeshHeightProjector.cs` — fast 2.5D XY-to-Z lookup for reference comparison analysis. It returns a
  fallback-required status for overlapping or near-vertical XY regions so Rhino-side callers can keep
  exact legacy projection behavior there.
