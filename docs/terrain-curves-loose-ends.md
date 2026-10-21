# Terrain curves: loose ends (2026-10-21)

Open items left by the scan-to-terrain and `mhTerrainToCurves` work on branch `sketch-route-relax`
(`575c670`..`ff30dc6`, not pushed). The findings behind them are in `docs/scan-terrain-output-review.md`.

## Not verified live

- **The extraction panel is not tested in Rhino.** Stop, the elapsed-time status, waiting for Recompute at
  100,000 vertices or more, the Contour interval and Recovery inputs per hectare rows, and Preview creases
  have only been compiled and unit-tested.
- **The kept output is not tested in Rhino.** That covers the new "Creases" layer, and contours whose corners are now
  kinked joins of quadratic pieces (`RouteCurveBuilder.FromFitted`). Check that they bake as one curve per
  contour and edit cleanly.
- **The scan-command defaults are not checked live** (inputs per hectare 500, paved accuracy, gap-fill exclusion) on
  Grønalid and Vardalag. They were measured only through `tools/MoleHill.ScanReview`.
- **The `hosted-perf` lane has not been run.** Curve recovery has no hosted baseline; all timings come from a scratch
  profiler (see "Measuring" below).

## Performance

- **The wall terrain (548k faces) still takes about 5 minutes**, about 4 of them removing redundant contours. Its
  long contours each have a window covering most of the site, so every trial is close to a full rebuild.
  Faster means judging redundancy differently, for example per stretch of contour instead of per whole contour.
  Tuning will not get there.
- **Some steps still cannot be stopped.** Stop still waits for a global rebuild (a few seconds on the largest terrain)
  and for any `RouteCurveFitter` fallback in progress (seconds per contour). Neither checks for cancellation.
- **The 100,000-vertex threshold** (`TerrainToCurvesCommandService.LargeTerrainVertices`) is set from four test
  terrains. Only `mhTerrainToCurves` uses it; the scan command still restarts on every change.
- **The corner fit costs a few extra spot heights:** 78 → 88 on geometry-heavy, 11 → 14 on interactive, and +31 of
  7,340 on bumpy ground.

## Quality

- **Jagged steep banks on the wall terrain** are still described by strings of spot heights. Lower crease
  angles or shorter chains (15°, 1.5 m) cost more inputs or did not finish.
- **Rough natural ground** needs many inputs: bumpy survey 7,371 at 0.1 m. Tracing extra contours where the
  rebuild misses was tried and rejected (contour length doubled, and it stalled short of tolerance).
- **Vardalag's remaining misses** lie on RealityCapture gap fills. They are now marked and left unmeasured; they are
  not fixed.
- **`Recover_SquareContours_AreFittedWithSharpCorners...`** passes on the old fitter too. It guards the behaviour,
  not the new code path.

## Clean-up

- **The error-driven and Informative recovery paths are unused by the commands.** Both now use interval recovery;
  with contours off, the error-driven path runs with `MaximumCurves = 0` and returns only borders. That covers
  `MaximumCurves`, `PreferredContours`, landform lines, `RecoverBest`'s two-pass Auto mode and
  `.Informative.cs`. Either delete them or decide what they are kept for.
- **Saved settings with the retired keys** (contour budget, ridges and valleys) are ignored, not migrated.

## Measuring

Time recovery in a separate process that opts out of Windows power throttling, and run one terrain at a time. On this hybrid CPU a
background console process is EcoQoS-throttled, and the same code measured 17 s and 31 s. The opt-out is the
one in `tests/MoleHill.Rhino.Tests/HostedPowerThrottling.cs`.

## Housekeeping

- An adopted Rhino window (process 10064, `Complex Wall Failure 001.3dm`) is still open and holds the Release
  build files. Close it before rebuilding or live testing.
