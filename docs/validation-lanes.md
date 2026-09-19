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

Note one deliberate asymmetry worth knowing when reading a host test result: `MoleHill.Rhino.Tests`
references the `RhinoCommon` **package** *and* the installed `RhinoCommon.dll`. The installed assembly
wins at runtime because it is copied local, which is what makes the native runtime usable — but it
means the version a host test exercised is the installed Rhino's, not the package's.
