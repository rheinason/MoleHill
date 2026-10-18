# Scan ground recovery

Pure [scan to terrain](../../../docs/scan-to-terrain.md) analysis, independent of Rhino.

`ScanBuildings` extracts building context without changing the terrain reference or fixed breaklines.
Its `.Footprints` partial finds dominant axes and regularises outlines, preserving courtyards and
closing diagonal contacts that would otherwise create non-manifold vertical edges. `.Planes` captures
upward scan triangles, welds UV seams for adjacency, grows connected robustly fitted plane regions,
and merges coplanar regions. Colour supports vegetation rejection; coherent planar turf roofs remain
eligible, and colour-free input requires stronger dominant-plane evidence.

`.Roofs` ranks flat/shed, gable, hip/half-hip and crossing-gable templates by measured error and facet
count. It then tries continuous plane lattices and spatial plane arrangements before a flagged
median-height extrusion. `.Solid` partitions the footprint by plane intersections and spatial patch
cuts, closes roof steps, walls and the level base, and checks every directed shell edge. `.Junctions`
removes alternating spatial labels that would make four walls share one vertical edge; every edit
must reduce the number of bad junctions, and the final shell is remeasured. Accepted roofs put at least
90% of covered scan-sample area within `RoofTolerance`, with at least 85% sample coverage; these are
robust context models, not an all-sample tolerance promise. RMS, coverage and fallback are explicit.
Green complex roofs, low buildings and noisy or occluded roof patches remain classification limits.

`ScanBuildingFaces` coalesces the validated triangulated shell into connected planar faces with hole
loops. It retains junction vertices shared by three or more face groups and removes redundant straight
vertices elsewhere; the host creates trimmed planar faces and joins one closed Brep per building,
instead of converting and merging every triangle in native code.

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
count. Roof, surface and kerb classification are separate stages over that inferred ground.

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


`ScanPointCloud` captures flat XYZ with optional packed RGB and LAS class bytes. It bins original
observations into top, lowest and classified-ground heights without triangulating the source cloud.
The top observation supplies colour and class. Empty nodes remain NaN; explicit cells beyond the
16-million-node limit are rejected before allocation. Automatic cells target four observations per
bin, with a dimension-aware cap. Sparse clouds need a larger user cell; no source gaps are bridged
for roof fitting. Only four observed neighbouring nodes produce roof-fit triangles.

The cloud pipeline uses the same later stages as mesh input. Class 2 ground observations become height
anchors, including below roofs, while unclassified areas retain filtered anchors. The morphological
filter runs independently to report disagreements above its initial allowance. Supplied ground is
trusted rather than silently discarded; `IsGround` still describes the observed top surface, so an
upper roof return never becomes measured kerb or paving evidence. Majority classes 3–5 exclude a
vegetation candidate even when its canopy looks planar. Unclassified clouds still work geometrically.

`ScanTerrainPipeline.Options.ReportPreview` publishes immutable stage snapshots after ground,
buildings, walls, areas and final reference preparation. Fixed-line lists are copied so later stages
cannot change earlier snapshots. Hosts marshal these callbacks to the UI and discard stale generations;
Core retains no preview geometry or document handles. `ScanPointCloudTests` covers observations/classes,
ground anchors/disagreements, vegetation, roofs, units, sparse gaps, stage callbacks, caps and cancellation.
