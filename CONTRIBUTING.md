# Contributing to MoleHill

Thanks for your interest. MoleHill is a beta Rhino 8 terrain plug-in with an optional Grasshopper
companion. Bug reports with a reproducible case are the most useful contribution; pull requests are
welcome too.

## Reporting a bug

Open an issue with the bug template. The single most helpful thing is a repro case: MoleHill's panel has
a **Copy Case** button (Status card) that exports the terrain definition and its inputs. Please say which
MoleHill version (Rhino's Package Manager shows it) and Rhino 8 build you used. Remove any project data
you cannot share before attaching a case.

Security issues: see [SECURITY.md](SECURITY.md), not the issue tracker.

## Prerequisites

- Windows with **Rhino 8** installed. The Rhino and Grasshopper projects compile against it, and the
  native tests and live checks run inside it.
- **.NET 8 SDK** or newer (`global.json` sets the floor and rolls forward).
- If Rhino is not in `C:\Program Files\Rhino 8`, pass `-p:RhinoInstallDir=...` or set
  `MOLEHILL_RHINO_DIR` (see `Directory.Build.props`).

`MoleHill.Core`, the terrain engine, has no Rhino dependency: it builds and its tests run on any machine
with the SDK, and that is what CI runs.

## Build and test

```bash
dotnet build MoleHill.sln
dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj
```

Close Rhino before building: it locks the installed `.gha`. Then use the validation lanes, which record
exactly what they exercised:

```powershell
./validate.ps1 managed      # all managed tests; native tests skip without Rhino
./validate.ps1 warnings     # the owned-code warning count must stay at 0
./validate.ps1 hosted-perf  # timings inside a real Rhino, against a baseline (maintainer machine)
```

A green `dotnet test` is not the whole story: native tests skip without Rhino and benchmarks only run
when asked. [docs/validation-lanes.md](docs/validation-lanes.md) explains what each lane proves.

## Finding your way around

Start with [docs/architecture.md](docs/architecture.md), then [docs/file-index.md](docs/file-index.md)
and the `README.md` in each source folder. [AGENTS.md](AGENTS.md) holds the conventions in one place;
the most important ones:

- Reusable computation goes in `MoleHill.Core`, with a test. Rhino and Grasshopper API calls stay in the
  host projects.
- Large classes are split into partial files by concern; add to the partial that owns the concern.
- Geometry tests should cover the awkward cases: collinear and duplicate points, crossing breaklines,
  tolerance boundaries, and grading slopes.
- Test names follow `MethodName_Scenario_ExpectedResult`.
- Do not modify `src/TriangleNet/` without recording the change in `LICENSES/TriangleNet.md` and the
  file's header; Triangle's license requires it.

## Pull requests

- Keep each PR to one change, with a short imperative commit subject (`Fix Steiner Z for parallel
  breaklines`).
- Say what you ran to verify it (`validate.ps1` lanes, a live Rhino check) and include screenshots for
  panel, toolbar or Grasshopper UI changes.
- Update the docs in the same PR when you add, remove or rename a source file or change a convention;
  regenerate the file index with `pwsh ./generate-file-index.ps1`.

## Licensing of contributions

MoleHill's own code is GPL-3.0-only with an additional permission for the TriangleNet subtree (see
[LICENSE](LICENSE)). By submitting a pull request you agree that your contribution is licensed under
the same terms.
