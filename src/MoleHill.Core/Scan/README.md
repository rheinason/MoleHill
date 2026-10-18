# Scan ground recovery

Phases 1–3 of [scan to terrain](../../../docs/scan-to-terrain-plan-2026-10-18.md), independent of Rhino.

`ScanTerrainPipeline` orchestrates one rasterisation, ground, wall-constrained reference, surface areas,
road profiles and the final reference with fixed breaklines. `ScanColourSource` accepts triangle colours
or `ScanTexture` RGB bytes plus per-corner UVs; the winning top triangle supplies each raster colour.
The host preserves UV seams rather than merging coincident scan vertices.

`ScanSurfaceAreas` uses normalised excess green, saturation, local slope and roughness on observed
ground. Removed roofs remain unknown. Components below the minimum area are omitted; smaller enclosed
holes are filled only in drawing masks, never in observed-ground flags. `ScanGridEdges` traces separate
closed area loops including holes; boundary heights follow the clean reference. `ScanRoadEdges` measures
trend-corrected cross profiles along paved edges: persistent narrow steps produce fitted top/bottom
kerb pairs, otherwise one draped edge. Without colour, only geometry-supported kerb-sized steps are
available. These are measured steps, not semantic guarantees that every line is a built kerb.

`ScanSoftRegion` clips contours exactly at mask cell boundaries, preserving paved islands and joining
adjacent intervals without densifying straight lines. Recovery evaluates soft-ground vertices and
keeps only the fragment nearest each selected error point, rather than spending the budget across
every disconnected lawn at that height. Paved boundaries and measured rails stay in every rebuild.
Contours are fitted before reconstruction, with an explicit polyline fallback where a smooth fit cannot
respect the region or vertical tolerance. Unknown areas are excluded from soft-ground accuracy claims.
All lengths use the input coordinate unit; the host converts physical defaults.

`ScanRasterizer` samples triangle interiors in plan into top and lowest `ScanGrid` heights. Either
winding is accepted; vertical faces contribute no plan area. NaN denotes no data. Automatic spacing
comes from plan area/face count and is enlarged if necessary to stay within 16 million nodes; explicit
cells exceeding that cap are rejected before integer conversion/allocation.

`ScanGround` progressively opens the top raster from the smallest to largest object width, with a
slope allowance. A region raised around enough of its perimeter is removed; terrace steps are returned
to ground. The mask grows by a margin, narrow scraps and small islands are removed, and a local median
stabilises fill-edge heights. `HarmonicFill` interpolates the unknown domain from those anchors using
coarse-to-fine initialisation and over-relaxed Gauss-Seidel. The domain is the largest connected scan
footprint with enclosed missing areas filled. Smaller disconnected scan pieces are discarded.

`ScanGroundMesher` averages at a rounded multiple of raster spacing, keeps the largest cell component,
and removes corner-only contacts until its border loops are simple. Symmetric averaging windows
preserve planar heights at rectangular borders. `Processing/TerrainCurveRecovery` then supplies the
editable curves and spots; the ground mesh is an internal intermediate.

Ground under an object is inferred, especially uncertain where it meets a terrace step. This filter
is geometric only: raised regions include vegetation and cars, and the object count is not a building
count. Neither colour classification nor roof/kerb reconstruction is implemented here yet.

`ScanWalls` traces Phase 2 top/toe pairs from narrow bands of unexplained rises, with the ground trend
measured outside the band. It merges a wall spread over multiple cells, rejects broad ramps and short
fragments, splits at branches/gaps, and uses `RouteCurveFitter` for the editable curves. Fitting stays
within a fraction of a cell so top and toe do not drift across each other. Prepared rails are checked
against the contouring footprint. Their light polylines feed `TerrainCurveRecovery.ConstrainReference`,
which inserts them before recovery and excludes old coarse heights on a measured segment. Pass those
same lines through `FixedBreaklines` to hold them during every contour rebuild. `ScanWallsTests` covers
height, orientation, scaling, multi-cell steps and constrained reconstruction, including heights between
rail vertices. Set `MOLEHILL_SCAN_WALLS=1` to opt the reference measurement into Phase 2.

`ScanGroundTests` covers sparse/overlapping triangles, houses and trees on a terraced slope, planar fill,
empty scans, raster overflow, border topology and recovery. Opt into `ScanGroundMeasurementTests` with
`MOLEHILL_SCAN_OBJ` (Z-up OBJ in metres); `MOLEHILL_SCAN_IMAGES` writes diagnostic PPMs and
`MOLEHILL_SCAN_MAX_CONTOURS` overrides its default 300-contour budget.
`MOLEHILL_SCAN_PLAN_TOLERANCE` overrides the default 0.5 m plan simplification tolerance.
An ordinary managed run without that OBJ does not exercise the reference scan. Measurement output
reports termination as well as error; successful execution does not imply the requested tolerance was
reached. These timings are observations,
not hosted performance acceptance or a claimed speed improvement.
