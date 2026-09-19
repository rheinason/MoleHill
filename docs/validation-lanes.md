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
nothing about the other 130.

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
