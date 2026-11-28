# Rhino live-testing methodology

The repeatable procedure for testing Rhino-hosted UI and commands such as `mhInspectCurve`.

**Drive Rhino through the `rhino-mcp` router, not through synthetic keystrokes.** The router
(`mcp__rhino-mcp__*`) spawns a disposable Rhino, runs commands and RhinoCommon scripts inside it, and
returns their output as data. That gives deterministic, inspectable results. Prefer disposable slots;
when the host blocks spawning, use the explicitly designated user-started test session below.
`WScript.Shell`/`SendKeys` and `user32` cursor calls are a fallback for
genuine pointer gestures only — they are blind, focus-dependent, and cannot report what happened.

## 0. Pending live checks

Scatter stress, 2026-11-07: `HostedScatterStressTests` exercised a three-level block containing
80 nested mesh leaves (40,960 canopy faces) plus a Brep trunk on a 12,800-triangle terrain. Counts
10,000 and 50,000 built and baked as block references; 200,000 built with capped point preview.
The bake test hid its results before redraw: it does not establish full-geometry viewport throughput.
Narrow strips (0.3 m and 2 m), oversized footprints, overlapping setbacks and rotated/scaled nested
bounds passed. See `.artifacts/scatter-stress/REPORT.md` for single-run observations, not baselines.

Found and fixed two defects: RDK dropped nested members, and the compact ratio timer was disposed
on the first UnLoad but stopped again on a later UnLoad/focus callback. Final Debug RHP MVID
`f5706d4a-b766-4282-bb8c-09721a410849`, loaded by ID from this checkout in owned PID 2280,
passed a 10,000-count repeat, 41,124 faces in both mesh paths, and native preview cap/cleanup (16).
The complete controller-subscribed panel in a document-parented Rhino-styled form passed actual
pointer/keyboard Enter (3.5), Escape (99 restored to 3.5), Tab (5.25), tab unload/reload, and per-card
Bake (2,472 tagged instances). The model retained 5.25 and no post-fix Eto exception was recorded.
Live was disabled: edits correctly showed quantities pending until Bake rebuilt the result.
This is full-panel lifecycle evidence; real Enscape assets/rendering remain pending below.

Scatter planting tranche, 2026-11-07: exact Debug RHP `e871d9cd-01ea-40f3-a3ff-f3baa00b5fb6`
loaded by ID (true) from this checkout in owned slot `aardvark`, PID 26516, using standalone router
0.4.0. `HostedScatterTrancheTests.Run` passed resolved zone priority/hole handling, exclusion clearance,
boundary setback/feather, disabled mix blocks, exclusive rejection counts, cached pose/count stability,
drawn planting report and real `CsvWriter` output, full Bake followed by per-card replacement of 807
instances while other outputs kept their IDs. Selecting both zones removed their shared internal
setback; duplicating the terrain remapped the scatter's selected zone IDs. The fixture used count
2,000 before filtering; this is behavioral evidence, not a performance comparison.

The actual Scatter card was rendered in a document-parented Rhino-styled form with the panel's real
scrollable width handling. At 240 logical pixels its controls stacked, filter names wrapped under
the compact active-count header, and quantities and per-card Bake remained readable. Native pointer
selection changed 20 plants/100 m² to plants/hectare; re-rendering the isolated card showed 2,000
plants/ha with stored density still 0.2/model m², the gross estimate still 80 and placed count still
45. (The isolated form is manually re-rendered; the actual panel subscribes to its controller.)
At the initial 300-pixel card inspection, zone selectors, disabled block controls, thumbnails and
filter editors were also visible. Evidence: `.artifacts/scatter-tranche/native-history.txt` and
`planting.csv`. No Enscape renderer was exercised. The owned slot closed through its creating client.

Validation: `dotnet build src/MoleHill.Rhino/MoleHill.Rhino.csproj --no-restore --verbosity quiet`
passed with zero warnings/errors. `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj
--no-restore --verbosity quiet`: 1,799 passed. `dotnet test tests/MoleHill.Rhino.Tests/MoleHill.Rhino.Tests.csproj
--no-restore --verbosity quiet`: 1,492 passed, 179 native skips. The hosted tranche tests above ran
separately inside Rhino. No package was published.

Scatter bake timing, 2026-11-20: same scene in a fresh slot per build (Release, EcoQoS opted out,
80x80-cell terrain, two simple blocks, 600 m square boundary, `BakeTerrain` timed, 3 samples, baked
objects deleted and undo cleared between samples). Baseline `aa55a2a` (MVID `bef76a54`) against the
working tree on `8c39f53` (MVID `4ca97484`), medians: 2,000 instances 103 -> 101 ms; 10,000 381 -> 352 ms;
30,000 1,062 -> 914 ms. The grouped bake is 0-14% faster here; both are ~30 us per instance, dominated
by Rhino's own instance insertion. A slow bake the user sees is therefore not this loop on a plain
headless scene: open panel, renderer listeners (Enscape) or heavy block attributes are the candidates.
Enscape is not installed on the maintainer's machine as of this date.

Run these at the next live session, then delete each line once it has passed (say so in the commit).
Scatter: verify **Enscape / native blocks** with a real Enscape asset and a running Enscape renderer.
Native instance/metadata lifecycle is verified below; asset replacement inside Enscape is not.

Scatter, 2026-11-07: exact Debug RHP `f186fa2a-bbe9-4330-a896-c36d5fdf7a00` loaded by ID (true)
from this checkout in owned slot `aardvark`, PID 11080. `HostedScatterTests.Run` passed transformed
whole-block region containment (45-degree yaw, 1.5 scale), preview cap 23, stable IDs on display
refresh, preview source exclusion, disable/mode changes, hide/show, native 3dm save exclusion,
472 persistent baked block instances with instance Enscape user strings retained, and delete cleanup.
Rhino 8's transient instances report `IsReference == false` despite being excluded from the saved
archive, so source exclusion uses the explicit preview tag as well. The isolated actual block-ratio
editor was shown in a document-parented, Rhino-styled Eto form: double-click/type `25.5` + Enter,
`99` + Esc restoring `25.50`, and `5.25` + Tab committed `5.25` to the callback. This verifies that
editor, not the complete Objects tab layout. Native history/save/callback evidence is under
`.artifacts/scatter/`. The owned slot was closed through the same standalone client.
Build: `dotnet build src/MoleHill.Rhino/MoleHill.Rhino.csproj --no-restore --verbosity quiet`
(zero warnings/errors). Full managed Core suite: 1,789 passed; full managed Rhino suite: 1,483 passed,
179 native skips. The hosted scatter run is separate native evidence. No measured bake speedup is
claimed; the bake change batches repeated preparation and redraw. No package was published.

Plant/Orient: manually exercise Change Base's face/three-point picker through accept and Esc, and
Show Base's finish prompt. The underlying base commit, rendered controls and placement lifecycle
passed hosted checks on 2026-10-30; the interactive picking loop was not driven automatically.

Plant/Orient native Undo/Redo after a live placement: **fixed and passed live on 2026-11-02** (Release
RHP, owned slot `aardvark`, Rhino 8.35.26251.13001, the 2026-10-30 car fixtures). Orient → Bake → native
`_Move 40,0,0` → live reorientation → `_Undo` left two cars with their original IDs at their exact
pre-Move poses (was four); `_Redo` redid Move and placement with sources and states intact; a second
Undo after the Redo stepped over both again; the second report repro (`_Move -40,0,0` → Undo) returned
the exact previous placed pose. Command history shows each pair ("Undoing Place MoleHill Objects |
Undoing Move"). **Harness trap:** the paired half runs at idle, and back-to-back router calls give Rhino
none (a `run_csharp` holds a command record open, and the cascade waits for it). Pause a few seconds
after each `_Undo`/`_Redo` before asserting, or the next Undo/Redo arrives first and correctly cancels
the queued half. The read-only `HostedObjectPlacementTests.AssertAfterNativeCommand` still refreshes
definitions after Undo and checks actual geometry count; do not force a rebuild to validate native Undo.

On 2026-10-21 the mouse checks passed live: `mhMergeControlPoints` ordered picks (First/Last),
and `mhSlopeControlPoints` after its rework (picking control points or a whole curve after the command
starts, the preview following the mouse, the Offset-style slope with Through). `mhRelaxRoute` dragging,
loop untangling on release and the reordered buttons passed in the same session.

The 2026-10-07 list (GH quad meshes, GH Remesh Mode, Ponding with model units) passed live
on 2026-10-07.

When driving GH components headless from `run_csharp`, clear a parameter's `PersistentData` before
`SetPersistentData`: an input with a default (Remesh's Edge Length is 0) otherwise keeps the default as its
first item, the component reads that, and the solve looks like a silent no-op.

## 1. Build before launching

Follow-up original-flow test, 2026-10-30: the same exact RHP
`7db2418f-3853-4971-9de1-9bd42ae077fc` loaded unblocked in owned slot `armadillo`, PID 16876,
Rhino `8.35.26251.13001`. This run used a synthetic car-shaped block and loose mesh (body, cabin,
four wheels), initially tilted 70 degrees. Orient/Bake/native Move onto the other slope passed;
after recovering the assignments lost by Redo, four further native Move/Bake crossings passed
with the same card. Assertions read the live result without forcing a build: two objects, correct
remembered base normal/contact, matching geometry/card history and unchanged body dimensions.
The actual loose-mesh body normal retained a constant 0.01536-degree automatic-base approximation;
the maximum wheel gap was 1.115 mm on a four-metre vehicle, without accumulation. The block's
body normal/contact errors were negligible. **Native Undo failed twice** as described above;
this is current-build evidence and supersedes any inference of Undo acceptance from the earlier
build below. Artifacts: `.artifacts/object-placement-live-flow/REPORT.md`, measured `results.jsonl`,
`failures.jsonl`, scripts, before/after Undo `.3dm` scenes and an actual viewport capture.
The owned slot closed gracefully and the original user Rhino was left open. No production fix was
made as part of this follow-up validation.

Pre-commit review reran `./validate.ps1 managed`: full solution build and 3,309 managed tests passed
(1,785 Core, 1,468 Rhino, 56 Grasshopper; 197 native skips). Evidence:
`.artifacts/validate/managed-20261030-235120/`. The sandbox blocked ILRepack's temporary-file move;
the approved host run passed. Existing NU1900 and xUnit1031 warnings remain. This rerun is managed
validation only and does not change the native Undo/Redo failure above.

Plant/Orient redesign, 2026-10-30: exact Debug RHP `7db2418f-3853-4971-9de1-9bd42ae077fc`
loaded unblocked in owned slot `armadillo`, PID 58592, Rhino `8.35.26251.13001`. Seven placement
tests ran inside Rhino (including Plant preserving a 70-degree tilt and forty absolute-pose cycles).
`HostedObjectPlacementTests` exercised the actual RHP by reflection: a 70-degree block and loose mesh
were oriented/baked, moved using native `_Move`, then reoriented by the same card. Thirty API moves
between slopes preserved frame/scale, footprint misses kept geometry/history, and recovery succeeded.
Native `.3dm` save/headless-reopen retained custom user data and live ownership. Watched-layer release,
fresh assignment, copied blocks/meshes, conflicts and a custom block-base commit passed. A changed
base between solve/sync rejected the stale result without moving the edited object.

On the preceding geometry build `cbad4821-0edd-45f8-9f91-baf5773e5965`, live asynchronous `_Move`
followed by one native `_Undo` and `_Redo` restored all four car positions; independent bounds
comparisons passed. The final panel was captured/inspected at 320 and 240 logical pixels using real
`GetWindowRect` bounds filtered by the owned PID. Its initially white WPF capture was resolved by
enabling SoftwareOnly rendering inside the disposable process, as in earlier checks below.
Artifacts: `.artifacts/object-placement/final-native.txt`, `placement-roundtrip.3dm`,
`final-panel-320.png`, `final-panel-240.png`, `undo-before.json`, `redo-expected.json`.

Validation: `./validate.ps1 managed` passed 1,785 Core, 1,468 Rhino and 56 Grasshopper tests (196
native skips at that run); the added Plant regression brought the subsequent full Rhino run to
1,468 passes / 179 native skips. `dotnet build src/MoleHill.Rhino/MoleHill.Rhino.csproj --no-restore
--verbosity quiet` and the final targeted Rhino test command passed. The targeted filter was
`FullyQualifiedName~ObjectPlacementTests|FullyQualifiedName~TerrainSchemaMigrationTests|FullyQualifiedName~ParameterSchemaGuardTests|FullyQualifiedName~TerrainUnitScalerTests`
(359 passed / four native skips; those four placement bodies ran successfully in Rhino).
`./validate.ps1 native` failed outside Rhino with the known `rhcommon_c` initialization error
`0x8007045A`; it is not native acceptance evidence. NuGet vulnerability-service warnings and the
existing Core xUnit1031 warning remain unrelated. No package was published. Owned slots were closed;
the original user-started Rhino was left open.

Georeferenced transfer, 2026-10-29: exact Debug RHP `a114b9b5-2f6e-4839-bbb5-484a813d74d1`
in owned slot `armadillo`, PID 20316, Rhino 8.35. `HostedGeoreferenceExportTests.RunFileCases`
(in the Rhino test assembly, invoked inside Rhino) exported three metre-unit curves beside an
unrelated 50,000-vertex mesh block and embedded image: `.3dm` was 14,926 bytes, retained names,
layers, units and tolerance, and excluded the unrelated definition, image payload, document text
and saved CPlane. DXF and STEP imported at the expected rotated/translated real-world coordinates.
A selected block retained its required definition and excluded the unrelated block. These hosted
cases are deliberately not reported as managed xUnit passes.

Native `run_command` also exercised `-mhExportWithGeoref`, `mhCopyWithGeoref`, ordinary `Paste`,
and `mhImportWithGeoref Paste` with a curve and block. Queries checked source ids, geometry,
selection and modified flag after copying, no temporary copies, correct real-world paste placement,
and the inverse mapping back into local coordinates. The owned slot was closed gracefully; the
user-started Rhino was left open. Artifacts: `.artifacts/georef-export/`.
Build: `dotnet build src/MoleHill.Rhino/MoleHill.Rhino.csproj --no-restore --verbosity quiet`
(zero warnings/errors). Managed: `dotnet test tests/MoleHill.Rhino.Tests/MoleHill.Rhino.Tests.csproj
--no-restore --verbosity quiet` (1,461 passed, 175 native skips). No package was published.

Upstream roof corrections, 2026-10-18: normal Debug RHP `ef6d3038-2009-419d-b745-ae2f9aac8c9e`
and Core `985b4ab6-a094-43fb-83a5-d44747517632` were loaded from the exact standard output paths
in a fresh owned `aardvark`, PID 20516, Rhino 8.35.26251.13001. The same UV-preserving reference,
settings, prearmed timers and `_mhScanToTerrain` command sequence as the clarity run below produced
588 valid grouped outputs. Native queries confirmed 51 valid closed outward positive-volume Breps,
837 total native faces and no more than twelve roof facets including internal vertical steps.
All 83 road/kerb runs met the 2 m minimum; road output remained 82 unpaired runs / 5,649 Core controls.
Forty contours retained exact level Z; their deliberately capped budget still left 2.138 m residual.
Source-off/on viewport captures were inspected. One Undo restored the source alone, confirmed by query.
The owned slot was closed gracefully and process exit checked. Evidence: `.artifacts/scan-early/native/`.

Five new regression cases cover sparse facets, a steep ridge, model units and subdivision invariance.
The original sparse cases failed before the fixes; the steep case exposed cross-ridge normal blending.
Full Core suite: 1,707 passed. Rhino: 1,457 passed / 175 native skips. Normal plugin build had zero
warnings/errors, using the same build/test commands below. The Core textured reference retained 51
buildings rather than 50 and four median extrusions rather than six, with 29 pitched approximations
still present. This is a modest early-stage correction, not completed roof recognition. Progressive
spatial-simplification retries were removed. No package was published.

Scan clarity and Astra review, 2026-10-18: fresh normal Debug RHP
`b51eac8d-3ccb-4a76-b7ef-d211f0f0d9a0` / Core `aae37372-1b58-4d76-ba2d-3e6e2a93ad4b`
were loaded from `src/MoleHill.Rhino/bin/Debug/net7.0/` in owned slot `aardvark`, PID 37528,
Rhino 8.35.26251.13001. `run_csharp` loaded the exact plugin and checked its path and module IDs,
imported the UV-preserving reference mesh, selected it, armed Eto timers, then `run_command`
invoked `_mhScanToTerrain`. Timers set the controls, asserted source-only document state and
captured the viewport with Show source off/on. Buildings shade in `PostDrawObjects` with the source
hidden and draw as crease lines over it when shown; source suppression uses `PreDrawObject`. A modal
dialog blocks later `run_csharp` calls until it closes, so screenshots during the preview come from the
armed timer (`CaptureToBitmap`), not a second script. Both captures were nonblank and inspected. The source-off view
shows roofs and linework without selection highlight. The real dialog was captured at PID-filtered
GetWindowRect bounds, including the road controls and contour/area explanation.

Native queries after Keep found 588 valid grouped outputs: 51 valid closed outward building Breps
with positive volumes and 813 total native faces. An independent Brep face audit excluded floors
and exterior facades, included internal vertical roof steps, and found at most seven roof faces
against the configured cap of twelve. All 83 road/kerb runs met the 2 m minimum; 82 unpaired roads
used 5,649 Core controls versus 14,324 before this change. Every control on each road plan segment
was collinear in XY. All forty contours had exactly one elevation in both samples and spline controls.
The forty-contour budget deliberately left 2.138 m residual: this was host/clarity validation, not
contour accuracy acceptance. One command Undo was queried to restore the source alone.
`close_slot` closed the owned PID gracefully and process exit was checked.

Astra reviewed the reconstruction and native evidence, corrected approximate-roof ranking and
budget semantics, and identified a short split-run leak; a failing regression preceded its fix.
The review's larger remaining directions are recorded in `scan-to-terrain.md`. Roof approximation
is still material: cleaner geometry does not establish survey accuracy. Astra reached its usage
limit before the final native pass; the primary agent finished it. Artifacts:
`.artifacts/scan-clarity/reference-final/` and `.artifacts/scan-clarity/reviewed-roofs/`.

Commands: `dotnet build src/MoleHill.Rhino/MoleHill.Rhino.csproj --no-restore --verbosity quiet`,
`dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --no-restore --verbosity quiet`
(1,702 passed), and the corresponding Rhino test project (1,457 passed / 175 native skips).
Grasshopper tests passed 56 with 18 native skips. Plugin build: zero warnings/errors. Native skips
are not acceptance evidence. No package was published.

Shared curve extraction and scan outline cleanup, 2026-10-18: normal Debug shared-workflow build
`3c874e29-95c5-4d65-bd2b-70c1083a2d70` / Core `c7776a08-f3f5-4335-89b5-92fa896d332c`
produced six DEM contours and one spot plus border, all valid/grouped, with five cubic contours;
one Undo restored the source. A native active terrain with a Smooth modifier used its final peak
4.9264 m instead of the raw 5.5 m, with no scan preparation and no document output on Cancel.
The common panel was captured at owned-PID window bounds.

The full scan cleanup/settings build `3e3eb04a-a22d-41f3-a626-ab68f3ddcedf` / Core
`aa02c30c-20e9-4ed7-a1a3-5180f8eee80d` exercised the textured reference with 10 m² minimum patch
area: 624 valid grouped outputs, 52 closed buildings and 157 closed area NURBS (previously 502).
Its deliberately limited 40-contour budget left 2.138 m residual, explicitly reported; this was
outline/host integration acceptance, not contour accuracy acceptance. Preview retained the source
alone and one command Undo restored it. The later facade-direction refinement is checked separately
below; no road/region code changed after this combined native run. Evidence: `.artifacts/scan-quality/`.

Final facade build `4f169a51-e5fa-49fe-a96b-2d2656cd5d48` / Core
`09eae81d-4882-443a-a3a8-b508d065e34b` was loaded in a fresh disposable slot. With other outputs
disabled, the textured reference produced 52 valid closed outward Breps with positive volumes;
22 roofs were explicit extrusion fallbacks. One Undo restored the source. The subsequent Redo/capture
was not verified: the bitmap was blank, so it supplies no evidence of final viewport appearance.
The new minimum patch area row was captured and inspected at real owned-PID window bounds
(`.artifacts/scan-quality/facades-native/`). The earlier all-output run verifies
the road/area settings; this later run verifies final native building conversion. Owned slots were closed.

Managed suites passed Core 1,689, Rhino 1,457 with 175 native skips, Grasshopper 56 with 18 native
skips. Normal plugin build had zero warnings/errors. No package was published. These checks do not
establish roof-recognition correctness: extrusion fallbacks remain explicitly marked.

`mhScanToTerrain` Phase 5, 2026-10-18: the combined reference check used normal Debug RHP
`7c5e7fdf-cf0e-4c66-a17a-f0b46437a0c0` / Core `1b80fe4e-4b5c-4f4e-910b-332e24cbb37e`.
The actual textured UV-preserving mesh produced 1,418 valid grouped objects: 52 closed outward
positive-volume building Breps (20 marked fallbacks), 502 closed area NURBS, 34 wall pairs, one kerb
pair, 249 single road edges, 300 contours and 244 spots. All 30,300 native contour samples and every
spot were inside an independently recomputed soft mask. The 0.25 m target was **not** reached at
budget 300: final residual 0.8937 m, explicitly reported. Preview retained the source-only document;
Keep wrote the group and one command Undo restored the source alone. Undo/Redo and the kept viewport
were inspected. The panel was captured at real `GetWindowRect` bounds filtered to the owned PID,
including resize and lower scrolling (`.artifacts/scan-phase5/panel-cloud.png`,
`panel-reference-bottom.png`, `reference-output.png`).

Native classified/coloured cloud checks exercised 11,418 observations, a closed gable, supplied ground
below the roof, physical settings across metre/millimetre document changes, independent preview/output
toggles, stale Keep prevention, cancellation during work, grouping and one-command Undo. A million-point
diagnostic exercised 1,002,001 native observations, 251,001 raster nodes and 0.008 m maximum ground-plane
error; its single-run timing is not hosted performance acceptance. Ten cloud Core regressions passed.
Final rebuilding after status wording and preserving Auto's original tie order used RHP
`07a0aec5-1f5e-4e8e-92c5-721a46d3ee37` / Core `29d99e80-0751-4ca9-8b58-bbb901968c06`: a fresh cloud
run exercised Auto, settings refresh, actual Keep, valid grouped output and one-command Undo again.
Final managed suites: Core 1,680; Rhino 1,450 plus 175 skips; Grasshopper 56 plus 18 skips. Normal plugin
build: zero warnings/errors. All owned slots were closed. No package was published.

The owned Rhino's default WPF renderer initially captured even a minimal Eto label form as white.
After confirming populated control bounds, setting `System.Windows.Media.RenderOptions.ProcessRenderMode`
to `SoftwareOnly` **inside that disposable process** restored both the probe and actual panel paint.
This was a test-session workaround, not a plugin change or global user setting. Do not treat a blank
capture as a panel bug; confirm the renderer and control tree, then inspect a real rendered capture.

`mhScanToTerrain` Phase 4, 2026-10-18: normal Debug RHP MVID
`95dd4eba-1f16-4604-9314-65b6152c4ec9`, Core `432b1e97-33fc-4ec7-b220-2a72e5c36fbd`, owned
disposable Rhino. Six synthetic roofs (flat, shed, gable, hip, half hip, crossing gable) produced
valid outward closed Breps with 6–13 faces; trimmed vertical `CurveBrep` probes agreed within 0.027 m.
Metre/millimetre models retained roof kinds and accuracy. `Buildings=No` emitted no Breps. Single
command Undo restored source-only scenes. `Intersection.RayShoot` ignores Brep trims and must not be
used to assert roof coverage. The actual textured UV-preserving reference produced 52 valid outward
closed Breps with positive volumes, 20 marked extrusion fallbacks, and 472 valid grouped outputs;
the source remained and one command Undo restored it alone. This check disabled walls/areas/roads
and used one contour: 6.386 m residual, explicitly budget spent. It is building conversion acceptance,
not contour acceptance. Start planar trimming precisely: document-tolerance trims can be valid yet
move tiny shared edges, leaving gaps after joining. Full managed suites: Core 1,670; Rhino 1,450 plus
175 skips; Grasshopper 56 plus 18 skips. Plugin build passed with zero warnings/errors. Evidence:
`.artifacts/scan-phase4/`. The owned slot was closed after testing.

`mhScanToTerrain` Phase 3, 2026-10-18: exact normal Debug `.rhp` MVID
`68fadd2e-22a1-472b-b544-0dae906d8221`, owned disposable Rhino. A textured road and lawn mound,
half shadowed, produced three closed NURBS areas, four measured kerb pairs, two single edges, one
contour, one spot, border and optional check mesh: 17 valid outputs in one group, 0.1369 m residual.
Native contour samples and spots avoided paving; kerb height/evidence metadata passed. Scaling the
source to millimetres retained counts and physical residual. One command Undo restored the source-only
document. The native seam fixture preserved six vertices and distinct UV/bitmap samples across a
coincident edge. The curve-only viewport capture was inspected (`.artifacts/scan-phase3/`).
Full managed suites passed (Core 1,645; Rhino 1,450 plus 175 skips; Grasshopper 56 plus 18 skips).
The separate Core reference run reached a 0.2498 m soft-region vertex miss using 1,219 contour
fragments and 453 spots, budget 2,000. Default budget 300 leaves 0.8938 m, explicitly budget spent.
These are inferred-ground results; the scan plan records settings and classification limits.
The textured native reference (633,522 UV-preserving vertices; 14 degenerate faces culled) produced
1,334 valid grouped outputs, including 500 closed area NURBS and 300 contour NURBS. All 30,300 sampled
contour points and every spot were inside a separately recomputed soft mask. Its fixed 0.276 m raster
and shared 0.5 m plan tolerance gave 0.9506 m residual at budget 300, explicitly budget spent;
33 wall stretches, 226 single road edges and no confirmed kerb. The native triangulation/settings
differ from the Core measurement. The curve-only capture was inspected and the owned slot closed.
Final geometry-only validation used the rebuilt Core MVID `aa2ab8dd-10ff-406a-bad1-b2b319dd6811`
in another fresh slot: two valid measured kerb pairs, two contours and one spot, no area classes,
0.15 m residual at a 0.25 m target. Filled heights are excluded from measured kerb evidence; the
metre/millimetre Core regression checks this. The owned slot was closed afterward.

`mhScanToTerrain` Phase 2, 2026-10-18: exact Debug `.rhp` MVID
`97e3453c-40bd-4d9a-8afd-e2b7b186fc6a`, owned disposable Rhino. Two 1.5 m terrace steps and a house
gave two wall pairs (four light NURBS), two contours, a border and no spots, within 0.25 m against the
wall-constrained ground (0.225 m residual). Native mesh rays at 19 stations per rail agreed within
0.01 m; measured step user text, `Walls and edges` layer, grouping and one-command Undo back to the
source-only scene passed. The multi-cell fixture specifically rejects double half-height wall output.
Core reference measurement with `MOLEHILL_SCAN_WALLS=1` found 36 stretches (162.8 m), 300 contours and
508 spots, with 0.524 m residual, budget spent. This does not claim a 0.25 m default-budget result.

`mhScanToTerrain` Phase 1 was tested on 2026-10-18 in an owned disposable Rhino with the exact Debug
`.rhp` (MVID `d3564ae4-cded-4704-9235-23c14d675f66`). A synthetic hillside with a house produced seven
contours, one border and one spot at a 0.1918 m reconstruction miss (0.25 m target). The same source
scaled to millimetres retained the counts and physical tolerance; `AddGroundMesh=Yes` produced a valid
1,681-vertex check mesh on its own layer. A rerun used a new root, and a single command Undo removed
that run while retaining the earlier output. Cancelling at the options prompt added nothing. Esc during
the ground worker was not exercised live; its cancellation probe has a managed test.

The reference OBJ was read into a double-precision native mesh, with 14 degenerate faces culled before
Rhino would accept it (383,128 vertices, 766,558 remaining faces). The registered command with automatic
raster spacing and a 300-contour budget emitted 300 contours, a border and 487 spots; native assertions
confirmed valid output on `Scan Reference scan::...` layers and source retention. It reported a 0.506 m
residual at the 0.25 m target, **budget spent**, not tolerance satisfaction. The pure Core run on the
unculled OBJ differs slightly (0.513 m, 488 spots). Viewport captures are in
`.artifacts/scan-phase1/`; the scan plan records remaining limits. Build:
`dotnet build src/MoleHill.Rhino/MoleHill.Rhino.csproj --no-restore -p:CopyRetryCount=0 -v minimal`.

Build with no Rhino holding the `.rhp`:

```powershell
dotnet build src\MoleHill.Rhino\MoleHill.Rhino.csproj --no-restore
```

`MSB3021`/`MSB3027` are **errors, not warnings** — a running Rhino holds `MoleHill.Core.dll` and
`MoleHill.Rhino.rhp`, the copy fails, and the build fails with it. Nothing is produced, so there is no
question of testing a stale binary; the build simply does not happen. Close the locking Rhino (see §2 for
how to close one the router will not) and build again.
Plugin path: `src\MoleHill.Rhino\bin\Debug\net7.0\MoleHill.Rhino.rhp`.

**So close the slot before every rebuild.** A live test cycle is spawn → test → `close_slot` → edit →
build → spawn again, and skipping the close is the most common way to lose ten minutes here.

**A green test run is not a compiling plugin.** `MoleHill.Rhino.Tests` *links* `Model/`, `Registry/` and
most of `Services/` as source and project-references only `MoleHill.Core` — it never builds
`MoleHill.Rhino.csproj`. So `UI/` is outside every test build, and a compile error there (a duplicated
helper, say) passes the whole suite and surfaces only when the plugin itself is built. If a lock is
blocking the real output, compile to a scratch directory rather than assuming:

```powershell
dotnet build src\MoleHill.Rhino\MoleHill.Rhino.csproj -p:OutDir=$env:TEMP\verifybuild\
```

## 2. Spawn a disposable slot

```
spawn_slot(version: "8")   →   { slotId, port, pid, adopted }
```

Record `slotId` and `pid`; pass `slot` explicitly on every later call. `close_slot` normally kills
exactly that instance.

### User-started test session when breakaway is blocked

Build first, then have the user start Rhino 8 with an empty document intended for testing and run
`MCPStart`. `MCPConnect` wires the assistant's configuration; its "wired the RhinoAI MCP server into
Codex" message does **not** establish that the current Rhino is listening. McNeel documents these
commands in its [connection guide](https://mcneel.github.io/RhinoAI/docs/getting-started/copilot/)
and [plugin startup instructions](https://mcneel.github.io/RhinoAI/docs/getting-started/connector/).

1. Call `list_slots` **before** `spawn_slot`. The router adopts advertised user-started sessions.
2. Record the returned slot ID/PID and confirm it is the designated test session. Pass `slot`
   explicitly on every call; if several sessions are listed, do not guess which one to use.
3. Probe with `get_context` and a read-only `run_csharp` using `__rhino_doc__`, then verify the exact
   plugin path and module version as in §3. Adoption alone is not a successful live test.
4. Preserve existing document content and selection. Track test-created object IDs and remove only
   those objects during cleanup. Do not clear, replace, close or save the user's document.
5. Leave the Rhino window open. `close_slot` refuses adopted slots; ask the user to close this window
   before rebuilding a loaded plugin. Do not terminate a user-started Rhino by PID.

If `list_slots` returns an empty array, ask for the `MCPStart` command output. On 2026-10-05,
`MCPConnect` updated Codex's configuration but left the router's list empty and
`%APPDATA%\McNeel\Rhinoceros\ai\listeners` empty. Do not treat configuration wiring as listener
startup or claim adoption worked without a returned slot and successful probe.

Verified on 2026-10-05 through Codex's MCP connection after `MCPStart`: slot `aardvark`, PID 24688,
port 10500, `adopted: true`, Rhino `8.35.26251.13001`. `get_context` returned an empty document;
`run_csharp` found MoleHill loaded from this checkout's `bin/Debug/net7.0/MoleHill.Rhino.rhp`
(module version `379f3ac1-44a4-4133-aceb-0e1af46b19aa`). `run_command("_SelNone")` returned `Done.`;
an in-memory native mesh probe asserted a valid closed 2×3×4 box with volume 24 and confirmed the
document remained empty. This establishes router discovery, C# execution, command dispatch and native
geometry access, not acceptance of a particular MoleHill workflow or a fresh build. The user-started
Rhino was left open.

If the desktop MCP connection reports Windows error 5 while spawning, its parent Job Object may
block the router's managed Rhino breakaway. The repository's
[`tools/rhino-live-client.py`](../tools/rhino-live-client.py) starts the same installed router from a
standalone shell that permits breakaway. Run it with Python, then send JSON lines such as
`{"tool":"spawn_slot","args":{"version":"8"}}` and
`{"tool":"close_slot","args":{"slot":"$lastSlot"}}`. It reports each router result and closes
its exact owned slot on exit. This route was verified on 2026-09-15 with Rhino 8 and real Grasshopper
canvas solves, and rechecked on 2026-09-16 by spawning slot `aardvark` (PID 33444), executing a
`run_csharp` probe with the router's `script` argument, and closing that exact slot successfully. A
sandboxed shell may still block process launch; use the host's approved unsandboxed
command execution when that happens. The normal build-before-spawn and exact-slot rules still apply.

**For Codex, try approved host execution before falling back to a user-started Rhino.** On
2026-10-05 the desktop MCP `spawn_slot` still failed with error 5, but the standalone client launched
and closed an owned Rhino when run with `exec_command(sandbox_permissions: "require_escalated",
tty: true)`. Use an actual Python executable; if `python` resolves to a Windows Store alias that
cannot launch, discover the bundled executable with `load_workspace_dependencies`.
Keep the returned shell session alive and send one JSON line at a time with `write_stdin`:

```json
{"tool":"spawn_slot","args":{"version":"8"}}
{"tool":"run_csharp","args":{"slot":"$lastSlot","script":"Console.WriteLine(__rhino_doc__.Objects.Count);"}}
{"tool":"close_slot","args":{"slot":"$lastSlot"}}
{"tool":"list_slots","args":{}}
{"tool":"quit"}
```

Inspect each result before sending dependent work. Test commands and verification scripts belong
between the probe and close. **Close through the same client that spawned the slot**, not a second
router that may see it as adopted. The client defaults `RHINO_MCP_STARTUP_TIMEOUT` to 300 seconds
(preserving an explicit environment value) and tracks a slot for exit cleanup only after `spawn_slot`;
listing someone else's session must never make it the cleanup target.

Full cycle verified on 2026-10-05: `dotnet build MoleHill.sln --no-restore -v minimal` succeeded with
zero warnings/errors; standalone router 0.3.0 spawned `aardvark`, PID 30168, `adopted: false`, Rhino
`8.35.26251.13001`. A C# probe verified the checkout's Debug `.rhp` path and matched its loaded module
version to the freshly built file (`9311d13f-6716-4e60-ace3-94e2b58e9a1b`). Two preselected crossing
10 m lines were passed to `mhSplitAtIntersections`; document assertions confirmed four valid 5 m
segments with an endpoint at the intersection and 20 m total length. One `_Undo` restored the two
10 m originals. `close_slot` returned `closed: true` and reported a graceful exit; subsequent
`list_slots` returned an empty array and `Get-Process -Name Rhino` found no remaining Rhino.

**`adopted` does not mean "the user started it".** It means "this router session did not spawn it", and a
Rhino the router spawned earlier can become adopted — after the router loses track of it (see §7 on
`_-Open`), or across router restarts. `close_slot` then refuses it with `cannot_close_adopted` and there
is **no router-side way to close it**, so it sits holding the build lock from §1 for the rest of the
session. Verified 2026-09-13: two Rhinos both launched with `/runscript="_MCPSpawn"` — router-spawned,
neither user-started — were both reported `adopted: true`, and both refused.

That command line is how to tell them apart, and it is the only reliable way:

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'Rhino.exe'" |
    Select-Object ProcessId, CreationDate, CommandLine
```

A router-spawned Rhino carries `/runscript="_MCPSpawn"`; the user's own does not. Stop **that exact
PID**, and only after reading its command line — never `Stop-Process -Name Rhino`, which would take the
user's unrelated session with it. If the environment blocks stopping the process, say so and ask the user
to close the window rather than leaving the build silently broken.

Confirm the slot is alive and the script host works before doing anything else:

```
run_csharp(slot, "Console.WriteLine(__rhino_doc__.Objects.Count);")
```

**If `run_csharp` fails, drive a script file through `run_command` instead.** Observed 2026-09-19:
`run_csharp` *and* `run_python` both failed on two separately spawned slots while `run_command`,
`get_context` and `get_commands` all worked and the freshly built plugin was correctly registered. The
router's script hosts are not the only way in — Rhino's own command is:

```
run_command(slot, '_-RunPythonScript "C:\...\probe.py"')
```

The leading `-` suppresses the file dialog. The script gets a full scripting host, so **have it write
its results to a file** and read that file from disk; `run_command` returns only `"Done."`, and
`RhinoApp.WriteLine` output is not returned either. Wrap the body in `try` / `traceback.format_exc()`
and write the traceback to the same file, or a failing script is indistinguishable from a passing one.
This route was verified on 2026-09-19 by reading back `Rhino.PlugIns.PlugIn.Find`'s assembly location
and module version id for the exact build.

## 3. Load the plugin and confirm it really loaded

First check whether the exact build is already loaded. MoleHill loads at startup; calling the path
overload again can open a modal "ID already in use" error even when the expected assembly is active.
Use `PlugIn.Find` with MoleHill's ID and verify the instance's assembly path. Only load by path when
there is no loaded instance — **not** via `_PlugInManager`, which opens a modal dialog:

```csharp
var id = new System.Guid("0c0b9e83-4959-437e-a935-4addf0d3f886");
string expected = @"C:\path\to\MoleHill\src\MoleHill.Rhino\bin\Debug\net7.0\MoleHill.Rhino.rhp"; // your clone
var plugin = Rhino.PlugIns.PlugIn.Find(id);
if (plugin == null)
{
    var result = Rhino.PlugIns.PlugIn.LoadPlugIn(expected, out id);
    Console.WriteLine("result=" + result);
    plugin = Rhino.PlugIns.PlugIn.Find(id);
}
if (plugin == null || !string.Equals(plugin.GetType().Assembly.Location, expected,
    System.StringComparison.OrdinalIgnoreCase))
    throw new System.Exception("The exact plugin build is not loaded.");
Console.WriteLine(plugin.GetType().Assembly.Location);
Console.WriteLine(plugin.GetType().Assembly.ManifestModule.ModuleVersionId);
```

Then verify two things, because `LoadPlugIn` reports `Success` for an already-registered plugin
whether or not this session actually loaded it:

- `get_commands(filter: "mh")` lists the MoleHill commands. Note that this also lists commands from
  *registered but unloaded* plugins, so it is necessary, not sufficient.
- Read the command history for a load refusal:

  ```csharp
  Console.WriteLine(Rhino.RhinoApp.CommandHistoryWindowText);
  ```

  On 2026-10-18, the exact instance and command ID existed despite the startup block, but the registered
  command would not execute until `Rhino.PlugIns.PlugIn.LoadPlugIn(id)` returned `true`. The path overload
  instead reported "ID already in use". Confirm command execution after loading by ID; an instance alone
  does not prove that its `OnLoad` completed.

  A `Blocking plug-in MoleHill.Rhino.` line requires checking the loaded instance, not assuming success
  or failure from history alone. In the 2026-09-10 smoke run, that startup line coexisted with a loaded
  instance at the exact build path. If the instance is absent or points elsewhere, the build remains
  unverified. Inspect registration and Mark of the Web (`Get-Item <file> -Stream Zone.Identifier`)
  before choosing a repair; never test another installed version as though it were the new build.

## 4. Build the scene and drive the command as data

Create known geometry and set the selection from script, so the test states its own preconditions:

```csharp
var pts = new System.Collections.Generic.List<Rhino.Geometry.Point3d>();
for (int i = 0; i <= 20; i++)
{
    double x = i * 5.0;
    pts.Add(new Rhino.Geometry.Point3d(x, 0, System.Math.Sin(x * 0.05) * 6.0));
}
var gid = __rhino_doc__.Objects.AddCurve(Rhino.Geometry.Curve.CreateInterpolatedCurve(pts, 3));
__rhino_doc__.Objects.Select(gid);
__rhino_doc__.Views.Redraw();
Console.WriteLine(gid);
```

Then `run_command(slot, "mhInspectCurve")`. **Before running any command that picks objects, read its
`GetObject` setup.** `mhInspectCurve` calls `EnablePreSelect(true, true)`, so a pre-selected curve is
consumed immediately and the call returns `Done.`. A command that does not pre-select will sit at its
prompt, block the tool call for 120 s, and leave the slot wedged.

Query results with `list_objects` / `get_context` / `run_csharp` rather than inferring them from a
screenshot.

## 5. Visual verification

`get_viewport_image` captures the **viewport only** — meshes, curves and display colour — and its
metadata block (camera, framed bounds, on-screen object count) diagnoses an empty capture without
re-shooting.

**It is not usable for conduit-only output.** The emptiness guard counts *document objects*, not what is
on screen: with a terrain preview conduit actively drawing and every document object hidden, the call
returns `"Viewport is empty — no document objects intersect the view frustum"` and **no image at all**
(verified 2026-09-13 — `totalObjectCount: 0`, while the same build reported `1 preview outputs`). To
capture preview output, bake it first or leave a document object in frame; and note that baking a terrain
with a preview-colouring analysis active bakes the per-face colour mesh (three vertices per face), not
the welded TIN.

Keep the image small. A 640×640 capture came back as ~254k characters, over the tool-result limit, and
was spilled to a file; 220×220 distinguishes a populated viewport from an empty one, and the metadata
answers most questions without an image at all.

Eto panels and modeless forms are separate top-level windows and are *not* in that image. Capture
them from their real window bounds — never from guessed desktop coordinates:

```powershell
# EnumWindows filtered to the slot PID → GetWindowText + GetWindowRect
# then System.Drawing CopyFromScreen over that rect → PNG → read the PNG
```

The window rect is authoritative evidence that the form exists, where it is and how large it is, and
resize behaviour can be checked by re-reading the rect after a resize. `CopyFromScreen` reads the
physical screen, so the window must be unobscured on an unlocked session; if the capture comes back
blank, treat that as *unresolved* — confirm through a second channel (walk the control tree via
`run_csharp`, or ask for manual inspection) before reporting either success or a paint bug.

### Panel design checks (worktree builds)

Used for the 2026-11-22 card header / input / number-field review against the Penpot boards.

1. Build **only** `src/MoleHill.Rhino` (`-c Release`). A solution build also rebuilds Grasshopper and
   copies its `.gha` over `%AppData%\Grasshopper\Libraries`; restore that copy if it happens.
2. Rhino loads MoleHill from `HKCU\Software\McNeel\Rhinoceros\8.0\Plug-ins\0c0b9e83-4959-437e-a935-4addf0d3f886\PlugIn`
   → `FileName`. Point it at the worktree `.rhp`, `spawn_slot`, confirm the loaded path and MVID (§3), and
   **restore the original value immediately**: the running slot keeps the build it loaded.
3. Host a fresh `MoleHillPanel` in an Eto `Form` at the review widths (240 and 416) and call
   `Rhino.UI.EtoExtensions.UseRhinoStyle(form)`. Without it, icons render blank and the stack background is
   light: harness artefacts, not panel bugs. Set WPF `RenderOptions.ProcessRenderMode` to `SoftwareOnly`
   in the slot so `PrintWindow` captures paint (§5).
4. Change panel state through the panel's own objects (for example its `_modifierCards`
   `CardStack.ToggleCollapsed`), never simulated input.
5. Input-style scan: build every tab (`Rebuild*Layout` with the selected terrain), walk each `CardStack.Layout`
   and the panel's `Children`, and count `TextBox`/`NumericStepper`/`TextArea`/`ComboBox` whose
   `BackgroundColor` is not `UiTheme.InputBackground`. Expected: 0. The first run found 1 of 91 (the
   terrain selector), which is why the fixed top section is styled as one tree.
6. Culture: the script host runs with the invariant culture while the panel runs with the Windows locale.
   Set `CultureInfo.CurrentCulture` explicitly (e.g. `fo-FO`) around any typed-input probe.

### Theme switching (light / dark)

Switch with `AppearanceSettings.SetToLightMode()` / `SetToDarkMode()` in the slot. **Appearance is the
user's shared Rhino setting and a closing slot saves it.** `GetCurrentState()` / `UpdateFromState()` do
**not** restore the paint colours (panel background, text): after them a fresh Rhino still started with a
light panel background. Restore with `SetToDarkMode()` (or `SetToLightMode()`) matching what
`GetPaintColor(PaintColor.PanelBackground)` read before the test, then confirm in a fresh slot.

PrintWindow captures the docked panel blank. Render it instead: `RenderTargetBitmap` over the panel's
`ControlObject` (by reflection), then composite onto the theme's `PanelBackground` colour - the panel's
root is transparent, so a bare render shows Rhino's background as white.

## 6. Acceptance sequence for the curve-review workflow

`mhInspectCurve` is **inspection only**: the form reports on a curve and never changes its geometry.
Regrading, offsetting and smoothing belong to the tools that shape the design (`mhSlopeCurve`,
`mhSlopeCurveSection`, `mhOffsetFeature`, `mhDrapeCurve`, the grading modifiers), and the form re-reads
the curve after they run. The acceptance sequence therefore checks that it inspects correctly and that
it leaves the curve alone:

1. `mhInspectCurve` on a pre-selected curve opens the form, and Rhino stays responsive (`get_context`
   still answers).
2. The form window exists at sane bounds. Resize it and re-read the rect.
3. The checks, limits, measurements, events and profile all populate for the curve. Record the curve's
   geometry before opening (for example `Curve.PointAt` at a set of parameters, or the control points)
   and confirm it is identical after every step below. **The form must never modify the curve.**
4. Change a rule's mode (Off / Report / Warn) and its threshold. Confirm the check row and the warning
   count update, and that the setting is still there after closing and reopening the form (rules are a
   per-user preference).
5. Edit the curve with another command while the form is open. Confirm the form refreshes to the new
   shape on its own, without taking ownership of the edit.
6. Toggle Ribbon, Labels, Events and Terrain. Confirm only the conduit display changes.
7. Use **Label curve**. It is the one action that writes to the document: it *adds* annotation objects
   in a single undo record and does not touch the curve. Confirm the labels appear, then `_Undo` once and
   confirm they are gone and the curve is unchanged.
8. Run `mhInspectCurve` again on a second curve and confirm the same form retargets (the `SetObject`
   path, not a second form).
9. Close the form. Confirm the conduit and the refresh timer are torn down.

## 7. Known failure modes

- **`spawn_slot` failing with Windows error 5 ("breakaway not permitted") is the host, not this
  procedure.** The router launches Rhino with `CREATE_BREAKAWAY_FROM_JOB` so the new process outlives
  the tool call. Whether that is allowed is decided by the Job Object the router itself was created in,
  and the router is a child of whichever agent host started it — each host spawns **its own** instance
  (measured 2026-09-14: two routers parented to `claude.exe`, eight to one `codex.exe`, all in jobs).
  A host that creates its children in a job without `JOB_OBJECT_LIMIT_BREAKAWAY_OK` — typical of a
  sandboxed runner, which wants killing the agent to kill everything it started — gets `ERROR_ACCESS_DENIED`
  before Rhino ever starts, and `list_slots` shows nothing.

  The build path and the `version: "8"` string do not fix the launch restriction, so **do not
  re-verify the build or retry with different arguments**. First try the standalone client through
  approved host execution (§2), keeping that router alive to own and close the slot. If that route
  is also blocked, use the user-started test-session route in §2: have the user start Rhino and run
  `MCPStart`, then call `list_slots` to adopt it. This avoids
  launching a process through the restricted router, but requires user-managed window cleanup before
  rebuilding. If no session is advertised, report that live testing remains unverified and ask for
  the startup command output, or hand testing to a host that permits breakaway.
- **Modal-dialog commands wedge the slot.** `_PlugInManager`, `_Options` and friends open a dialog;
  `run_command` never returns, and after aborting the tool call the slot stays inside a command.
  Recovery is `close_slot` + `spawn_slot` — there is no in-place unwedge. Use script APIs, or the
  dash form of a command, instead.
- **Running a Rhino command from inside `run_csharp` wedges the slot too.** `RhinoApp.RunScript(...)`
  within a script call — even the dash form, even one that opens no dialog — blocked for the full 120 s
  and left the slot unusable. The two mechanisms do not nest: `run_command` for commands, `run_csharp`
  for RhinoCommon, never one inside the other.
- **Anything that replaces the document detaches the slot, and the detached Rhino cannot be closed.**
  `run_command(slot, "_-Open …")` reports the file read successfully, and then the *next* call fails with
  `rhino_closed` — "its document or the Rhino window was closed" — and the slot is pruned. The process
  does not exit. It is later re-adopted (§2), so `close_slot` refuses it, and it holds the build lock
  until stopped by PID. `_-New` is the same shape.

  So **do not reopen a document inside a live test.** To check that state survives a save and reload,
  either serialize and deserialize in-process (`TerrainSerializer.Serialize` → `Deserialize`, the same
  code path the document read uses), or spawn a *fresh* slot whose first action is the open — accepting
  that the slot is then single-use.
- **`Unknown command: _-ScriptEditor` from `run_python`/`run_csharp` is the wedge symptom**, not a
  missing script editor. Respawn the slot and the same script runs.
- **The C# script host has a minimal `using` set** — no `System.Linq`, so use `foreach` or fully
  qualify. Use the injected `__rhino_doc__`, never `RhinoDoc.ActiveDoc` or `scriptcontext.doc`.
- **A `Success` return is not evidence a plugin loaded.** See step 3.

## 8. Isolate a crash before changing code

When Rhino exits, narrow the boundary first. Add temporary, timestamped trace lines around
`GetObject.Get`, form construction, `form.Show()`, positioning, conduit enable and timer start; remove
them immediately after diagnosis. Reproduce in normal mode, then compare against `/safemode` (launch
that one manually — the router spawns normal mode).

- A managed exception caught at a trace boundary is an application/UI lifecycle bug: capture its type
  and message and fix that boundary.
- A process exit with no managed trace is a native host/plugin failure: record the slot PID and exit
  time and inspect `%TEMP%`, `%LOCALAPPDATA%\CrashDumps`, `C:\ProgramData\Microsoft\Windows\WER\` and
  the Application event log around it. Do not assume the WER helper process is the crashing component.

Keep the reproduction minimal: one curve, one command, one transition.

## 9. Eto/Rhino lifecycle checklist

- Show modeless document-parented forms through `Application.Instance.AsyncInvoke` after the command
  returns; set `Owner = RhinoEtoApp.MainWindowForDocument(doc)` and apply `UseRhinoStyle`.
- Start timers and enable display conduits from the form's `Shown` event, not during construction.
- Treat `SystemFonts.Default` and other shared Eto resources as borrowed; never dispose them from a
  paint pass. Dispose only resources the control owns.
- Guard refresh/paint paths while closing; stop the timer, disable/dispose the conduit, and dispose
  the edit session exactly once.
- Keep edits provisional until Apply, and commit the document mutation in one Rhino undo record.

## 10. Cleanup and evidence

`close_slot` the exact owned slot (leave a user-started test session open as described in §2),
delete temporary traces, rebuild if code changed, and run the relevant
tests (normally the full solution). Finish with `git status --short` and `git diff --check`.

**Then check no owned test Rhino is left running.** `close_slot` reporting `closed: true` only accounts for the slot
named; `list_slots` plus the `Get-CimInstance` query in §2 accounts for the processes. A leaked
router-spawned Rhino is invisible until the next build fails on a file lock, by which point the cause is
several steps behind.

The report should include: Rhino version and slot id/PID, plugin build path and the load-verification
result, the exact command/script sequence, what was queried to confirm each assertion, which claims
rest on a screen capture and which on document state, any wedge/respawn, the cleanup result, and the
test commands and counts.
