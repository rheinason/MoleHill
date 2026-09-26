# Codebase cleanup plan

> Current cross-cutting review and implementation sequence:
> [Codebase review — 2026-09-19](codebase-review-and-implementation-plan-2026-09-19.md).
> The July snapshot below is retained as historical context; use the newer review for current
> priorities and validation gaps.

Grounded in a survey of the current tree (2026-07-11). Ordered by value/risk: do the safe, high-value
items first. The bar for every step: **`dotnet build` clean + `dotnet test tests/MoleHill.Core.Tests`
green**; for static-only code moves, compile-clean implies behavior-identical. Do NOT touch
`src/TriangleNet/**` (vendored).

## Progress snapshot (2026-07-11)

Status: active, with repository hygiene and documentation maintenance substantially complete.

- **Complete:** generated directories are ignored, completed plans are archived, `.gitattributes` is
  present, and the full solution test pass is green.
- **In progress:** the panel/build-service partial decomposition continues; `TerrainController.cs` and
  several large Core/Rhino files remain intentionally monolithic.
- **Not started:** a fresh dead-code sweep and the broader 2D-helper deduplication audit.
- **Grading follow-up:** the tier cascade and copied-case coverage are in place; add a regression whenever
  a real case reaches `grade_pad.all_tiers_deferred`.

## Housekeeping pass (2026-09-26, branch `housekeeping-2026-09`)

- **Dead code (step 1):** done. IDE0051/IDE0052 were raised for one build and the sweep repeated until
  it was clean, which removed about 1,080 lines. Re-run it the same way. The analyzer runs one target
  framework at a time, so a member used only under `#if WINDOWS` in the multi-targeted Grasshopper
  project looks unused. Such a member belongs inside the `#if`, not in the deletion list.
- **Unused usings and broken XML doc `cref`s:** done. Most broken crefs came from `MoleHill.Rhino` and
  `MoleHill.Grasshopper` shadowing the `Rhino`/`Grasshopper` root namespaces; write `global::` in a cref.
- **Dedupe (step 3):** mesh normal orientation (`MoleHill.Shared.MeshNormalOrientation`, with a guard
  test), split-result sub-meshes (`SplitResultMeshBuilder`), edge-use counting and edge keys
  (`IndexedMeshTools.CountFaceEdges`/`GetEdgeKey`), the CSR cell index (`CellMembershipIndex`),
  content visibility (`TerrainContentVisibility`) and two containment predicates now each have one
  home. Two copies stay separate on purpose: the partition sub-mesh keeps a sorted vertex order as
  part of its contract, and the annotation builder's boundary test treats "no boundaries" as outside.
- **Build hygiene:** the `MoleHill.Shared` compile list is one glob import (`MoleHill.Shared.props`),
  and no test names an absolute developer path (`RepositoryPaths`).
- **Still open:** step 4 (god files), the catchment-preview cancellation and GH snapshot-hash items in
  `code-review-findings-2026-09-25.md`, 106 file-index entries whose files have no header comment,
  and some root clutter (`CaseMeshResult.obj/.mtl`, `GradePadTest.3dmbak`) to delete.

## 1. Delete dead code (safe, compiler-verifiable)
- The previously listed `GradedRegionAssembler.Assemble`, `TryBuildOutsideTerrain`, and `RegionInsert`
  cleanup targets are already gone. Do not delete the remaining `GradedRegionAssembler` split/weld
  helpers; they have live call sites or tests.
- **Re-run the unreferenced-private/internal sweep** across `src/MoleHill.Core` + `src/MoleHill.Rhino`
  and only remove methods with zero callers.
- Keep any deletion that touches grading fallback behavior out of safe-cleanup commits unless a Rhino
  visual pass and regression tests already prove it unchanged.

## 2. Grading reliability follow-up
The old Grade Pad legacy whole-mesh fallback has been removed from the active cascade. The current
cascade is explicit batter -> split-keep -> region-remesh -> clean failure with structured diagnostics.
Future grading work should:
- keep copied-case tests asserting watertight/slope invariants instead of legacy diagnostic vocabulary,
- add regressions for any scene that reaches `grade_pad.all_tiers_deferred`, and
- avoid emitting non-watertight meshes as a fallback.

## 3. De-duplicate geometry helpers
- **`PointInPolygon`** is exposed through `GradingGeometry2D` and compatibility wrappers in
  `PadGrader.Spatial.cs`. Keep `GradingGeometry2D` canonical and route or remove duplicates only when
  callers are clear.
- Audit neighbours for the same drift: `DistanceToPolygon`, `PolygonInteriorPoint`, and flat-polyline
  builders. Consolidate shared 2D primitives under `GradingGeometry2D` unless a broader neutral
  `Geometry2D` name becomes warranted.

## 4. Break up the remaining god files
Same partial-class decomposition already applied to `TerrainBuildService` and `MoleHillPanel`. Continue
incrementally:
- **`UI/MoleHillPanel.cs`** - done 2026-09-26 (2,847 -> ~480 lines). `BuildContent` became four
  section builders, and every member now lives in a partial named for its concern.
- **`Services/TerrainController.cs`** - done 2026-09-26 (2,089 -> ~190 lines, 9 new partials).
- Both were pure moves: the code lines across each class's files are identical before and after.
- Next largest partials, if one grows further: `MoleHillPanel.Annotations.cs` (~900),
  `TerrainController.Display.cs` and `.Build.cs` (~920 each).
- Assess but do not force: `Engine/SurfaceRemesher.cs`, `TerrainBuildService.Grading.cs`, and
  `GradedRegionAssembler.cs`.
- Verification for UI/controller splits: compile-clean + Rhino smoke load (panel opens, a build runs).

## 5. Repo hygiene
- Keep generated/temp dirs ignored (`.codex-cases/`, `.artifacts/`, `.tmp_build/`, `*.tmp.*`).
- Move completed plan docs to `docs/archive/` so live `docs/` stays navigable.
- Large copied-case tests may eventually move inline vertex/face arrays to embedded data files, but this
  is lower priority than behavior coverage.

## 6. Consistency pass
- Confirm file-scoped namespaces + one-type-per-file in `MoleHill.*` where practical.
- Consider a `.gitattributes` normalization if CRLF/LF churn keeps obscuring diffs.

## Suggested order
1 (dead code) -> 5/6 (hygiene, cheap) -> 3 (dedupe) -> 4 (god files, incrementally) -> 2 (grading
reliability cases). Each as a focused commit; full Core suite green after each.
