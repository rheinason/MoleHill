# MoleHill.Core/Analysis

Terrain analysis math. Pure, unit-tested.

- `ContourGenerator.cs` — single-pass marching-triangles contour extraction (one pass over faces, each
  triangle only contributes to the levels in its own Z-span — far faster than one mesh-plane per level).
  Returns `ContourLevel`s of `ContourPolyline`s. The Rhino side (`TerrainBuildService.Analysis.cs`)
  wraps these as curves.
- `ContourLevel.cs` / `ContourPolyline.cs` — result types.
- `SlopeAnalyzer.cs` — slope analysis. `Summarize` computes min/max/area-weighted average without
  allocating preview colors; `Analyze` keeps the per-face slope + palette mapping path for colored
  previews. Both paths parallelize above 20,000 faces.
- `AnalysisColorMapper.cs` — shared smooth-gradient and stepped-band classification used by Rhino
  elevation, slope, cut/fill, and sculpt previews. Signed infinities clamp to their corresponding low or
  high palette stop.
- `MeshHeightProjector.cs` — fast 2.5D XY-to-Z lookup for reference comparison analysis. It returns a
  fallback-required status for overlapping or near-vertical XY regions so Rhino-side callers can keep
  exact legacy projection behavior there. Faces are registered in every touched grid cell, so queries
  inspect only the owning cell rather than repeating work across a 3x3 neighbourhood.
- `MeshRegularityAnalyzer.cs` — sampled density and minimum-angle checks used to identify coarse or
  skinny triangulations before vertex-based Smooth/Sculpt operations.
- `WaterflowTracer.cs` — deterministic downhill path tracing from XY starts through adjacent 2.5D
  triangles, stopping at terrain boundaries or local flat/sink faces. A per-call XY face index avoids
  repeated full-mesh start scans, and cancellation is checked during lookup and path transitions.
