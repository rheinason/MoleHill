# `grading-rebuild` Branch Review

**Reviewed:** 2026-06-07
**Branch:** `grading-rebuild` (1 commit on top of `main` — `a09b7c5 Rebuild grading around constraint-first topology`)
**Scope:** 37 files changed, +14,659 / -7,387

**Follow-up status:** the Rhino build blocker called out below was fixed in
`3ea4204 Fix Rhino grading fallback integration`. A later cleanup removed the
now-unreachable Rhino Grade Path remesh fallback cluster, replaced the brittle
pad slope-warning string check with structured diagnostic-code detection, and
added a focused test for constraint-network intersection splitting.

Summary of the rebuild: pad and path grading now both go through a shared
`ConstraintFirstGradingEngine` that builds constraint topology first
(`SurfaceRemesher` with `ConstraintInsertionOnly: true`), validates a single
closed boundary loop, then evaluates Z via a callback. `PadGrader.Protected.cs`
(988 lines) is gone; `PathGrader.Patches.cs` shrank from ~2.6k → 174 lines;
`PadGrader.Patches.cs` shrank from ~3.5k → 298 lines.

---

## Build state — **fixed after follow-up**

The original review found that `MoleHill.Rhino` did not compile on this branch:

- `src/MoleHill.Rhino/Services/TerrainBuildService.cs:5215` calls
  `PathGrader.TryBuildLocalizedFallbackBoundary`, which was deleted as part of
  the path-grader rebuild. `MoleHill.Core` and `MoleHill.Core.Tests` build
  clean (119 grading tests pass), but the Rhino plugin doesn't, so the `.rhp`
  cannot be produced.
- Two other deleted `internal` methods (`TryValidatePathPatchForStitching`,
  `IsIsoElevationStrip`) have no remaining callers.

This blocker has since been resolved by replacing the deleted path boundary
call and then deleting the unreachable fallback code that still depended on the
old path-remesh methodology.

---

## Correctness & robustness

1. **Resolved:** `ShouldPreferProtectedPadLocalRefinement` (`PadGrader.cs:153`) matched on
   diagnostic message text: `Contains("batter slope warning", ...)`.
   Brittle: changing the wording of `BuildPadSlopeDiagnostics`
   (`PadGrader.Patches.cs:188`) silently disables the fallback. You already
   have structured diagnostics with codes (`GradingDiagnostic.Code`); this now
   detects `grade_pad.slope.deviation` instead.

2. **Slope-deviation fallback is gated to `pads.Length == 1`**
   (`PadGrader.cs:152`). Multi-pad cases with one bad pad never trigger local
   refinement. Follow-up note: broadening this gate to coupled pads regressed
   copied multi-pad topology cases because the local-refinement fallback is too
   weak for coupled pads; leave this gated until that fallback is replaced.

3. **Resolved:** density-guard retry used to double edge length once. If the first remesh
   exceeds `WarningFaceMultiplier (4×)`, it retries with `effectiveEdgeLength
   * 2.0` and accepts whichever is smaller
   (`ConstraintFirstGradingEngine.cs:104-127`). If the doubled retry still
   exceeds `HardFaceMultiplier (8×)`, the build fails. The guard now performs
   a bounded sequence of coarser retries and accepts only retries that actually
   reduce face count.

4. **`AddFailureDiagnostic` only writes to `structuredDiagnostics`**
   (`ConstraintFirstGradingEngine.cs:358`), not the message list. On failure,
   `TryBuild` returns `null` and the structured diagnostics are dropped by
   callers — the failure detail is only visible through `errorMessage`. Either
   also append to the message list or have the caller surface structured
   diagnostics on the null path.

5. **Resolved:** `ApplyPreservedConstraintElevations` (`ConstraintFirstGradingEngine.cs:276`)
   was `O(V × Σ segments)` with no spatial index. For meshes with many
   preserved-elevation constraints and large vertex counts this could dominate
   rebuild cost. It now builds a coarse segment AABB index and queries only
   nearby preserved-elevation segments per output vertex while preserving the
   previous constraint overwrite order.

6. **Partially resolved:** `TryBuildLocallyRefinedFallbackTopology` is a very weak fallback — it
   inserts one Steiner per pad centroid and 1-3 splits a single face
   (`PadGrader.RefinedFallback.cs:228-335`). It won't repair boundary-topology
   issues, so most failures will fall through to the whole-mesh
   retriangulation path anyway. Multi-pad fallback now prefers constrained
   whole-mesh retriangulation with pad boundary segments and pad centroid
   vertices before falling back to the local split. Single-pad fallback remains
   local-first because copied rectangular-pad regressions showed whole-mesh
   fallback increased slope error there.

7. **Partially resolved:** `ConstraintNetworkNormalizer.SplitAtIntersections` is an `O(N²)` segment-pair
   check with linear-scan inserts (`AddSplitParameter`) and string-keyed
   dedup (`BuildUndirectedSegmentKey`). For large pads with shoulder + apron
   + lock curves this is fine (hundreds of segments), but it will hurt past
   ~5k segments. The low-hanging allocation issue has been handled by
   replacing `HashSet<string>` with a value-key set. If perf becomes an issue,
   the segment loop should use the same `Bounds2D` index but with a real
   spatial bucket.

8. **Resolved:** `Quantize` in `ConstraintNetworkNormalizer.cs:209` did `value /
   tolerance` with no overflow guard. For default `tolerance ≈ 1e-3` this is
   fine; if a future caller passes a tiny tolerance, the rounded long could
   exceed `long.MaxValue` and produce nonsense keys. It now uses a tolerance
   floor and clamps non-finite/out-of-range scaled values.

9. **`PadGrader.Topology.cs` retains the old `TryTriangulatePadTopology`
   path**, but `Grade()` (`PadGrader.cs:68-130`) no longer uses it — only
   `GradeWithConstraintFirstTopology` and `GradeWithRefinedZOnlyFallback` are
   reachable from `Grade`. Either `TryTriangulateTopology` is now dead code or
   it has external callers — confirm via:
   ```
   git grep -n "TryTriangulateTopology"
   ```
   and remove if unused. (Same question for the duplicated public
   `CreateConstraints` overload in `PadGrader.Topology.cs:184`.)

---

## Tests

10. **`TerrainGradePathAfterProtectedPadsCopiedCaseTests.cs` is a single
    12,904-line `[Fact]` with inline geometry packed 10 floats/line.** It will
    pass or fail as one unit; on failure the diff is unreadable. Three
    options:
    - Move the captured arrays to `.json` or `.bin` resources and load via
      `Assembly.GetManifestResourceStream` (matches the "copied case" pattern
      other tests in the suite already follow).
    - Split into smaller scenarios driven by the same data — at minimum,
      separate the topology-validity assertions from the constraint-touch
      assertion so failures localize.
    - Decide whether this test belongs in the Core test suite at all — at
      12.9k lines it dwarfs `PadGraderTests.cs` (1.6k) and `PathGraderTests.cs`
      (1.1k) combined.

11. **Partially resolved:** no direct unit tests for the new engine pieces.
    `ConstraintFirstGradingEngine.TryBuild`,
    `ConstraintNetworkNormalizer.SplitAtIntersections`, and
    `TryBuildLocallyRefinedFallbackTopology` are only exercised end-to-end. A
    focused test covering an explicit X-intersection in `SplitAtIntersections`,
    and one for the density-budget retry, would lock the regressions you just
    fixed. Direct tests now cover constraint-network intersection splitting and
    preserved constraint elevation handling inside `ConstraintFirstGradingEngine`.

---

## Maintainability

12. **`PathGrader.cs` is 1,686 lines** and `PadGrader.ZOnly.cs` is 1,570. The
    constraint-first rebuild was a chance to shrink these; they're still doing
    too much. `PathGrader.cs` mixes the public entry points, constraint
    sampling, tangent smoothing, clipped-run generation, station-constraint
    construction, and result building. Splitting tangent/sampling helpers into
    a `PathGrader.Sampling.cs` is straightforward and would make the diff
    legible.

13. **`PadGrader.ConstraintFirst.cs` and `PadGrader.RefinedFallback.cs` both
    reach into many shared helpers** (`BuildPadBoundaryPolylines`,
    `BuildPadPatchSummaries`, `BuildPreparedPadSections`,
    `BuildPadSlopeDiagnostics`, `AppendPadOutputSlopeDiagnostics`). The
    constraint-first flow now has an "orchestrator → engine → callback" shape
    that's clearer than the old patch-based code, but the helpers it depends
    on are still spread across `Patches`, `Support`, `Topology`, `Constraints`.
    Consider naming convention by phase: `PadGrader.Constraints` (build),
    `PadGrader.Surfaces` (Z evaluation), `PadGrader.Diagnostics`
    (slope/output checks). Current `.Patches.cs` is just "stuff left over
    after rebuild."

14. **`MoleHillPanel.cs` has uncommitted modifications.** Not touched by this
    branch's commit; decide whether they belong in this PR or a separate one.

---

## Suggested next steps

1. **Done:** restore or replace `PathGrader.TryBuildLocalizedFallbackBoundary` (or
   update `TerrainBuildService.BuildGradePathFallbackBoundaries` to use a
   different boundary builder). This unblocks the Rhino build.
2. **Break the 12.9k-line test** into resource-driven fixtures.
3. **Done:** replace the `Contains("batter slope warning", ...)` string match with a
   structured diagnostic code.
4. **Decide on the dead-code status of `PadGrader.Topology.cs::TryTriangulateTopology`**
   and the duplicated public `CreateConstraints`.
5. **Done:** replace `HashSet<string>` segment keys with a value tuple in
   `ConstraintNetworkNormalizer`.
