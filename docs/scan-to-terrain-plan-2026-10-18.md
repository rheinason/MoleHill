# Scan to terrain: plan (2026-10-18)

**Status:** Phases 1–3 implemented; validation and measured limits below. Phases 4–5 remain planned.
Backlog entry **B26**. Supersedes nothing; builds on B25 (`mhTerrainToCurves`).

## Phase 3 implementation and validation (2026-10-18)

`ScanMeshReader` preserves coincident vertices carrying different UVs and captures the diffuse bitmap
on the UI thread; the pure raster samples each winning top triangle through its face-corner UVs.
Vertex colour averages are the fallback. No colour means no paved/soft split; measured narrow steps
in the kerb height range can still be traced from observed geometry. Filled ground is excluded from
that evidence; a synthetic regression checks a removed object's inferred step is not emitted as a kerb.

`ScanSurfaceAreas` classifies observed ground using normalised excess green, saturation, slope and
roughness, retaining paved islands and building-sized unknown holes. Sub-minimum-area components are
omitted and smaller enclosed holes are closed in drawing masks only; the observed-ground flags and
raw classes remain unchanged. Fitted closed area boundaries follow the clean wall-constrained reference.
`ScanRoadEdges` measures trend-corrected profiles at colour edges. Persistent steps of 0.06–0.3 m within
0.6 m produce paired kerb curves; otherwise the run becomes one draped road edge. Output carries
measured step height and evidence share. This detects measured steps, not the semantic identity of
every kerb; dark materials, coloured paving and vegetation remain classification limitations.

`ScanTerrainPipeline` prepares walls before areas, then adds paved boundaries and paired kerbs to the
fixed reference. `ScanSoftRegion` clips contours exactly to soft cells, including holes. Spots and
deviation evaluation stay in the same region. A pick retains only the clipped fragment nearest its
error point; adding every disconnected lawn fragment at that level wasted the curve budget. Contours
are fitted before reconstruction so reported error describes their sampled geometry. A corner that
cannot be smoothed inside the mask retains its clipped line as a degree-one NURBS. The ordinary
`mhTerrainToCurves` defaults retain their existing full-terrain polyline behavior.

New interim command options: `Surfaces`, `RoadEdges`, `GreenThreshold`, `PavingSaturation`, `KerbMin`
and `KerbMax`. Both output families default on; the floating panel remains Phase 5.

Reference Core measurement with the supplied diffuse atlas:

| Measure | Phase 3 result |
|---|---|
| Paved / soft drawing mask | 10,970.8 / 20,895.7 m² |
| Closed area loops | 495, including holes |
| Walls / measured kerb stretches / single road-edge stretches | 36 / 1 / 247 |
| Final reference | 63,364 vertices |
| 300 contour budget, 0.25 m target | 236 spots; 0.8938 m miss; `CurveBudgetSpent` |
| 2,000 contour budget, same settings | Stops at 1,219 contours, 453 spots; 0.2498 m miss; `ToleranceSatisfied` |

The larger-budget recipe uses automatic raster spacing, 1 m ground spacing, `PlanTolerance=0.5`,
`Tolerance=0.25`, `RidgesValleys=No`, walls/surfaces/road edges enabled, and wall fitting plan tolerance
0.25 m in the Core measurement (the command shares its main plan tolerance with wall fitting, so its
precise reference can differ). Accuracy is vertex-sampled against inferred soft ground, not surveyed
truth or a full-site claim. Unknown and paved ground are
excluded. The diagnostic larger-budget run took about 17 s for the surface pipeline and 112 s for
contour recovery; these are not hosted performance acceptance figures.

Thirteen surface tests cover shadowed grass/roads, top-face UV sampling, removed roofs, small drawing
holes, paved islands, exact clipping, measured and disappearing kerbs, plain/sloping colour edges,
geometry-only steps in metres/millimetres, and fitted recovery over disconnected lawns. The additional
route regression checks exact endpoints at survey coordinates. Full suites: Core 1,645 passed;
Rhino 1,450 passed / 175 skipped; Grasshopper 56 passed / 18 skipped. Native skips are not acceptance.
The normal plugin build passed with zero warnings/errors.

Live synthetic validation used exact Debug plugin MVID `68fadd2e-22a1-472b-b544-0dae906d8221`.
A textured road across a lawn mound, with half the bitmap shadowed, produced three closed NURBS area
boundaries, four measured kerb pairs, two single road edges, one soft contour and one spot. All 17
outputs (including border and optional check mesh) were valid and shared one group. Native contour
samples and spots stayed outside paving; kerb metadata satisfied the height and evidence ranges.
Reported residual was 0.1369 m at a 0.25 m target. Scaling the source to millimetres retained output
counts and physical residual. A single command Undo restored the source-only scene. A separate native
seam fixture retained six vertices rather than merging to four, with distinct face UVs and the expected
green/grey bitmap samples. The curve-only viewport capture was visually reviewed.

Live reference validation preserved OBJ UV seams (633,522 native vertices, 766,558 triangles after
culling 14 degenerate faces) and read the actual 8192² atlas. With a fixed 0.276 m raster, 1 m requested
ground spacing and shared 0.5 m plan tolerance, it emitted 500 closed area NURBS, 33 wall stretches,
226 single road edges, no confirmed kerb, 300 contour NURBS and 240 spots. All 1,334 outputs were valid
and grouped; the source remained. All 30,300 native contour samples and every spot were inside a
separately recomputed soft mask. The residual was 0.9506 m, budget spent. These settings and the culled
native triangulation differ from the Core run; kerbs near the scan's resolution limit are sensitive
to sampling. The viewport capture was inspected.

After excluding inferred heights from geometry-only kerb evidence, the final Core MVID
`aa2ab8dd-10ff-406a-bad1-b2b319dd6811` was rebuilt and loaded in another fresh disposable Rhino.
The same road fixture without colour produced two valid measured kerb pairs, two contours and a spot,
with no invented area classes; residual was 0.15 m at a 0.25 m target. All owned sessions were closed.

Evidence and diagnostic logs: `.artifacts/scan-phase3/`. Reproduce reference classification with
`MOLEHILL_SCAN_OBJ`, `MOLEHILL_SCAN_TEXTURE_RGB` (binary int32 width/height then top-down RGB bytes),
`MOLEHILL_SCAN_MAX_CONTOURS=2000`, and optional `MOLEHILL_SCAN_IMAGES`, then
`dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Measure_ReferenceSurfaces --logger "console;verbosity=detailed"`.
The build and full-suite commands are the same as Phase 1 below. No package was published.

## Phase 2 implementation and validation (2026-10-18)

`ScanWalls` measures height changes after subtracting the trend outside each transition. Adjacent rises
over up to 1 m form one wall band, so a 1.5 m wall rasterised across two cells is one full-height wall,
not two half-height ones. Directed grid edges chain the bands, splitting branches and missing data;
short stretches and fits outside the contouring footprint or fitting tolerances are discarded.
Top and toe use the measured outer ground nodes, fitted through `RouteCurveFitter` to light curves.
Their smoothing is bounded by rail clearance, so independently fitted top/toe curves stay on their sides
of the step. Only measured narrow steps are emitted: broad ramps remain ground.

`Walls=Yes` is the command default; `WallHeight` (0.6 m) and `WallLength` (3 m) are physical defaults
converted to document units. Output is paired NURBS curves on `Scan {source}::Walls and edges`, carrying
measured mean step and plan length in user text, grouped and undone with all other output.
`Walls=No` retains Phase 1 recovery. These are geometry-derived steps, not a semantic identification of
every built retaining wall. Curves can stop at a branch or where the ground evidence is missing.

Light polyline samples of the fitted curves are inserted into the contouring reference, keeping its
original border. Coarse averages on those lines are excluded: otherwise the triangulator would split
a rail through the old averaged height and create a sawtooth edge. `TerrainCurveRecovery.FixedBreaklines`
holds the same rails in every rebuild and in ridge/valley trials. This preserves the measured step
instead of trying to fit both it and the earlier coarse smoothing at once. `TerrainCurveRecovery` is now
split into its recovery implementation and `TerrainCurveRecovery.Breaklines.cs` reference preparation.

Validation: 12 wall regression cases cover model-unit scaling, both directions, horizontal/diagonal and
closed walls, planes/hills/spikes, gaps, multi-cell steps, invalid rails and reconstruction. Heights are
checked between rail vertices as well as at them; the prepared reference retains one simple border.
The focused wall/recovery run passed 21 cases; the plugin build passed with zero warnings/errors.
The full managed Core/Rhino/Grasshopper suites were also run (native skips are not acceptance).
Totals: Core 1,630 passed; Rhino 1,450 passed / 175 skipped; Grasshopper 56 passed / 18 skipped.

In disposable Rhino, exact Debug plugin MVID `97e3453c-40bd-4d9a-8afd-e2b7b186fc6a`, two 1.5 m terrace
steps plus a house produced two wall pairs (four light NURBS), two contours, one border and no spots.
Residual was 0.225 m against the constrained reference at a 0.25 m target. Native rays at 19 stations
along each output rail agreed with the check mesh within 0.01 m; layers, grouping and a single Undo
restoring the source-only document passed. Evidence is under `.artifacts/scan-phase2/`.
`Walls=No` was also run live: it wrote a separate root with no new wall curves and preserved the prior
wall output. The viewport capture was visually reviewed, and the owned Rhino was closed afterward.

Reference scan, current Phase 2 settings: 36 wall stretches, 162.8 m total paired centreline length;
48,417 constrained reference vertices; 300 contours, 508 spots and one border, with a 0.524 m residual
and `CurveBudgetSpent` at a 0.25 m target. This is measured against the new wall-constrained reference;
it is not a ground-truth survey comparison. No tolerance claim is made for the default budget.
Diagnostic wall extraction was about 0.93 s and recovery about 26 s on that run, not hosted performance
acceptance. Set `MOLEHILL_SCAN_WALLS=1` in the reference measurement test to reproduce Phase 2.

Commands used: the Phase 1 build and managed-suite commands below, plus
`dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ScanWallsTests|FullyQualifiedName~TerrainCurveRecoveryTests" -v minimal`.

## Phase 1 implementation and validation (2026-10-18)

`MoleHill.Core.Scan` now supplies triangle-interior top/lowest rasters, a progressive morphological
ground filter with raised-perimeter terrace preservation, local median fill anchors, harmonic fill,
and a coarser mesh with simple border loops. `mhScanToTerrain` reads a Z-up mesh and shares contour
recovery and output writing with `mhTerrainToCurves`. Each run writes grouped editable geometry on
`Scan {source}::Border`, `Contours`, `Spot heights`, and optional `Ridges and valleys`, under a fresh
root, in one Undo step. `AddGroundMesh=Yes` adds a check mesh; it is off by default. Inputs and defaults
respect document units. Esc cancels the ground worker before any output or stops recovery and keeps
the curves found so far.

Phase 1 uses command-line options. The requested floating panel, staged preview and Keep workflow
remain Phase 5; the full panel described below is the intended design. Colour, point clouds, walls,
road edges and buildings are not implemented by Phase 1. Recovered contours are currently simplified
polylines, as in B25; fitting them as light curves remains open.

Reference scan rerun on 2026-10-18 (diagnostic measurement, not a hosted performance comparison):

| Measure | Phase 1 result |
|---|---|
| Input | 383,128 vertices, 766,572 triangles |
| Raster | 813 × 944 nodes, automatic 0.276 m spacing |
| Raised regions ≥20 m² removed | 102 (includes non-building objects) |
| Regions retained as ground steps | 239 |
| Ground footprint | 58,480 m²; 34.4% filled |
| Contouring mesh | 47,705 vertices at the rounded raster multiple (~1.1 m) |
| 200-contour budget, 0.25 m target | 452 spots, 0.738 m worst miss, `CurveBudgetSpent` |
| Command default 300-contour budget, 0.25 m target | 488 spots, 0.513 m worst miss, `CurveBudgetSpent` |
| 1,000-contour budget, same plan tolerance | Stops at 753 contours and 703 spots; 0.262 m worst miss, `NoProgress` |
| 1,000-contour budget, 0.1 m plan tolerance | 665 contours and 633 spots; ≤0.25 m worst miss, `ToleranceSatisfied` |

**The reference scan does not meet 0.25 m at the default curve budget.** This is a reconstruction error
against the inferred ground mesh, not a measurement of ground truth beneath buildings. Smooth filling
cannot infer hidden terrace discontinuities; houses against terrace steps and remaining wall-foot
anchors are known limits. The command reports the residual and termination rather than claiming a
tolerance guarantee. Raising the budget alone to 1,000 did not meet the target; recovery exhausted its
useful candidates before that budget was spent.

**A measured recipe does meet the target:** `Tolerance=0.25`, `PlanTolerance=0.1`, `MaxContours=1000`,
`RidgesValleys=No`, automatic raster cell and 1 m requested ground spacing. The pure Core reference run
stopped at 665 contours and 633 spots with `ToleranceSatisfied` (≤0.25 m at every ground-mesh vertex).
It took about 73 seconds for recovery in this diagnostic run; this is not a hosted performance claim.
The command retains its faster 300-contour default and reports its actual residual. The tighter recipe
was verified in Core; the live native reference test above used the default budget.

Synthetic tests cover a terraced hillside with gable houses and trees, a 50% plain slope, planar fill,
sparse overlapping triangles in both windings, overflow/empty inputs, planar coarse-mesh borders and
recovery through a simply bordered mesh. The synthetic house fill error is asserted below 0.35 m;
the separate linear-hole fill test is asserted below 0.001 coordinate units.

Live Rhino validation used the exact Debug plugin and the registered command: synthetic geometry in
metres and millimetres, output layers/grouping, opt-in check mesh, distinct rerun roots, one-command
Undo and cancellation at the options prompt passed. The reference native mesh needed 14 degenerate
faces culled before document insertion; that run emitted 300 contours, one border and 487 spots,
with a 0.506 m reported residual and budget-spent termination. All output geometry was valid.
The native and pure Core figures differ slightly because their source triangulations differ.
See [live testing](rhino-live-testing.md) for the build identity and evidence.

Validation commands (all passed; native skips do not count as native execution):

- `dotnet build src/MoleHill.Rhino/MoleHill.Rhino.csproj --no-restore -p:CopyRetryCount=0 -v minimal`
  — zero warnings/errors.
- `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --no-restore --logger "console;verbosity=quiet"`
  — full managed Core suite. The final scan regressions were also run with
  `--filter FullyQualifiedName~ScanGroundTests -v minimal` (12 tests).
- `dotnet test tests/MoleHill.Rhino.Tests/MoleHill.Rhino.Tests.csproj --no-restore -v minimal -p:NoWarn=CA1416`
  — 1,450 passed, 175 native tests skipped; source exclusion fixed for the new command service.
- `dotnet test tests/MoleHill.Grasshopper.Tests/MoleHill.Grasshopper.Tests.csproj --no-restore --logger "console;verbosity=quiet"`
  — 56 passed, 18 native tests skipped.

The disposable live Rhino was closed through its owning client after testing. No package was published.

Reproduce the reference measurement with `MOLEHILL_SCAN_OBJ` set to the local Z-up OBJ (metres), then
`dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --no-restore --filter FullyQualifiedName~ScanGroundMeasurementTests --logger "console;verbosity=detailed"`.
`MOLEHILL_SCAN_MAX_CONTOURS` defaults to 300; set it to 200 for the earlier comparison.
For the tolerance-satisfying Core run, set `MOLEHILL_SCAN_MAX_CONTOURS=1000` and
`MOLEHILL_SCAN_PLAN_TOLERANCE=0.1` (the latter defaults to 0.5 m).
`MOLEHILL_SCAN_IMAGES` writes north-up surface/ground hillshades and the ground/fill classification.
The real scan is not bundled, and ordinary test execution without that variable does not measure it.

## Decisions (user, 2026-10-18)

1. **Buildings go straight to roof shapes.** LOD1 boxes are not a deliverable; the extruded footprint is
   only the fallback for a building no roof model fits, and is marked as such.
2. **Kerbs: a kerb where it is clear, otherwise a single breakline.** Where the scan shows the step, emit a
   kerb (top and bottom lines at the measured kerb height); where only the colour edge shows, emit one
   breakline along the paved edge at ground height. Both are output, not suggestions to review.
3. **Output is curves only: contours and breaklines.** The cleaned ground mesh is an internal step, not an
   output (it may be offered as a debug toggle).
4. **Input is mostly a textured mesh like the reference; sometimes a point cloud.** A point cloud is
   welcome as an input because it is denser (and usually classified and coloured), which is what kerbs need.

## The problem

A drone photogrammetry mesh of a site (houses, roads, gardens, walls on a hillside) is the most common
starting point, and today it is a dead end. The user's manual routine is to draw the roads and kerbs from
Top view and drape them, guess the kerbs "a little by eye", then add contours where the green areas are.
`mhTerrainToCurves` refuses such a mesh outright: its border is not a set of simple loops, because the
houses are in it.

The goal is one command that takes the scan and gives back, on layers:

1. **Buildings**: simplified closed volumes with the right roof shapes (flat, shed, gable, hip and
   combinations), standing on the ground.
2. **Walls and terrace edges**: the steps in the ground, as top and toe breaklines.
3. **Kerbs and road edges**: a kerb (two lines) where the step is clear, otherwise one breakline along the
   paved edge.
4. **Paved and soft areas**: closed boundaries from the photo colours.
5. **Contours and spot heights**: the fewest that rebuild the soft areas within a tolerance (B25),
   with the breaklines above as constraints, so contours go where the green is.

The ground surface with houses, cars and bushes removed is computed (everything above depends on it)
but not output.

All of it is editable Rhino geometry, ready to become the sources of a MoleHill terrain.

## What the reference scan shows (measured)

Reference: `Grønalid21 - simp.obj` (Tórshavn), with its diffuse atlas
`3D_MODEL_Gronalid_21_Torshavn_u0_v0_diffuse.jpg`. Not in the repository (size, and the data is not ours);
the measurements below come from `tools/scan-explore.py` (numpy and scipy, about 3 s for the whole
mesh), which prints them and writes the images described.

| Measure | Value |
|---|---|
| Mesh | 383,128 vertices, 766,572 triangles, one object, Z up |
| Extent | about 225 x 261 m plan, 66 m relief |
| Density | about 13 triangles per m² overall; about 7 vertices per m² on flat ground ("simp") |
| Faces | 64% face up, 33% are walls (steeper than 60°), 3% face down |
| Ground slope | median 8°, 90th percentile 20° |
| Texture | one 8192 x 8192 "lightbox" atlas; every triangle has UVs |

**The atlas is useful.** Sampling it at each triangle's UVs and laying the colours out in plan gives a
proper orthophoto: grey roads, green lawns, coloured roofs. The atlas layout does not matter; the UVs carry
the mapping. Colour is the strongest signal for paved against soft ground and helps reject trees.

**Buildings separate cleanly.** A first-cut progressive morphological ground filter (0.5 m cells, windows
of 1 to 24 m, slope allowance 0.6) finds 235 raised components, 99 of them at least 20 m² (11,070 m² in all,
median 6.8 m tall, middle sizes 27/108/192 m²), and they land on the houses in the orthophoto. Roof
facets cluster by aspect: the eight largest show 2 dominant facets (gables), 4 to 6 (hips, cross gables,
L-plans) and one 29%-flat roof. So parametric roof fitting is realistic for most of the stock.

**The ground model needs real work.** With removed houses filled by nearest ground and smoothed, the
first-cut ground is lumpy where houses stood. That needs a better filter and a proper fill (below).

**Walls and terrace edges are clear; kerbs mostly are not.** Two road cross-sections at full mesh
resolution: a 3 m step 4.5 m off one road edge and a 1.5 to 2 m wall beside another read unmistakably. A
0.15 to 0.2 m kerb shows on one side of one road; the other side of the same road rises 0.5 m over 4 m
with no step at all. At 7 vertices per m², 10 to 15 cm kerbs are at the resolution limit of this
mesh. Kerbs therefore come from the **colour boundary** between paved and other ground, confirmed by the
geometry where it shows, and are offered as suggestions, which is how the user places them today.

## Pipeline

All analysis in Core (a new `MoleHill.Core.Scan` namespace: pure, unit-free, testable on synthetic
scenes); reading the Rhino mesh, its material texture and writing layers in `MoleHill.Rhino`.

### S0. Read the scan

Host: take the picked mesh and, when its material has a diffuse bitmap, sample it at every face's texture
coordinates (Rhino keeps an OBJ's UVs and material). Hand Core flat arrays: XYZ vertices, faces, and an
optional RGB per face. Works without colour, with fewer results (no paved/soft split, kerbs from geometry
only).

**Point clouds** (Rhino `PointCloud`, from LAS/E57/XYZ imports) are the second input: points with optional
colour and, when the source classified them, a ground/building/vegetation class. They go straight into the
rasters of S1 (no triangles to rasterise), at a finer cell where the density allows: a typical drone cloud
has tens to hundreds of points per m² against this mesh's 7 vertices, which is what makes 10 to 15 cm
kerbs measurable. A supplied ground class short-cuts S2's filter (used when present, checked against it).

### S1. Rasters

Rasterise the triangles (not the vertices; the vertex-only first cut left 40% of 0.5 m cells empty) into a
top-surface model (DSM) at 0.25 m, with per-cell colour of the top face, its normal, and the lowest
surface. Cell size from the mesh density, user-overridable.

### S2. Ground

Classify each cell ground or not, then rebuild the ground under what is not.

- **Filter.** Cloth simulation (an inverted cloth dropped onto the inverted DSM, a well-tested method
  robust on slopes) or the progressive morphological filter with a slope-adaptive threshold. Pick by
  measurement on the reference scan and synthetic slopes. Colour helps: roof colours and dark greens on
  tall objects are not ground.
- **Fill.** Harmonic (Laplace) interpolation across removed regions, from the ground around them, not
  nearest-neighbour, so a removed house leaves a smooth patch, not a lump.
- **Result.** A ground model (grid) used by every later stage; not written to the document (the user
  wants curves), except behind a debug toggle. Report the share of the site that was filled.

### S3. Buildings

Per raised component that passes planarity and size checks (trees and bushes fail planarity and are
usually green):

1. **Footprint.** The component's outline, regularised: dominant orientation from the roof edges and
   walls, edges snapped to that orientation and its perpendicular where within an angle, short jogs
   removed. Most of this stock is rectangular or L-shaped.
2. **Roof planes.** Region growing on the mesh's up-facing faces inside the footprint (normals within a
   few degrees, plane distance within a tolerance), merging small regions.
3. **Roof model.** Fit flat, shed, gable, hip, half-hip and cross-gable models to each rectangle of the
   footprint by least squares against the roof planes, choosing by error and facet count. For roofs no
   template fits, intersect the fitted planes over the footprint (a general plane arrangement). Only when
   both fail is the footprint extruded to the median roof height, and the building is flagged in its user
   text so it can be found and fixed.
4. **Solid.** A closed Brep from ground (lowest ground along the footprint) to the roof, one per building,
   with eave, ridge and ground heights in user text.

Output on a `Buildings` layer. The buildings are context, not terrain inputs: they show the scheme in
place, and their footprints can later become pads or holes in a MoleHill terrain (a separate decision).

### S4. Walls and terrace edges

On the ground model: steps much larger than the local slope explains (the exploration's "unexplained
step" measure) chained along their length, kept when long and straight enough to be built (bushes give
short, blotchy responses), and each emitted as a pair of lines, top and toe, at the ground's heights,
simplified with the route curve fitter (few control points). Layer `Walls and edges`. These are hard
breaklines in MoleHill's sense.

### S5. Paved and soft areas

From the per-cell colour (excess green for vegetation; low saturation for asphalt and concrete) cleaned
with the geometry (paved areas are smooth and gently cross-fallen; lawns are rougher). Shadows are the
known trap (dark asphalt against dark green in shade): use saturation and the local geometry, not
brightness alone. Output closed boundaries on `Paved areas` and `Soft areas`, simplified to few control
points.

### S6. Kerbs and road edges

Candidates: the boundaries of paved areas, straightened along the road's direction, then measured across
at intervals for a step within about a metre.

- **Clear step** (a step of kerb height, 0.06 to 0.3 m, found along most of a stretch): a **kerb**, two lines
  (bottom on the paved side, top on the other) at the measured heights. Layer `Kerbs`.
- **No clear step** (colour edge only, or a step found too rarely): **one breakline** along the paved edge
  at ground height. Layer `Road edges`.

A stretch can switch between the two along its length. Each line carries its evidence in user text (step
height measured, share of stations that found it), so a doubtful one is easy to spot. On the reference mesh
most edges will come out as single breaklines; a point cloud should turn many into kerbs.

### S7. Contours and spots

B25's recovery, restricted to the soft areas, with the walls, kerbs and paved boundaries as fixed
breaklines, so contours go where the green is and the rest is described by edges. Contours and spot
heights on their own layers. Contours fitted as light curves (the route fitter), not polylines.

## Command and panel

`mhScanToTerrain` (mesh or point cloud), following the user's stated preference (2026-10-17): every
setting on one floating panel, the command line only takes Enter and Esc. The panel has one row per output
(toggle, preview, its main setting): ground cell size and filter strength; building minimum size; wall
minimum height; kerb height range; colour thresholds with a live preview of the paved/soft split; contour
tolerance.
Results preview in the viewport as they are computed, stage by stage; Keep writes the layers under
`Scan {source}::...` (a fresh root per run, as Terrain to Curves now does).

## Phasing

Re-phased after the decisions: the first deliverable is curves, so ground and contours come together.

| Phase | Delivers | Main risk | Status |
|---|---|---|---|
| 1 | S0, S1, S2, S7: contours and spot heights of the ground from a scan (mesh), houses removed | the filter on steep, terraced ground | Checked; commit `fe69846` |
| 2 | S4 walls and terrace edges, as breaklines the contours respect | chaining noisy steps into clean lines | Implemented and checked |
| 3 | S5 + S6 paved and soft areas, kerbs and single road-edge breaklines; contours only in soft areas | shadows in the photo; kerbs below resolution | Implemented and checked |
| 4 | S3 buildings with roof shapes | trees passing as buildings; roof segmentation on a smoothed mesh | Planned |
| 5 | Point cloud input (S0), and the full panel | reading large clouds quickly | Planned |

Phase 1 alone replaces "trace the ground by hand"; phases 2 and 3 replace the draped breaklines. Buildings
come fourth because nothing else depends on them (the ground filter only needs to know where they are,
which phase 1 already finds).

## Validation

- **Synthetic scenes in Core tests**: a hillside with gable and hip houses (known footprints, ridges,
  eaves), a road with 0.15 m kerbs and a 1.5 m terrace wall, generated as meshes with and without colour,
  at the reference scan's density. Assert footprint IoU, ridge height and orientation, wall line
  position, ground error under removed houses.
- **The reference scan**: measured figures recorded in this document as each phase lands (buildings
  found, ground filled, walls traced, time), and a live Rhino run.
- **Speed**: the reference scan should complete each stage in seconds; the exploration's numpy version
  took 3 s end to end at 0.5 m.

## Still open

- Whether building footprints should also feed the terrain (as pads, or as holes the terrain is cut
  around). Not needed before phase 4.
- A point cloud of the reference site (or any site that also has a mesh) would let phase 3 be measured on
  both inputs; none is in hand yet.
