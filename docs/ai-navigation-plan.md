# Plan: docs + code indexing for AI-agent (and human) navigation

Goal: an agent dropped into this repo can answer "where does X live / how does the pipeline flow" from
committed docs, without re-deriving it from a 6,000-line file each session. Today the entry docs are
**stale**: `AGENTS.md` says docs are "currently `docs/retainingWall.md`" and neither it nor `CLAUDE.md`
mention the `Analysis`/`Scattering` namespaces, the Scatter object type, the single-pass contour
generator, or the work-boundary crop. Memory (`MEMORY.md`) still says "Version 0.3.0" (actual is
0.7.x). Fix the entry points, add per-area indexes, and add one architecture map.

## 1. Refresh the two entry-point docs (canonical, committed)
- **`CLAUDE.md`** (build/test + architecture for Claude Code) — update: current version source
  (`Directory.Build.props`), add `Analysis/` (`ContourGenerator`, `SlopeAnalyzer`) and `Scattering/`
  (`ScatterSampler`) namespaces; add the features shipped since (Scatter object type, work-boundary
  input crop, single-pass contours, region-remesh + split-keep grading tiers); correct the component
  list and the data-flow section.
- **`AGENTS.md`** (general agent/repo guidelines) — fix the stale `docs/` inventory line; add the new
  namespaces/features; document the load-bearing conventions an agent must know: "Core is pure +
  testable, Rhino/GH is glue", the partial-class decomposition pattern, the `.gha` lock on rebuild,
  and the parallel-`File.AppendAllText` probe-contention trap.
- **Define which is canonical** and cross-link. Recommended: `AGENTS.md` = repo/build/conventions
  (tool-agnostic), `CLAUDE.md` = a thin pointer to `AGENTS.md` + `docs/architecture.md` plus any
  Claude-Code-specific notes. Avoid maintaining the same facts in two places.

## 2. One architecture map — `docs/architecture.md`
The high-level map an agent reads first. Sections:
- **Projects** (TriangleNet vendored; Core pure; Grasshopper + Rhino are the two hosts) and the
  dependency direction.
- **Build pipeline** (`TerrainBuildService.Build` → stage executors): TIN → modifiers → grading →
  analysis → zones → objects/scatter; which stages cache (fingerprints) and which run only in Final
  mode; the `TerrainBuildMode` distinction; conduit-preview vs SyncOutputs vs Bake.
- **Grading tier cascade** (Pad): explicit batter → split-keep (CDT conform) → region-remesh →
  clean structured failure; the "always watertight 2.5D" invariant; where each tier lives.
- **Flat-array data format**, the XY-hash caching in `TinEngine`, and the Steiner-Z priority order
  (these already live in `CLAUDE.md` — move the authoritative copy here and link from both entry docs).
- A simple ASCII data-flow diagram (GH path and Rhino path).

## 3. Per-folder index files (the "indexing" ask)
Add a short `README.md` in each key folder: a one-line purpose per file + the local entry points. Keep
them terse so they're cheap to maintain. Target folders:
- `src/MoleHill.Core/Grading/README.md` — the tier architecture + which file owns each tier
  (`PadGrader.*`, `PathGrader.*`, `GradedRegionAssembler`, `MeshAreaTopologySplitter`, the shared
  `GradingGeometry2D`).
- `src/MoleHill.Core/Engine/README.md`, `Processing/README.md`, `Analysis/README.md` — one-liners
  (`TinEngine`, `SurfaceRemesher`, `TinBoundaryPreparer`; `PointCloudProcessor`, `RegionInputFilter`,
  `TinInputCleaner`; `ContourGenerator`, `SlopeAnalyzer`).
- `src/MoleHill.Rhino/Services/README.md` — the `TerrainBuildService` partial map (what each
  `.Stage.cs` owns), `TerrainController` responsibilities, the conduit/cache/displaystate trio, the
  snapshot resolver/builder.
- `src/MoleHill.Rhino/UI/README.md` — panel partial map + the card/editor primitive vocabulary
  (cross-link `rhino-panel-ui-framework` knowledge).
- `src/MoleHill.Rhino/Model/README.md` — the definition hierarchy (`ModifierDefinition`,
  `AnalysisDefinition`, `TerrainObjectDefinition` incl. scatter) and the JSON polymorphism /
  `SchemaVersion` + serializer.

## 4. Code-level navigability conventions
- **Standardize file-header summaries**: every partial and every >300-line file starts with a 1–2 line
  `//` comment stating what it owns (many already do — make it universal). Agents grep these first.
- **XML `<summary>` on public Core APIs**: the newest ones are well-documented (`ScatterSampler`,
  `ContourGenerator`, `RegionInputFilter`); backfill the under-documented public types in
  `Engine/Grading/Processing` so signatures are self-describing.
- **Stage-map comment** at the top of `TerrainBuildService.cs` listing the partials and order.

## 5. A maintained file index (optional, high ROI for agents)
- `docs/file-index.md` = generated table of `path → first header-comment line` for `src/MoleHill.*`.
  A small `pwsh`/`dotnet` script (`generate-file-index.ps1`) regenerates it; run in CI or pre-commit so
  it stays fresh. Gives an agent a single grep-able map of the whole codebase.

## 6. Reconcile memory vs repo docs
Durable architecture facts belong in committed docs (versioned, shared); the agent memory
(`MEMORY.md` + files) should hold session/feedback/working-state context, not the canonical
architecture. Update `MEMORY.md`'s stale "Version 0.3.0" + component list to point at
`docs/architecture.md` rather than restating it.

## Verification
Markdown needs no build. The real test: pose 4–5 navigation questions ("where is the grading tier
cascade?", "how does a scatter instance get previewed vs baked?", "which stage caches contours?",
"where do I add a new analysis type?") and confirm each is answerable from the entry docs +
per-folder READMEs in ≤2 hops. Keep `AGENTS.md`/`CLAUDE.md` as the front door.

## Suggested order
1 (entry docs — biggest immediate win) → 2 (architecture map) → 3 (per-folder indexes) → 4 (header/XML
sweep, alongside the cleanup decomposition) → 5/6 (index script + memory reconcile).
