# Codebase cleanup plan

Grounded in a survey of the current tree (2026-06). Ordered by value/risk: do the safe, high-value
items first. The bar for every step: **`dotnet build` clean + `dotnet test tests/MoleHill.Core.Tests`
green (currently 285)**; for static-only code moves, compile-clean ⟹ behavior-identical (the compiled
type is byte-for-byte unchanged). Do NOT touch `src/TriangleNet/**` (vendored).

## 1. Delete dead code (safe, compiler-verifiable)
- **`GradedRegionAssembler.Assemble` + `TryBuildOutsideTerrain` + `RegionInsert`** — no call sites
  remain (`.Assemble(` matches nothing). Long flagged dead in memory. Remove them and any helpers that
  become unreferenced as a result (iterate a reference-count sweep to fixpoint, as was done for the 21
  earlier dead helpers).
- **Re-run the unreferenced-private/internal sweep** across `src/MoleHill.Core` + `src/MoleHill.Rhino`
  (single-pass identifier reference count, iterated): removing a method with zero callers is
  provably behavior-preserving.
- **`tests/TopoTIN.Tests`** — empty legacy placeholder; remove it (and from the solution).

## 2. Legacy grading tiers (gated on Rhino verification)
`ConstraintFirstGradingEngine`, `GradeWithConstraintFirstTopology`, `GradeWithRefinedZOnlyFallback` are
reachable in the cascade but **proven unreached at runtime** (the Phase-7 throw test: full suite green
with both throwing). They are the last-resort watertight producer, so deletion needs one Rhino visual
pass first (see the grading memory). Steps: remove the cascade calls in `PadGrader.cs`, delete the
three engines + their now-orphaned helpers, migrate any copied-case test asserting their diagnostics to
the watertight/slope invariants, full regression. This is "Phase 7"; keep it as its own commit.

## 3. De-duplicate geometry helpers
- **`PointInPolygon`** is defined twice (`Grading/GradingGeometry2D.cs` and `Grading/PadGrader.Spatial.cs`,
  both `public static`, identical signature). Pick `GradingGeometry2D` as canonical and route the other
  to it (or delete the duplicate and repoint callers).
- Audit the neighbours for the same drift: `DistToPolygon` vs `GradingGeometry2D.DistanceToPolygon`,
  `PolygonInteriorPoint` (exists in both `GradingGeometry2D` and `PadGrader.Spatial`),
  `TerrainTriangulationInputBuilder.ToFlatPolyline`/`CreateFlatPolylines` vs the boundary-polyline
  builders. Consolidate the 2D-geometry primitives under `GradingGeometry2D` (or rename it to a neutral
  `Geometry2D` if it is now used well beyond grading — it already backs scatter + work-boundary).

## 4. Break up the remaining god files (the "messy" feeling)
Same partial-class decomposition already applied to `TerrainBuildService` (10+ `.Stage.cs` partials)
and `MoleHillPanel` (`.Cards.cs`, `.Editors.cs`). Continue it:
- **`UI/MoleHillPanel.cs` (6,481 lines)** — extract per-tab card builders into partials:
  `MoleHillPanel.Modifiers.cs`, `.Objects.cs` (incl. scatter body + block selector wiring),
  `.Analysis.cs`, `.Markers.cs`, and `.Toolbar.cs`. Keep the shared primitives in `.Cards.cs`/`.Editors.cs`.
- **`Services/TerrainController.cs` (3,623 lines)** — split by concern into partials:
  `.State.cs` (load/save/GetState/mutate), `.Build.cs` (schedule/run/displaystate), `.Output.cs`
  (SyncOutputs/Bake/AddObject), `.Sources.cs` (EditSourceObjectIds/layers/boundary), `.Analysis.cs`
  (contour refresh/active analysis). Static-heavy ⟹ low risk.
- Assess (don't force): `Engine/SurfaceRemesher.cs` (2,196), `Services/TerrainBuildService.Grading.cs`
  (1,412), `Grading/GradedRegionAssembler.cs` (1,172, shrinks after step 1).
- Verification for UI/controller splits: compile-clean + a Rhino smoke load (panel opens, a build runs).

## 5. Repo hygiene
- **`.gitignore`**: add `.codex-cases/` (untracked case-bundle clutter) and any other generated dirs
  (`.artifacts/`, temp case dirs) not already ignored.
- **Untracked docs**: `docs/comment.md` and `docs/verandi-crisp-plan.md` — verdict each: the Verandi
  plan is superseded (Verandi now grades crisp) → delete or move to `docs/archive/`; `comment.md` looks
  like a scratch file → delete.
- **Stale plan docs**: `docs/crop-region-modifier-plan.md` is now implemented (work-boundary),
  `docs/grading-rebuild-*.md` describe completed work. Move finished plans to `docs/archive/` (keep for
  history) so `docs/` shows only live material.
- **Huge copied-case test files** (`TerrainGradePadComplexUpstreamCopiedCaseTests.cs` 28k lines, etc.):
  lower priority, but consider externalizing the inline vertex/face arrays to embedded data files
  (`.json`/`.obj`) loaded by a small harness, so the `.cs` shrinks to the scenario + asserts. Optional.

## 6. Consistency pass (low effort)
- Confirm file-scoped namespaces + one-type-per-file everywhere in `MoleHill.*` (a couple of small
  nested result types were added recently — fine, but verify the convention).
- Normalize the LF/CRLF churn (commits show repeated "LF will be replaced by CRLF") via a
  `.gitattributes` (`* text=auto eol=crlf` for `.cs`) to stop the noise.

## Suggested order
1 (dead code) → 5/6 (hygiene, cheap) → 3 (dedupe) → 4 (god files, incrementally) → 2 (legacy tiers,
after a Rhino pass). Each as a focused commit; full Core suite green after each.
