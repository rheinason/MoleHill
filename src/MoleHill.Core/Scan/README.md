# Scan ground recovery

Pure [scan to terrain](../../../docs/scan-to-terrain.md) analysis, independent of Rhino.

`ScanBuildings` extracts building context without changing the terrain reference or fixed breaklines.
Its `.Footprints` partial finds dominant axes from long facade runs (fourfold direction votes, with
a bounding-box fallback when evidence is weak) and regularises outlines, preserving courtyards and
closing diagonal contacts that would otherwise create non-manifold vertical edges. Plan-only thinning
at `FootprintTolerance` precedes facade fitting; consecutive supports are combined before intersecting
orthogonal walls. Short uninterrupted raster edges are not treated as architectural corners. The
orthogonal fit requires 95% of the raster outline within the footprint tolerance and all of it within
twice that tolerance, allowing isolated scan-edge outliers without moving a whole facade. Fits
retain simplicity/area checks and a thinned fallback for non-orthogonal annexes. `.Planes` captures
upward scan triangles, welds UV seams for adjacency, grows connected robustly fitted plane regions,
and merges coplanar regions. Plane fitting integrates projected triangle area and second moments;
triangle count and centroid locations do not substitute for geometric support. Normal smoothing
does not cross the plane-angle crease threshold. Even one large triangle can support a plane, while
the minimum measured area still applies. The upper-height guard uses observed triangle vertices,
so a ridge is not rejected for lying above its face centroids. Colour supports vegetation rejection; coherent planar turf roofs remain
eligible, and colour-free input requires stronger dominant-plane evidence. Green or vegetation-classed
cells more than two footprint tolerances from every measured roof plane are trimmed from the footprint
(a canopy touching a building is raised too), and samples over trimmed cells or over scraps the outline
leaves out drop out of the roof's sample set. Uncoloured, unclassified cells are never trimmed, so a
roof part no plane was found for stays.

`.Roofs` ranks flat/shed, gable, hip/half-hip and crossing-gable templates by measured error and facet
count. It then tries the convex and lattice plane arrangements, then `.Parts` composites, then the
spatial plane arrangement, before a flagged median-height extrusion. A composite (`ScanRoofKind.Composite`)
splits the plan along the facade axes into parts (best-first, while a split buys back a tenth of the
roof tolerance over the whole roof), each under the cheapest lower envelope of up to four measured
planes, or a local flat or pitched plane, that stays above the base. Split positions are ranked for every
gap between samples with prefix sums, and only the best two per axis are fitted exactly. Parts then
overlap where that lowers the error, the higher roof winning, so wings meet in valleys rather than in a
wall standing on an eave; an overlap edge reaches on while it costs nothing and snaps to a facade-parallel
ridge or valley within a sample row, and halves that overlaps made redundant are joined again. The
composite is offered at every part count from the most to two, because the face budget is only known
once a shell is built. Its plane-pair cuts are limited to planes of one part or of parts that overlap
or touch: touching parts whose roofs cross on their shared edge need the step there cut where it turns. Each spatial arrangement uses the configured simplification once; it does
not retry progressively weaker settings. `.Solid` partitions the footprint by plane intersections and spatial patch
cuts, closes roof steps, walls and the level base, and checks every directed shell edge. `.Junctions`
removes alternating spatial labels that would make four walls share one vertical edge; every edit
must reduce the number of bad junctions, and the final shell is remeasured. Accepted roofs put at least
90% of covered scan-sample area within `RoofTolerance`, with at least 85% sample coverage; these are
robust context models, not an all-sample tolerance promise. RMS, coverage and fallback are explicit.
Green complex roofs, low buildings and noisy or occluded roof patches remain classification limits.

`MinimumRoofDetail` optionally removes narrow/small arrangement patches before shell validation.
`MaximumRoofFaces` independently limits actual coalesced roof facets, including internal vertical roof
steps; floor and exterior footprint walls are exempt. Its Core default zero is unbounded. The host
defaults to 2 m detail and 12 faces. When detailed models fail or exceed that budget, simple pitched
templates can survive as `IsApproximateRoof` fallbacks. Final-shell RMS ranks admissible approximations
against the median extrusion; a pitched approximation needs at least 50% within tolerance, 85% coverage
and the same upper-height bound. Actual RMS/within/coverage remain explicit; these do not meet the 90%
accurate-fit contract. `IsFallback` includes both approximation kinds.

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


Scan terrain outputs use one canonical `ScanCurveGeometry`: rational B-spline pieces plus a sampled
approximation. `ScanAlignmentFitter` chooses supported line/arc plans and fits height separately;
height refinement cannot bend a straight plan or change an arc's radius. Top/toe rails share alignment
ranges, retain their side, and reject unsupported complexity. Closed terraces retain rolling profiles.
`ScanCurveBuilder` creates the same ordinary Rhino NURBS for preview and Keep.

Wall evidence requires observed ground on both sides. `ScanSourceSampler` measures kerb profiles
against original triangles or nearby original cloud observations, excluding supplied vegetation/building
classes; interpolated cloud gaps cannot establish evidence. `ScanFeatureBridge` joins only compatible
accepted pairs across short gaps, checking direction, height, obstacles and crossing rails. Observed
share and inferred station intervals travel with the canonical geometry and baked attributes.

Area classification does not run on the height grid. A texture is rasterised a second time onto a
`ScanColourLattice` up to four times finer (point clouds and per-face colours use factor one).
`ScanSurfaceClassifier` gives every lattice node logistic paved and vegetated scores around the
configured thresholds, gated by height-grid slope and roughness, and averages them over
`SmoothingRadius` (default 0.6 x plan tolerance, three box passes) before the larger score wins. Hard
thresholds on single texel samples made every speck of colour noise an outline detail. Averaging also
erased footpaths narrower than about four radii (a 0.6 m path lost to the lawn either side), so the same
scores averaged a third as wide add back paved strips that a disc opening of two radii removes, when a
strip covers at least twenty squared radii; stepping stones stay lawn. A strip five times longer than wide is
exempt from the minimum area, so a stretch of path that moss cuts off from the drive is kept; shorter
blobs (shadows and dark fringes beside buildings) are not. Ground standing
more than four paved roughnesses above an opening of the surface about 3 m across gives no class evidence:
the ground filter keeps cars and bushes smaller than its minimum object as ground, and "rough ground is
soft" outlined every parked car. Components below the minimum area become unknown. `Classes` stays
unknown wherever ground was removed.

`ScanSurfaceGraph` traces that fine partition, fits each shared class interface once and reuses reversed
geometry in adjacent loops; junction endpoints stay shared. A short moving average turns lattice steps
into a sub-node line, resampled at a fixed spacing. `ScanOutlineFitter` then builds each interface
directly. It first finds corners at four plan tolerances (two cells at least): a turn of 50 degrees, the
sharpest nearby. Each corner moves to where lines fitted to the trace beyond its rounding cross, and the
rounded samples move onto those lines, never past the corner. Each run between corners is smoothed by
a Gaussian at half that scale. A classified edge wanders by about the plan tolerance, so fitting it raw
drew every wander as a wobble or zig-zag while still rounding real corners. Then Douglas-Peucker at half
the plan tolerance; paved interfaces become maximal straight runs, long ones held, with circular arcs
only across the shorter runs between them, heights draped onto lines by subdivision. Natural interfaces
hold straight runs of eight plan tolerances as lines too (a lawn ends at a facade or a bed edge as often
as at paving), with clamped quadratic splines on the thinned vertices between them, or one spline seamed
at a leg midpoint when there is no straight. A spline is tightened before a polyline fallback. After
fitting, any interface that crosses itself or another, or folds back at a cusp, falls back to lines
through its thinned trace and then to the trace itself (`PolylineCrossings`, checked on 32 samples per
span). Loops of no area are dropped. On the reference scan the fitted stage carries about 6,500 outline
controls. Outline heights need only the terrain accuracy. Paved outlines join
`FixedBreaklines`: paving is close to planar between its edges, so they carry it. Soft outlines are
drawings. Complete duplicate closed interfaces are drawn only once.

Enclosed unknown pockets up to six minimum areas are filled in drawing masks, each node from the class
it reaches first through the pocket. A car between a lawn and a drive carries both edges on through it.
A three-quarter majority left such cars and their shadows outlined as holes. With
`MaximumFilledObjectHeight` (host: 2.5 m) only low raised objects qualify, opened at about half a metre
so the ground filter's thin margin band around a building does not join a car parked against it to the
building.

With `SoftNeckWidth` (host: 3 m), soft ground joined only by narrower strips is outlined as separate areas
(`ScanSoftNecks`): cores are what a disc opening of that width leaves, and every other soft node joins
the core its soft ground reaches first. One soft outline had run 4.2 km round nearly every garden as a
single curve of 3,241 controls. A square window would split diagonal strips 1.4 times wider, and the
reference streets run diagonally.

`ScanBoundaryFitter` remains for colour-only paving edges, which are traced only when area outlines are
off; with outlines drawn, `ScanRoadEdges` traces kerbs alone (`KerbsOnly`). Colour edges are labelled
paving edges, not confirmed kerbs.

`ScanGroundCleaner.Lower` (pipeline `StandingObjectWidth`/`Height`/`MaximumHeight`, host 3 m / 0.4 m /
2.5 m) lowers small standing objects the ground filter kept as ground. These are ground standing over
0.4 m above a 3 m opening, no taller than 2.5 m, and not beside a taller removed object (a building, a
tree). Each is filled harmonically from the ground round it, with a half-metre rim. Without it the
reference rose over every parked car: paving took spot heights on car roofs, and lawns took contour
rings round bushes. A flat opening keeps anything wider than its window, so terrace edges and bank
crests stay. Without the height and building rules it had also flattened building margin bands and
narrow wall tops up to 4 m tall. The original reference is taken after lowering.
`ScanGroundCleaner.Clean` then only adjusts small connected residuals within a height allowance, away
from accepted breaklines. The original and filtered references remain available, with changed-node
count and maximum displacement. This does not smooth away sustained terrace steps.

Scan recovery uses interval mode (`TerrainRecoveryPolicy.ContourInterval`, `.Interval.cs`); other terrain
recovery callers keep their behaviour. `ScanRecoveryRegion` decides what is measured how. `Region` is
smooth soft ground, contoured at the terrain accuracy; contours are clipped to the whole soft outline, so
they run through rough patches. `SecondaryRegion` is paving plus rough soft ground (`RoughGround`: within
about half a ground spacing of a node departing `RoughGroundHeight` from its neighbourhood mean), measured
at the paved accuracy with spot heights only. On the reference scan 62% of spot heights had sat on the
roughest 19% of ground; relaxing it cut spots from 1,225 to 671. Error-driven selection (`.Informative.cs`)
left slopes covered in short contour fragments; interval contours met both tolerances in about 25 s.
Measured rails and paved outlines stay in every rebuild. Hitting the cap or making no progress never
claims tolerance success. Unknown areas are excluded from accuracy claims.
All lengths use the input coordinate unit; the host converts physical defaults.
Row-indexed outline edges serve containment and segment clipping, so recovery does not end on the
obsolete raster stairs after the visible boundary has been cleaned.

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

`ScanWalls` finds narrow measured rise bands after removing the surrounding ground trend, rejecting
broad ramps, short fragments and unobserved endpoints. Its coupled alignment fitter replaces the old
fraction-of-cell independent fits. Light rail samples enter `TerrainCurveRecovery.ConstrainReference`
before recovery and remain mandatory through every reconstruction.

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
