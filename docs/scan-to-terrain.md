# Scan to Terrain

`mhScanToTerrain` turns a Z-up drone/photogrammetry mesh or Rhino point cloud into editable ground
curves and building context. Select the source, then use the floating panel. All settings are on this
panel; after selection the command line takes Enter/Esc only.

`mhTerrainToCurves` uses the same recovery runner, fitted contour geometry, preview panel and output
writer. A DEM mesh or the active MoleHill terrain's final built mesh bypasses scan preparation entirely.
The shared settings retain separate defaults/cache namespaces for the two commands. Auto ridge/valley
selection lives in Core and follows the same tolerance/count rules for either source.

## Review and Keep

Each output has an output checkbox and an independent Preview checkbox. Ground/filter controls,
building size and roof tolerance, wall height/length, colour thresholds, kerb heights, contour
accuracy/budget and ridge/valley mode are together in the scrollable panel. Ground slope accepts the
user's preferred slope unit and typed alternatives such as `60%`, `1:3` or `14deg`. Lengths and areas
show model units; accepted settings retain their physical values when document units change.

Plan tolerance controls the physical detail of road/area outlines and building footprints. Raster
steps are removed in plan before boundary heights are sampled, so a tight height tolerance on sloping
ground does not lock in the grid. Footprints combine facade supports instead of extruding every small
step. Genuine wings and courtyards are retained. Minimum patch area (default 10 m²) removes smaller
paved/soft components and fills smaller enclosed gaps in the drawing mask; observed-ground flags stay
unchanged. Lower it to retain small planted islands. Colour-only road heights use up to 0.1 m of the
chosen vertical tolerance; measured kerbs retain their separate 0.015 m profile tolerance.

Roof fitting remains a context approximation. A red preview and fallback metadata identify roofs
that could not be reconstructed reliably; a cleaner footprint does not establish a reliable roof.

Changes debounce, cancel superseded work and recompute asynchronously. Ground, buildings, walls,
surface classes and the final curves appear as their stages complete. The paved/soft split is drawn
with grey/green sample points (at most about 50,000); the boundaries use the full drawing masks.
Red building previews identify median-height extrusion fallbacks. Preview visibility changes are
immediate and do not recompute. Source visibility and document objects remain unchanged during review.

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
| Contours / Spot heights | Fitted, editable ground inputs; clipped to soft areas when classified |
| Ridges and valleys | Optional lines offered to reduce the recovery curve count |
| Walls and edges | Paired measured top/toe breaklines, held in every contour reconstruction |
| Paved areas / Soft areas | Closed area boundaries from colour and ground geometry |
| Kerbs / Road edges | Persistent measured narrow steps as pairs; otherwise one colour edge |
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
models fail does a flagged median-height extrusion survive. Every shell edge has two opposite uses.
A fitted roof requires at least 90% of covered roof-sample area within its roof tolerance and at least
85% sample coverage; this is a robust context fit, not an all-point tolerance guarantee. User text
records roof kind, fallback, RMS, tolerance share, coverage, planar share, plane count, footprint area,
ground/eave/ridge heights and diagnostics. Green complex roofs, low buildings and occluded/noisy scans
remain recognition limits.

Contour accuracy is vertex-sampled against inferred ground (soft ground when classified), not hidden
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
