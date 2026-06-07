# `grading-rebuild` Branch Review

**Reviewed:** 2026-06-08 (post `PathGrader.ZOnly` split)
**Branch:** `grading-rebuild`
**Scope (grading core):** ~4,447 insertions / ~9,930 deletions across `src/MoleHill.Core/Grading/`

```
7e39cf5 Split PathGrader Z-only helpers by phase
0704a96 Split PadGrader surface helpers by phase
d82e75f Split PathGrader helpers by phase
f691f5f Separate annotation collapse from analysis cards
ef7b308 Update grading rebuild review status
1a0882c Surface multi-pad slope fallback policy
6daee41 Extract pad fallback result builder
5f4f379 Index constraints and externalize copied path case
e4c8e28 Remove legacy pad topology route
cc79b54 Extract path grading sampling helpers
151a5c2 Surface constraint-first failure diagnostics
00e8af4 Prefer constrained multi-pad fallback
b9de186 Index preserved grading constraints
abec117 Clean up constraint-first grading follow-up
3ea4204 Fix Rhino grading fallback integration
a09b7c5 Rebuild grading around constraint-first topology
```

## What the rebuild does

Pad and path grading both route through one shared
`ConstraintFirstGradingEngine.TryBuild`: normalize the constraint network
(`SplitAtIntersections`), build constraint topology first via `SurfaceRemesher`
with `ConstraintInsertionOnly: true`, guard output density, validate a single
closed boundary loop (`MeshTopologyValidator.AnalyzeBoundaryGraph`), evaluate Z
through a caller-supplied delegate, snap preserved-elevation constraints, build
the result. `PadGrader.Protected.cs` (988 lines) is gone;
`PathGrader.Patches.cs` ~2.6k → 175; `PadGrader.Patches.cs` ~3.5k → 298;
`TerrainBuildService.cs` shed 911 lines of the old path-remesh fallback.

---

## Build & test state — **green**

- `dotnet test MoleHill.sln -p:BaseOutputPath=.codex-build\solution-path-zonly-split\ -p:UseSharedCompilation=false`
  → **Core 223 passed / 0 failed; Grasshopper 3 passed / 16 skipped**.
- `dotnet build src\MoleHill.Rhino\MoleHill.Rhino.csproj -p:BaseOutputPath=.codex-build\rhino-path-zonly-split-final\ -p:UseSharedCompilation=false`
  → **0 warnings, 0 errors**.
- Focused `PathGrader` tests after the split → **52 passed, 0 failed**.
- Direct unit tests now cover the engine (`ConstraintFirstGradingEngineTests`:
  preserved-elevation overlap precedence, far-segment lookup, Z-eval failure
  diagnostics) and the normalizer (`ConstraintNetworkNormalizerTests`:
  distant-candidate broad phase). `.codex-build/` and `.artifacts/` under the
  test project are gitignored local scratch, not tracked.

---

## Resolved since the prior passes (verified in code)

- **Rhino build blocker** (deleted `PathGrader.TryBuildLocalizedFallbackBoundary`
  call) — gone; full solution compiles.
- **Preserved-elevation lookup** — spatial-indexed
  (`PreservedConstraintSegmentIndex`), `O(V·k)` instead of `O(V·Σsegments)`.
- **Failure diagnostics** — structured codes surfaced on the null path;
  `grade_path.constraint_first.failed` is asserted by test.
- **Density guard** — bounded coarse-retry loop that only accepts a retry which
  actually reduces face count.
- **Normalizer** — value-type `SegmentKey`, `SpatialHashGrid2D` broad phase,
  `Quantize` non-finite/overflow clamp; dead `TryTriangulateTopology` removed.
- **Fallback emit duplication** (`6daee41`) — the three tiers in
  `GradeWithRefinedZOnlyFallback` now share `BuildRefinedFallbackResult`
  (`PadGrader.RefinedFallback.cs:116`). Genuine ~120-line dedup.
- **Giant captured test** (`5f4f379`) — the 12,904-line single-`[Fact]` is now
  94 lines; geometry lives in the tracked, embedded
  `TestData/TerrainGradePathAfterProtectedPadsCopiedCase.json`.
- **Multi-pad slope gate is now observable** (`1a0882c`) — when coupled protected
  pads keep the constraint-first result despite slope-deviation diagnostics,
  `AddMultiPadSlopeDeviationFallbackDiagnosticIfNeeded` (`PadGrader.cs:169`)
  emits `grade_pad.fallback.multi_pad_slope_deviation_skipped`.
- **MoleHillPanel** annotation-card edits committed on their own (`f691f5f`),
  not riding along with grading core.
- **Path Z-only monolith** (`7e39cf5`) — `PathGrader.ZOnly.cs` now contains
  the fallback entry points and top-level `ApplyPathGrading` orchestration only;
  section solving, daylight reach, influence blending, shoulder reference
  profiles, and diagnostics live in phase-specific partial files.

---

## Remaining findings

### Correctness / robustness (minor, by-design)

1. **`TryBuildLocallyRefinedFallbackTopology` remains a weak last resort**
   (`PadGrader.RefinedFallback.cs:351`). One centroid Steiner per pad + a single
   1→3 face split cannot repair boundary-topology problems, so on real failures
   it usually falls through to the whole-mesh path anyway. It earns its keep only
   for the single-pad copied-rectangle regressions where whole-mesh
   retriangulation raised slope error. Keep as a shim; don't mistake it for a
   real recovery strategy.

2. **Multi-pad slope-deviation still does not attempt local refinement**
   (`PadGrader.cs:158` gate stays `pads.Length == 1`). Now *tracked* via the
   diagnostic above rather than silent, which is the right interim state. The
   underlying limitation — a coupled-pad job with one over-sloped pad keeps the
   constraint-first result — stands until the local-refinement fallback is strong
   enough for coupled pads (see finding 1).

3. **`ApplyPreservedConstraintElevations` rebuilds its spatial index every
   `Grade` call** (`ConstraintFirstGradingEngine.cs`). Correct and cheap at
   current call rates; if grading moves onto a hot interactive path, cache the
   index on the constraint set.

---

## Suggested next steps

1. Strengthen `TryBuildLocallyRefinedFallbackTopology` (or retire it in favor of
   the constrained whole-mesh path) so the `pads.Length == 1` slope-deviation
   gate (finding 2) can be widened to coupled pads.
2. Otherwise the branch is in good shape — build green, tests green, prior
   correctness findings closed. Reasonable to open the PR.
