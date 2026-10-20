# Scan terrain output review — 2026-10-18

Status: review complete. The area stage and recovery loop were rebuilt on 2026-10-20 (see the end); other corrections remain open.

The target is editable terrain input: straight runs stay straight, kerbs and retaining walls have
credible alignments, and raster steps do not become design corners. Buildings are outside this review.

## Reference and reproduction

Input: `Grønalid21 - simp.obj` and `3D_MODEL_Gronalid_21_Torshavn_u0_v0_diffuse.jpg` in the maintainer's
`Desktop/TEMP EXPORT`. The original OBJ has 383,128 vertices and 766,572 triangles. The diagnostic
reader retains face-corner texture coordinates and uses the previously captured RGB bytes of that
texture. This is the unculled Core input, not the native mesh with fourteen degenerate faces removed.

The fresh run used the current panel's physical defaults in metres: automatic raster spacing,
1 m ground spacing, 0.5 m overall plan tolerance, 0.25 m recovery height tolerance, 10 m² minimum
surface patch, walls at least 0.6 m high / 3 m long with 0.1 m profile tolerance and 1 m maximum width,
and roads at 1 m plan / 0.2 m height tolerance. Kerb evidence uses 0.06–0.3 m steps, 0.6 m maximum
width and 0.015 m profile tolerance. Buildings were disabled; landform lines were disabled for the
300-contour diagnostic. These stages do not depend on building output.

Local evidence is under `.artifacts/scan-terrain-review/` (ignored, not a committed fixture):

- `Review.csproj`, `Program.cs`: standalone Core runner; `plot.py`, `metrics.py`: drawings and counts.
- `before/terrain.json`: fitted pieces, controls, sampled rails, boundaries and raster placement.
- `before/quality.json`: complexity counts; `before/contours.json`: reconstruction results.
- `before/orthophoto.ppm`, `before/overview.png`, `before/wall-details.png`: texture overlays in World XY.

Run from the repository root after restoring the diagnostic project:

```powershell
dotnet run --project .artifacts/scan-terrain-review/Review.csproj --no-restore -p:UseAppHost=false -- .artifacts/scan-terrain-review/before --contours
```

This measures actual Core output on the reference scan. It does not establish surveyed accuracy,
hosted performance, or native Rhino Keep/Undo acceptance. No disposable Rhino was launched in this
review. The host's `RouteCurveBuilder.FromFitted` converts these pieces to NURBS without a further
geometry-cleanup pass.

## Findings

### 1. Retaining-wall rails preserve raster corners and can run backwards

**High priority.** The run emits 36 wall pairs. Their 72 rails contain 1,573 joined controls across
500 spline pieces; 430 pieces have endpoint chords shorter than 1 m. The median is 19 controls per
rail. Wall indices here are zero-based indices in `terrain.json`, not persistent identities.

Wall 10's top rail is about 7.85 m long with 58 controls and 19 pieces. Its plan deviation from the
endpoint chord is only about 0.25 m: a broadly straight stretch has become a staircase. More severe
examples have repeated returns and spurs: wall 30's top rail travels 7.29 m between endpoints only
2.23 m apart. The texture overlay does not support treating those returns as deliberate built corners.
Wall 24's top rail has excursions about 0.65 m from its endpoint chord while its toe is much simpler.

`ScanWalls` assembles top and toe from independently selected outer raster nodes
([rail assembly](../src/MoleHill.Core/Scan/ScanWalls.cs#L152)). Ordering dual-grid crossings does not
ensure either outer rail progresses along that alignment. The independent fits are then limited to
15% of a raster cell ([fitting](../src/MoleHill.Core/Scan/ScanWalls.cs#L167)): about 0.0415 m on this
0.2765 m grid, despite the requested 0.5 m plan tolerance. Pixel corners survive as sharp corners.
The generic route fitter splits at those corners before fitting. A height change can also retain an
otherwise unnecessary plan vertex because the initial thinning considers XYZ together.

Minimum length is checked on the resulting average rail travel. Backtracking can inflate that
measurement. Recheck supported length after alignment cleanup rather than allowing raster travel
to establish a credible long wall.

### 2. Surface boundaries remain too heavy to edit and also constrain the terrain

**High priority.** The 10 m² patch filter yields 157 area boundaries, but they contain 41,060 joined
controls across 13,073 pieces. Median controls per boundary: 97; maximum: 3,217. There are 6,473
pieces with endpoint chords shorter than 1 m. A large connected boundary can reasonably be complex;
these counts alone are not a reason to discard it. The overview nevertheless shows extensive small
teeth, indentations and detours around texture/vegetation detail.

`ScanBoundaryFitter` does separate initial plan thinning from elevation, but only the colour-road path
uses bounded plan denoising and a held-plan profile. Area boundaries use a second general XYZ fit
after draping ([fitter](../src/MoleHill.Core/Scan/ScanBoundaryFitter.cs#L43)). Their protection against
self-intersections can fall back to a separate spline for every draped segment
([fallback](../src/MoleHill.Core/Scan/ScanBoundaryFitter.cs#L63)). A valid closed result can therefore
still be extremely difficult to edit. This audit did not count how many loops used that fallback.

Every paved boundary is added to `FixedBreaklines`
([pipeline](../src/MoleHill.Core/Scan/ScanTerrainPipeline.cs#L95)). These are terrain inputs as well
as drawings. A display-only simplifier would leave the reconstruction carrying their defects.

### 3. Road edges are paving-class edges; kerb and arc recovery remain weak

**High priority for the requested result.** This run returns one measured kerb pair and 80 single
colour edges. Their bottom curves contain 4,357 joined controls, median 28 and maximum 511. The
single edges trace drives, parking and other paving as well as streets. They are not an inventory of
road kerbs. The one measured pair does not establish whether the site contains few kerbs or whether
the evidence detector misses them; that needs hand-reviewed reference stretches.

Colour edges use straight XY chords with independent height fitting. That keeps a chosen straight
segment straight, but does not identify circular arcs or infer a connected road alignment. Paired
kerbs use another independent XYZ fit capped at 10% of a cell
([kerb fitting](../src/MoleHill.Core/Scan/ScanRoadEdges.cs#L159)), so the wall-style raster problem
also exists in that path. There is no shared primitive fitter selecting long straights and arcs from
measured built-edge evidence. Raising road tolerance globally cannot solve road identity, junctions
or rail pairing, and can erase real corners.

### 4. Wall evidence does not distinguish observed ground from inferred fill

**High priority for trust in the result.** The wall stage receives only `ground.Ground`
([call](../src/MoleHill.Core/Scan/ScanTerrainPipeline.cs#L79)), including harmonic fill. It cannot
check `IsGround`, unlike geometry-only kerb tracing. A plausible step in interpolated ground can
therefore qualify as a measured wall. This is a code-path limitation; the review has not established
which individual reference walls depend on filled samples.

Wall acceptance should record support on both sides and distinguish observed sections from inferred
continuations. A complex chain without sustained support should be split or omitted, not polished
into a confident retaining-wall pair. Fit simplicity is evidence to consider, not permission to invent
a straight wall where the observations do not support one.

### 5. Default contour budget does not meet the requested height tolerance

The reference recovery returns 300 contours and 222 spot points, with `CurveBudgetSpent` and maximum
vertex residual **0.9651 m**, against **0.25 m** requested. Of the contours, 269 have fitted spline
geometry; 31 use the polyline fallback. All fitted contour controls are exactly at their contour's
level. Levelness is working; the reconstruction tolerance is unmet.

Do not use more contours to compensate for dubious fixed inputs. Correct the alignments and boundary
constraints first, then rerun recovery and report the remaining error honestly. This comparison is
against inferred ground, not independent survey truth.

## Correction order and acceptance criteria

1. **Establish a small reviewed reference set on this texture.** Mark a long straight wall, an angled
   wall, a real corner, a straight kerb, a curved kerb, a junction and a vegetation/occlusion edge.
   Record expected alignments and uncertain intervals. Synthetic fixtures alone cannot establish
   recognition quality on this scan.
2. **Fit built edges in plan before their profiles.** Use sustained evidence to prefer a line, then
   circular arc, with piece boundaries at supported real corners. Fit wall/kerb pairs together with
   common progression and signed separation, preventing independent rail crossings and returns.
   Apply physical plan tolerance to alignment and vertical tolerance to height separately. Extra
   height controls must stay on the accepted plan. Variable-height arc outputs need an explicit
   representation that preserves the arc in XY; an arbitrary XYZ cubic does not guarantee it.
3. **Gate wall and kerb claims on observations.** Preserve gaps and evidence metadata; reject
   unsupported returns and short cleaned runs. Do not bridge a building or vegetation gap just to
   reach minimum length. Complex genuine corners remain eligible when the observations support them.
4. **Clean the area partition coherently.** Remove pixel-scale spurs and slots using physical size,
   retaining supported islands, holes and narrow real features. Fit shared paved/soft interfaces
   once so their drawings and clipping agree. Natural soft outlines can remain smooth free curves;
   they should not be forced into the same architectural line/arc prior as a kerb.
5. **Rebuild from those same accepted curves.** Preview, Keep, region clipping and fixed breaklines
   must use the accepted geometry. Check open-rail self-intersections, top/toe crossings and order,
   closed-loop simplicity and holes, shared edges, footprint containment, and watertight 2.5D
   reconstruction. Then check residual and budget, with metre/millimetre equivalents.

Acceptance is a simpler supported alignment, not an arbitrary universal control-point cap. A straight
run must be collinear in XY even on rolling terrain; a supported bend must have a stable radius and
tangent joins; a real corner must remain a corner. A closed outline must retain its topology without
falling back to hundreds of tiny segments unnoticed. Review both curves and their reconstructed
terrain, followed by native preview/Keep/Undo checks on the exact built plugin.

## 2026-10-20 rebuild of the area stage and recovery loop

Diagnosis on the same reference: colour was one texel point-sampled per 0.28 m height node and classified
with hard thresholds, so the mask carried texel noise; outlines traced its cell edges and a general NURBS
fitter then followed that noise (all of the 16.8 s area stage). Loosening the outline height tolerance
from 0.05 m to 2 m changed controls only from 28.4k to 25.3k: the plan shape was the cost. Informative
recovery rebuilt the whole terrain for every candidate and prune trial, with 22.6k paved-outline vertices
held as fixed breaklines; the run had reached about round 20 and was still 1.2 m out when stopped after 25 minutes.

Replaced: fine colour lattice (4x) with smoothed soft class scores, direct outline construction (held
straights and arcs for paved edges, one quadratic spline for natural edges), area outlines as drawings
only (no longer fixed breaklines), kerb-only road tracing while outlines are drawn, and local windowed
recovery trials confirmed by one global rebuild per round.

| Measure (Review runner, Release) | Before | After |
|---|---|---|
| Area stage | 16.8 s | 2.6 s |
| Pipeline before recovery | 25 s | 7 s |
| Area outline controls (paved + soft) | 28,371 | 11,235 |
| Recovery | over 25 min, unfinished | 3.3 min, NoProgress |
| Recovery residual (target 0.25 m) | 1.2 m when stopped | 0.785 m (original reference 0.781 m) |

The tolerance is still not met: 694 contours and 1,217 spots leave 112 m² over 0.25 m. Where the remaining
misses sit (steps, walls not traced, or fill edges) has not been established.

### Native check and editability, 2026-10-20

Release RHP `02cde14b-d022-4a53-8f95-e842f11b4407` / Core `8945d7d5-8fb5-49d1-bf9c-87cd8ce8d9a8` in a disposable
slot, UV-preserving reference, all outputs on, 300 preferred contours, 2,000 cap, 0.25 m accuracy. Preview was ready
in 530 s: ridges/valleys Auto runs the whole recovery twice (with and without landform lines). Keep wrote 2,136
valid grouped objects; one Undo of `mhScanToTerrain` restored the source alone. Evidence: `.artifacts/scan-raze/`.

| Layer | Curves | Controls (median / max) | Notes |
|---|---|---|---|
| Contours | 688 | 11,634 (13 / 132) | 0.69 m per control, 3,332 G1 kinks, 14 self-intersecting |
| Spot heights | 1,233 points | | |
| Paved areas | 67 closed | 6,377 (37 / 867) | 3,000 G1 corners |
| Soft areas | 74 closed | 8,397 (31 / 3,667) | 2,229 G1 corners (junction seams) |
| Walls | 20 | 104 (5 / 9) | |

Findings:

- Outlines are acceptable drawings. Error-driven contours are not: recovery chases individual misses and keeps the
  fragment nearest each, so slopes fill with short broken contours and over a thousand spots.
- Making paved outlines drawings left paved ground with no recovered inputs while accuracy was measured on soft
  ground only: about half of the paved reference vertices were more than 0.25 m out, up to 4 m. The pipeline region is
  now paved plus soft. Error-driven recovery then spends the 2,000-input cap at 0.53 m (240 vertices out of tolerance).
- Regular-interval contours from the same reference, thinned at 0.15 m, with spot heights added at the worst miss of
  each separated error region, meet 0.25 m at every paved and soft vertex in under one second: 579 contours (pieces
  under 4 m dropped) plus 1,009 spots at a 0.5 m interval; 285 contours plus 2,491 spots at 1 m. This was measured in
  a scratch runner without region clipping, so its contours also crossed unknown ground, and without spline fitting.

### Split representation, 2026-10-20

Following the owner's approach (trace edges, simplify, drape; contour the green areas), scan recovery now:
paved outlines are draped breaklines again; soft ground is contoured at a Contour interval (0.5 m) with
redundant contours removed; spot heights are added only where ground misses its own accuracy, 0.25 m on
smooth soft ground and Paved accuracy (0.5 m) on paving and on rough soft ground (local departure over
0.2 m: 62% of spots had sat on the roughest 19% of ground). Low raised objects (cars) enclosed mostly by
one class are filled in the drawing masks. Contours are single quadratic splines on their simplified
vertices where that holds the plan, vertical and region tolerances.

Measured alternatives that did not help on this scan: slope-break (bank top/toe) breaklines cut spots
only from about 2,840 to 2,620; bounded smoothing of the scan before contouring needed more spots; a
harmonic fill between contours instead of flat triangles needed more spots (2,242 against 1,870).

Native run, Release RHP `67355d2c-284d-466c-ae43-fa563d483e81` / Core `52e44731-d34e-4c13-bcc1-2bde89eae167`,
all outputs on: preview ready in 57 s (was 530 s). Both tolerances met: 704 contours and 724 spots; smooth
soft ground worst 0.2498 m, paving and rough ground worst 0.4985 m. Keep wrote 1,666 valid grouped objects.

| Layer | Curves | Controls (median / max) | Notes |
|---|---|---|---|
| Contours | 704 | 7,906 (7 / 123) | 387 quadratic, 215 cubic, 102 polyline; 4 self-intersecting |
| Spot heights | 724 points | | |
| Paved areas (breaklines) | 66 closed | 6,002 (35 / 799) | 1 self-intersecting |
| Soft areas | 98 closed | 7,556 (17 / 4,016) | |
| Walls | 20 | 104 (5 / 9) | |

The earlier native screenshot of error-driven contours looked like hundreds of broken fragments largely
because the textured source mesh hides curves lying exactly on it; with the mesh lowered 1 m the same kind
of output reads as continuous lines (`.artifacts/scan-raze2/native_final_lowered.jpg`). Undo was not
re-verified on this build. Open: preview curves are occluded by the shown source; one paved outline and
four contours self-intersect; some cars against buildings or in pairs on verges still leave outline loops;
the largest soft outline carries 4,016 controls.

### Drawing quality and clutter, 2026-10-20 (later)

The owner's review of the outlines: "both too precise and not precise enough. They jiggle a lot but can
stick to corners", "all the straight lines that are getting ignored", and a car and its shadow outlined.
Causes and fixes, measured on the reference scan:

- Outlines were fitted raw at the plan tolerance, which a classified edge wanders by; corners were never
  found, so splines rounded them. Corners are now found at about 1-2 m and squared to the crossing of
  the edges either side; runs between are smoothed; long straights are held on soft outlines too.
- Cars and bushes smaller than the ground filter's minimum object were ground, and "rough ground is
  soft" drew them as soft areas. Standing ground now gives no class evidence, and enclosed pockets fill
  from the nearest class on each side, so a lawn or drive edge runs straight through a parked car.
- The same cars were in the reference terrain: paving took spot heights on car roofs, lawns contour
  rings round bushes. Small standing objects (to 2.5 m, not beside buildings) are lowered and filled
  harmonically. A first unrestricted version also flattened building margin bands and wall tops up to
  4 m; the height and building rules keep those.
- One soft outline ran 4.2 km round most gardens; soft ground is split at necks under 3 m (disc opening,
  as the streets are diagonal). Crossing or cusped outlines and contours fall back to lines.
- Preview: soft outlines ochre (were green like contours), paved heaviest, spots 6 px, source shown by
  default with buildings as crease lines over it (a shaded solid z-fought with the roof), one shared
  palette with the kept layers (walls, kerbs, buildings and spots had previewed in other colours).

Native run, RHP `e89d02bf-e9ad-4eb6-b09a-23f95ea1c9c8` / Core `623410d4-841f-4937-9c2e-86dcbc0bd391`
(`.artifacts/scan-raze4/`): preview ready in 18 s (57 s before this pass), both accuracies met, 588
contours and 582 spots (704 and 724 before). Kept 1,407 objects, none invalid, none self-intersecting;
one Undo removes them all.

| Layer | Curves | Editable points (median / max) |
|---|---|---|
| Contours | 588 | 6,052 (7 / 86) |
| Paved areas | 62 | 2,500 (18 / 335) |
| Soft areas | 101 | 3,984 (17 / 1,043) |

Open: a few spots remain round tightly parked rows, where the ground filter itself filled under removed
cars from car-height edges; the largest soft area (the connected open fields) is one outline of about
1,000 points.

## Footpaths and zone fills, 2026-10-20 (later)

A 0.6 m footpath across a lawn was outlined only in pieces. Its texels are plainly paved (saturation
about 0.1, excess green 0.02), but the 0.3 m evidence average mixed it with the lawn on both sides to a
paved score of 0.4-0.5 against a soft one of 0.5, and the pieces left fell under the 10 m2 minimum area.
Loosening the colour thresholds instead (saturation 0.35 or 0.40) added 1,250-1,650 m2 of bare soil
and shade and still missed the path. A third-width average now adds back paved strips the wide average
erased (narrower than four radii, at least 1.8 m2 counted whole): 251 m2 more paving on the reference
scan, nothing lost, every recovered piece a path or a strip beside a house. The top of the reported
path stays missing: the ground filter removed it with the bush beside it (0.5 m margin).

Areas are also kept as solid hatches below the source, for MoleHill zones, which now read a hatch's
outer loops. Native run, RHP `53c3a844-1a91-489e-b33e-093e8ac52b51` / Core
`49194f58-29f4-4e4e-810f-05d8f2d74cab` (`.artifacts/scan-raze5/`): preview 18.5 s, 582 contours and 587
spots, both accuracies met; 57 paved and 83 soft hatches at z -29 (source bottom -27.5), 9 nested ones a
centimetre higher, every outer loop a closed simple polyline. A terrain with a Paved and a Soft zone given
those two layers built with no diagnostics: Paved 11,426 m2, Soft 25,704 m2 (the hatches cover 11,981 and
26,109 m2 including ground outside the test terrain's spot hull).

The owner then noticed the upper (left) stretch of the same path still missing. The fine pass did find
it (paved score 0.8-0.9), but moss greens the path for about 4 m before the drive (score 0.4-0.5 against
0.5-0.55), so the stretch was an island of about 6.6 m2, and the 10 m2 minimum area culled it. Sparing
every kept strip from the minimum brought back 73 pieces (434 m2), mostly shadows and dark fringes beside
buildings, blobs at most three times longer than wide; the path is 6.3, wall-foot strips 5.5-15. Strips
at least five times longer than wide are now spared: 16 pieces come back, the path, a path in shrubs and
strips along walls and houses, plus two small doubtful fringe scraps. The mossy 4 m and the stretch the
ground filter removed with the bush stay unpaved. Native run, Core `a97ab8c4-ac29-4034-9e40-05ada69b2d2e`
(`.artifacts/scan-raze6/`): preview 18.5 s, 588 contours and 579 spots, both accuracies met.


Buildings, 2026-10-20 (composite roofs). Run offline with `tools/MoleHill.ScanReview --buildings` into
`.artifacts/scan-build2/` (`base` without composites, `final12` at the native twelve faces, `n0`-`n20`
for other budgets): fallbacks 33 to 15, area-weighted roof share within 0.3 m 70.7% to 90.2%, median RMS
0.458 to 0.263 m, 126 to 225 roof facets over 52 buildings. The building stage costs about 0.9 s more.
The first native run failed: 18 composites' faces did not join, because plane pairs of parts that only
touch were not cut and their steps twisted. With touching parts cut, all 52 buildings joined natively
(`.artifacts/scan-build3/`, Core `6a602b39-66d1-42a7-a8f1-7487c7fd3828`): 12 fallbacks, preview 18.0 s,
588 contours and 579 spots as before. Remaining: the twelve-face budget stops L-shaped and cross roofs
(#9, #27) that fit at fourteen; shaded green-less bushes beside #30 stay in its footprint.

Second reference, 2026-10-20: `Vardalag_6_Torshavn_LOD0.obj`, a full-detail RealityCapture export (7.6 M
triangles over about 320 x 365 m, ten times Grønalid's density). Run offline into `.artifacts/vardalag/`.
It found two faults Grønalid never reached. Wall rails split at a corner lifted each leg's heights
separately, and samples overshooting the corner were averaged into each leg's end, so the legs did not
meet and the run threw; range ends now take the shared vertex's height. A paving outline traced 6 cm
inside the scan's west edge left the reference border touching itself, so recovery gave no contours at all;
fixed breaklines within the plan tolerance of the border are now left out of the triangulation. Roof
normals are now averaged over about the footprint tolerance rather than one ring of triangles, with the
ridge guard at twice the plane angle: on 10 cm triangles a corrugated roof's normals scatter by ±20°.
Buildings rose from 51 to 71 of 134 raised candidates (Grønalid 52 to 53, its fit unchanged). Still open on
Vardalag: recovery spends the 2,000-input cap (545 contours, 1,455 spots) with green ground worst 0.34 m
against 0.25 and paving worst 3.86 m against 0.5; 53 candidates, mostly plain houses, still fail the 70%
planar share (85-96% of them lies within tolerance of their planes, trees 48-71%, but accepting on that
admitted scan-edge blobs on Grønalid); the building stage takes 17 s here.

Where Vardalag misses, 2026-10-20. Interval recovery now reports its misses as regions (worst vertex, signed miss,
area; a step missed on both sides is two regions) and the measured area its rebuild does not reach, which was
silently scored as no error (about 1 m² here). The twelve worst are all paved or rough ground, and all lie beside
an abrupt, fully observed rise of 2 to 7 m within 1.5 m, where the reference matches the raw ground to a few
centimetres. They are not caused by cleaning after the paving is draped: those steps are never lowered or cleaned.
Seen in the orthophoto, most are houses cut into the slope (dark slate, a white gutter along the drop) and a few are
compact tall blobs of 6 to 16 m² (small trees or sheds). The ground filter keeps both as ground. A region counts as
an object only when it stands up along half its edge, and a house whose uphill eave meets the hill counts as a terrace.
Anything under the 20 m² minimum object is ground too, and standing-object lowering refuses anything taller than
2.5 m. None of these sites is a building candidate, because candidates come from the filter's objects. Spot heights
then spend the input cap trying to rebuild walls of houses.

Tried and rejected: tracing those rises as walls. At 12 cm cells a smeared step is owned by alternating edges, so
the wall tracer's strict chains break into one-cell fragments. Chaining across a corner gap with a least-turn
continuation, and splitting chains the alignment rejects, raised Vardalag from 26 to 180 walls and Grønalid from
11 to 52. Paved worst stayed at 3.86 m and green worst went from 0.34 to 0.36 m. The wall stage took 65 s instead
of 5 s. Most of the rises are not walls, so the fix belongs in ground interpretation.

Raised regions parted at their drops, 2026-10-20. The filter judged each raised region whole, and a hillside
region runs on for hundreds of square metres: Vardalag's worst tree (8 m) sat in 574 m² raised along a third of its
edge, and was kept as ground with it. A region judged a step is now parted at every drop of 1.5 m within it, and
a part standing up along half its edge is an object. Regions that pass whole are not parted, so roofs are not split.
Grønalid: a house the filter had kept as ground is now removed (its two eave "walls" went with it), buildings 52 to
54, tolerances still met. Vardalag: the three worst misses went, paved worst 3.86 to 3.52 m, buildings 71 to 74.
With the input cap lifted both versions meet both tolerances. Before parting it took 2,341 inputs, after it 2,220,
so the 2,000 cap was what left the misses. Still open: slope-cut houses with no drop inside them (45 to 49% raised).

