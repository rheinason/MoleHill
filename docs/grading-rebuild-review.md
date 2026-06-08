# `grading-rebuild` Branch Review

**Reviewed:** 2026-06-08 (post pad local-refinement follow-up)
**Branch:** `grading-rebuild`
**Scope (grading core):** broad rebuild across `src/MoleHill.Core/Grading/`

```
7e39cf5 Split PathGrader Z-evaluation helpers by phase
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
through a caller-supplied delegate, snap preserved-elevation constraints, and
build the result. `PadGrader.Protected.cs` is gone, the old path-remesh fallback
was removed from `TerrainBuildService.cs`, and the remaining grader code is split
by phase.

---

## Build & Test State - Green

- `dotnet test MoleHill.sln -p:BaseOutputPath=.codex-build\solution-release-readiness-final\ -p:UseSharedCompilation=false`
  - Core 223 passed / 0 failed; Grasshopper 3 passed / 16 skipped.
- `dotnet test MoleHill.sln -p:BaseOutputPath=.codex-build\solution-local-refinement\ -p:UseSharedCompilation=false`
  - Core 224 passed / 0 failed; Grasshopper 3 passed / 16 skipped.
- `dotnet test MoleHill.sln -p:BaseOutputPath=.codex-build\solution-preserved-diagnostics\ -p:UseSharedCompilation=false`
  - Core 224 passed / 0 failed; Grasshopper 3 passed / 16 skipped.
- `dotnet build src\MoleHill.Rhino\MoleHill.Rhino.csproj -p:BaseOutputPath=.codex-build\rhino-release-readiness\ -p:UseSharedCompilation=false`
  - 0 warnings, 0 errors.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-yak-package.ps1 -Configuration Release`
  - Yak package built: `.artifacts\yak\MoleHill-0.6.8-beta\molehill-0.6.8-beta-rh8_9-win.yak`.
  - Yak reports the known acceptable content-name warning (`MoleHill.Rhino` vs package id `MoleHill`).
- `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj -p:BaseOutputPath=.codex-build\core-local-refinement\ -p:UseSharedCompilation=false`
  - Core 224 passed / 0 failed.
- `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj -p:BaseOutputPath=.codex-build\core-preserved-diagnostics\ -p:UseSharedCompilation=false`
  - Core 224 passed / 0 failed.
- `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj -p:BaseOutputPath=.codex-build\core-density-diagnostics\ -p:UseSharedCompilation=false`
  - Core 225 passed / 0 failed.
- Focused `PathGrader` tests after the split: 52 passed / 0 failed.
- Focused copied Grade Pad regressions after local-refinement follow-up: 17 passed / 0 failed.

---

## Resolved Since Prior Passes

- **Rhino build blocker:** deleted `PathGrader.TryBuildLocalizedFallbackBoundary` call; full solution compiles.
- **Preserved-elevation lookup:** spatial-indexed through `PreservedConstraintSegmentIndex`, avoiding the old `O(V * all segments)` scan.
- **Preserved-elevation diagnostics:** shared constraint-first grading now reports how many output vertices were snapped to preserved-elevation constraint segments via `*.preserved_elevation.snap`.
- **Failure diagnostics:** structured codes surfaced on null-path failures; `grade_path.constraint_first.failed` is asserted by test.
- **Density guard:** bounded coarse-retry loop only accepts a retry that actually reduces face count.
- **Density diagnostics:** density growth, density guard retries, and seed fallback paths are now structured diagnostics instead of plain log strings only.
- **Normalizer:** value-type `SegmentKey`, `SpatialHashGrid2D` broad phase, non-finite/overflow clamp in `Quantize`, and dead `TryTriangulateTopology` removed.
- **Fallback result building:** the tiers in `GradeWithRefinedZOnlyFallback` share `BuildRefinedFallbackResult`.
- **Large copied path fixture:** the 12,904-line single `[Fact]` became a normal test backed by embedded JSON.
- **Multi-pad slope gate:** coupled protected pads now attempt local-refinement slope fallback. The fallback is accepted only when slope diagnostics materially improve; otherwise the constraint-first result is kept with `grade_pad.fallback.local_refinement_rejected`, and coupled-pad skipped cases still emit `grade_pad.fallback.multi_pad_slope_deviation_skipped`.
- **Pad local fallback:** `TryBuildLocallyRefinedFallbackTopology` now splits the bounded pad influence footprint for ordinary meshes, keeps the minimal centroid split for very sparse meshes, and reports split/candidate/cap counts.
- **MoleHillPanel:** annotation-card edits committed separately from grading core.
- **Path Z evaluation split:** shared path Z application now lives in `PathGrader.ApplyPathGrading.cs`; sections, reference profiles, daylighting, influence blending, and diagnostics are phase-specific partials.

---

## Remaining Findings

### Correctness / Robustness

1. **The local pad fallback is still Z-only, not local constraint insertion.**
   It now splits every bounded upstream face touched by the prepared pad
   influence footprint, which is a real improvement over the old one-face shim.
   It still does not insert exact pad/daylight constraint segments into upstream
   topology, so whole-mesh recovery remains the safer first choice for coupled
   topology-deficiency failures.

2. **`ApplyPreservedConstraintElevations` rebuilds its spatial index every
   `Grade` call.** This is correct and cheap at current call rates; cache the
   index on the constraint set only if profiling shows grading is hot in an
   interactive path.

---

## Suggested Next Steps

1. If production cases show the Z-only pad fallback still matters, replace it
   with bounded local constraint insertion rather than adding more centroid-split
   heuristics.
2. Otherwise the branch is in good shape: build green, tests green, prior
   correctness findings closed. Reasonable to open the PR.
