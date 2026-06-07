# `grading-rebuild` Branch Review

**Reviewed:** 2026-06-07 (re-review)
**Branch:** `grading-rebuild` — 8 commits on top of `main`, plus uncommitted work
**Scope:** 43 files changed, +15,722 / −9,282

```
e4c8e28 Remove legacy pad topology route
cc79b54 Extract path grading sampling helpers
151a5c2 Surface constraint-first failure diagnostics
00e8af4 Prefer constrained multi-pad fallback
b9de186 Index preserved grading constraints
abec117 Clean up constraint-first grading follow-up
3ea4204 Fix Rhino grading fallback integration
a09b7c5 Rebuild grading around constraint-first topology
```

Uncommitted: a spatial broad-phase for `ConstraintNetworkNormalizer.SplitAtIntersections`
(+ its test), unrelated `MoleHillPanel.cs` annotation-card edits, and this doc.

## What the rebuild does

Pad and path grading now both route through one shared
`ConstraintFirstGradingEngine.TryBuild`. It: normalizes the constraint network
(`SplitAtIntersections`), builds constraint topology first via `SurfaceRemesher`
with `ConstraintInsertionOnly: true`, guards output density, validates a single
closed boundary loop (`MeshTopologyValidator.AnalyzeBoundaryGraph`), evaluates Z
through a caller-supplied delegate, snaps preserved-elevation constraints, and
builds the result. `PadGrader.Protected.cs` (988 lines) is gone;
`PathGrader.Patches.cs` ~2.6k → 174; `PadGrader.Patches.cs` ~3.5k → 298;
`TerrainBuildService.cs` shed 911 lines of the old path-remesh fallback.

---

## Build & test state — **green**

- `dotnet build MoleHill.sln` → **0 warnings, 0 errors** (full solution, including
  `MoleHill.Rhino` and the `.gha`/Yak pipeline). The original blocker — a call to
  the deleted `PathGrader.TryBuildLocalizedFallbackBoundary` in
  `TerrainBuildService` — is resolved.
- `dotnet test --filter "FullyQualifiedName~Grad"` → **141 passed, 0 failed**.
- New focused tests exist for the engine (`ConstraintFirstGradingEngineTests`:
  preserved-elevation overlap precedence, far-segment lookup, Z-eval failure
  diagnostics) and the normalizer (`SplitAtIntersections` distant-candidate case).

The original review's correctness findings are verified fixed in code:
spatial-indexed preserved-elevation lookup (`PreservedConstraintSegmentIndex`),
structured failure diagnostics surfaced on the null path, bounded coarse-retry
density guard, value-type `SegmentKey` + `SpatialHashGrid2D` broad phase,
`Quantize` non-finite/overflow clamp, and removal of the dead
`TryTriangulateTopology` route (`CreateConstraints` retained as the active API).

---

## Remaining findings

### Correctness / robustness

1. **Slope-deviation fallback is still gated to `pads.Length == 1`**
   (`PadGrader.cs:155`). A multi-pad job where one pad grades with excessive
   slope deviation will not trigger local refinement. This is a deliberate hold:
   the multi-pad path prefers constrained whole-mesh retriangulation
   (`GradeWithRefinedZOnlyFallback`, `PadGrader.RefinedFallback.cs:18`), and the
   local-refinement fallback is too weak for coupled pads. Acceptable for now,
   but it means "one bad pad among several" is silently accepted rather than
   repaired. Worth a tracked TODO so it isn't forgotten.

2. **`TryBuildLocallyRefinedFallbackTopology` remains a weak last resort**
   (`PadGrader.RefinedFallback.cs:392`). It inserts one centroid Steiner per pad
   and 1→3 splits a single face — it cannot repair boundary-topology problems,
   so on real failures it usually falls through to the whole-mesh path anyway.
   It still earns its keep only for the single-pad copied-rectangle regressions
   where whole-mesh retriangulation raised slope error. Keep, but treat as a
   shim, not a real recovery strategy.

3. **`ApplyPreservedConstraintElevations` rebuilds its spatial index every
   `Grade` call** (`ConstraintFirstGradingEngine.cs:301`/`:382`). Correct and now
   `O(V·k)` instead of `O(V·Σsegments)`, but the index (segment array + grid +
   scratch) is allocated fresh per invocation. Fine at current call rates; if
   grading moves onto a hot interactive path, consider caching it on the
   constraint set.

### Fallback architecture

4. **Resolved:** `GradeWithRefinedZOnlyFallback` had three near-duplicated emit
   blocks. The branch-specific topology choices now call a shared fallback
   result builder that owns Z application, diagnostics, structured warning merge,
   slope diagnostics, and `GradingResultBuilder.BuildFromXyz`.

### Tests

5. **Resolved:** `TerrainGradePathAfterProtectedPadsCopiedCaseTests.cs` was one
   12,904-line `[Fact]` with geometry inlined ~10 floats/line. The captured
   vertices, faces, path, and hard constraints now live in an embedded JSON
   fixture loaded via `Assembly.GetManifestResourceStream`, leaving the test
   focused on setup and topology assertions.
   The new direct `ConstraintFirstGradingEngineTests` /
   `ConstraintNetworkNormalizerTests` are the right model — small, intentional,
   readable. Prefer growing those over more giant captured cases.

### Maintainability

6. **`PathGrader.cs` (1,398 lines) and `PadGrader.ZOnly.cs` (1,570) are still
   doing too much.** `cc79b54` extracted tangent/resampling into
   `PathGrader.Sampling.cs` (good direction), but `PathGrader.cs` still mixes the
   public entry points, guide/station selection, shoulder-constraint
   construction, boundary-clipped runs, closest-location queries, and result
   building. Continue the split by phase — e.g. `PathGrader.Constraints`
   (already exists), a `PathGrader.Shoulders`, and a `PathGrader.Result`.

7. **Pad helper files are still organized by rebuild history, not by phase.**
   `PadGrader.Patches.cs` is "what was left after the rebuild." A phase-based
   naming pass (`Constraints` = build, `ZOnly`/`Surfaces` = Z evaluation,
   `Diagnostics` = slope/output checks) would make the constraint-first
   orchestrator → engine → callback shape easier to follow.

8. **`MoleHillPanel.cs` has uncommitted, grading-unrelated edits** (annotation
   vs. analysis card collapse scoping). Decide whether they belong in this PR or
   a separate one before committing — they'll otherwise ride along with the
   grading rebuild's history.

---

## Suggested next steps

1. Done: the normalizer broad-phase + test landed in `5f4f379`.
2. Done: the `pads.Length == 1` slope-deviation fallback gate is now tracked by
   `grade_pad.fallback.multi_pad_slope_deviation_skipped` when coupled protected
   pads keep the constraint-first result despite slope-deviation diagnostics.
3. Decide the fate of the `MoleHillPanel.cs` changes before committing.
