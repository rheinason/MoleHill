# MoleHill.Core/Analysis

Terrain analysis math. Pure, unit-tested.

- `ContourGenerator.cs` — single-pass marching-triangles contour extraction (one pass over faces, each
  triangle only contributes to the levels in its own value span — far faster than one mesh-plane per
  level). Returns `ContourLevel`s of `ContourPolyline`s. The Rhino side (`TerrainBuildService.Analysis.cs`)
  wraps these as curves. It contours an arbitrary **per-vertex field**, with the points it emits still on
  the mesh; elevation contouring is that same pass with the field left null, so there is one
  implementation rather than one per caller. The field overload is what draws a cut/fill delta — a level
  is then a depth, and level 0 is the balance line. A vertex whose field value is not finite marks ground
  the field does not describe, and every face touching one is skipped, so unmapped ground draws nothing
  rather than a line derived from a value that was never measured. Stitching keeps node incidence as flat CSR (a `List<int>` per welded node cost
  a list object plus a backing array for roughly every segment) and walks chains into two reusable
  buffers instead of a `LinkedList` node per point — the emitted order is `backward` reversed then
  `forward`, which is what the old `AddFirst`/`AddLast` produced. Seeds are ordered by minimum endpoint
  degree with an explicit index tiebreak, matching the stable `OrderBy` it replaced.
- `ContourLevel.cs` / `ContourPolyline.cs` — result types.
- `SlopeInput.cs` — the single parse/format point for slope values a user reads or types. Converts
  between ratio, percent, promille, degrees and `1:n` pairs; accepts a unit written into the text
  (`25%`, `150prom`, `14deg`, `1:3`, `1v:3h`) so a field takes any unit whatever it displays; and owns
  the one table of per-unit suffix/name/decimals. Every unit has an ASCII spelling, since `‰` and `°`
  are unreachable from a keyboard. `a:b` is read vertical:horizontal, so `1:3` is the flat one.
  Pure and Rhino-free — the Rhino side supplies only *which* unit to show, via `SlopeUnitPreference`.
- `SlopeAnalyzer.cs` — slope analysis. `Summarize` computes min/max/area-weighted average without
  allocating preview colors (auto-fit additionally collects a transient slope + area array, since fitting
  a range needs the distribution); `Analyze` keeps the per-face slope + palette mapping path for colored
  previews and handles both gradient and stepped modes. Both paths parallelize above 20,000 faces. The
  display range comes from `AnalysisRange`, never from the raw maximum.
- `AspectAnalyzer.cs` — aspect: the compass bearing each face drains towards, which is the other angle of
  the normal `SlopeAnalyzer` already takes. Shaped like it — same flat arrays, same plan-area weighting,
  same palette/band apparatus, a `Summarize` that allocates no colours. Two things it needs that slope
  does not: a **pinned cyclic range** (bearings live on 0..360 and the ends meet, so fitting is
  corruption, not improvement) and a **flat exemption** — a level face has no aspect, only rounding
  noise, so faces flatter than a stated slope ratio carry NaN and are drawn neutral. North is supplied by
  the caller as an azimuth CCW from +X, which is the convention Rhino's `Sun.North` uses. `SectorName`
  names the nearest cardinal for a human readout; that rounding is deliberately *not* the same division
  as the colour bands, whose edges sit on the cardinals. The summary's dominant bearing is a **circular**
  mean — averaging the numbers would put the mean of 350° and 10° at due south.
- `AnalysisRange.cs` — the single owner of what an analysis maps across its palette, and of what
  "auto-fit" means: an area-weighted 2nd-98th percentile (via a fixed-bin `Histogram`, so no sort and no
  retained values), snapped outward to a 1/2/5/10 number. Values outside the fitted range still draw,
  clamped to the end colours. `RangeShape` anchors the fit — `FromZero` (slope), `SymmetricAboutZero`
  (cut/fill), `MinMax` (elevation), `Cyclic` (aspect) — and is applied before the snap. `Cyclic` is the
  one shape that is never fitted at all: it pins a full turn with or without data, because a trimmed
  bearing range stops naming directions. Everything that colours faces,
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
  `turbo`, `terrain`, `blackbody`, `mono`, `aspect-wheel`). `aspect-wheel` is the cyclic one: its first
  and last stops are the *same* colour, so a plain linear sample wraps seamlessly and nothing needs cyclic
  sampling code — a linear ramp over a compass would otherwise put a hard seam at due north. The keys are a persistence contract — `AnalysisDefinition.
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
  inspect only the owning cell rather than repeating work across a 3x3 neighbourhood. Cell storage is
  flat CSR built by a count pass then a fill pass, both in face order, so each cell's run is ascending -
  the order the first-match rule depends on.
- `MeshRegularityAnalyzer.cs` — sampled density and minimum-angle checks used to identify coarse or
  skinny triangulations before vertex-based Smooth/Sculpt operations.
- `WaterflowTracer.cs` — deterministic downhill path tracing from XY starts through adjacent 2.5D
  triangles, stopping at terrain boundaries or local flat/sink faces. A per-call XY face index avoids
  repeated full-mesh start scans, and cancellation is checked during lookup and path transitions.
  Setup (face adjacency + face index) runs once per call; the traces are independent functions of
  read-only state, so above a start-count/face-count threshold they run in parallel. `FaceSpatialIndex`
  is immutable and its query buffers live in a per-worker `QueryState` — that separation is what makes
  one index shareable, so do not put scratch back on the index. Scratch uses sparse visited sets,
  sized by queried candidates rather than terrain faces per worker. Results are written by start index and
  compacted afterwards, so path order and the rejected count match the serial loop exactly; a
  cancellation raised inside the parallel loop is unwrapped from `AggregateException` so callers still
  see `OperationCanceledException`.
