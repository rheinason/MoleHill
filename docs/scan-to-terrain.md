# Scan to Terrain

`mhScanToTerrain` turns a Z-up drone/photogrammetry mesh or Rhino point cloud into editable ground
curves and building context. Select the source, then use the floating panel. All settings are on this
panel; after selection the command line takes Enter/Esc only.

`mhTerrainToCurves` uses the same recovery runner, fitted contour geometry, preview panel and output
writer. A DEM mesh or the active MoleHill terrain's final built mesh bypasses scan preparation entirely.
The shared settings retain separate defaults/cache namespaces for the two commands. Auto ridge/valley
selection lives in Core and follows the same tolerance/count rules for either source.

## Review and Keep

The everyday controls are output/preview checkboxes and Terrain accuracy. Advanced starts collapsed
and holds ground filtering, built-edge/natural plan tolerances, bridge length, classification, building
context, recovery limits and ridge/valley mode. Ground slope accepts the user's preferred unit and typed
alternatives such as `60%`, `1:3` or `14deg`. Accepted lengths/areas retain physical values across units.

Paved/soft classification uses the texture at up to four times the height grid's resolution, with
class evidence averaged over about 0.3 m before deciding, so outlines follow real edges rather than
texel noise. Paved outlines are straight runs and circular arcs; natural outlines are smooth splines.
Built edges prefer supported straight sections and circular arcs (default plan tolerance 0.25 m).
Their height profiles are fitted separately, so rolling ground cannot bend the plan. Natural boundaries
use 0.5 m plan tolerance. Walls/kerbs need measured evidence; unsupported complicated candidates are
omitted and counted. Compatible short gaps can be joined up to 1 m, with observed share and inferred
station intervals on the output attributes. Paving-class outlines do not establish road or kerb identity.
When closed area boundaries are enabled, duplicate colour-only paving curves are suppressed.

Minimum patch area (10 m²) removes small components and fills small drawing holes. Local pixel-spur
cleanup preserves observation flags. Minimum noise feature (0.5 m, zero disables) allows only small
isolated ground corrections, capped at 0.1 m or half the terrain accuracy, away from accepted lines.
Before that, small standing objects the ground filter kept as ground (cars, bushes, narrow hedges: up
to 2.5 m standing 0.4 m above a 3 m opening of the surface, not beside a building or tree) are lowered
and filled from the ground round them, so paving takes no spot heights on car roofs and lawns no contour
rings round bushes. The status reports the lowered area, the noise displacement, and both
filtered-reference and original-reference reconstruction error (the original is after lowering).

The terrain is described the way a designer would draw it. Paving is close to planar between its edges:
its simplified outlines, draped on the ground, are its breaklines. Soft ground is contoured every
Contour interval (default 0.5 m); contours that the reconstruction does not need are removed. Spot
heights are added only where ground still misses its accuracy: Terrain accuracy (0.25 m) on smooth soft
ground, Paved accuracy (0.5 m) on paving and on rough soft ground (bushes, hedges, bed edges departing
0.2 m from their surroundings), where contours would otherwise chase scan clutter. Recovery inputs per
hectare (500 contours and spots per hectare of measured ground) caps the result. The tolerance decides how many are
used; both reference scans need about 195 per hectare, so the cap only stops a runaway. A cap or no-progress result reports its remaining
error; it does not mean the requested accuracy was achieved. Accuracy is checked at inferred reference
vertices on classified ground; unknown ground (under buildings and trees) is excluded. It is not an
overlay-wide or surveyed accuracy certificate. On the reference scan the preview is ready in about 18 s
in Rhino: 588 contours and 582 spots, smooth soft ground within 0.25 m and paving within 0.5 m. Looser
accuracy or a wider interval trades accuracy for fewer curves.

Outlines are drawn the way they would be traced by hand: corners found at about 1 to 2 m and kept sharp
at the crossing of the edges either side, long straight runs held as lines on paved and soft outlines
alike, splines only between them. Soft ground joined only by strips narrower than 3 m is outlined as
separate areas. No outline or contour crosses itself or another: a crossing one falls back to straight
lines. A car between a lawn and a drive carries both edges on through it instead of being outlined.
Footpaths down to about half a metre wide are paved areas, outlined along both sides, even a stretch
smaller than the minimum area that moss or grass cuts off from the rest. Where the path itself is green
enough to read as lawn it is lawn, and a path whose ground the filter removed with a bush beside it is
not drawn.

Paved and soft areas are also filled as flat solid hatches on Paved zones and Soft zones, a metre below
the source mesh so they never cover it, as input for MoleHill zones: give a zone the layer and it takes
the hatches' outer loops. An area inside another (a patio in a lawn) sits a centimetre above it, so with
Priority by elevation (the default) the inner area wins where the outer loop covers it.

Roof fitting remains a context approximation. A red preview and fallback metadata identify roofs
that could not be reconstructed reliably; a cleaner footprint does not establish a reliable roof.
A house with a lower annex, porch or second wing is split along its facades into parts, each under its
own simple roof (roof kind Composite); a wing's roof may reach into the main house, the higher roof
winning, so an L of two hipped wings meets in valleys. Canopy and hedges touching a building are trimmed
from its footprint where the colour is green and no measured roof plane is near.
Minimum roof detail (default 2 m; zero disables size suppression) removes narrow arrangement patches.
Maximum roof faces (default 12) applies independently and includes interior vertical roof steps,
excluding the floor and exterior facades. An over-complex roof can become a simpler flagged pitched
approximation rather than a flat box. Approximate templates must cover at least 85% of samples, explain
at least 50% within tolerance, and stay below the measured height bound; the final measured RMS ranks
them against the median extrusion. They retain actual fit errors and do not inherit the 90% fit claim.

Changes debounce, cancel superseded work and recompute asynchronously. While a run is going the Recompute
button reads Stop, and the status shows the elapsed time. `mhTerrainToCurves` on a terrain of 100,000
vertices or more waits for Recompute after a change instead of restarting a run that takes minutes. Ground, buildings, walls,
surface classes and the final curves appear as their stages complete. The source is shown by default:
preview curves and points draw over everything, and buildings draw as their crease edges over it (a
shaded solid would z-fight with the scanned roof). Preview and kept layers share one colour per output
(`TerrainCurveExtractionStyle`). The paved/soft split is drawn with optional sample points (off by
default, at most about 50,000). Green curves are horizontal contours: every control has the contour's
single Z value. Closed area outlines are classification boundaries draped onto the ground: paved
outlines (dark slate, heaviest) are the paving breaklines; soft outlines (ochre) are drawings. Runs of
straight pieces keep as one polyline segment of the kept polycurve, editable with PointsOn.
They carry elevation data and are not horizontal contours. Red roofs identify approximate pitched or
median-extrusion fallbacks. Preview visibility changes are immediate and do not recompute. The source
is deselected and shown by default (curves draw over it); clearing Show source suppresses it during drawing. Its document
visibility and geometry stay unchanged, and closing the panel removes this drawing-only suppression.

Keep is disabled until the current settings have a completed preview. Keep validates every native
building Brep, then writes a fresh `Scan {source}` layer tree, one group and one command Undo. Repeating
the command produces a numbered root. Enter keeps a ready preview; Esc/Cancel discards the entire run,
including partially recovered curves. Settings are cached only on Keep. Ground mesh output is an
optional check, off by default. The source remains in place.

## Output and evidence

The 2026-10-18 geometry-cleanup regression checks noisy rotated facades, a real L-shaped wing,
courtyards, and a curving diagonal road on sloping ground in metres and millimetres. On the Core
reference measurement, total footprint corners fell from 3,530 to 629 (median 54 to 10), with 51
retained buildings. Nineteen roofs still used flagged extrusions: this is improved footprint detail,
not completed roof recognition. The native 10 m² patch setting reduced closed area outputs from 502
to 157; Keep produced valid grouped output and one Undo restored the source. These are geometry
quality diagnostics, not a claim of surveyed accuracy or measured performance improvement.

| Output layer | Meaning |
|---|---|
| Border | Outer/inner loops of the retained ground footprint |
| Contours / Spot heights | Fitted, editable ground inputs; clipped to paved and soft ground when classified |
| Ridges and valleys | Optional lines offered to reduce the recovery curve count |
| Walls and edges | Paired measured top/toe breaklines, held in every contour reconstruction |
| Paved areas / Soft areas | Closed draped outlines; paved outlines are breaklines in every rebuild, soft outlines are drawings |
| Paved zones / Soft zones | Flat solid hatches of the same areas beneath the source, as MoleHill zone input |
| Kerbs / Paving edges | Persistent measured narrow steps as pairs; otherwise one colour edge, when area output is disabled |
| Buildings | Closed roof context Breps with fit and fallback metadata; never terrain inputs |
| Ground (check) | Opt-in internal reference mesh |

Ground is a progressive morphological filter plus harmonic fill beneath raised objects. Narrow
terrace steps are distinguished from objects raised around their perimeter. Under buildings and
vegetation, ground is inferred rather than surveyed. Classification uses chromaticity, slope and
roughness, including shadow-resistant excess green; it does not establish the semantic identity of
every paved patch or kerb. Without colour there is no paved/soft split, but measured kerb-sized steps
can still be traced. Filled heights never count as measured kerb evidence.

Buildings use dominant footprint axes, connected fitted roof planes, flat/shed/gable/hip/half-hip/
cross-gable templates, then general plane arrangements. Courtyards are retained. Only after these
models fail does a flagged simple approximation or median-height extrusion survive. Every shell edge has two opposite uses.
A fitted roof requires at least 90% of covered roof-sample area within its roof tolerance and at least
85% sample coverage; this is a robust context fit, not an all-point tolerance guarantee. User text
records roof kind, fallback, RMS, tolerance share, coverage, planar share, plane count, footprint area,
ground/eave/ridge heights and diagnostics. Green complex roofs, low buildings and occluded/noisy scans
remain recognition limits.

Contour accuracy is vertex-sampled against inferred ground (paved and soft ground when classified), not hidden
surveyed ground or every point between triangles. Budget exhaustion reports the remaining residual.
More contours can be necessary than the default 300. There is no automatic promise that a chosen
budget meets the requested tolerance.

## Mesh and cloud capture

Meshes retain coincident UV seam vertices. The host captures diffuse bitmap bytes and per-corner UVs,
falling back to vertex colours. Core rasterises triangle interiors into top/lowest heights and takes
colour from the winning top triangle.

Rhino `PointCloud` objects imported from LAS/E57/XYZ are accepted directly. Core bins the original
observations without first triangulating the cloud: top/lowest heights, the winning top colour/class,
and any supplied ground observation. Automatic cells target four observations per bin; a sparse cloud
may need a larger cell. Empty bins stay missing. Both input paths cap raster size at 16 million nodes
and retain the largest connected footprint with enclosed gaps filled. Roof fitting for clouds uses
only four-corner observed raster cells; it does not bridge missing observations. Raster quantisation
and top-return selection affect cloud roof fitting as well as feature resolution.

Rhino does not expose a standard LAS class per `PointCloudItem`. An importer can retain one byte per
point in `PointCloud.UserDictionary["MoleHill.Scan.Classification"]`, in point order, using LAS codes:
2 ground, 3–5 vegetation, 6 building. Wrong type/length is rejected. If the importer discarded classes,
geometry and optional colours still work. This command does not parse LAS/E57 files itself.

Supplied ground heights override inferred anchors, including below upper roof returns. An independent
filter check reports cells disagreeing by more than the initial height allowance; supplied classes are
trusted rather than silently discarded. Unclassified areas retain filtered anchors. An upper roof is
still excluded from observed-ground colour/kerb evidence, even when a lower ground return anchors the
fill. Majority vegetation classes reject a building candidate even if its canopy is planar.

## Validation (2026-10-18)

Each implementation phase was checked and committed separately. The original plan is retained in git
history; ongoing architecture belongs in `architecture.md` and the source READMEs.

- Ground/contours: native hillside, houses removed, source retention, metre/millimetre scaling,
  cancellation, separate roots and one-command Undo. A reference ground-only Core run met 0.25 m using
  665 contours and 633 spots with plan tolerance 0.1 m and budget 1,000.
- Walls: known full-height multi-cell terrace steps, measured top/toe heights and held reconstruction.
  The reference Core run traced 36 stretches (162.8 m); its 300-contour run left 0.524 m residual.
- Surfaces/roads: shadowed colours, UV seams, soft-region clipping and measured kerbs. A reference Core
  run met 0.25 m using 1,219 contour fragments and 453 spots at budget 2,000; default budget 300 left
  0.894 m. Native reference classification produced 500 closed areas and 226 single road edges;
  30,300 contour samples and every spot stayed in a separately recomputed soft mask.
- Buildings: 24 Core roof/footprint/canopy/topology regressions and native six-roof fixtures in metres
  and millimetres. Canonical native roofs had 6–13 Brep faces and trimmed-intersection errors below
  0.027 m. The textured UV-preserving reference produced 52 valid outward closed building Breps with
  positive volume, including 20 explicitly flagged fallbacks. The unculled Core reference differs:
  51 solids, including 18 fallbacks, from 85 candidates; this is not a ground-truth inventory.
- Clouds/panel: ten Core regressions, native coloured/classified cloud input, physical settings across
  units, stage previews, stale Keep prevention, independent preview/output toggles, Cancel during work,
  source-only preview, grouped output and one-command Undo. A native cloud gable in millimetres agreed
  with trimmed analytic roof probes within 0.126 m at a 0.3 m roof tolerance. A diagnostic native capture
  and ground pass exercised 1,002,001 observations and 251,001 raster nodes, with 0.008 m maximum plane
  error; it is not hosted performance acceptance or a comparative speed claim.

Full managed suites: Core 1,680 passed; Rhino 1,450 passed / 175 native skips; Grasshopper 56 passed /
18 native skips. Native skips are not live acceptance. Builds use the normal Debug output with Rhino
closed; live tests verify the exact loaded RHP/Core paths and module IDs. Evidence and captures are
under `.artifacts/scan-phase1/` through `scan-phase5/`; `rhino-live-testing.md` records native identities.
The final combined textured native run produced 1,418 valid grouped outputs: 52 buildings, 502 closed
areas, 34 wall pairs, one kerb pair, 249 single road edges, 300 contours and 244 spots. All 30,300 sampled
contour points and every spot stayed in an independently recomputed soft mask. Its 300-contour budget
left 0.8937 m residual at a 0.25 m target, explicitly budget spent. One command Undo restored the source
alone. The panel was visually checked at real window bounds, including resize and bottom scrolling.

No package was published. There is no paired dense site cloud in hand, so cross-input site accuracy
is still unmeasured. Synthetic cloud tests do not supply that missing survey truth.

Reproduce the opt-in reference measurements with `MOLEHILL_SCAN_OBJ` (Z-up OBJ, metres),
`MOLEHILL_SCAN_TEXTURE_RGB` (binary int32 width/height then top-down RGB bytes), and optional
`MOLEHILL_SCAN_IMAGES`, `MOLEHILL_SCAN_MAX_CONTOURS` and `MOLEHILL_SCAN_PLAN_TOLERANCE`:

```powershell
dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Measure_ReferenceSurfaces --logger "console;verbosity=detailed"
dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Measure_ReferenceBuildings --logger "console;verbosity=detailed"
```

An ordinary run without the external files does not exercise those reference measurements. Performance
changes must be established with `validate.ps1 hosted-perf`, not these single diagnostic runs.

## Reconstruction improvements still open

The [terrain output review](scan-terrain-output-review.md) records a fresh textured-reference run
with the current panel defaults. Wall rails still retain raster steps and backtracking; area curves
remain too heavy to edit, and colour edges do not establish road/kerb identity. Its correction order
starts with supported plan alignments and coupled rails before reconstructing from those same curves.

The 2026-10-18 upstream roof correction fixes three causes before reconstruction: triangle-count
rejection of sparse facets, normal averaging across ridges, and a height guard based on centroids
instead of the observed vertices. Plane fits integrate projected triangle area, including second
moments, making least-squares fitting invariant to subdivision of the same triangle support. Regressions cover sparse flat
and gabled roofs, steep ridges, metres/millimetres and unequal subdivision. The spatial-arrangement
retry sequence with progressively weaker simplification was removed.

On the same Core textured reference and settings (2 m detail, twelve roof faces), retained buildings
changed from 50 to 51 and median extrusions from six to four. Eighteen models met the accurate-fit
contract versus seventeen previously; twenty-nine still used pitched approximations. This is a
modest improvement, not evidence that the complex roof segmentation problem is solved.

Road boundaries currently follow classified paving, which includes drives and parking as well as roads.
The next useful step is a connected corridor graph: infer paired sides and junctions, fit shared
alignments, and retain other paving loops as area boundaries. Simplifying each loop alone cannot
resolve that semantic ambiguity. Each emitted run now meets the minimum length independently,
including runs split by intermittent kerb evidence.

Complex roofs are now decomposed into parts before fitting (2026-10-20, below). A global small-plane
template can be clean but inaccurate; the face cap is a drawing-complexity control, not a promise of
survey accuracy. On the diagnostic reference, the reviewed Core run reduced total planar building
faces from 3,028 to 792, while approximate pitched roofs still had median RMS about 0.52 m at a
0.30 m fitting tolerance. Keep the approximate designation and actual error when comparing outputs.

The 2026-10-20 composite roofs answer "high complexity but low accuracy": composite buildings (a main
house with a lower annex, porch or wing, or an L or cross plan) had one simple roof stretched over all
of them. On the Core textured reference at the native settings (0.3 m roof tolerance, 2 m detail, twelve
faces) fallbacks fell from 33 to 15 and the area-weighted share of roof within tolerance rose from 71% to
90%, median RMS 0.46 m to 0.26 m, at about 4.3 rather than 2.5 roof facets per building; one more
building is kept (its roof samples had run off the outline). The face budget is now the main limit: an
L-shaped hip beside a flat block needs about fourteen counted faces. At sixteen faces 9 fallbacks remain
and 92% is within tolerance; unbounded, 5 and 93%. Natively 52 buildings were kept as closed Breps (12
fallbacks), with an 18.0 s preview, and terrain output unchanged (588 contours, 579 spots).

Recovered contours already have one exact elevation per curve, including all fitted controls.
An optional fixed interval drawing mode would make level spacing more familiar; the current adaptive
reconstruction allocates its contour budget by residual error instead.
