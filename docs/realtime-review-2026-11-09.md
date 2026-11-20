# Realtime review: the whole edit loop (2026-11-09)

How far MoleHill is from feeling realtime on **a medium terrain with a mix of modifiers and a light
analysis**, what structurally holds it back, and how much is left to win. Section 7 is a correctness
list for a smaller model.

This builds on three earlier documents and does not repeat them:
[interactive-terrain-plan-2026-09-16.md](interactive-terrain-plan-2026-09-16.md) (targets, the
interaction design), [incremental-rebuild-design-2026-09-29.md](incremental-rebuild-design-2026-09-29.md)
(exact incremental builds) and [performance-roadmap-2026-10-01.md](performance-roadmap-2026-10-01.md)
(park-scale levers). Those were aimed at a first wall workflow and at a 6-million-face park. Nobody
has yet asked the question for the ordinary middle case. Scatter has its own sub-review:
[scatter-realtime-review.md](scatter-realtime-review.md).

**Nothing was measured in this pass.** Rhino's MCP router was down. Every number is from
`tests/perf-baselines/hosted-perf.json` (recorded at a7416b6, 9 samples, worker time only) or from
the earlier documents, and is labelled. The machine has since been reading about 1.45 times slower
than that baseline (noted 2026-10-21, cause not found), so treat the baseline figures as the
optimistic end.

## 1. The reference case

No fixture matches the question, so this review defines one. It should become a lane scenario
(Phase 0).

- 50,000 to 100,000 faces.
- Triangulate, Grade Pad, Grade Path, Retaining Wall, Smooth, Remesh.
- One colouring analysis (Slope) and contours.

The target is the one already written down: **input to visible within 66 ms at p95 during a
gesture, exact settlement within 200 ms after release.**

## 2. Verdict

It depends almost entirely on **which card is edited and what sits below it**, and on **how the
edit arrives**. Three cases.

| Edit | Worker time today (baseline) | Edit to visible | Distance from 66 ms |
|---|---|---|---|
| Last card, nothing heavy below (a wall rail at the end of the stack) | 13 ms at 25k faces, 18 ms at 51k, 39 ms at 100k (measured) | unmeasured; publication is extra | Reachable now at 25k to 50k with input and scheduling changes only |
| Mid-stack edit with Remesh below (a pad elevation) | 351 ms at about 100k faces (measured: Pad 84, Path 38, Smooth 14, Remesh 214), plus a wall insert of about 30 and Slope of about 45 (scaled from other fixtures) | roughly 0.5 to 0.7 s once publication is added (estimate) | 8 to 10 times away. Not reachable exactly. Reachable as an approximate frame during the gesture with exact settlement after. |
| Dragging source geometry in Rhino (a contour, a pad boundary curve) | same as above | nothing moves until the drag is released | No continuous feedback exists at all. This is an input-model gap, not a speed gap. |

Two things are true at once. The pipeline is in much better shape than it was in September: a warm
edit on a 100,000-face terrain was 452 ms then and is 39 ms now on the same fixture. And the
ordinary mixed stack is still an order of magnitude from realtime, because the work that made the
wall fixture fast (windowed insertion on the last stage) does not help a stack whose last stage is
a global one.

The honest ceiling: **exact** 66 ms frames for a mid-stack edit at 100,000 faces with a remesh below
are not available from this architecture and should not be promised. A **bounded approximate frame
at 30 Hz with exact settlement in 150 to 300 ms** is, and most of the machinery for it already
exists unused.

## 3. Where the time goes in the reference case

Composed from per-stage medians on different fixtures, so it shows the shape, not a budget.

| Step | Cost | Scales with | Source |
|---|---|---|---|
| Slider trailing wait | 120 ms, and restarts on every tick | fixed | code |
| Snapshot, cache copy, dispatch | 1 to 6 ms | sources | measured 09-19 |
| Grade Pad (edited) | 84 ms | window + whole-mesh setup | baseline |
| Grade Path (below it) | 38 ms | whole-mesh setup | baseline |
| Retaining Wall insert | about 30 ms | window + mesh fill | baseline, other fixture |
| Smooth | 14 ms | whole mesh | baseline |
| Remesh | 214 ms | nearly whole mesh at this size | baseline |
| Slope | about 45 ms | whole mesh | scaled from 110 ms at 244k |
| Publication: recolour, shading copy, GPU upload, redraw | unmeasured today; 73 ms redraw at 49k and 692 ms recolour at 244k in September | whole mesh | old trace |

Remesh is 61 percent of the measured part. Publication is the largest unmeasured part.

## 4. Structural blockers

Ordered by how much each one limits the verdict.

### B1. There is no continuous input

- **Panel sliders use a trailing debounce.** `slider.ValueChanged` does `timer.Stop(); timer.Start()`
  on a 120 ms timer (`MoleHillPanel.Editors.cs`, `UiTiming.SliderCommitSeconds`). While the slider
  keeps moving nothing is committed. The scheduler below it has a leading-edge debounce, a policy
  that lets overtaken builds finish, and a path that publishes them as frames, and none of it ever
  sees a stream. A slider drag is one edit, 120 ms after the hand stops.
- **Rhino object edits arrive on commit.** Moving a contour or a pad boundary raises events when the
  drag ends. Only Sculpt and Relax Route run their own `GetPoint` session and so can follow the
  cursor.

Until this changes, every other improvement makes the wait after an edit shorter but does not make
anything follow the hand.

### B2. There is no interactive tier

`TerrainBuildMode.Preview` exists, relaxes Remesh, and is already refused by bake, interop and the
Grasshopper bridge. It is still never dispatched: both `QueuePendingBuild` call sites pass `Final`
(`TerrainController.Build.cs:40`, `TerrainController.RebuildState.cs:70`). So every sample of a
gesture is a full exact build including analyses, zones, markers, scatter and report tables. This
was found on 2026-09-19 and is still open.

The exact pipeline cannot reach 66 ms on the reference case (B3, B4). An approximate frame can. This
is the single largest lever.

### B3. The stack is linear and hands over whole meshes

Each stage takes a whole Rhino mesh and returns one. An edit to card 2 of 6 re-runs cards 2 to 6.
Windowed graders and wall insertion reuse their expensive inner work, but each stage still pays
whole-mesh costs around the window: array extraction, normalisation, a Rhino mesh fill, normals, a
content hash, boundary loops, face grids, constraint setup. The roadmap measured this at park scale
("every grading window was reused; what remains is whole-mesh work around the windows"). At 100,000
faces those floors are 10 to 40 ms per stage, which across five stages is the whole budget before
any real work.

Open levers, already designed: flat stage data and lazy Rhino meshes (incremental design, Layer 1;
roadmap lever 3), per-window constraint setup (roadmap lever 2), dirty-region splice (Layer 2 R1,
never built; windows replaced it for pads only).

### B4. Remesh is a global stage at the bottom of the stack

On the pad edit, Remesh costs 214 ms against 243 ms cold, so its incremental path saves about 12
percent at this size. Likely reason, to verify from the stage's own tile-reuse report: a tile is 64
target edges across, roughly 9,000 faces, so a 90,000-face terrain is about ten tiles per pass, a
pad's reach touches several, and three offset passes spread the change further. Tiling was built for
the park, where an edit touches 10 of 2,134 tiles. At medium scale the tile is the terrain.

Options, none free: skip or hold Remesh during a gesture (B2); smaller tiles below a face-count
threshold (changes output, needs the quality comparison the tiled prototype went through); or
accept it as the settlement cost.

### B5. Publication is whole-mesh work on the UI thread, and is not measured

Every build produces a new terrain mesh object. On the UI thread that then means: recolouring every
face for the active analysis (`TerrainAnalysisPreviewBuilder.UpdatePreviewMesh`), a whole-mesh
content hash (`ComputeMeshFingerprintForDiagnostics` in `ApplySuccessfulBuild`), a new shading copy
with wall seams split (`TerrainPresentationMesh.CreateForDisplay`, built lazily inside the first
draw), and Rhino uploading the whole mesh again because it is a new object. None of it depends on
how local the edit was.

The hosted lane measures the worker only. The claim "100k warm edit is inside the 66 ms target"
in the roadmap is explicitly evaluation time. The last full trace (September) had redraw at 73 ms
for 49,000 faces, which alone exceeds the budget. That trace predates the managed seam splitter, so
the current figure is unknown.

### B6. Tail latency

Baseline p95 against median: geometry-heavy cold 1,134 against 633 ms; pad edit 568 against 351;
Grade Pad 149 against 84. The known cause is large-object allocations waiting on a background
gen 2 collection (about 28 ms a stall). At a 351 ms build that is noise. At a 66 ms frame one stall
is half the budget, and the target is stated at p95. The attribution work in
`handoff-gc-stall-attribution.md` was approved and never started.

### B7. Dependent outputs run on every final build

Analyses, contours, zones, markers, objects, scatter and reports all run after geometry on each
build. Light per-face analyses are cheap (Slope about 45 ms at 100,000 faces) but the colouring is
then computed again on the UI thread for the preview (B5). Interim publication, which shows geometry
before outputs, is gated on the peak output cost seen, so a terrain with only light analyses never
uses it and always waits for them.

### B8. The reference case is not measured anywhere

No lane scenario combines graders, a wall, Remesh and an analysis. No lane measures publication, a
sustained gesture, or frame rate. The baseline is stale by about 1.45 times against the machine. The
plan's own Step 0 list (a five-second sustained gesture, allocation figures, presentation latency
past `Redraw()`) is still open.

## 5. How much is left to win

Estimates for the reference case at about 100,000 faces, to be replaced by Phase 0 numbers.

| Change | Mid-stack edit, during gesture | After release | Kind |
|---|---|---|---|
| Today | nothing until release | 0.5 to 0.7 s | |
| Sliders stream, Preview dispatched, outputs skipped in gesture frames (B1, B2) | about 350 ms frames (3 Hz) | unchanged | scheduling only |
| Gesture frames skip Remesh and everything that only refines (B2, B4) | about 140 ms frames (7 Hz) | unchanged | approximate frame, labelled |
| Gesture frames re-evaluate only the edited card on pinned upstream data, Z-only where topology allows | 30 to 60 ms | unchanged | the interactive plan's Step 5 |
| Publication moved off the UI thread or chunked (B5) | removes an unmeasured 50 to 150 ms from every frame | same | needed above about 50k faces |
| Exact floors: lazy meshes, per-window constraints (B3) | | 350 to about 200 ms | exact |
| Remesh at medium scale (B4) | | a further 100 to 150 ms if tiles can shrink | exact, changes output once |

At 25,000 faces everything is about a quarter of this, and the second row alone would likely feel
live.

So: scheduling and input changes take the product from "nothing moves" to a few frames a second.
Skipping refinement in gesture frames takes it to roughly interactive. True 30 Hz mid-stack needs
the per-card pinned evaluation, which is real work per modifier. Exact settlement can come down
from about half a second to about 200 ms, and no further without rethinking Remesh's place.

## 6. Plan

Each phase ends with a measurement. Phases 1 and 2 are where the feel changes.

**Phase 0. Measure the real case.** Add a `mixed-medium` scenario to `hosted-perf` at 50,000 and
100,000 faces with the section 1 stack. Edits: pad elevation, pad boundary move, wall rail raise,
one survey point, a ramp change. Record publication through the latency trace (it already has
merge, display, sync, redraw marks) so the lane reports edit to visible, not worker time. Add a
five-second sustained slider gesture and report frames shown per second and displayed-revision age.
Find the machine drift or re-record the baseline. Log Remesh tile reuse on the pad edit to confirm
or kill B4's explanation.

**Phase 1. Make input a stream.** Slider rows commit on a throttle (leading edge, then at most one
per frame interval) instead of a trailing timer, and signal gesture begin and end. Gesture samples
dispatch `Preview`; release dispatches `Final`. Undo must record one step per gesture, not per
sample. This is scheduling over parts that exist; the interactive plan's log entry 5 and its
freshness rules apply unchanged.

**Phase 2. Decide what a gesture frame contains.** Per modifier descriptor, declare what Preview may
skip: dependent outputs (already), Remesh and Retopo (hold or skip), Smooth iterations. Show the
frame plain or with the previous colouring marked stale. Measure the visible jump at release, as the
plan requires, before calling any of it acceptable.

**Phase 3. Publication.** Measure first. Then, in order of cost: build the shading copy and the
analysis colouring on the worker; drop the UI-thread mesh hash; if upload dominates above 50,000
faces, chunk the display mesh by the same world tiles Remesh uses so an edit re-uploads a few.

**Phase 4. Native gestures for the common edits.** A grip or `GetPoint` session for pad elevation
and for moving a boundary or rail, modelled on the Sculpt and Relax Route sessions. Without it,
editing source geometry stays commit-only whatever the pipeline costs.

**Phase 5. Exact floors.** Roadmap levers 2 and 3, then per-card pinned evaluation for graders (the
interactive plan's Step 5). Only after Phase 0 shows which floor is largest at this size.

**Phase 6. Tail.** The approved GC attribution plan, scoped to the gesture path.

If only one thing is done, do Phase 1 with the outputs-skipping half of Phase 2. It is the smallest
change and the only one that turns a wait into motion.

## 7. Correctness pass (for a smaller model)

Scope: the paths read for this review, which are the build dispatch and completion code, the
display state, the conduit and the scatter stage. It is not a whole-product audit. Found by
reading, not by running. One item at a time, each with its own test, no refactors folded in.

### From the edit loop

**L1. A slider value is lost if its card unloads inside the commit window (confirmed by reading).**
`MoleHillPanel.Editors.cs`: `slider.UnLoad` and `textBox.UnLoad` call `timer.Stop()` without
committing `pendingValue`. A drag followed within 120 ms by a tab switch or a card refresh is
dropped. The compact ratio editor in `MoleHillPanel.Objects.cs` has the same gap (scatter review
C9). Fix: commit the pending value before stopping, when a slider edit is active. This disappears if
Phase 1 replaces the trailing timer, so do it only if Phase 1 is not imminent.

**L2. The comment on `EnsureBuildWakeTimer` describes a diagnosis that was later overturned
(confirmed).** `TerrainController.Build.cs:54-76` and `:112-123` say the 320 ms wake is "the host's
scheduling" and unresolved. `architecture.md` ("The UI thread is a shared queue") records that it
was the panel's own refreshes. Rewrite the comments to the resolved account; leave the code.

**L3. A failed final build may not refresh the panel (verify).** In `CompleteBackgroundBuildCore`
the `Failed` case sets the message and calls `Save` for a final build, but unlike the cancelled and
superseded cases it never calls `RaiseStateChanged` itself. Check whether `Save` raises it. If not,
a failure message is not shown until something else refreshes. A failed Preview build raises
nothing either.

**L4. Whole-mesh hash on the UI thread per final build (confirmed; cost unverified).**
`ApplySuccessfulBuild` calls `ComputeMeshFingerprintForDiagnostics(build.PrimaryMesh)`. The last
stage already computed an output fingerprint for that mesh on the worker. Check whether the call is
served from a cache; if it rehashes, carry the worker's fingerprint on `TerrainBuildResult` instead.
Output must stay identical, since `sectionReferenceMeshChanged` depends on it.

**L5. `Preview` mode is reachable only from diagnostics (confirmed).** `TerrainController.Diagnostics.cs:167`
and `:192` are the only places that choose it. Not a bug; listed so nobody assumes preview-only
branches in stages are exercised in production. Any Phase 1 work must add hosted tests for them
first.

### From the scatter path

Eleven items, C1 to C11, in [scatter-realtime-review.md](scatter-realtime-review.md) section 6. The
three that affect results a user would notice: a capped preview of a Grid or Poisson scatter shows a
filled corner rather than a thinned field (C1); a vertical line that meets more than one face picks
the hit nearest Z = 0, so the result depends on the site datum (C2); the slope filter reads a
vertex-averaged normal and so rejects flat ground beside a wall (C3, needs an owner decision).

### Not code

The `hosted-perf` baseline no longer matches the machine (about 1.45 times slower since at least
2026-10-21). Until that is resolved the gate either fails on unchanged code or has to be ignored,
and no number in this review can be re-checked. Resolve it before Phase 0.

## 8. Progress

- **Tranche 1 (2026-11-09), part of Phase 1.** Live-scrub slider rows commit on a 50 ms throttle
  during the drag instead of 120 ms after the hand stops (`streamCommits`). Covers the 31 rows that
  already declared `LiveScrub` (Smooth, Displace, Project To, Scatter); Plant and Orient opt out.
  L2 done (wake-timer comments). Built and guard tests pass; **not live-verified** (the Rhino MCP
  router was down), so the open questions are how it feels and whether a slow terrain now starts and
  abandons builds mid-drag. L1 was left: at 50 ms the loss window is small, and committing from
  `UnLoad` re-enters the controller during a panel rebuild.
- **Next: tranche 2.** Dispatch `Preview` for gesture samples and `Final` on release (B2), with
  hosted tests for the preview-only stage branches first (L5).
