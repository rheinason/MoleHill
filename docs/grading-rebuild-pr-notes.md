# Grading Rebuild PR Notes

## Summary

- Rebuilt Grade Pad and Grade Path around shared constraint-first topology via `ConstraintFirstGradingEngine`.
- Removed legacy pad/path remesh fallback routes and dead topology paths.
- Added bounded density retry, structured failure diagnostics, preserved-elevation spatial lookup, and constraint-network spatial broad phase.
- Split large grader files into phase-specific partials for path/pad topology, Z evaluation, daylighting, shoulders, diagnostics, and result building.
- Externalized the large copied Grade Path regression fixture into embedded JSON.
- Strengthened pad local fallback to split bounded pad-influence faces and allowed coupled protected-pad slope fallback when the local result materially improves slope diagnostics.

## Validation

- `dotnet test MoleHill.sln -p:BaseOutputPath=.codex-build\solution-release-readiness-final\ -p:UseSharedCompilation=false`
  - Core: 223 passed, 0 failed.
  - Grasshopper: 3 passed, 16 skipped.
- `dotnet test MoleHill.sln -p:BaseOutputPath=.codex-build\solution-local-refinement\ -p:UseSharedCompilation=false`
  - Core: 224 passed, 0 failed.
  - Grasshopper: 3 passed, 16 skipped.
- `dotnet build src\MoleHill.Rhino\MoleHill.Rhino.csproj -p:BaseOutputPath=.codex-build\rhino-release-readiness\ -p:UseSharedCompilation=false`
  - 0 warnings, 0 errors.
- `dotnet test tests\MoleHill.Core.Tests\MoleHill.Core.Tests.csproj -p:BaseOutputPath=.codex-build\core-local-refinement\ -p:UseSharedCompilation=false`
  - Core: 224 passed, 0 failed.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-yak-package.ps1 -Configuration Release`
  - Built `.artifacts\yak\MoleHill-0.6.8-beta\molehill-0.6.8-beta-rh8_9-win.yak`.
  - Yak reported the known acceptable content-name warning: `MoleHill.Rhino` vs package id `MoleHill`.

## Known Non-Blockers

- Pad local fallback is still Z-only rather than exact local constraint insertion; whole-mesh recovery remains preferred for coupled topology-deficiency failures.
- Coupled slope fallback can still be rejected when local refinement would worsen slope diagnostics; rejected cases surface `grade_pad.fallback.local_refinement_rejected`.
- Preserved-elevation constraint indexing is rebuilt per `Grade` call. This is correct and cheap at current call rates; cache only if profiling shows it is hot.

## Release Note

This branch is ready for PR review or beta packaging. The remaining items are follow-up architecture/performance notes, not merge blockers.
