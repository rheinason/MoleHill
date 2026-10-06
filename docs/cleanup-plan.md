# Codebase cleanup plan

Open maintenance work, and the rules for doing it safely. The cross-cutting review with the remaining
reliability and architecture items (R01–R13) is
[Codebase review — 2026-09-19](codebase-review-and-implementation-plan-2026-09-19.md).

The bar for every step: `dotnet build` clean, `./validate.ps1 managed` green, and `./validate.ps1
hosted-perf` for anything on a build path. Moving members between partials is compile-clean ⟹
behaviour-identical. Do not touch `src/TriangleNet/**` (vendored).

## Done (housekeeping pass, 2026-09-26)

- **Dead code:** IDE0051/IDE0052 raised for one build, repeated until clean (~1,080 lines). IDE0051 does
  not see unused *nested types*; search for those separately. The analyzer runs one target framework at
  a time, so a Grasshopper member used only under `#if WINDOWS` looks unused: move it inside the `#if`.
  IDE0051 also missed private members of `TerrainBuildService` that a review found. Cross-check with
  a text sweep: a private or internal method whose name occurs once across `src/` and `tests/`.
- **Unused usings, broken XML doc `cref`s:** `MoleHill.Rhino`/`MoleHill.Grasshopper` shadow the `Rhino`/
  `Grasshopper` root namespaces, so write `global::` in a cref.
- **One home for copied helpers:** `MeshNormalOrientation` (with a guard test), `SplitResultMeshBuilder`,
  `IndexedMeshTools.CountFaceEdges`/`GetEdgeKey`, `CellMembershipIndex`, `TerrainContentVisibility`.
  Deliberately still separate: the partition sub-mesh (sorted vertex order is its contract) and the
  annotation builder's boundary test ("no boundaries" means outside).
- **God files:** `MoleHillPanel.cs` (2,847 → ~480 lines) and `TerrainController.cs` (2,089 → ~190) split
  into one partial per concern; each root file's header names its partials.
- **Build hygiene:** `MoleHill.Shared.props` compiles the Shared folder into every consumer; no test names
  an absolute developer path.
- **Docs:** completed plans, resolved reviews and superseded explorations were deleted rather than
  archived. Git history keeps them; a live doc should describe the code as it is or work still open.

## Public release (2026-09-27)

The repository went public as a fresh `rheinason/MoleHill` holding a rewritten history. The previous
repository is the private `rheinason/MoleHill-archive` (its pull requests, old releases and the
unrewritten history).

- Removed from all history: the office toolset (`Python Commands Source/`) and every toolbar icon
  derived from it (earlier `.rui` versions carry transparent bitmaps), survey and project data, the
  panel glyphs' reference SVGs, SDK and build state, and the private session links in commit messages.
- Yak `0.14.4`–`0.14.6-beta` shipped the office-derived icons and are yanked; `0.14.7-beta` replaces them.
- Accepted residuals: old versions of `PanelButtonIcons.cs` still hold the ported glyph paths; old
  versions of our own docs name the file `Master.ai` and contain local paths; the perf baseline records
  the recording machine's hostname, which the lane needs; `ProjectBaseCPlaneService` still migrates the
  legacy `FOTM` construction plane by name.
- Never push the `archive` remote's history to `origin`.

## Open

- **2026-10 refactoring sweep:** prioritized recommendations (forked face-cut engine, mesh value
  type, geometry kernel, registry completion, Services layout) are in
  [refactoring-recommendations-2026-10.md](refactoring-recommendations-2026-10.md).
- **Catchment and ponding previews** re-run the full solvers on the UI thread with no cancellation.
- **Grasshopper snapshot component** copies and hashes the whole mesh on every Rhino `StateChanged`.
- **106 file-index entries** belong to files with no header comment; add one when you next touch a file.
- **Large partials to watch:** `MoleHillPanel.Annotations.cs` (~900 lines), `TerrainController.Display.cs`
  and `.Build.cs` (~920 each). Split by concern if one starts mixing unrelated work. Assess, but do not
  force, `Engine/SurfaceRemesher.cs`, `TerrainBuildService.Grading.cs` and `GradedRegionAssembler.cs`.
- **Geometry helpers:** `DistanceToPolygon`, `PolygonInteriorPoint` and flat-polyline builders may have
  drifted copies. Consolidate under `GradingGeometry2D` only after documenting semantic differences
  (review R12).
- **Grading:** add a regression for any real scene that reaches `grade_pad.all_tiers_deferred`, and never
  emit a non-watertight mesh as a fallback.
- **Copied-case tests** could move inline arrays to embedded data files (review R11), after behaviour
  coverage.

## Doc lifecycle

`docs/` holds reference (`architecture.md`, `file-index.md`, `validation-lanes.md`,
`rhino-live-testing.md`, contracts such as `project-base-georeference.md`), the backlog, and plans or
investigations for work that is still open. When a plan ships, fold what a reader still needs into
`architecture.md` or the folder README and delete the plan in the same change. Do not archive it.
