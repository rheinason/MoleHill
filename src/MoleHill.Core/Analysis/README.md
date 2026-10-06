# MoleHill.Core/Analysis

Terrain analysis math. Pure, unit-tested.

- `IncrementalContourTracer.cs` / `ContourSegment.cs` — per-face elevation contour segments, re-traced
  only for the faces whose Z changed (a sculpt dab: 1–5 ms, against ~29 ms for a full pass on 540k
  faces). Unstitched; uses `ContourGenerator`'s own crossing rule so live and built lines coincide.
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
- `AnalysisLegend.cs` — `AnalysisLegendBuilder`, the key to a coloured analysis independent of drawing:
  a gradient strip with `AnalysisRange.BuildTicks` ticks, or swatches for Stepped bands, Constant
  thresholds and categories. Built through `AnalysisColorMapper` from the same range and palette the mesh
  uses; end swatches are labelled open because values past the range take the end colours. The Rhino
  Legend annotation draws it.
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
- `DrainageBasinAnalyzer.cs` / `BasinGraph.cs` — segments the terrain into drainage basins: a downstream
  face pointer per face, the basins those resolve to, and whether each drains off the terrain edge or
  into a closed depression. One result serves both drainage questions (catchments and ponding), which is
  why `BasinGraph` is its own type rather than either analyzer's return; it carries the face adjacency
  too, because every consumer walks it. Routing follows each face's plane gradient — the same quantity
  `WaterflowTracer` follows, through the same `FaceAdjacency` — so a traced path cannot cross a catchment
  divide. Three things carry the weight:
  - **Flat regions.** A graded pad is one enormous level area with no gradient to route by; routed face
    by face it becomes confetti. Flat faces are grouped into connected regions and routed breadth-first
    inward from the edges the region actually spills across. A region with nowhere to spill is a
    depression, and saying so is the point. Vertical faces (a retaining wall) have no gradient and no
    plan area, so they route as flat and spill onto the ground below instead of standing as a phantom
    pond behind every wall.
  - **Cycles are not an edge case.** Wherever water converges on a *vertex*, the two faces sharing that
    vertex's opposite edge each fall towards it and each leave through their shared edge — face-to-face
    routing goes round for ever. That happens at the bottom of every valley and on the low corner of
    every graded pad, so a cycle is handed to the same spill routine flat regions use, and becomes a sink
    only when there is genuinely nowhere lower.
  - **Sinks sharing a floor are one depression.** A round bowl routes as two half-bowls that share their
    lowest vertex; a pond has one water surface, so they are merged on floor-vertex identity. Two
    distinct pits cannot share their lowest point.
  Small basins optionally merge into the neighbour they spill into (`MinimumBasinAreaShare`) — a real
  survey yields hundreds of slivers. Basins come out ordered by descending plan area so a categorical
  colouring keeps its colours across rebuilds. ~65 ms over 180k faces.
- `BasinBoundaryExtractor.cs` — a basin's faces to its closed boundary polygon(s), through
  `Engine/EdgeLoopChainer`. Edges are emitted in each face's own winding, so the directed edges balance
  at every vertex and the loops close without any geometric decision about what is inside — which is
  what keeps a basin with an island in it, or one pinching to a point, from needing a special case.
- `PondingSolver.cs` — measures the depressions `DrainageBasinAnalyzer` found: spill elevation, impounded
  volume, water-surface area and the shoreline. Three things are easy to get wrong here and are settled
  in the file:
  - **The spill elevation is a bottleneck, not a shortest path.** Water escaping a depression does not
    care about the total climb, only about the highest lip it must get over — so the level is the minimum
    over all routes out of the maximum crossing elevation along that route, found by flooding from the
    floor and always taking the lowest lip. A crossing costs the *lowest* point of the edge it passes
    over, and a route costs the highest crossing along it.
  - **Not the lowest vertex on the basin's boundary.** A depression's catchment runs up to the watershed
    divide, so that boundary usually reaches the terrain edge far below the depression's own lip and the
    answer comes back an order of magnitude low. The flood is bounded by the basin; the level is decided
    by the lip.
  - **The shoreline is contoured on `z − spillZ`, not `spillZ − z`.** The zero level must sit at the
    field's *maximum*: `ContourGenerator` skips a level equal to the minimum, since no vertex is below it
    and there is no below-to-above transition to find. With the sign the other way a bunded pad — the
    whole depression at or below its rim, nothing above water — silently draws nothing. The inclusive
    upper bound this leans on is the same one added so a contour at a pad's exact design elevation still
    draws its outline.
  Volume is the prism sum the earthworks analysis uses, over the basin's faces with depth clamped at
  zero, so the dry upper catchment drops out on its own. Depressions shallower than `MinimumDepth` are
  not reported: a millimetre of survey dimple is not a pond, and a check that says it is gets switched
  off.

- `GradientComplianceAnalyzer.cs`: checks the built surface against accessibility gradient limits.
  Stage one (backlog B13) covers **level areas**: landings, turning spaces and plazas. These have no
  direction of travel, so their rule is one limit in every direction. Three things set it apart from
  thresholding the slope analysis:
  - **It measures over a footprint, like a level.** A face's value is the plan-area-weighted mean of
    the face gradient vectors within `MeasurementLength` of it. For a plane that is exact; for a surface
    it is the least-squares plane over the footprint. Face by face, one sliver triangle on a survey fails
    a landing that a level laid across it would pass.
  - **It averages magnitude after direction, not before.** A 1:60 fall along both axes is 1:42 on the
    diagonal, and a level area answers for its fall line. Averaging slope magnitudes instead would make
    opposite-facing faces add up rather than cancel.
  - **Only inside faces are averaged.** A landing measured at its edge must not borrow slope from the
    ramp beside it, so the footprint never reaches past the level-area boundary.

  **Routes** (stage two, `EvaluateRoutes`): a face within half the corridor width of a route takes the
  nearest segment's plan direction. Its averaged gradient is split into running slope (along) and cross
  slope (across), both as magnitudes, so the drawing direction does not matter. Running slope is classed
  walk / ramp / exceeds, and cross slope is checked separately. Averaging again uses only corridor
  faces. This is the case a slope map cannot see: one 1:15 plane is a ramp walked downhill and a
  cross-slope failure walked along the contour.

  **Runs** (stage three, `RouteRunAnalyzer`): each route is sampled at even stations and followed along
  its length. Landings are **detected, not drawn**: a stretch within the level limit for at least the
  minimum landing length. A station counts as flat when the ground is flat on *either* side of it,
  because a centred window would shorten every landing by its own width. A shorter flat does not end
  its run, so a too-short landing shows up as the run it failed to break being too high or too long.
  Walk and ramp runs are checked for rise, and ramps for going against a per-gradient table,
  interpolated linearly in the n of 1:n (this reproduces Approved Document M's own worked examples).

  Membership is even-odd across all loops, so a nested loop is a hole (Project To reads boundaries the
  same way). A limit hit exactly passes: a landing graded at 1:48 must not fail on floating point.
