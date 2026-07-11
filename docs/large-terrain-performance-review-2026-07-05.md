# Large terrain performance review - 2026-07-05

Focused review of large-terrain build performance using the exported case:

`C:\Users\hbxma\AppData\Local\Temp\MoleHillCases\Terrain-1-20260705-111421-3ba51dc1.zip`

The in-repo `MoleHill.CaseReplay` harness could not initialize Rhino in-process on this machine
(`COMException E_FAIL` during Rhino startup), so this review uses the bundle's captured
`build-log.txt`, `manifest.json`, OBJ outputs, and copied Core regression tests as the measured
evidence.

**Overall assessment:** this case is not currently a catastrophic build. The captured final rebuild is
1.33 s, with 1.32 s in the build pipeline. The work is concentrated enough to act on: Grade Path is
the primary cost, analysis is the next visible cost, and retaining-wall diagnostics show an expensive
fallback path that can be hidden by cache hits. TIN itself is not the bottleneck for this case.

Severity legend: **HIGH** = highest expected user-visible performance win on large terrain.
**MEDIUM** = meaningful improvement or important scaling guard. **LOW** = instrumentation,
ergonomics, or hardening.

## Completion snapshot (2026-07-11)

All jobs from this review are now closed, either by implementing the proposed work or by replacing it
with a safer measured optimization. Debug benchmark timings below are machine-specific and are intended
for before/after comparison, not as release-mode guarantees.

- **H1 complete:** `MeshHeightProjector` is used for cut/fill and preview projection. Because every face
  is registered in each grid cell touched by its XY bounds, queries now inspect only the owning cell.
  A 100,000-query benchmark fell from 836.4 ms to 90.9 ms with the same checksum and zero misses; index
  construction remained approximately 50 ms.
- **H2 complete:** summary-only slope builds avoid face-color allocation, and both summary and preview
  analysis use the large-mesh parallel path above 20,000 faces.
- **H3 complete:** environment-gated Grade Path, height-projector, and retaining-wall planner benchmarks
  cover the copied large-terrain case without slowing the normal test run.
- **M1 closed with a safer alternative:** full dirty-region crop/stitch remains available as future
  substrate through `DirtyRegionPlanner`, but it was not integrated because of the topology risk at the
  stitch boundary. Large final Rhino Grade Path builds with persistent hard constraints instead prefer
  the existing split-and-keep tier. The copied case fell from 2,496.5 ms / 174.1 MB allocated to
  825.4 ms / 94.8 MB with the same 8,410 vertices, 16,547 faces, and healthy topology.
- **M2 complete/superseded:** the reduced-interior-seed remesh attempt was already prepared first and is
  accepted before the carried-interior pass when requested. Retaining-wall self-intersection validation
  now uses spatial segment buckets while retaining the exact intersection predicate. End-to-end planner
  time fell from 2,134.9 ms to 323.0 ms; preprocessing fell from 1,902.7 ms to 15.5 ms. This removed the
  measured need for a separate retained-plan cache.
- **M3 complete/superseded:** enabled analyses are cached independently by analysis id and fingerprint,
  and shared mesh/elevation/area context is built lazily only for cache misses. Cached generated output
  is restored per analysis. Summaries remain current rather than adding the proposed stale/deferred mode.
- **L1 complete:** timing records expose structured cache-hit state, build logs show cache-hit details,
  and hot restores no longer replay cold timing diagnostics.
- **L2 complete for the safe measured scope:** output fingerprints remain threaded through cache entries
  and cached meshes are no longer normalized a second time after cloning. The clone remains an ownership
  boundary so callers cannot mutate a cache entry in place.

The original item descriptions and work order below remain as implementation history. New dirty-region
work should be reopened only when a benchmark demonstrates a material gap beyond the split-and-keep path.

---

## Start Here (junior dev onboarding)

Read this section before touching any item. It gives you the context the items below assume.

**Read these first, in order:**

1. `docs/architecture.md` — the pipeline map (build stages, grading tier cascade, preview vs bake).
   Every item below lives somewhere on that map.
2. `docs/file-index.md` — path → one-line summary for every source file. Use it to locate anything.
3. The `README.md` in the folder you're editing (e.g. `src/MoleHill.Core/Analysis/README.md`).
4. `AGENTS.md` — full build/test/convention reference.

**Build and test (PATH is required on this machine):**

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build MoleHill.sln
dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj      # Core-only, no Rhino
dotnet test --filter "ClassName"                                       # single class
```

Core changes (H2, most of H1, H3) can be built and tested without Rhino. Rhino-side changes
(`MoleHill.Rhino/Services/*`) compile against Rhino 8 / net7.0 but the panel can only be exercised by
loading the `.rhp` in Rhino — close Rhino before rebuilding (it locks the output). See `CLAUDE.md`.

**Where the evidence and assets live:**

- The exported case bundle is the zip named in the header, under
  `C:\Users\hbxma\AppData\Local\Temp\MoleHillCases\`. Unzip it to read `build-log.txt`, `manifest.json`,
  and the `output-*.obj` meshes — that is the measured evidence for every timing in this doc.
- The copied regression cases (`Terrain_1_Grade_Path_CopiedCase.cs`, the retaining-wall planner case)
  ship **inside that bundle**, and older copies sit in scratch dirs (`.codex-cases/`, `.codex_tmp/`).
  They are self-contained Core tests (hard-coded point/curve literals, no Rhino). H3 is about promoting
  one of them into a permanent fixture.

**Two files this doc references already exist — do not create them from scratch:**

- `src/MoleHill.Core/Grading/TerrainFaceGrid.cs` — the "2.5D face grid" H1 tells you to reuse. It is
  currently `internal` and lives under `Grading/`; `FaceGrid` in `PadGrader.Spatial.cs` subclasses it.
  H1's job is to make this idea usable from the analysis path, not to invent it.
- `tests/MoleHill.Core.Tests/LargeDatasetBenchmarkTests.cs` — an existing *ad-hoc CSV* benchmark. H3
  adds new perf fixtures **alongside** it (following its `ITestOutputHelper` + `Stopwatch` pattern),
  not a replacement.

**Glossary (terms used below without definition):**

- **TIN** — triangulated irregular network; the terrain mesh. **CDT** — constrained Delaunay
  triangulation.
- **Breakline** — a curve the mesh edges must follow (wall top, road edge). **Hard constraint** — an
  edge grading may not cross/relax.
- **Batter** — the graded slope face between a flat pad/path and existing ground. **Daylight** — where
  a graded slope meets existing ground (zero-crossing of `newZ − origZ`).
- **Steiner point** — a vertex Triangle.NET inserts during refinement; its Z must be interpolated.
- **2.5D** — a surface with one Z per XY (a heightfield). Projecting "along world Z" onto a 2.5D mesh
  is just an XY lookup, which is why H1 can drop the Rhino line-intersection.
- **Cut/fill** — earthwork volume vs a reference mesh (cut = removed, fill = added).
- **Stage cache / fingerprint** — each build stage hashes its inputs; an unchanged fingerprint restores
  the cached output instead of recomputing. Central to items L1, L2, M2, M3.

**How to read the historical items:** each has *Where* (files + lines — verify them, code drifts),
*Problem*, *Do this* (the originally proposed steps), and *Acceptance* (the intended outcome). The
completion snapshot above is authoritative where the final implementation differs from that proposal.
The work-order table at the bottom records the sequence originally recommended before the jobs closed.

---

## Case Evidence

Bundle metadata:

- Exported from `LargeTerrainTest.3dm` with plugin version `0.9.2.0`.
- Terrain stack: `Triangulate -> Retaining Wall -> Remesh -> Grade Pad -> Grade Path`.
- Enabled analyses: `Cut / Fill`, `Slope`; `Earthworks` disabled.
- `showAnalysisOutputs=false` and `showSlopePreview=false`, but enabled analysis summaries still run.
- Source geometry: 294 curves total. The main contour source set resolves 287 curves; retaining wall
  resolves 4 objects; Grade Pad resolves 1 object; Grade Path resolves 1 object; only zone Layer 01
  resolves an object.
- Output meshes:
  - `output-base.obj`: 31,144 verts / 62,071 faces.
  - `output-terrain.obj`: 8,392 verts / 16,511 faces.
  - `output-preview.obj`: 8,392 verts / 16,511 faces.

Captured timings:

| Stage | Time | Notes |
|---|---:|---|
| Rebuild total | 1.33 s | Snapshot, clone, merge, display, save, redraw all near 0 |
| Build pipeline | 1.32 s | Dominates total |
| Triangulate | 0.04 s | 160 duplicate points merged; not the bottleneck |
| Retaining Wall | 0.06 s | Stage timing likely cache-hit influenced |
| Retaining Wall Remesh diagnostic | 0.33 s | Replayed diagnostic shows fallback remesh cost |
| Remesh | 0.01 s | Isotropic remesh details show prior cost mostly in collapse/flip, but stage is cheap here |
| Grade Pad | 0.02 s | Explicit batter tier |
| Grade Path Core | 0.67 s | 6,341 verts / 12,411 faces -> 8,392 / 16,511 |
| Grade Path | 0.87 s | About 66% of build time |
| Analysis | 0.26 s | About 20% of build time |
| Zones | 0.03 s + 0.03 s | Mostly empty zones |

Regression assets in the bundle:

- `Terrain_1_Grade_Path_CopiedCase.cs`: one path, five hard constraints, expected Grade Path result
  `8,392` vertices / `16,511` faces.
- `Terrain_Terrain_1_Retaining_Wall_1_PlannerCopiedCase.cs`: four wall curves, 18,203 `Point3d`
  literals.

---

## H1 - Replace Rhino per-face projection in cut/fill analysis (HIGH)

**Where:** `src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:466-520`,
`src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:601-643`,
`src/MoleHill.Rhino/Services/TerrainMeshProjection.cs:28-50`.

**Problem.** Reference comparison analysis projects every current face centroid onto the reference
mesh through `TerrainMeshProjection.TryProjectPointAlongWorldZ`. That helper recomputes the reference
mesh bounding box, builds a vertical Rhino `Line`, calls `Intersection.MeshLineSorted`, sorts hits,
then calls `ClosestMeshPoint` for every sample. On this case the current terrain has 16,511 faces and
the base/reference mesh has 62,071 faces, so even a 0.26 s analysis stage has a clear scaling cliff.

**Do this.** Move reference projection onto a pure 2.5D face grid:

1. Add a Core analysis helper, for example `MeshHeightProjector`, built from flat `vertices/faces`.
   It can reuse the `TerrainFaceGrid` idea but should be public/internal to the analysis-facing
   assembly boundary rather than tied to grading internals. (`TerrainFaceGrid` already exists at
   `src/MoleHill.Core/Grading/TerrainFaceGrid.cs` as an `internal` class — decide whether to move it,
   expose it, or copy the algorithm into an analysis-owned type.)
2. Build the projector once per reference mesh fingerprint in the analysis stage.
3. For each current face centroid, interpolate reference Z by XY using the grid. No Rhino
   line-intersection call, no per-sample bounding-box calculation, no hit sorting.
4. Share the same projected deltas between `BuildCutFillSummary`, `BuildEarthworkSummary`, and cut/fill
   preview when the reference and boundary fingerprint match.
5. Keep the old Rhino projection as a fallback only for non-2.5D reference meshes, and record a
   diagnostic when that fallback is used.

**Correctness trap — read before coding.** The existing projection does not return the topmost surface;
`TerrainMeshProjection` picks the intersection hit **nearest the sample's own Z**
(`OrderBy(hit => Math.Abs(hit.Z - point.Z))`). For a genuine 2.5D heightfield there is one hit, so an
XY grid lookup is exact and matches. But a retaining-wall base mesh can contain near-vertical faces that
are multi-valued in Z; there a naive "topmost Z" grid will disagree with the old code and silently fail
the "match existing implementation within tolerance" acceptance. So: build the grid for the 2.5D case,
and route any XY that maps to a multi-valued/near-vertical region through the Rhino fallback (that is the
"non-2.5D reference mesh" case above). Detecting 2.5D-ness per cell — not just per mesh — is the crux of
this item.

**Acceptance.**

- Cut, fill, net volume, and `CutFillDisplayAbsMax` match the existing implementation within model
  tolerance on this bundle.
- Add a perf test around a 50k-100k face reference mesh. The new projector should scale roughly
  linearly with current face count and should not allocate Rhino geometry per face.
- The exported case remains at `8,392` terrain verts / `16,511` faces and keeps the same analysis
  summaries within tolerance.

---

## H2 - Split slope summary from slope coloring (HIGH)

**Where:** `src/MoleHill.Core/Analysis/SlopeAnalyzer.cs:103-193`,
`src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:164-194`,
`src/MoleHill.Rhino/Services/TerrainAnalysisPreviewBuilder.cs:51-67`.

**Problem.** `BuildSlopeSummary` only needs min, max, and area-weighted average slope. It calls
`SlopeAnalyzer.Analyze`, which allocates `double[faceCount]`, allocates `byte[faceCount * 3]`, and
maps every face through the palette. The preview builder calls `SlopeAnalyzer.Analyze` again when a
colored slope preview is actually needed. In this case slope preview is off, so the analysis stage is
paying colorization cost for a summary-only result.

**Do this.**

1. Add `SlopeAnalyzer.Summarize(...)` returning min, max, average, and optional display range.
2. Have `BuildSlopeSummary` call `Summarize` only.
3. Keep `Analyze` for preview coloring, or make it call `Summarize` plus a color pass.
4. Add a parallel path for large face counts, matching the existing `TerrainAnalysisPreviewBuilder`
   threshold style.

**Acceptance.**

- Existing slope summary tests keep the same min/max/average values.
- A new test verifies summary-only mode does not allocate `FaceColors`.
- On the exported case, analysis time should drop even when cut/fill remains enabled.

---

## H3 - Add a repeatable large-terrain benchmark harness (HIGH)

**Where:** `tools/MoleHill.CaseReplay/Program.cs`, copied case tests under `tests/`, and new perf
guards alongside the existing `tests/MoleHill.Core.Tests/LargeDatasetBenchmarkTests.cs` (which today is
only an ad-hoc CSV benchmark reading a hard-coded local path — reuse its `ITestOutputHelper`/`Stopwatch`
pattern rather than replacing it).

**Problem.** The exported build log is useful, but it is not enough for engineering iteration. The
current replay harness depends on Rhino in-process startup, which failed here. The copied Core tests
are deterministic and already contain the expensive Grade Path and retaining-wall planner inputs, but
they are correctness tests only.

**Do this.**

1. Import the copied Grade Path case into a permanent Core test fixture or benchmark fixture.
2. Add an opt-in benchmark test category, for example env-gated `MOLEHILL_PERF=1`, that records:
   Grade Path core time, allocation size if easy, output vertex/face count, and topology health.
3. Add a separate retaining-wall planner benchmark from the copied planner case.
4. Extend `MoleHill.CaseReplay` so failure to initialize Rhino is reported cleanly and does not hide
   bundle parsing/inventory output.
5. Keep thresholds loose enough for developer machines; use trend data first, not strict CI failure,
   until the kernels are stabilized.

**Acceptance.**

- `Terrain_1_Grade_Path_CopiedCase` can be run without Rhino and reports a stable elapsed time.
- The benchmark captures the current `6,341/12,411 -> 8,392/16,511` Grade Path shape.
- The benchmark output can be pasted into future review docs without manually opening the zip.

---

## M1 - Narrow Grade Path rebuilds to dirty regions where possible (MEDIUM)

**Where:** `src/MoleHill.Rhino/Services/TerrainBuildService.ModifierStages.cs:167-180`,
`src/MoleHill.Rhino/Services/TerrainBuildService.Grading.cs:760-887`,
`src/MoleHill.Rhino/Services/TerrainRuntimeCache.cs:143-205`,
`src/MoleHill.Core/Grading/PathGrader.cs:49-90`,
`src/MoleHill.Core/Grading/PathGrader.Explicit.cs:90-209`.

**Problem.** Grade Path is cached at the stage level, which is good for untouched rebuilds. When a
local path or upstream local grading input changes, the stage still grades against the whole incoming
mesh. The existing grading topology cache stores patch summaries and invalidates overlapping
downstream stages, but it does not yet let a changed path patch reuse unaffected topology.

**Do this.**

1. Start with detection, not surgery: identify when the incoming mesh fingerprint is unchanged and
   only one Grade Path definition changed.
2. Use old and new `GradingPatch` bounds to compute a dirty region, expanded by path width,
   daylight reach, hard-constraint tolerance, and model tolerance.
3. Crop the incoming mesh plus needed hard constraints to that region, grade locally, and stitch the
   result back into the cached full output.
4. Fall back to whole-stage `PathGrader.Grade` if the dirty region touches the terrain boundary,
   overlaps another grading patch, crosses a hard constraint in a new way, or fails topology checks.
5. Keep the fallback silent for users but visible in structured diagnostics.

**Acceptance.**

- The copied case continues to pass through the whole-stage path until local dirty-region support is
  explicitly applicable.
- Add a synthetic large terrain with two distant paths. Editing one path must not rerun the other
  path's topology.
- Topology must stay no worse than the whole-stage path on naked edges, non-manifold edges, and
  boundary component count.

---

## M2 - Reduce retaining-wall remesh fallback work (MEDIUM)

**Where:** `src/MoleHill.Rhino/Services/TerrainBuildService.RetainingWalls.cs:46-171`,
`src/MoleHill.Core/Engine/SurfaceRemesher.cs`.

**Problem.** The build log includes a retaining-wall remesh diagnostic with `fallback.prepare_input`
at 142.882 ms and total fallback remesh at 220.148 ms, followed by
"retried from boundary, hard-constraint, and coarse interior guide seeds because carried mesh vertices
prevented refinement." The stage timing list reports only 0.06 s, likely because the expensive
diagnostic was replayed from a cached stage. The underlying fallback is still real work when wall
inputs or upstream mesh change.

**Do this.**

1. Cache wall planning and prepared wall constraints separately from the mesh remesh result. Their
   fingerprint is mostly the wall curve source set, max wall width, and tolerance profile.
2. Preflight whether carried mesh vertices will block the first remesh attempts. If the preflight says
   yes, skip directly to the fallback strategy instead of paying for attempts that are expected to
   fail.
3. Record selected attempt, input/output counts, and cache-hit status as structured timing data.
4. Keep the retaining-wall planner copied case as a planner regression and add a mesh-remesh fixture
   only if the source bundle can be replayed reliably.

**Acceptance.**

- On this bundle, a cold retaining-wall rebuild should either avoid the fallback or make the fallback
  the first intentional path.
- Cached retaining-wall stages should not replay old detailed remesh timings as if they were paid in
  the current build.

---

## M3 - Make analysis execution mode explicit (MEDIUM)

**Where:** `src/MoleHill.Rhino/Services/TerrainBuildService.cs:100-114`,
`src/MoleHill.Rhino/Services/TerrainBuildService.Analysis.cs:20-161`,
`src/MoleHill.Rhino/Services/TerrainBuildService.Fingerprints.cs:11-30`.

**Problem.** Final builds run every enabled analysis summary after every mesh change. That is defensible
for always-fresh panel numbers, but expensive on larger terrain, especially when `showAnalysisOutputs`
and preview coloring are off. The current analysis fingerprint is also all-or-nothing: any current mesh
change invalidates all enabled analyses.

**Do this.**

1. Introduce an analysis compute mode: `Summary`, `PreviewColor`, and `GeneratedOutputs`.
2. Let live rebuilds update cheap summaries immediately and defer expensive summaries behind an
   "analysis stale" marker when the previous analysis took longer than a threshold.
3. Split the analysis cache by analysis id instead of one monolithic `analysis` stage key.
4. Fingerprint each analysis by only the mesh/reference/source data it needs.
5. For cut/fill and earthwork with the same reference/boundary pair, compute shared reference deltas
   once.

**Acceptance.**

- Toggling one analysis setting does not invalidate unrelated analysis summaries.
- When analysis outputs are hidden, no generated-output work runs.
- The panel clearly distinguishes fresh analysis from intentionally deferred analysis.

---

## L1 - Fix timing diagnostics for cache hits (LOW)

**Where:** `src/MoleHill.Rhino/Services/TerrainBuildService.Cache.cs:12-23`,
`src/MoleHill.Rhino/Services/TerrainBuildService.cs:464-469`,
`src/MoleHill.Rhino/Services/TerrainController.Build.cs:540-543`,
`src/MoleHill.Rhino/Services/TerrainBuildResult.cs:8-83`.

**Problem.** Cache-hit stages restore cached diagnostics, so old detailed timing messages can appear
in the current build log. The stage list records only stage name and elapsed time, dropping the
`"cache hit"` detail already computed by `AppendCacheHitDetail`. That makes a build log harder to
interpret: a user can see a 0.06 s stage beside a replayed 0.33 s remesh diagnostic.

**Do this.**

1. Add `IsCacheHit` or `TimingKind` to `TerrainBuildTiming`.
2. Include timing detail in `FormatBuildMessage`, for example `Retaining Wall: 0.06 s (cache hit)`.
3. Do not replay old `timing.*` diagnostic lines from cached entries, or prefix them as cached
   diagnostics from the prior cold run.
4. Prefer structured timing output over embedding parse-only lines in `Diagnostics`.

**Acceptance.**

- The same build log cannot imply that a cached stage paid an old cold-stage sub-timing.
- The "Copy Case" bundle contains enough timing metadata to separate cold work from cache restore.

---

## L2 - Avoid avoidable mesh-wide work on cache hits (LOW)

**Where:** `src/MoleHill.Rhino/Services/TerrainBuildService.Cache.cs:12-23`,
`src/MoleHill.Rhino/Services/TerrainRuntimeCache.cs:459-619`,
`src/MoleHill.Rhino/Services/TerrainBuildService.Fingerprints.cs:161-189`.

**Problem.** Cache hits still duplicate Rhino meshes, normalize them, and sometimes fingerprint all
vertices and faces if an output fingerprint is not already being threaded through. That is acceptable
for this 16k-face output, but it will become visible on much larger meshes with many cached stages.

**Do this.**

1. Thread cached `OutputFingerprint` through every downstream fingerprint path before considering a
   full mesh hash.
2. Measure `DuplicateMesh` and `NormalizeMeshInPlace` time on a 100k-500k face cache-hit stack.
3. If clone cost is material, consider a small immutable flat-mesh cache for Core stages and only
   materialize Rhino meshes at final display/output boundaries.
4. Keep native Rhino mesh ownership conservative; do not reintroduce shared disposed-mesh risk.

**Acceptance.**

- A no-op final rebuild with a hot cache reports cache restore time separately from cold stage time.
- Large cached stages remain bounded by mesh clone cost, not accidental reprocessing.

---

## Historical Suggested Order Of Work

| # | Item | Size | Risk | Why first |
|---|---|---:|---|---|
| 1 | H3 - benchmark harness for copied Grade Path and wall planner cases | 0.5-1 day | Low | Makes every later optimization measurable |
| 2 | H2 - slope summary without color allocation | 0.5 day | Low | Isolated Core change with easy tests |
| 3 | H1 - 2.5D reference projection grid for cut/fill/earthwork | 1-2 days | Medium | Biggest analysis scaling win |
| 4 | L1 - cache-hit timing diagnostics | 2-3 h | Low | Prevents misleading perf reports |
| 5 | M2 - retaining-wall remesh fallback preflight/caching | 1 day | Medium | Removes known cold-path waste |
| 6 | M3 - split analysis cache/execution modes | 1-2 days | Medium | Reduces final-build work after the kernels are cheaper |
| 7 | M1 - dirty-region Grade Path rebuilds | 3-5 days | High | Largest architectural win, but topology risk is real |
| 8 | L2 - cache-hit mesh clone/fingerprint tuning | Measure first | Medium | Only worth doing if benchmarks show clone cost |

Recommended acceptance run after each item:

```powershell
dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj
```

For Rhino-side changes, also run the copied case bundle in Rhino once the replay harness can initialize
reliably, then compare the build-log stage timings against this report's evidence block.
