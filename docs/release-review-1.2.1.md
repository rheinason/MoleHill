# 1.2.1 release review — 2026-10-04

Scope: pending triangulation and layer-picker changes, commits since 1.2.0-beta, release configuration,
and the existing ownership/validation findings. This is a focused release review, not a complete audit.

## Fixed

- Shared triangulation splits segments at existing T-junction vertices without modifying caller inputs.
  The regression checks actual retained segment edges, not only vertex counts. Raw and stationed
  persisted lines now both have to rebuild without zero-area caps; the old test required the defect.
- The native layer grid avoids recreating controls for every layer. Arrow navigation selects and scrolls
  without filtering again; grid-focused Enter/Escape work. Selection survives data-store replacement,
  even when Eto raises SelectionChanged while resetting it.
- Packaging compares duplicate runtime DLLs by SHA-256 rather than file size. Recursive cleanup checks
  that the resolved target is a child of the Yak staging root.
- The warning lane now reports non-compiler build failures directly instead of calling a file-access
  error "Owned-code warnings: 0".
- Version, README, changelog and relevant architecture/folder references describe stable 1.2.1.

## Crucial outstanding findings

1. **Native geometry lifetime needs explicit ownership.** Canceled, failed or superseded background
   builds return before cache merge without disposing newly produced worker geometry. Generated objects
   and their preview caches also lack a deterministic disposal endpoint. Worker copies borrow main-cache
   objects, so indiscriminate disposal would be unsafe. See [build-result-ownership.md](build-result-ownership.md)
   and `TerrainController.Build.cs` / `TerrainRuntimeCache.cs`. Follow-up needs owned-versus-borrowed
   tracking plus a native private-memory soak across rapid edits, cancellation, deletion and document close.
   This review verifies the disposal gaps; it does not quantify their memory impact.
2. **Native acceptance remains incomplete.** The managed run skips 144 Rhino and 15 Grasshopper native
   tests. The documented headless-runtime initialization issue is still a validation limitation.
   Disposable Rhino launch was attempted through both the MCP tool and `validate.ps1 hosted-perf`; the
   execution host denied process breakaway (Win32 access denied). No live package load, picker screenshot,
   interactive behavior or hosted performance result was obtained in this session.

## Validation

- `pwsh -NoProfile -File ./validate.ps1 managed`: 1,249 Core, 1,135 Rhino and 39 Grasshopper tests passed;
  159 native tests skipped. Evidence: `.artifacts/validate/managed-20261004-210939/`.
- `pwsh -NoProfile -File ./validate.ps1 warnings`: nine projects compiled, zero owned-code warnings.
  Final evidence path is recorded in `.artifacts/release-warnings.log`. An earlier retry encountered a
  transient access error on TriangleNet's generated file list; the subsequent complete gate passed.
- `pwsh -NoProfile -File ./validate.ps1 package`: combined archive verified, 19 entries including the
  Rhino plugin, merged Grasshopper plugin, Core, Interop and manifest.
  Evidence: `.artifacts/validate/package-20261004-211248/`.
- Both packaged host assemblies report version `1.2.1.0`; manifest version is `1.2.1`.
- `pwsh -NoProfile -File ./validate.ps1 hosted-perf`: unavailable because Rhino launch was denied;
  no regression comparison or new performance claim. Evidence: `.artifacts/validate/hosted-perf-20261004-211115/`.
- `git diff --check` passed. `generate-file-index.ps1` was run; source paths did not change.

Existing backlog edits and the untracked temporary document-wall probe are kept outside this release commit.
