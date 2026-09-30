# Validation lanes

What a MoleHill test report does and does not prove, and how to produce each kind of evidence.

Driver: `./validate.ps1 <lane>` from the repo root. Every lane writes its TRX logs, console output and
an `environment.json` (commit, SDK, machine, processor count, Rhino directory, timestamp) into
`.artifacts/validate/<lane>-<timestamp>/`, so a result can be attached to a claim later.

## Why the lanes are separate

A single green `dotnet test` is routinely read as "MoleHill works". It is not:

- Host tests **link production sources** rather than referencing the built `.rhp`/`.gha`, so a green
  managed run does not establish that the shipped assemblies load or that plugin discovery works.
- `[RhinoNativeFact]` **skips** when the native Rhino runtime is unavailable. On a machine without
  Rhino that silently removes most host coverage while the totals stay green.
- Benchmarks **return early** without `MOLEHILL_PERF=1`. They report as passes without measuring
  anything.

Each lane therefore states which of these it exercised, and the ones that can silently degrade fail
instead.

| Lane | Command | Proves | Does not prove |
|---|---|---|---|
| managed | `./validate.ps1 managed` | Source-linked regressions across Core, Rhino and Grasshopper | Anything native, packaged, or measured |
| native | `./validate.ps1 native` | The same suites with a real Rhino runtime present | Live UI behaviour (that is the live-testing doc) |
| perf | `./validate.ps1 perf` | Benchmark bodies actually executed, in Release | A budget — timings need the protocol below |
| warnings | `./validate.ps1 warnings` | Zero owned-code compiler warnings, solution-wide | Anything about vendored TriangleNet |
| package | `./validate.ps1 package` | The Yak archive contains the plugin, the merged `.gha` and Interop | Installation into a real Rhino |
| hosted-perf | `./validate.ps1 hosted-perf` | Full-stack build timings inside a real Rhino, per stage and per analysis, within a margin of the committed baseline | Scheduling, UI marshal and redraw (worker time only) |
| all | `./validate.ps1 all` | managed + warnings + package | native, perf |

## managed

Fast lane. Native tests skip here and the lane prints how many, followed by an explicit reminder that
skips are not acceptance.

## native

Sets `MOLEHILL_REQUIRE_NATIVE=1`. `RhinoNativeFactAttribute` then stops skipping, and
`NativeRuntimePreflightTests` fails once, up front, naming the directory it probed — so a native run on
a machine without Rhino fails loudly instead of reporting a green skip.

Point it at a non-default install with `./validate.ps1 native -RhinoDir 'D:\Rhino 8'`, which sets
`MOLEHILL_RHINO_DIR` for both the MSBuild references (via `RhinoInstallDir` in
`Directory.Build.props`) and the runtime probe.

This lane is still **headless RhinoCommon**, not the plugin in a running Rhino. For UI acceptance use a
disposable `rhino-mcp` slot as described in [rhino-live-testing.md](rhino-live-testing.md).

## perf

Sets `MOLEHILL_PERF=1` **and** `MOLEHILL_REQUIRE_PERF=1`, builds Release, and disables test-collection
parallelism so process-wide memory and allocation numbers mean something. Every benchmark gate routes
through `PerformanceLane.ShouldRun`, which prints `BENCHMARK RAN:` or `BENCHMARK NOT RUN:`; in this
lane a not-run body fails the test, and the lane additionally fails if nothing ran at all.

Read the benchmark sizes before running on a constrained machine. Timings from this lane are inputs to
the measurement protocol in
[codebase-review-and-implementation-plan-2026-09-19.md](codebase-review-and-implementation-plan-2026-09-19.md)
("Performance measurement and acceptance protocol") — report median and p95 over repeated samples on
one machine and fixture, not a single best time.

## hosted-perf

The benchmarks that time what a user waits for — a terrain rebuild through `TerrainBuildService`, with
its stages and analyses — need Rhino's native runtime, which Rhino 8.35 will not start outside its own
process (see "Known broken" below). So this lane runs them **inside** a disposable Rhino instead:

1. Builds `MoleHill.Rhino.Tests` in Release with `--no-incremental`.
2. `tools/rhino-hosted-perf.py` starts the installed Rhino-MCP router over stdio and spawns a slot it
   owns. It refuses an adopted Rhino and closes exactly its own slot afterwards, whatever happens.
3. The slot loads the test assembly into its **own `AssemblyLoadContext`**, preloading that build's
   `MoleHill.Core` and `MoleHill.Interop` beside it. The slot has already loaded the plug-in's **Debug**
   Core, and a plain `Assembly.LoadFrom` binds to that silently (the 25% trap below).
   `HostedPerformanceLane` records the Core it actually ran against, and refuses to measure unoptimized code.
4. `HostedPerformanceLane.Start` runs on a background thread and returns at once. A `run_csharp` script
   runs on Rhino's UI thread, and holding it would stall the process being measured. The driver polls
   for the result file.
5. Each scenario runs one discarded warm-up and then `-Samples` (default 5) measured samples, each on a
   fresh fixture and cache with a GC between samples. Every `TerrainBuildTiming` row becomes a metric
   `{scenario}/{phase}/{stage}`, alongside the measured `wall`, and gets a median and a p95.
6. The result is compared with `tests/perf-baselines/hosted-perf.json`. A metric **regresses** when its
   median is both more than `-Margin` (20%) slower and more than `-FloorMs` (25 ms) slower. The relative
   test alone fails on 2 ms stages; the absolute test alone misses a 20% loss in a 100 ms stage. Any
   regression fails the lane. Improvements, new metrics and missing metrics (a renamed stage) are reported
   but do not fail.

| Scenario | Fixture | Phases |
|---|---|---|
| `geometry-heavy` | 62,500-point survey, Triangulate -> Grade Pad -> Grade Path -> Smooth -> Remesh | `cold`, `pad-edit` |
| `analysis-heavy` | 122,500-point survey (243,602 faces), Slope, Aspect, Elevation, Waterflow, Catchments, Ponding | `cold`, `point-edit` |
| `interactive` | Triangulate + Retaining Wall at 2.5k / 25k / 50k / 100k faces | per scale: `cold`, `warm-edit` |

```powershell
./validate.ps1 hosted-perf                               # compare against the baseline
./validate.ps1 hosted-perf -Scenario analysis-heavy      # one scenario; the others are not reported missing
./validate.ps1 hosted-perf -UpdateBaseline               # record this run as the baseline
```

**A baseline belongs to one machine.** Comparing across machines fails as "not comparable" and does
not count as a pass. Re-baseline, and commit the baseline with the change it measures, when a speed-up
lands (lock the gain in) or when a slowdown is deliberate (say why in the commit). The per-stage
detail lines (output counts such as ponds found) are saved beside the metrics and never compared. They
tell you whether a time changed because the work changed. A baseline is only ever recorded from a
run of every scenario: `-UpdateBaseline` refuses a result that would drop a scenario, because a dropped
scenario's metrics read as New afterwards and New never fails.

**The lane also checks the terrain did not change.** Each build phase records an `output mesh` detail: the
counts plus a hash of the finished mesh's own vertex and face lists, read from Rhino rather than from
any cached extraction. The comparison lists every phase whose hash differs from the baseline's. That is
reported, not failed, because a deliberate geometry change lands there too. A change meant only to be
faster must print "Finished meshes identical to the baseline", and one that does not is not a speed-up.

**A regression in a stage you did not touch is usually allocation, not noise.** On 2026-09-26 a check
that allocated two large arrays per mesh normalization added 40 ms to the unchanged Ponding analysis,
consistently across all five samples. Its full collections were landing inside that stage. Renting the
buffers removed it. Look at the sample spread first: a tight spread is systematic.

This lane measures **worker time only**. Debounce, the marshal back to the UI thread, display
publication and redraw happen outside `TerrainBuildService` and need `mhLatencyTrace`
(architecture.md, "Edit-to-visible latency").

It needs the Rhino-MCP router (`docs/rhino-live-testing.md` section 2) and Python 3. It also fails if a
slot still holds the test DLL, because the Release build cannot overwrite it.

## park-stress

Not a lane, and never gated: a probe for where one terrain stops scaling. `ParkScaleStress`
(`tests/MoleHill.Rhino.Tests/ParkScaleStress.cs`) builds a synthetic park the size of Central Park
(4.0 x 0.8 km, ~40 m relief, rock outcrops, a reservoir, three graded lawns, 24 roads and paths, three
retaining walls) from a jittered LiDAR-style point cloud, at spacings from 8 m (50k points) to 1 m (3.2M).
Each rung adds the stack **one stage at a time** on one cache, so each step's time is that stage's own and
a hang names the stage that hung. It then makes a pad edit and a single survey-point edit. Every step runs
under a budget through the build's `shouldCancel`, and the ladder stops at a stage that ignores it.

It uses the hosted lane's route, with `--entry ParkScaleStress`:

```powershell
dotnet build tests/MoleHill.Rhino.Tests/MoleHill.Rhino.Tests.csproj -c Release --no-incremental -p:SkipGrasshopperLibraryCopy=True
py -3 tools/rhino-hosted-perf.py --bin tests/MoleHill.Rhino.Tests/bin/Release/net8.0 `
    --request park.json --entry ParkScaleStress --timeout-minutes 120
# park.json: { "ResultPath": "...", "StepBudgetSeconds": 300, "Spacings": [8, 4, 2, 1] }
```

A step whose stage failed inside the build (the build still succeeds, handing the input mesh on) reports
`stage-failed`, not `ok`. `PathsCrossConstraints` lays paths across the lawns. Grade Path used to refuse
the whole modifier on it ("Road edge crosses a hard constraint"). It now stops each path at the lawn edge
and reports `grade_path.barrier_stops`.

First results, 2026-09-29, on a 24-thread machine with 32 GB (full stack, cold, then one edit):

| Spacing | Points | Cold stack | One edit | Peak working set | Largest stage |
|---|---|---|---|---|---|
| 8 m | 50k | 8 s | 6 s | 1.2 GB | Remesh 3.3 s |
| 4 m | 200k | 14 s | 15 s | 2.2 GB | Remesh 8.7 s |
| 2 m | 800k | 54 s | 55 s | 6.2 GB | Remesh 35 s |
| 1 m | 3.2M | 300 s | 298 s | 15.2 GB | Remesh 167 s (flip phase 74%) |

After the flip-phase index (same day, architecture.md "Remesh flip phase"), Remesh fell to 13.9 s at 2 m
and 57 s at 1 m, the 2 m edit to 32 s and the 1 m edit to about 175 s, with identical meshes.

What broke, and where: at 1 m the Retaining Wall's local insert is rejected (it would open the terrain
boundary), and its whole-mesh constrained rebuild, now also carrying 2,346 path elevation constraints,
fails after 53 s. The terrain keeps the upstream mesh, so it stays hole-free, but the walls are silently
absent. At 2 m they insert. Every edit, including one survey point, re-runs the whole stack downstream of
Triangulate, so edit time equals cold time at every scale. The far-from-origin warning fires on any site
wider than ~1.7 km at a 1 mm model tolerance, even one that starts at the origin. (It now says so, and names
the tolerance that would fit, rather than advising a move to the origin.)

After the wall fixes (architecture.md, "Retaining walls: getting the rails in") the 1 m walls insert, by
local triangulation, in 14 s instead of failing after 60 s, and one edit at 1 m takes about 130 s.

After the collapse-phase work and with `PathsCrossConstraints` on (architecture.md, "Remesh collapse
phase" and "Grade Path meets a hard constraint"), the 1 m cold stack grades its paths up to the lawns. Remesh
takes 36.5 s and one edit about 120-124 s. An edit still costs as much as a cold build, because every stage
downstream of the edit re-runs on the whole mesh.

Two request fields help Remesh work: `RemeshMode` sets the Remesh card's mode (`"tiled"` for the prototype),
and `ExportRemeshInputFolder` writes the mesh and hard constraints the Remesh stage receives as
`remesh-input-{spacing}.bin`, for a Core-level replay: `TiledRemeshReplayBenchmarkTests` (set
`MOLEHILL_REMESH_INPUT` to the file) remeshes it cold, then after a one-vertex edit with the memo, reports both
costs and asserts the incremental result equals a cold remesh of the edit.

The probe also times three edits that cannot change the terrain, after the survey-point edit: a rebuild
with nothing changed, an empty Grade Pad card inserted below Triangulate, and a renamed Remesh card. At 1 m
they take 0.5 s, 92 s and 37 s. The last two should cost nothing; see
`incremental-rebuild-design-2026-09-29.md`.

## wall-grade

Also a probe, not a lane. `WallGradeProbe` (`tests/MoleHill.Rhino.Tests/WallGradeProbe.cs`) builds small
60 x 44 m sites and sweeps a Retaining Wall across survey spacing, slope, rail gap, shape (`straight`,
`bend`, `ring`, `arc`, `straight-dense`) and stack context (`alone`, `remesh-before`, `pad-before`,
`wall-then-pad`, `path-near`, `terrace`), in grade mode and optionally breakline mode as a control. For each
case it reports whether the grade ran and which tier, how the rails went in (`local`, `rebuild`, `failed`),
the finished terrain's boundary loops and non-manifold edges, and the batter **measured on the surface**
along sections square to the wall against the slope the card asked for. Each case runs under a budget, and
the sweep stops at one that ignores cancellation.

```powershell
py -3 tools/rhino-hosted-perf.py --bin tests/MoleHill.Rhino.Tests/bin/Release/net8.0 `
    --request walls.json --entry WallGradeProbe
# walls.json: { "ResultPath": "...", "Contexts": ["alone", "path-near"], "IncludeBreaklineControl": true }
```

`DumpDiagnostics` keeps every timing row and diagnostic, plus a toe-side section every 25 cm.
`CaptureFolder` writes, instead of building, each case's upstream mesh, rail grades and the mesh and
constraints its rail insertion receives, as JSON for a Core-level repro without Rhino.

Result on 2026-09-29 after the wall fixes (architecture.md, "Retaining walls: getting the rails in"):
1,152 cases, every wall inserted in both modes, every terrain one watertight loop. Grade-mode batter
error: median 1.7% of wall height, 90th percentile 3.7%. The worst cases are rings at shallow slopes, where
the far side's batter reaches across the ring and the probe's single-section model no longer applies.

## warnings

`MoleHill.Core` compiles the vendored TriangleNet sources directly, and those sources predate nullable
reference types. That is the whole reason the project-wide `NoWarn` existed: of 456 nullability
warnings in the 2026-09-19 inventory, **450 were TriangleNet** and 6 were owned code.

The suppression is now scoped to the vendor where it belongs — `.editorconfig` marks
`[src/TriangleNet/**.cs]` as `generated_code = true`, which is a Roslyn-level exemption for those files
only — so the nullability codes are gone from `MoleHill.Core.csproj` and owned code is analysed
normally. The 6 owned warnings were fixed (`TriangulationHelper` forgives TriangleNet's unannotated
`quality` parameter, where null is the vendor's "no refinement" sentinel; `MeshTopologyOperations`
declares `[NotNullWhen(true)]` on a try-pattern out parameter).

The lane therefore just builds the solution with `-warnaserror`. Zero is the standing count, and a new
owned-code warning is a build failure rather than a number in a log. It refuses to run if the
`.editorconfig` exemption has gone missing, and refuses to report success if the build compiled
nothing — a clean log from a build that never ran is the failure mode this lane exists to prevent.

If a new owned warning appears, fix it, or scope the suppression to the sources that need it. Do not
widen `NoWarn`.

## package

Runs `build-yak-package.ps1` (never `-Push`; publishing is a separate release decision and a Yak
version can never be overwritten) and opens the produced archive to verify it contains
`MoleHill.Rhino.rhp`, `MoleHill.gha`, `MoleHill.Core.dll`, `MoleHill.Interop.dll` and `manifest.yml`.
Staging the right files is not evidence the archive holds them — 0.14.3-beta shipped a
Grasshopper-only package that looked publishable.

## SDK and runtime

`global.json` records the SDK floor MoleHill is known to build with (`8.0.400`) and rolls forward to a
newer major, because the 2026-09-19 review ran on `10.0.400`. It is a floor, not a ceiling.

**A newer SDK is not a reason to retarget the plugin.** The `.rhp` and `.gha` target `net7.0` because
that is what Rhino 8 loads; the test projects target `net8.0` because they run under `dotnet test`.
Changing either is a host-compatibility decision needing its own verification, not a build-tooling
cleanup.

Host assembly paths (`RhinoInstallDir`, `RhinoSystemDir`, `RhinoGrasshopperDir`,
`WindowsDesktopRefPackDir`) are declared in `Directory.Build.props` and overridable per machine, so no
absolute developer path is buried in a csproj.

**RhinoCommon resolves differently in src and in tests, deliberately.**

- `src/MoleHill.Rhino` and `src/MoleHill.Interop` pin `RhinoCommon 8.9` with `ExcludeAssets="runtime"`
  and `PrivateAssets="all"`. 8.9 is the **compatibility floor** — the plug-in must load on any Rhino
  8.9+, so it compiles against the oldest supported API and takes the runtime assembly from the host.
  `PrivateAssets` stops that floor flowing to consumers.
- The test projects pin **nothing**. They load a real Rhino, so they take `RhinoCommon` from
  `$(RhinoSystemDir)` through a copy-local `Reference`, and always match the installed runtime.

So the version a host test exercised is the installed Rhino's. This is not cosmetic: managed
RhinoCommon, native `rhcommon_c.dll` and the installed Grasshopper are a version-locked set. The test
projects previously pinned `8.34`; Rhino auto-updated to `8.35` on 2026-09-19 and
`MoleHill.Grasshopper.Tests` stopped compiling (CS1705). If you find yourself adding a RhinoCommon
version to a test project, that is the bug.

**Known broken (2026-09-19): the native lane cannot start on Rhino 8.35.** Every `[RhinoNativeFact]`
fails with `DllNotFoundException: rhcommon_c … initialization routine failed`. This is **not** the
version skew above — it survives matched 8.35/8.35 assemblies, and `LoadLibraryEx` on
`rhcommon_c.dll` returns Win32 1114 from a bare PowerShell process with the search path set. Rhino
8.35's native core will not initialise outside a Rhino process, so the lane's premise — that setting
the DLL directory suffices — no longer holds. Repairing it means hosting the runtime properly
(`Rhino.Inside` / `RhinoCore`) rather than pointing at a directory. Until then the native lane reports
nothing, and `[RhinoNativeFact]` coverage is unverified on this machine.

**The working detour, for a test you need an answer from now.** Rhino 8.35 runs on .NET 8, the same
target as the test projects, so the test assembly loads straight into a live Rhino. Split the body out
of its `[RhinoNativeFact]` wrapper into a `public static` entry that writes its result to a file, spawn
a `rhino-mcp` slot, and invoke it there by reflection:

```csharp
var asm = System.Reflection.Assembly.LoadFrom(@"...\MoleHill.Rhino.Tests\bin\Release\net8.0\MoleHill.Rhino.Tests.dll");
asm.GetType("MoleHill.Rhino.Tests.GeometryHeavyStackBenchmark").GetMethod("RunToFile").Invoke(null, new object[] { outPath });
```

`GeometryHeavyStackBenchmark` is the worked example. The result file is the report, because
`run_command` returns only "Done.". This is a detour, not a lane: it runs one body on demand and proves
nothing about the other 130. **For timings, use the `hosted-perf` lane above.** It automates this
route, avoids both traps below by loading into an isolated `AssemblyLoadContext`, and compares the result
against a baseline.

**Two traps in the detour, both of which silently produce wrong numbers.**

- **Rhino has already loaded the plugin's assemblies, and yours will bind to those.** A slot loads
  `MoleHill.Rhino.rhp` at startup, which pulls in `MoleHill.Core` from the plugin's **Debug** output.
  `Assembly.LoadFrom` on the Release test DLL then resolves `MoleHill.Core` to the copy already in the
  AppDomain - not the one beside the test DLL. Everything measured is Debug Core, and nothing says so.
  Caught here by a profile whose new instrumentation did not appear in its own output. Assert it before
  believing a number:

  ```csharp
  var core = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "MoleHill.Core");
  Console.WriteLine(core.Location);   // which build
  var dbg = (System.Diagnostics.DebuggableAttribute)core.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)[0];
  Console.WriteLine(!dbg.IsJITOptimizerDisabled);   // true = optimized
  ```

  To measure Release Core, stage it into the plugin's output directory before spawning the slot and put
  the Debug copy back afterwards. On this fixture Debug vs Release Core was 3,840-4,037 ms against
  2,909-3,249 ms - a 25% error, large enough to reverse a ranking.
- **The slot locks the test DLL too.** `LoadFrom` holds it, so the next `dotnet build` fails with
  MSB3021/MSB3027 naming the Rhino PID. The close-before-rebuild rule in
  [rhino-live-testing.md](rhino-live-testing.md) covers the `.rhp`; it applies to the test assembly
  the same way.
