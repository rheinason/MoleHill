# Drainage analysis — implementation plan (B1 + B2)

Backlog items **B1 (watershed / catchment delineation)** and **B2 (depression / ponding detection and
pond volume)**, planned together because they are two questions asked of one computation: a sink is a
basin with no outlet. Written 2026-09-13 against `analysis-aspect-and-difference`.

The backlog entries state the *what*. This document settles the *how*, and in particular the five
decisions the backlog deliberately left open.

---

## 1. One analysis or two?

**Two analyses over one shared Core result**, following the Earthworks / Cut-Fill precedent exactly.

Earthworks and Cut / Fill are separate cards sharing `ReferenceComparisonAnalysisDefinition` as a base
and a `referenceComparisonCache` threaded through the analysis stage, so the expensive projection runs
once no matter how many cards read it. Drainage is the same shape:

```
DrainageAnalysisDefinition (abstract)      ← shared: flat-slope epsilon, boundary source set
├── CatchmentAnalysisDefinition            ← B1: basin polygons, areas, outlet paths
└── PondingAnalysisDefinition              ← B2: sinks, spill elevation, impounded volume, pond outline
```

Both resolve through a `BasinGraphCache` keyed the way `referenceComparisonCache` is, so a terrain
carrying both cards pays for the basin graph once.

Why not one card with a toggle: the two answer different questions and a user wants them
independently. "Where does water go" produces forty catchment polygons on a real survey; "did I build
a bathtub" produces one red outline and usually nothing at all. Forcing the first to be on to get the
second would make the check that catches design errors annoying enough to leave off — and it is the
one analysis worth leaving on permanently.

Why not two unrelated analyses: the flat-region routing below is most of the work, it is identical for
both, and computing it twice on a 180k-face terrain is not affordable.

---

## 2. The Core algorithm

New files in `src/MoleHill.Core/Analysis/`, adjacent to `WaterflowTracer` and operating on the same
flat vertex/face arrays:

| File | Contents |
|---|---|
| `DrainageBasinAnalyzer.cs` | the shared pass: flow routing → basin labels → rims |
| `BasinGraph.cs` | its result: per-face basin id, per-basin outlet, rim adjacency |
| `PondingSolver.cs` | B2 only: spill elevation, impounded volume, pond outline |
| `EdgeLoopChainer.cs` | unshared-edge set → closed polylines (see §6) |

### 2a. Flow routing

1. **Face adjacency.** `WaterflowTracer.BuildNeighbors` already produces exactly the array needed and
   is currently `private static`. Lift it to an internal shared helper rather than writing a second
   one — two adjacency builders that disagree on the degenerate cases would be a genuinely nasty bug.
   (`IndexedMeshTools.BuildEdgeTopology` is the other candidate; it is sort-based and already avoids
   the edge-key hashing trap. Pick one during implementation and delete the duplicate.)

2. **Per-face steepest descent.** Each face is a plane; its gradient is constant. The face drains
   through whichever of its three edges the descent direction exits. That yields one `flowsTo[face]`
   pointer per face — a forest.

3. **Roots are one of three things:** a face whose exit edge is naked (drains off the terrain edge — a
   *boundary outlet*), a face in a flat region (§2b), or a face that is a strict local minimum (an
   interior sink — B2's raw material).

4. **Basin labelling** by pointer-jumping to the root with path compression, iteration-capped. Cycles
   are numerically possible on near-flat ground; break them by merging the cycle into one flat region
   and re-routing it through §2b rather than by picking an arbitrary member.

### 2b. Flat regions — the part that decides whether this ships

This is the whole difficulty, and the backlog is right that it is what makes the feature usable on
*graded* terrain, which is the point. A Grade Pad is one enormous flat area; naive steepest descent
gives every face on it a different arbitrary outlet and the catchment map turns to confetti.

Treatment, adapted from the standard Garbrecht–Martz flat-routing approach to a TIN:

1. Faces whose gradient magnitude is below a **flat-slope epsilon** are flat. The epsilon is a user
   parameter, declared with the `Slope` factory so it takes `0.5%`, `1:200` or `0.3deg` like every
   other slope field — never a bare number. Default small but non-zero; a survey-derived surface is
   never exactly level.
2. Connected components of flat faces are flat regions.
3. For each region, find its **outflow edges**: region-boundary edges whose non-region neighbour is
   lower. BFS inward from all outflow edges at once, assigning each flat face a `flowsTo` pointer
   toward its BFS parent. The whole region then drains coherently to its real outlets.
4. A region with **no** outflow edge is a **sink region** — B2's input, and the correct answer, not a
   failure.

Do not skip step 3 and route flats to a nearest-lowest-vertex; that is the naive version and it is
what makes other tools' watershed output unusable on engineered surfaces.

### 2c. Catchment boundaries (B1 output)

Group faces by basin id, collect edges used by exactly one face of the group, chain them into closed
polylines. Output per basin: the boundary polyline(s), plan area, and the flow path from the basin's
lowest point to its outlet (a `WaterflowTracer.Trace` call from that point, so the two features draw
the same kind of line).

**Merging.** Civil 3D asks for a merge tolerance because a real survey yields hundreds of basins, and
it will here too. Merge any basin below a minimum plan area into the neighbour it spills into, before
boundary extraction. Area has no `ParameterUnit` today — see §5.

### 2d. Ponding (B2 output)

For each sink region:

1. **Spill elevation** by priority flood: push the sink's rim edges into a min-heap keyed by the
   higher of the edge's two vertex Z values; pop the lowest. That edge is the spill point and its
   far face names the basin the sink overflows into. Growing the region across the popped edge and
   continuing gives nested/merged ponds correctly if that is ever wanted; for v1, stop at first pop.
2. **Impounded volume** = Σ over basin faces of `planArea × max(0, spillZ − centroidZ)` — the same
   prism sum `EstimateReferenceComparison` in `TerrainBuildService.Analysis.cs` already uses for
   cut/fill. Match its convention rather than writing a second integrator.
3. **Ponded outline** = the zero level of the per-vertex field `spillZ − z`, which is exactly the
   per-vertex field overload B3 just added to `ContourGenerator`. No new tracing code: the pond
   outline is a contour, and the marching-triangles pass that draws the balance line draws this too.
4. **Thresholding** — mandatory, per the backlog: a 2 mm numerical dimple on a 200 m pad is not a
   pond. Threshold on **maximum ponded depth** (`ParameterUnit.ModelLength`, unit-aware, user-set).
   Volume as a second threshold is discussed in §5.

---

## 3. Preview colouring — the backlog's one wrong assumption

The B1 entry says basins "want the colour-ramp apparatus, which only analyses carry". Half right. The
ramp maps a **measurement** onto a continuum; a basin id is a **category**. Fitting a ramp across
basin indices produces a picture where adjacent colours mean nothing and the legend is meaningless.

So:

- **Catchment** gets a **categorical** face colouring — a fixed set of well-separated hues cycled by
  basin index, with basins ordered by descending plan area so the large basins keep their colour
  across rebuilds instead of shuffling when a small one appears. Its card declares **no**
  `ColorRamp` row. Waterflow is already a card with no ramp row, so this is precedented, not novel.
- **Ponding** *is* a measurement — ponded depth — and takes the ramp normally.

Both paint through the existing `TerrainAnalysisPreviewBuilder.BuildFaceColorMesh(vertices, faces,
faceCount, byte[] colors, alpha)`, which needs nothing new.

---

## 4. Rhino wiring — the checklist

The Aspect commit (`23c6dd2`) is the exact template; the file set is known. For each of the two
analyses:

- `Model/…AnalysisDefinition.cs` + the shared `DrainageAnalysisDefinition` base.
- `Registry/AnalysisDescriptors.cs` — a descriptor each: `Kind`, `TypeLabel`, `IconLabel`,
  `AccentArgb`, `SortOrder` (after waterflow, so 6 and 7), `Parameters`, and a `DescribeBlocker` for
  ponding when the terrain has no closed depression to report on. JSON polymorphism is
  registry-driven, so registering the descriptor is the whole serialization story.
- `Model/LayerRole.cs` + `Registry/LayerRoleRegistry.cs` — new roles. Proposed: `Catchment`
  (`Line`, boundary polylines), `CatchmentFlowPath` (`Line`), `Ponding` (`Line`, pond outlines),
  `PondingSpillPoint` (`Line`, a marker at the spill). Every generated object names a role; never
  build a path by appending a suffix.
- `Services/TerrainBuildService.Drainage.cs` — a new partial, sibling of
  `TerrainBuildService.Waterflow.cs`, which is the closest and cleanest template in the tree
  (resolve sources → call Core → emit `GeneratedRhinoObject`s with `Role` + `LayerPath` → return a
  summary).
- `Services/TerrainBuildService.Analysis.cs` — two arms on the stage `switch`, plus the
  `BasinGraphCache` threaded alongside `referenceComparisonCache`.
- `Services/TerrainAnalysisPreviewBuilder.cs` — the two preview meshes and `SupportsTerrainPreview`.
- `Model/TerrainAnalysisSummary.cs` — new fields, **and** `TerrainRuntimeCache.CloneAnalysis`.
- `UI/MoleHillPanel.Analysis.cs` — the summary rows each card reads back.

### The two summary traps, stated because this is exactly where they bite

Both are documented in `CLAUDE.md` and both were found live rather than by any test of the analysis:

1. **A field left out of `CloneAnalysis` reads back as zero on every cached build** — indistinguishable
   from an analysis that measured nothing. "0 ponds" is the most dangerous possible wrong answer here,
   because it is also the right answer most of the time.
2. **A field defaulting to `double.NaN` or an infinity stops `System.Text.Json` writing the document at
   all**, so every terrain carrying any summary fails to save — and because every edit snapshots for
   undo, it surfaces as a failed *edit*. Spill elevation, pond depth and pond volume are all "not
   measured" most of the time: they must be **nullable**, never NaN.

`TerrainRuntimeCacheClonerTests` and `TerrainSummarySerializationTests` fail if either is forgotten.

### Fingerprinting

Nothing document-level is needed. Unlike Aspect — which had to add `snapshot.NorthAzimuthDegrees`
explicitly because north lives on the document, not the definition — every drainage input is either on
the definition or on the mesh, both already fingerprinted. The `LayerRoles.Fingerprint` term is added
automatically by `ProducesGeneratedOutput`.

---

## 5. Open decision: area and volume have no unit

`ParameterUnit` is `None | ModelLength | Degrees | Slope | Percent`. The convention is that no card
shows a bare unlabelled number — `ParameterSchemaGuardTests` enforces declared rows — but a minimum
*area* threshold (§2c) and a minimum *volume* threshold (§2d) are length² and length³.

Three options, in order of preference:

1. **Ship v1 with depth only.** Max ponded depth (`ModelLength`) is the threshold that matters, and
   basin merging can key off a minimum **share** of total plan area (`Percent`), which is both
   unit-clean and more robust across scales than an absolute area. *Recommended.*
2. Add `ParameterUnit.ModelArea` / `ModelVolume`, with `TerrainUnitScaler` scaling them as length² and
   length³. Correct long-term, and B4's reporting will want it anyway — but it is a separate change
   with its own tests, and folding it into this one blurs two things.
3. Bare numbers. Rejected; it is the convention this codebase explicitly does not break.

**Decided (Phase 2): option 1.** The catchment merge threshold is `MinimumBasinAreaPercent`, a
`ParameterUnit.Percent` row. A share turned out to be the better parameter on its own merits, not merely
the one that could be labelled: it is scale-free, so the same 1% is right on a housing plot and on a
quarry, where an absolute area has to be retyped for every site. Phase 3's ponding thresholds follow the
same rule — depth in `ModelLength`, nothing in area or volume.

Option 2 remains a prerequisite B4 (volume and quantity reporting) will hit regardless; doing it there and
adopting it here later is still fine, and now costs nothing, since no row is waiting on it.

---

## 6. Small piece of new Core: edge-loop chaining

Catchment boundaries need "set of unshared edges → closed polylines".

**Revised during Phase 1: no extraction from `FeaturePolylineGraph`.** The plan assumed the two were the
same problem. They are not. A basin boundary is collected by walking the basin's faces and emitting each
outward edge *in that face's own winding*, so the edges are **directed** and balance at every vertex —
following the direction closes the loop with no geometric decision about what is inside, which is what
keeps a basin with an island in it, or one pinching to a point, from needing a special case.
`FeaturePolylineGraph` chains *undirected* feature edges and needs corner classification and arc-length
parameters that a boundary polygon has no use for. Refactoring the remesher's feature extraction to
share code with a twenty-line directed walk would have been risk for nothing. `EdgeLoopChainer` is the
directed case only, and says so.

**Performance note, per `CLAUDE.md`:** any dictionary or set keyed by a packed edge key MUST use
`IndexedMeshTools.EdgeKeyComparer.Instance`. The default `long` hash collapses adjacent mesh indices
into a handful of buckets and turns this O(n) pass quadratic — it previously cost 7 s of a 10 s remesh
on a 180k-face terrain.

---

## 7. Testing

**Core (`MoleHill.Core.Tests`)** — hand-built meshes with known answers:

| Scene | Asserts |
|---|---|
| Single cone | one basin, outlet at the rim, boundary = the rim |
| Two cones with a saddle | two basins, boundary follows the ridge, spill at the saddle |
| Bowl (inverted cone) | one sink; impounded volume within tolerance of the analytic cone volume; spill Z = rim Z |
| Flat pad with one low corner | **the flat-routing test** — all pad faces in one basin, not confetti |
| Flat pad with no outflow | recognised as a sink region, not as N arbitrary basins |
| Pad cut into a hillside | the realistic case: pad drains as one unit into the downslope basin |
| Terrace that ponds | pond outline is closed and lies at the spill elevation |
| Sub-tolerance dimple | reported as nothing at the default depth threshold |

**Rhino (`MoleHill.Rhino.Tests`)** — round-trip serialization of both definitions, `CloneAnalysis`
coverage (the guard test catches omissions automatically), layer-role registry coverage.

**Live** — per `docs/rhino-live-testing.md`, on `GradePadTest.3dm` through a disposable `rhino-mcp`
slot. This is not optional: the last two analysis features each shipped with defects that only a live
run caught (`811e31c`, `a36d97b`), both in summary/cache state rather than in the mathematics.

**Benchmark** — a 180k-face terrain alongside `mhBenchmarkLargeTin`. Target: basin graph well under a
second, since it must run on every rebuild of a terrain that has the card on.

---

## 8. Staging

| Phase | Scope | Done when |
|---|---|---|
| 1 | Core: adjacency lift, flow routing, flat regions, basin labelling, `EdgeLoopChainer` | **done** — see §10 |
| 2 | B1 end-to-end: `CatchmentAnalysisDefinition`, descriptor, layer roles, build partial, categorical preview, summary + cloner | **done** — see §11 and §12 |
| 3 | B2: `PondingAnalysisDefinition`, priority flood, volume, contour outline, thresholds | **done** — see §13 and §14 |
| 4 | Docs + benchmark | `docs/architecture.md` "Analysis vs annotation" extended, `Core/Analysis/README.md` and `docs/file-index.md` regenerated, backlog entries closed |

Phase 1 is the risk. If flat routing does not hold up on a real graded scene, stop there and
reconsider — everything downstream is plumbing, and plumbing built on confetti basins is wasted.

---

## 9. What this plan deliberately does not do

- **No hydrology.** No rainfall, no time of concentration, no flow accumulation weighting. The
  backlog's boundary is right: export catchments so a real hydrology tool can consume them.
- **No automatic fixing.** Ponding *reports*; it never lifts the ground to drain a depression. That
  is the drift the backlog's opening test rules out, and the same argument that keeps swale inverts
  (B10) off the terrain.
- **No nested pond hierarchy in v1.** One spill elevation per sink. The priority flood extends to
  merged ponds naturally if it is ever asked for.

---

## 10. Phase 1 outcome (2026-09-13)

Shipped in `src/MoleHill.Core`: `Engine/FaceAdjacency.cs` (lifted out of `WaterflowTracer`, which now
calls it), `Engine/EdgeLoopChainer.cs`, `Analysis/BasinGraph.cs`, `Analysis/DrainageBasinAnalyzer.cs`,
`Analysis/BasinBoundaryExtractor.cs`, and 16 tests in
`tests/MoleHill.Core.Tests/DrainageBasinAnalyzerTests.cs`. Whole suite green (1,374 passing).

**Flat routing holds up**, which was the stated gate. A level plateau with one notch cut through its rim
comes out as a single catchment; the same plateau sealed comes out as a single depression; a pad cut
into a hillside reports no sink at all.

Three things the plan did not anticipate, all found by the tests rather than by reasoning:

1. **Cycles are the dominant failure mode, not an edge case.** The plan treated a routing cycle as a rare
   numerical artefact of a saddle and proposed breaking it by declaring the lowest member a sink. In fact
   a cycle forms wherever water converges on a **vertex**: the two faces sharing that vertex's opposite
   edge each fall towards it and each leave through their shared edge. That happens at the bottom of
   every valley and on the low corner of every graded pad — the first run put phantom sinks on the pad
   scene *and* split the plateau. A cycle is now handed to the same spill routine flat regions use (it is
   the same phenomenon: a connected set of faces with no outlet among themselves) and becomes a sink only
   when there is genuinely nowhere lower.
2. **A pit routes as several basins that share one floor.** A round bowl came out as two half-bowls. They
   are one depression — a pond has one water surface — so sinks are consolidated on floor-vertex
   identity, which is exact: two distinct pits cannot share their lowest point. Without this, B2 would
   have double-counted pond volume and drawn two outlines over each other.
3. **Vertical faces had to be folded into flat routing.** A retaining-wall face has no gradient and no
   plan area. Given its own treatment it swallows the water arriving from the terrace above and reads as
   a depression behind every wall; routed as flat ground it spills onto the terrace below.

`BasinGraph` also carries the face adjacency, which the plan did not call for — boundary extraction walks
it and the ponding rim walk will walk it again, and rebuilding it per consumer costs more than the
routing does.

**Performance:** 180k faces in 65 ms unmerged, 62 ms with sliver merging (321 basins → 11), 1 ms to
extract the largest basin's boundary. Comfortably inside the "well under a second" target, so no
optimisation work is owed before Phase 2.

**Still open, unchanged:** the area/volume `ParameterUnit` question in §5, which Phase 2 hits first.

---

## 11. Phase 2 outcome (2026-09-13)

B1 is wired end to end. New: `Model/DrainageAnalysisDefinition.cs` (the shared base),
`Model/CatchmentAnalysisDefinition.cs`, `CatchmentAnalysisDescriptor` in `Registry/AnalysisDescriptors.cs`,
the `Catchments` and `CatchmentFlowPaths` layer roles, `Services/TerrainBuildService.Drainage.cs`,
`Core/Analysis/CategoricalPalette.cs`, four summary fields with their `CloneAnalysis` entries, the panel
summary rows, and `tests/MoleHill.Rhino.Tests/CatchmentAnalysisTests.cs`. Whole suite green (1,392
passing).

The card offers: **Flat Below** (a `Slope` row — takes `0.5%`, `1:200` or `0.3deg`), **Merge Below**
(`Percent`), **Boundaries** + colour, **Flow Paths** + colour. It reports catchment count, largest area,
the closed-depression count, and how many curves it drew.

Worth recording:

- **The closed-depression count is on the catchment card**, before any ponding card exists. The routing
  already knows the terrain holds water somewhere, and withholding the one number in the Analyses tab
  that indicates a *mistake* rather than describing the design would have been a strange thing to do for
  the sake of phase boundaries. Phase 3 measures those depressions; this only counts them.
- **Flow paths are traced, not read off the basin's own pointers.** The pointers only say which face is
  next, so drawing them gives a staircase between centroids; `WaterflowTracer` follows the gradient
  continuously within each face. It also means the catchment card and the Waterflow card draw the same
  kind of line, from the same code.
- **Flow paths start at the basin's *high* point**, which needed `HighestX/Y/Z` adding to
  `BasinGraph.Basin`. Tracing from the lowest point — which is what the plan's wording implied — would
  draw nothing, because the lowest point of a basin *is* its outlet.
- **No boundary source set on the base**, though §1 listed one. Nothing uses it yet, and unused persisted
  state is worse than a later addition; the flat-slope threshold is the whole of the shared base for now.
- The preview recomputes the basin graph per refresh rather than reading the build's cached one, which is
  what the slope and aspect previews also do. At 65 ms on 180k faces that is acceptable; if a colour-only
  edit ever feels slow on a large terrain, this is the thing to cache, not the routing to optimise.

**The live run happened and was worth it — see §12.**

---

## 12. Live run (2026-09-13)

Rhino 8, slot `aardvark`, plugin verified at
`src/MoleHill.Rhino/bin/Debug/net7.0/MoleHill.Rhino.rhp` by `PlugIn.Find` → assembly location, module
version id and file write time, plus the presence of `CatchmentAnalysisDefinition` in the loaded
assembly. Scene built from script: a 441-point 40×40 m gentle hillside with a level pad cut into it
(Terrain A), and the same plus a 1.5 m bowl (Terrain B).

**What passed first time.** Terrain A: 20 catchments, **0 closed depressions** — the graded pad does not
register as a pond, which is the false positive the whole feature had to avoid. Terrain B: 21
catchments, **1** — exactly the bowl. A second rebuild with nothing changed came back
`Analysis Catchments: 0 s (… cache hit)` with every summary field intact, so `CloneAnalysis` is
complete; that is the trap both preceding analysis features fell into. The document saved (385 KB, no
`NaN` or `Infinity` in the JSON) and a serialize/deserialize of the live-built state restored
`basins=21 sinks=1 largest=370 flatFaces=128` unchanged. Baked output landed on
`MoleHill::Annotation::Catchments` and `MoleHill::Annotation::Catchment Flow Paths`, 21 boundaries for
21 catchments. The baked mesh carries all 12 `CategoricalPalette` hues.

**What it caught, which no unit test did.** Only **9 flow paths for 21 catchments**. Zero rejected
starts, so the tracer was finding a face and then stopping after a single point. The head was the
basin's highest **vertex**, and a basin's highest vertex is usually a local maximum: the trace lands in
one arbitrary face of the several sharing it, where the descent ray from that exact corner has no
forward exit. A level face fails the same way, having no direction to leave by at all.

`BasinGraph.Basin.Highest{X,Y,Z}` is now `FlowStart{X,Y,Z}` — the centroid of the basin's highest
*falling* face, falling back to its highest face for a basin that is flat throughout. A centroid is
strictly interior, which is what guarantees an exit exists. Re-verified live: **21 of 21**. Three Core
tests pin it (`FlowStart_OnAHillsideWithAPad_TracesARealPathForEveryBasin`,
`FlowStart_IsAFaceCentroid_NotAVertex`, `FlowStart_SitsOnAFallingFace_WhereverTheBasinHasOne`).

The renaming is deliberate: `Highest*` invited exactly the misuse that caused this, so the member is now
named for the one job it has.

**Two notes, neither a defect of this work.**

- Running a Rhino command inside a `run_csharp` call wedges the slot, and `_-Open` swaps the document
  out from under the router and prunes the slot. Both are already in `docs/rhino-live-testing.md`; the
  round-trip was re-done in-process instead.
- Baking a terrain with any preview-colouring analysis active bakes the per-face colour mesh (2400
  vertices for 800 faces, three per face) rather than the welded TIN. That is pre-existing and shared
  with slope, aspect, elevation and cut/fill — worth a look some day, but not from here.

**Not established:** a viewport capture was taken but could not be decoded here, so no claim in this
section rests on a screenshot. The colouring claim rests on counting distinct vertex colours on the
baked mesh, which is document state.

---

## 13. Phase 3 outcome (2026-09-13)

B2 is wired end to end. New: `Core/Analysis/PondingSolver.cs`, `Model/PondingAnalysisDefinition.cs`,
`PondingAnalysisDescriptor`, the `Ponding` and `PondingSpillPoints` layer roles, the build stage in
`TerrainBuildService.Drainage.cs`, a masked-ramp preview, four summary fields with their `CloneAnalysis`
entries, panel rows, and two test files (`PondingSolverTests`, `PondingAnalysisTests`). All three suites
green — 1,435 passing.

The card offers **Ignore Below** (`ModelLength`), **Flat Below** (`Slope`, shared with Catchments),
**Shorelines** + colour and **Spill Points** + colour, and reports the number of depressions, the deepest
water, the total volume and the total wet area.

Three things worth recording:

1. **The spill elevation is a bottleneck problem, and §2d's plan for it was too simple.** The plan said
   "push the sink's rim edges into a min-heap keyed by the higher of the edge's two vertex Z values; pop
   the lowest". Two corrections. The key is the *lower* of the two vertices — water crosses an edge at
   its lowest point, not its highest. And a single pop off the rim is not the answer: the route out may
   cross several faces, and its cost is the **highest** crossing along it, minimised over routes. That is
   a minimax path, so the flood carries `max(levelSoFar, crossing)` and the first face reached in another
   basin fixes the level.
2. **The shoreline field had to be negated.** `spillZ − z` puts the zero level at the field's *minimum*,
   which `ContourGenerator` skips — a level equal to the minimum has no below-to-above transition. That
   silently drew nothing for a bunded pad, where the whole depression sits at or below its rim and
   nothing is above water. `z − spillZ` puts it at the maximum and picks up the inclusive-`vmax` path
   already added for contouring at a pad's exact design elevation.
3. **Phase 1's sink consolidation was too narrow, and only Phase 3 could expose it.** It merged sinks
   sharing an *identical* floor vertex. But a bowl can split into a piece holding the true low point and
   a piece whose own lowest vertex is slightly higher — no shared floor, no merge. The catchment card
   showed "2 depressions" where there is one, which looks merely cosmetic; the real damage was that each
   one's escape runs straight into the other at its own floor, so both measure zero depth and **a real
   depression reports as no pond at all**. Sinks are now also merged when joined *below the higher of
   their two floors* — water standing at that floor already spans both. Joined above both floors is a
   bund between two ponds and correctly stays two.

**Dry ground is masked, not mapped to zero,** in the preview. Mapping it would paint every draining face
at the ramp's low end, and the reader would have to know that this particular colour means "no water"
rather than "a little water" — on the one analysis whose job is to make a problem obvious.

**The live run happened — see §14.**

---

## 14. Phase 3 live run (2026-09-13)

Rhino 8, plugin verified at the expected path by assembly location and file write time, with
`PondingAnalysisDefinition`, `PondingAnalysisDescriptor` and Core's `PondingSolver` all present in the
loaded build. Four scenes, each carrying **both** drainage cards.

| Scene | Result |
|---|---|
| Pad enclosed on three sides, open at the low corners | **0 depressions** — it drains sideways |
| Pad sunk below every surrounding point | **1 pond**, depth 0.5999985, volume 169.6 m³, wet area 326 m² |
| Small pit, Catchments merging at 25% | **1 depression on both cards** |
| Rebuild with nothing changed | `cache hit` on both stages, every field intact |

The volume checks out by hand: a 16 × 16 m floor at 0.6 m is 153.6 m³, and the rest is the submerged
batter ring, which the 326 m² wet area against a 256 m² floor accounts for.

**The first scene is the one worth keeping.** It was meant to be the bathtub and was built wrong — a bund
across the downhill edge only, with the pad's sides left lower than the pad. The analysis said no
depression, and it was right. A pad that looks enclosed but drains at one corner is exactly the false
positive this feature must not raise, so the mistake became the better test and both are kept.

**What the live run caught:** with both cards on a terrain, **Catchments merges slivers and Ponding never
does**, so the two routed the terrain with different settings — and `MergeSmallBasins` was free to absorb
a sink basin into a neighbour. A depression's basin is its whole catchment, so a pit near the top of a
slope has a small one and is exactly what an area threshold eats. Absorbed, it vanishes from the graph:
the Catchments card reports "no closed depressions" while the Ponding card beside it reports one, the two
contradicting each other on screen. The reverse matters as much and is less obvious — a sliver absorbed
*into* a depression extends it, and since the spill is found by flooding until the water reaches another
basin, a larger basin pushes the escape outward and the pond is measured too deep.

Depressions are now exempt from merging in both directions, with two Core tests. Verified live at a 25%
threshold, which collapses that terrain from ~20 catchments to 3 and still leaves the pit reported by
both cards.

This also corrected something overstated in §1 and in the first draft of the cache comment: the two cards
share a basin graph only when their routing settings match, and **the defaults do not match**. A terrain
carrying both routes twice, about 65 ms each at 180k faces. That is worth paying — the alternative is one
card's settings quietly deciding what the other computes.

**Also fixed, and caught only by building the plugin:** `MoleHillPanel` already had a `FormatVolume` and
the ponding card added a second, so the type would not compile. Every test still passed, because
`MoleHill.Rhino.Tests` links `Model/`, `Registry/` and most of `Services/` but never builds
`MoleHill.Rhino.csproj`, leaving `UI/` outside every test build. Recorded in `docs/rhino-live-testing.md`.
