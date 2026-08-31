# mhInspectCurve review — 2026-08-31

Code review plus live Rhino verification of the `mhInspectCurve` command and the curve-review stack
behind it (`CurveReviewService`, `CurveReviewAnalyzer`, `CurveReviewForm`, `CurveReviewConduit`,
`CurveProfileControl`, `CurveProfileEditSession`, `CurveReviewLabeller`).

Branch: `layer-role-routing`. Reviewed at plugin version 0.14.1.

## Resolution

All findings below were addressed on 2026-08-31:

- untouched edit sessions now remain clean, and both discontinuity collectors exclude domain ends;
- plan stationing uses an exact, reusable World-XY projection and native length/parameter queries;
- terrain gaps require a terrain mesh, and adjacent raw-grade threshold crossings merge into one PI event;
- true plan corners are reported as `corner` and excluded from finite-radius sampling;
- every viewport label uses invariant numeric formatting and one shared collision set covers span, event and
  violation labels;
- rule-row refreshes are deferred until the originating Eto event has unwound, brush strokes always emit
  their end gesture, and paint-pass pens are disposed;
- the linear span/event searches, repeated bisection/chord station lookup, unused evaluation overload,
  orphan XML summary and zero fallback radius station were removed.

Validation after the fixes: `dotnet build MoleHill.sln --no-restore` succeeded with no warnings or errors;
`dotnet test MoleHill.sln --no-restore` passed Core 560, Grasshopper 35 (14 skipped), and Rhino 321
(98 native-runtime skips). Focused Rhino-native regression tests were added for exact polyline plan length,
clean initial edit state, endpoint-free kink counts, semantic plan corners, no-terrain event suppression,
single-PI break reporting and unavailable minimum-radius station. They remain `[RhinoNativeFact]` because
the standalone test host cannot initialize Rhino's native runtime.

## Post-fix live UI verification

Repeated on 2026-08-31 in disposable Rhino 8 slot `aardvark` (PID 31556) against the exact Debug
assembly at `src/MoleHill.Rhino/bin/Debug/net7.0/MoleHill.Rhino.rhp`; the project build completed with
zero warnings and errors before loading it.

- A smooth interpolated curve opened clean (`HasChanges=False`), with zero kinks, zero events and no
  terrain gap. Untouched Apply left its runtime serial unchanged.
- A 125-unit polyline reported exact plan length 125, two semantic plan corners and no finite sampled
  radius. A separate vertical PI reported one break at station 100 (`25.0% to -10.0%`) and no plan
  corner.
- Changing a check mode kept the originating `DropDown` alive through its event handler, then rebuilt
  it asynchronously with the new value preserved and `_ruleRefreshPending=False`.
- Brush release outside the plot and the no-capture mouse-leave fallback both closed the stroke; the
  two gestures produced two undo entries rather than merging.
- The real Label workflow accepted a constrained viewport pick and created one `TextDot` (`5.38`) on
  `MoleHill::Annotation::Labels`, then restored interactivity while leaving the edit session clean.
- A +5 preview produced only floating-point-noise XY drift (`2.93e-14`), Apply replaced the document
  object, and Rhino Undo restored the original with zero sampled deviation.
- The document-parented window resized from 470x700 to 620x820. Close disabled the conduit, disposed
  and nulled the edit session, removed the inspector window, and left Rhino responsive.

Native window capture again returned blank client pixels even though the Eto controls were populated,
visible and responding to their real event paths. Per `docs/rhino-live-testing.md`, pixel-level appearance
remains unverified rather than being reported as either a success or a rendering bug.

## Method

- `dotnet build MoleHill.sln` — clean.
- `dotnet test` — Core 560 passed, Grasshopper 35 passed / 14 skipped, Rhino 320 passed / 94 skipped.
  Of the six new curve tests, the three `CurveProfileEditSessionTests` are `[RhinoNativeFact]` and
  **skip** without the native runtime, so only `CurveReviewRulesTests` actually executes.
  `CurveProfileEditSession` has no running coverage in a normal `dotnet test`.
- Live, per `docs/rhino-live-testing.md`: disposable Rhino 8 slot `aardvark` (PID 34880), plugin
  loaded by path from script and verified through `PlugIn.Find(...).Assembly` (build stamp 11:41,
  2026-08-31). Scene: one interpolated degree-3 curve and one 6-point kinked polyline whose true
  plan length is 146.867.
- Assertions are on document state and reflected object state unless noted otherwise.

---

## Blocker — the Label button never works, and Apply mutates unedited curves

`CurveProfileEditSession.HasChanges` (`CurveProfileEditSession.cs:37`) counts
`_breakStations.Count > 0`. `_breakStations` is seeded at construction by `CollectExistingBreaks`
(`:259`), which walks `Continuity.G1_locus_continuous`. **Locus continuity reports the curve's own
endpoint as a discontinuity** — confirmed live, `GetNextDiscontinuity` returns `t = 20` (= `Domain.T1`)
on a perfectly smooth interpolated curve.

So every session opens with `HasChanges == true` and zero edits. On a freshly opened inspector:

```
session.HasChanges=True  AddedControls=0  AddedBreaks=0
LabelCurve() >>> "Apply or reset the profile preview before placing labels."
textDotsInDoc=0
```

`ApplyToDocument()` on that same untouched curve went through: object runtime serial **52 → 179**,
replacing the curve with a NURBS conversion and pushing an undo record for no user edit.

**Fix (both parts needed):**

1. `HasChanges` should test `AddedBreakCount`, not `_breakStations.Count`.
2. The discontinuity walk should drop parameters at the domain ends, or use `Continuity.G1_continuous`.

The same `_locus_` call is in `CurveReviewAnalyzer.CollectKinkParameters`
(`CurveReviewAnalysis.cs:293`), so **`KinkCount` is inflated by one on every open curve** — the smooth
curve reports 1 kink, the 4-corner polyline reports 5.

---

## High

### Plan length is a fixed 32-chord approximation

`GeometryCommandAlgorithms.CalculatePlanLength:210` always subdivides into 32 chords regardless of
geometry. On the test polyline the panel reports `Plan 145.39` against a true **146.867** — 1.0%
short — while the `3D 148.70` printed beside it is exact (`Curve.GetLength()`). Two numbers on one
line, one exact and one not, produced by different methods.

Every station value inherits the error, and `CurveReviewRun.PlanLength` (summed sample chords)
disagrees with `analysis.PlanLength` elsewhere in the same UI. For a measurement tool this is the
first thing to fix after the blocker.

### A terrain gap is reported when there is no terrain

`CollectEvents` (`CurveReviewAnalysis.cs:510-528`) runs the gap scan unconditionally. With no terrain
mesh: `TerrainSamples=0`, `TerrainMisses=0`, the Terrain coverage check correctly reads `N/A` — and an
`off terrain` event still lands in the event list, on the profile chart, and in the viewport overlay.
The gap scan needs the same "is there a terrain at all" guard the coverage check already has.

### One grade break reports as three

`SmoothGrades` is a 3-point moving average, so a single PI smears across three intervals and each
transition trips the threshold:

```
Sta 65.6  break 25.0% to 14.4%
Sta 66.1  break 14.4% to  3.9%
Sta 66.3  break  3.9% to -6.7%
```

All three describe the same PI at `(60,80,12)`. The intermediate grades (14.4%, 3.9%) are smoothing
artifacts — grades the curve does not have anywhere. `VerticalBreakCount` and the check's occurrence
count are inflated roughly 3×. Vertical breaks want run-merging like `CollectRuns` already does for
grade and radius.

---

## Medium

- **Plan radius at a corner is a sampling artifact.** `ComputePlanRadii` takes the circumradius of
  three consecutive *samples*; at a polyline corner that collapses toward zero and scales with sample
  spacing — reported `R 0.287` here, and denser sampling would report less. Every polyline alignment
  therefore fails Minimum plan radius permanently and gets a `SharpRadius` event per corner. A corner
  genuinely has no radius, but the check should say "corner" rather than print a number that carries
  no information.
- **The overlay mixes number cultures in one picture** — `R 0,6 < 25,0` (event labels precomputed on
  the UI thread) beside `24.96% > 12.00%` (conduit strings formatted at draw time on the display
  thread). Overlay text should pick one explicit culture.
- **Label pileup.** `TryOccupy` collision avoidance covers only span grade and elevation labels; event
  dots and violation-run dots stack freely. At a normal zoom six labels overlap into an unreadable
  block (observed in the viewport capture).
- **Check rows rebuild themselves from inside their own event handlers.**
  `mode.SelectedIndexChanged` and the threshold's `LostFocus` both run
  `SaveRules → RefreshReview(true) → RebuildChecks`, which clears `_checks.Items` and recreates the
  control that is mid-event. That is the classic Eto/WinForms hazard, and even benignly it steals
  focus while the user is typing a limit.
- **Brush strokes can merge.** Release the mouse outside the plot: `OnMouseLeave` clears
  `_hoverStation`, so `OnMouseUp`'s `else if (_hoverStation.HasValue)` never fires the End gesture,
  `_strokeOpen` stays true, and the next `BeginBrushStroke` early-returns without pushing an undo
  record — two strokes collapse into one undo step.
  (`CurveProfileControl.cs:170-200`, `CurveProfileEditSession.cs:60`.)
- **Pens leaked in the paint pass.** `PaintProfile` and `PaintStructuralBreaks` allocate
  `new Pen(...)` per line per paint and never dispose them — roughly 400 per repaint at 4 Hz.

---

## Low

- `CurveReviewAnalyzer.StationAt` is a linear scan per span; `DrawEvents` does `MinBy` plus a second
  linear scan with record-struct equality per event.
- `TryParameterAtPlanStation` costs about 1050 `PointAt` evaluations per call (32 bisections × 33
  chords), and `RefineForRange` calls it up to 65 times per edit.
- `CurveReviewService.Evaluate(doc, Guid, ...)` is unused.
- `CurveReviewLabeller.cs` carries an orphan `<summary>` describing a sublayer constant that no longer
  exists.
- `MinimumPlanRadiusStation` stays 0 when no radius is finite.

---

## What holds up

Core edit invariants are correct, checked against document state rather than by eye:

| Check | Result |
|---|---|
| `ApplyOffset(+5 over sta 30–90, exit 10)` — plan XY drift | **0.000000** |
| same — max ΔZ | exactly 5.0000 |
| `Undo()` — max deviation from original | **0.000000** |
| `ApplyGrade(5%)` — realised grade sta 40 → 80 | 4.994% |
| `SetObject` retarget | same form reused; header → `Curve 1d782e43`, 0–108.1, "1 warning" |
| Resize | `GetWindowRect` 470×700 → 620×820 |
| Teardown on Close | `conduit.Enabled False`, session disposed and nulled, `OpenForms.Count=0`, no orphan windows |
| Responsiveness | Rhino answered scripts throughout; command returned `Done.` on preselect |

## Unverified

The form's on-screen appearance. `CopyFromScreen` over the window's real rect returned blank white on
two attempts, but the full-desktop capture shows the surrounding Rhino viewport as solid black as
well, so that session was not compositing normally — treated as a capture artifact, not a paint bug.
The second channel (walking the Eto control tree) shows all 91 controls present, visible, and carrying
correct sizes and text, so the form does build its content.

## Cleanup

Slot `aardvark` closed via `close_slot`. No code changed: `git status --short` shows only the
pre-existing branch modifications, `git diff --check` clean.

---

## Follow-up — the profile-editing stack was removed (2026-08-31)

The findings above were all fixed, but most of them lived in the same place: the preview edit session.
On review of the panel against its own design reference, the editing half was removed outright rather
than restyled.

Why, in short: the reference design is a read-only instrument, and interleaving fourteen edit controls
between the plot and the checks is what destroyed the panel's hierarchy. Beyond layout, the edit session
kept its own undo/redo stack over a working NURBS curve and only committed on Apply — two Undo meanings
alive at once, with Apply/Reset/Undo edit/Redo existing only to disambiguate them — and it wrote straight
to the document, so a curve feeding a Grade Path could be rewritten behind the terrain definition that
consumes it. Its coverage was also the thinnest in the stack: `CurveProfileEditSessionTests` were entirely
`[RhinoNativeFact]` and never executed in a normal `dotnet test`.

Deleted: `StationProfileEditor.cs`, `CurveProfileEditSession.cs`, their two test files, the five
edit-tool glyphs in `PanelButtonIcons.cs`, the `AddedControlPointWarning` rule, and the brush/range/break
gesture half of `CurveProfileControl.cs`.

Reworked: the panel is now a reading order of titled blocks (profile, checks, measurements, events,
display) with rule thresholds behind a disclosure; profile colour is selectable (grade, elevation,
cut/fill, plan radius) and comes from one `CurveReviewMetricSeries` shared with the conduit; and the
conduit draws its whole pass with depth testing off, which fixes the terrain burying the ribbon, drape and
scrub marker (only the label dots had been depth-exempt before).

If vertical-profile editing is wanted later it belongs on the Grade Path modifier — editing the
definition, rebuilding through the pipeline, with Rhino's undo as the only undo.

### Live verification of the rebuild

Disposable Rhino 8 slot `aardvark` (PID 35648), plugin loaded by path and confirmed through
`PlugIn.Find(...).Assembly` (build stamp 22:01, 2026-08-31): `CurveReviewMetricSeries` present,
`CurveProfileEditSession` absent. Note this machine carries a second, stale, load-protected registration
of `MoleHill.Rhino` which logs `Blocking plug-in MoleHill.Rhino.`; the live plugin is the one under
`0c0b9e83-…`, loaded from `bin/Debug/net7.0`, so that line is not a load failure of the build under test.

Scene: a sine-profile curve (z −14 … +14) crossing a flat mesh slab at z = 0, so half the curve lies
under the mesh — the Grade-Path-inside-its-own-terrain case.

- **Depth fix.** Sampling the captured viewport at 400 points along the curve, with a 7×7 neighbourhood
  test for a saturated (non-grey) pixel: conduit on → 200/200 above the mesh and **186/186 below** it;
  conduit off → 0/200 and 0/186; restored → 200/200 and 186/186. Bare mesh reads (138,139,141) grey, and
  the under-mesh curve pixels read (234,112,66), (137,196,101), (237,160,69) — ramp colours. The overlay
  draws through the mesh along its whole length. (This verifies the new behaviour; it is not a
  before/after diff against the old build.)
- **Metric switching** drives both surfaces and visibly recolours the curve. At the same point under the
  mesh: Grade (137,196,101); Elevation (47,87,140) = the low end of the elevation ramp, correct for the
  curve's lowest point; Cut/fill (150,150,156) = exactly `NeutralColor`, correct because no MoleHill
  terrain is present so the series has no readings; Plan radius (86,196,116) = exactly `GradeOkColor`,
  correct because the curve is straight in plan (infinite radius → tightness 0).
- **Panel state.** Analysis built (plan length 200.0, 4 checks, 2 events, 201 plan radii). Every edit-stack
  field (`_session`, `_offset`, `_radius`, `_strength`, `_toolStrip`, `_crossBreaks`, `_maxPoints`) is gone
  from the live form.
- **Scrub** set and cleared through the conduit with clean redraws.
- **Teardown** on Close: conduit disabled, analysis nulled, no inspector window left, Rhino responsive.

Panel *appearance* remains unverified: as before, native window capture of the Eto client area returns
blank pixels on this machine. Layout was checked through the live control tree, not pixels.

### Correction — depth testing was only half the fix

The first pass at the occlusion bug moved the whole overlay behind `PushDepthTesting(false)` and verified
it against a curve buried in a plain Rhino mesh object. That test passed and the bug persisted in the real
product, because the scenario it exercised was the wrong one.

A MoleHill terrain preview is **not** a document object — `TerrainDisplayConduit` draws it in
`PostDrawObjects`, the same channel `CurveReviewConduit` was using. Two conduits sharing a channel paint
in registration order, and turning depth testing off does nothing about a *later painter*: the terrain
mesh was simply drawn on top of the finished overlay. Measured on a real terrain preview with a draped
curve, the ribbon was visible at **3 of 164** sampled points.

The fix is the draw channel. `CurveReviewConduit` now overrides `DrawForeground`, which runs after every
conduit's `PostDrawObjects`, so the inspector wins regardless of registration order. The depth flags are
still needed — they handle the depth buffer — but they are not sufficient on their own.

Re-measured on the same scene (terrain preview of 288 faces, curve draped on the surface, 201 terrain
samples, 24 spans, 8 events):

| | conduit off | conduit on |
|---|---|---|
| ribbon along curve | 0/220 | **210/220** |
| elevation dots | 0 px | 350 px |
| grade dots | 250 px (background) | 5414 px |
| event dots | 0 px | 1491 px |
| break dots | 0 px | 438 px |
| cut ties | 0 px | 1225 px |
| fill ties | 0 px | 847 px |

**Methodology note for future overlay work:** a plain mesh object is not a valid stand-in for a MoleHill
terrain when testing draw order. Build the terrain through `TerrainController.CreateTerrainFromPointIds`
plus `RebuildTerrain` (poll `PeekFinalTerrainMesh` until non-null; the build is asynchronous), so the
preview conduit is actually live.
