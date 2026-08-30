# MoleHill.Core/Analysis

Terrain analysis math. Pure, unit-tested.

- `ContourGenerator.cs` — single-pass marching-triangles contour extraction (one pass over faces, each
  triangle only contributes to the levels in its own Z-span — far faster than one mesh-plane per level).
  Returns `ContourLevel`s of `ContourPolyline`s. The Rhino side (`TerrainBuildService.Analysis.cs`)
  wraps these as curves.
- `ContourLevel.cs` / `ContourPolyline.cs` — result types.
- `SlopeAnalyzer.cs` — slope analysis. `Summarize` computes min/max/area-weighted average without
  allocating preview colors (auto-fit additionally collects a transient slope + area array, since fitting
  a range needs the distribution); `Analyze` keeps the per-face slope + palette mapping path for colored
  previews and handles both gradient and stepped modes. Both paths parallelize above 20,000 faces. The
  display range comes from `AnalysisRange`, never from the raw maximum.
- `AnalysisRange.cs` — the single owner of what an analysis maps across its palette, and of what
  "auto-fit" means: an area-weighted 2nd-98th percentile (via a fixed-bin `Histogram`, so no sort and no
  retained values), snapped outward to a 1/2/5/10 number. Values outside the fitted range still draw,
  clamped to the end colours. `RangeShape` anchors the fit — `FromZero` (slope), `SymmetricAboutZero`
  (cut/fill), `MinMax` (elevation) — and is applied before the snap. Everything that colours faces,
  writes a summary, or draws a legend resolves through here, so they cannot drift apart. `Histogram`
  additionally exposes `Resample(binCount)`, which downsamples its fixed bins into normalized bars for
  the panel's ramp-card histogram — the distribution auto-fit read, drawn behind the ramp it produced.
- `ColorRamp.cs` — an editable ramp: ordered 0..1 stops plus the pure operations behind the panel's ramp
  card (`WithStopAt`, `WithStopColor`, `Insert`, `RemoveAt`, `Distribute`, `Reverse`, `Resample`,
  `IndexNearest`). Immutable — every operation returns a new ramp — and it never samples colours itself,
  it hands its stops to `AnalysisColorMapper`, so the card and the mesh cannot disagree. Degenerate input
  (empty, one stop) widens into a usable ramp rather than throwing, because this is fed by deserialized
  documents. `Insert` takes the colour the ramp already shows at that position, so adding a stop changes
  nothing visually; `RemoveAt` refuses below two stops.
- `ColorRampPresets.cs` — the built-in named ramps (`terrain-spectrum`, `viridis`, `magma`, `cool-warm`,
  `turbo`, `terrain`, `blackbody`, `mono`). The keys are a persistence contract — `AnalysisDefinition.
  PalettePreset` stores one — so add freely but never rename, and `Resolve` falls back to the default
  rather than throwing on a key from a newer build. User-saved ramps live Rhino-side in
  `Services/ColorRampPresetStore`.
- `AnalysisColorMapper.cs` — shared smooth-gradient, stepped-band and constant classification used by Rhino
  elevation, slope, cut/fill, and sculpt previews. `ResolveBands` divides a range into flat-coloured
  bands that tile it exactly (the last absorbs the remainder); a band's colour is the palette sampled at
  its centre, which is what makes stepped mode legible. Hot loops resolve bands once and call
  `SampleBanded`. Signed infinities clamp to their corresponding low or high palette stop.
  `Mode.Constant` holds each stop's colour until the next stop, so the stops themselves are the band
  edges — the only way to express *unequal* bands ("one wide acceptable range, then a few narrow ones"),
  which a fixed interval cannot. Every colouring path funnels through `SampleResolved` (paired with
  `ResolveBandsFor`), so a mode cannot be honoured by the mesh and forgotten by the legend.
- `MeshHeightProjector.cs` — fast 2.5D XY-to-Z lookup for reference comparison analysis. It returns a
  fallback-required status for overlapping or near-vertical XY regions so Rhino-side callers can keep
  exact legacy projection behavior there. Faces are registered in every touched grid cell, so queries
  inspect only the owning cell rather than repeating work across a 3x3 neighbourhood.
- `MeshRegularityAnalyzer.cs` — sampled density and minimum-angle checks used to identify coarse or
  skinny triangulations before vertex-based Smooth/Sculpt operations.
- `WaterflowTracer.cs` — deterministic downhill path tracing from XY starts through adjacent 2.5D
  triangles, stopping at terrain boundaries or local flat/sink faces. A per-call XY face index avoids
  repeated full-mesh start scans, and cancellation is checked during lookup and path transitions.
