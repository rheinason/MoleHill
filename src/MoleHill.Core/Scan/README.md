# Scan ground recovery

Phase 1 of [scan to terrain](../../../docs/scan-to-terrain-plan-2026-10-18.md), independent of Rhino.
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
