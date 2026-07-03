# Remesh faceting on curved areas — analysis & prototype

## Symptom
A smooth curved batter/shoulder/swale becomes **faceted with soft triangular dents** after the Remesh
modifier runs (most visible in dense areas). Vertices stay on the surface; the surface between them
develops creases.

## Root cause — connectivity, not elevation
Remesh is a *rebuild-from-points* op: it feeds all vertices (+ pre-densified constraints + optional
Steiner points) to Triangle.NET, which returns a **constrained Delaunay** triangulation
(`SurfaceRemesher.TriangulatePrepared` → `TriangulationHelper.Triangulate`).

- Delaunay picks each diagonal from the **2D (XY) circumcircle** — blind to Z.
- Every output vertex stays **on the original surface**: carried Z, or interpolated on the original
  mesh face, or pinned constraint Z (`SurfaceRemesher.cs` ~`:978–991`). Nothing moves off-surface.
- On a curved quad the Delaunay diagonal often chords **across** the curvature → the two triangles dip
  below / bulge above the true curve → the dents.
- Independent of `MinAngle`: even constraint-insertion-only (MinAngle 0) re-triangulates and re-picks
  diagonals, so **tuning the quality numbers cannot fix it**.
- Grading hard-constraints are forced segments (un-flippable), which is why the dents sit on the smooth
  faces *between* feature lines, not on the features. The pre-remesh smoothness comes from grading's
  explicit batter strips using **surface-aligned diagonals**; the Delaunay rebuild discards them.

## Options considered
| Option | Fixes it? | Cost / risk |
|---|---|---|
| **A. Data-dependent edge-flip post-pass** (chosen) | Yes, directly | Low–moderate; connectivity-only; needs convexity + angle-floor + constraint guards; smoothness↔quality tradeoff |
| B. Connectivity-preserving / adaptive remesh (keep good input faces) | Yes, highest fidelity | High; Triangle.NET is global, needs a different local refiner |
| C. Height-aware (2.5D) Delaunay predicate | Yes | Very invasive; rewrites bundled Triangle.NET predicate |
| D. Tune default quality numbers | No (verified) | Re-triangulation re-picks diagonals regardless |
| E. Post Laplacian smoothing | Badly | Moves vertices off-surface, blurs real features, shifts volumes |

## Implemented prototype — Option A
`SurfaceRemesher.SmoothInteriorDiagonals` (Dyn–Levin–Rippa *angle-between-normals* criterion):

- After the constrained Delaunay build (and after Z is assigned), walk every interior edge shared by two
  triangles; flip its diagonal when the alternative makes the two faces **more coplanar** (flatter local
  surface). Iterates to convergence (≤ 8 sweeps).
- **Guards:** never flips constraint or boundary edges; skips non-convex quads (a flip there would
  overlap); skips a flip that would duplicate an existing edge; enforces a **12° min-angle floor** so it
  can't manufacture slivers; only flips when strictly flatter by a small cosine margin.
- **Invariant:** mutates the face array in place — vertex/face counts, Z, constraints, boundary, and
  watertightness are all unchanged. Connectivity-only.
- **Wiring:** `Options.DataDependentFlips`. Enabled **on by default for the Remesh modifier only**
  (`ApplyRemesh` → `RebuildMeshWithConstraints(dataDependentFlips: true)`); off for grading rebuilds, to
  keep the change's blast radius small. Logged as a remesh-timing phase with the flip count.

### A/B
Flip `dataDependentFlips: true` → `false` at the `ApplyRemesh` call site
(`TerrainBuildService.Tin.cs`) to compare before/after on the same scene.

### Tests
`tests/MoleHill.Core.Tests/SurfaceRemesherDataDependentFlipTests.cs` — flips a known-wrong diagonal on a
curved quad, no-ops on the already-flat diagonal, and refuses to flip a constraint edge. Full Core suite
green (299).

## Open knobs (defaults chosen, easy to tune)
- **Metric:** angle-between-normals (simple, robust). Rippa roughness functional is the principled
  alternative if min-dihedral over-flips.
- **Min-angle floor:** 12°.
- **Scope:** Remesh modifier only. Could extend to grading's constraint-insertion pass later.

## Iteration 1 result (visual)
Flip pass is a clear improvement on the shoulders (broad faceting mostly gone) but **sharp isolated
darts remain** in some curved areas. Synthetic swale test (jittered grid over a rounded valley):
- ABN flip pass lowers true L∞ surface error ~22% (0.308 → 0.240) and roughness ~17% — it helps.
- **The angle floor is NOT the limiter:** 12° → 3° → 1° barely changes anything.
- Dominant non-flips are **non-convex quads** (a diagonal swap would overlap) and edges already locally
  optimal — neither is unlocked by loosening the floor.

So the residual darts are a different failure mode a connectivity-only pass cannot fix:
1. **Non-convex quads** — unflippable; OR
2. **Off-surface Steiner points** — min-angle refinement inserts a vertex whose Z is interpolated
   *linearly on the coarse original face* (`SurfaceRemesher.cs:985`); on a curved face the point lands on
   the chord, and triangles fan around it into a sharp cone/dart. The pointy shape matches this best.

## Iteration 2 — forensics on the real shoulder mesh (BeforeRemesh/AfterRemesh OBJs)
Loaded the actual exported meshes (note: Rhino OBJ is **Y-up** — col1 is height, plan = col0,col2).
- Remesh **added** 282 Steiner points (730→1002 verts) — denser, not coarser.
- The Steiner points near darts sit **exactly on the Before surface** (off-surface ≈ 0) → **mechanism 2
  rejected**. The darts are pure **connectivity**.
- Classifying the beneficial-but-unapplied flips: ~**21 gentle blocked by the 12° floor**, ~**95 gentle
  non-convex** (a diagonal swap would overlap), plus steep real features.

### Two real bugs found & fixed
1. **Criterion was wrong-direction.** The old test compared only the *diagonal's* two triangles, ignoring
   that a flip also re-assigns the quad's **four rim edges** to different triangles. Measured on the real
   mesh it *raised* total roughness (81.2→81.9). Replaced with a **full-stencil delta** (diagonal + 4 rim
   edges); now only flips when the whole neighbourhood gets flatter → roughness **81.2 → 70.0 (−14%)**.
2. **Non-convergence / cycling.** Mutating faces mid-sweep while reading a start-of-sweep adjacency scored
   some flips against stale neighbours, so ~14 edges flipped back every sweep forever. Fixed by
   **locking all six stencil triangles** per flip (skip if any is already touched this sweep) → converges
   to 0 in ~18 sweeps (cap raised 8→16; the tail is tiny).
- Floor lowered 12° → 3° (unlocks the 21 gentle flips; only guards against degenerate slivers).
- New test `SmoothInteriorDiagonals_CurvedGrid_ReducesGlobalRoughness` (saddle z=x·y) guards bug #1.

## Iteration 3 — edge-flipping is the wrong tool (DISABLED)
Visual confirm in Rhino: the corrected flip pass made the road **far worse** — it shredded grading's
clean, road-aligned corridor topology into a chaotic mesh. Root reason: minimising roughness is **not**
the same as preserving structure; on a curved road the roughness-optimal diagonals are irregular. No
edge-flip criterion recovers a structured mesh.

`dataDependentFlips` is now **false** at the Remesh call site (`TerrainBuildService.Tin.cs`). The
`SmoothInteriorDiagonals` implementation + tests are left in place but unused.

### The actual problem (restated)
Remesh **rebuilds the region from scratch as a global Delaunay triangulation**, discarding grading's good
topology. The user wants remesh to *"improve topology and leave features mostly as before"* — i.e. **leave
already-good regions alone, only rework coarse/poor ones.** That is a region/quality-gated remesh, not a
post-pass on a global rebuild.

## Iteration 4 — the real issue is upstream, in Grade Path itself
The dense/irregular/clean inconsistency the user sees is in the **Grade Path** mesh (pre-remesh), not the
remesher. Grade Path fills each corridor with a plain CDT (no quality refinement) of: daylight boundary +
road edges + **batter row seeds**.

- **Dense parallel bands:** batter row count is `ceil(maxReach / spacing)` using the single largest reach
  over the WHOLE path (`BatterStripBuilder.BuildBatterStrip:697`), applied to every cross-section. A
  shallow section gets the deepest section's row count crammed into a short reach → tight bands.
- **Irregular patches:** the corridor CDT runs with `minAngle:0, maxArea:0` and its boundary follows the
  existing (bumpy/sparse) terrain → irregular triangles, nothing smooths them.
- **Clean stretches:** reach ≈ global row spacing and smooth landing terrain → tidy by luck of inputs.

### Fix shipped (dense bands): per-station batter seeding
New `BatterStripBuilder.BuildBatterSeeds(loop, edgeLength)` chooses the row count **per station from its
own reach** (≈ one seed every `edgeLength` down that station's slope), interior rows only. Both pad and
path consume the batter strip as seed *points* only (faces discarded), so ragged per-station rows are
fine. **Wired into Grade Path only** — the pad relies on its global ~3-row strip for slope accuracy
(`Grade_ExplicitStripFan_ShoulderSlopesNeverExceedTarget`), so it keeps `BuildBatterStrip`. Path oval
copied-case is leaner (verts ~2000→1616) and still watertight/manifold; bound updated. Tests:
`BatterStripSeedDensityTests`.

Remaining (not yet done): the **irregular-patch** lever (light quality refinement on the corridor fill,
and/or evenly resampling the daylight boundary).

## Iteration 5 — merge-by-distance for batter-toe pinches
Symptom: grading leaves near-duplicate vertices (often at **batter toes**); the remesh keeps both and
*protects* the tiny edge between them, which refinement fans into a pinched cone. Measured on the mesh:
the pinch edges are ≤0.05 while real edges start at ~0.08 (1st-percentile) — a clean gap. The remesh's
dedup runs at the model tolerance (0.01) and is deliberately capped to protect constraint chains, so the
0.01–0.05 toe clusters survive.

Fix: `SurfaceRemesher.Options.VertexMergeTolerance` — an explicit decimation threshold that raises the
seed/constraint dedup tolerance (overriding the chain-protection cap, since it's a deliberate request),
collapsing near-duplicate input *and* constraint vertices before triangulation. Exposed as a **Remesh
modifier "Merge Distance"** parameter (default 0 = off; the early no-op return also honours it). Keep it
well below the typical edge length. Tests: `SurfaceRemesherVertexMergeTests` (pinch survives without,
collapses with). All 305 Core tests green.

## Iteration 6 — tangency rule: daylight despike (grazing batters)
Newest case confirmed the spiky "upper batter merging into terrain": the remesh log *kept 32 tiny faces*
it couldn't delete without tearing the mesh, and the 88 sliver faces cluster on the **cut batter into
rising terrain** (pad upper batter + road ends). Root: in `BuildDaylightLoop` each station ray-marches to
its daylight point independently, so where a cut batter grazes rising terrain the zero-crossing of
(grade − terrain) is ill-conditioned — one station reaches far while neighbours stop short → jagged
daylight line → slivers.

Fix: `BatterStripBuilder.RegularizeDaylightSpikes` — a one-pass **median filter on per-station reach** that
clamps DOWN only clear outliers (reach > 1.5× the larger neighbour), recomputing the daylight point on
terrain. A smooth ramp is its own median (untouched); a single spike collapses to its neighbour. It only
ever *shortens* a reach, so it can't push a daylight point past where the batter meets ground. Runs inside
`BuildDaylightLoopCore`, so **both pad and path** benefit. No test churn (only clear spikes change), all
308 green. Tests: `BatterStripDaylightSpikeTests`.

## Iteration 7 — transient crease preservation (toe lines)
Textbook case: before remesh a batter toe enters terrain cleanly; after remesh a spike disturbs the toe.
The toe is a **crease** (batter meets terrain at an angle) but not a constraint, so the Delaunay rebuild
flips across it. The user explicitly does NOT want toe lines persisted as breaklines (stacking grades
would accumulate them and get messy).

Fix: `SurfaceRemesher.Options.PreserveCreaseAngleDeg` — each remesh **auto-detects** interior edges whose
adjacent faces fold ≥ the angle (`DetectCreaseEdges`, normal-dot test) and pins them as constraints for
THAT pass only. Detected from geometry, never persisted, so nothing accumulates and it protects *every*
toe/slope-break (including ones baked in by earlier grades). Only runs on the full-vertex-seed pass (the
crease endpoints must be seeded). Exposed as the Remesh modifier **"Crease Angle"** (default 0 = off; the
no-op early return honours it). Tests: `SurfaceRemesherCreaseTests` (ridge found, flats ignored, ridge
edges survive remesh). All 311 Core green.

## Iteration 8 — crease preservation lost when MaxArea is set (fixed)
Setting MaxArea made crease protection vanish. Cause: the remesh runs two passes — full-seed "initial"
and reduced-seed "fallback". The fallback-preference score (`MaxConstraintTriangleArea`) is only computed
when MaxArea > 0, so **only with MaxArea** does the remesh prefer the cleaner-refining reduced-seed
fallback — and (a) the fallback Options didn't carry `PreserveCreaseAngleDeg`/`VertexMergeTolerance`, and
(b) crease detection was guarded to the full-seed pass. Two fixes: carry both knobs into
`CreateBoundaryAndGuideSeedFallbackOptions`, and drop the guard (crease endpoints are added on demand via
`EnsureSeedVertex`, so creases now run in both passes). New test
`Remesh_CreasePreservation_SurvivesReducedSeedFallback` forces the reduced-seed pass and asserts the
crease survives. All 313 Core green. (Same fix also restores Merge Distance under MaxArea.)

## Iteration 9 — MaxArea messed up the batter (over-eager reduced-seed fallback)
Setting MaxArea churned an already-fine batter into irregular triangulation. Cause: with MaxArea the
remesh prefers the **reduced-seed fallback** (it re-seeds from boundary/constraints/coarse guides and
re-triangulates from scratch, discarding the input's structured topology) whenever the fallback's worst
constraint/perimeter triangle is merely &lt;85% of the full-seed pass's — even when the full-seed pass
*already met the area target*. Fix: `ShouldPreferBoundaryAndGuideSeedFallback` now takes the target and
**keeps the structured full-seed result when its near-constraint triangles already satisfy MaxArea**; the
coarse re-seed is only chosen when the full-seed pass genuinely overshot (carried/constraint vertices
blocked refinement — the wall-corridor case it was built for). No regressions (313 green): the cases that
needed the fallback still overshoot the target and still get it.

## Iteration 10 — MaxArea churn is the global-rebuild root cause (diagnosed, not yet fixed)
Reproduced by running the pad copied case (pad-after-path, so it carries the road) → `PadGrader.Grade`
→ `SurfaceRemesher.Remesh` with MaxArea 0 vs 20. Findings:
- Fallback fix confirmed working (both passes select `initial`).
- MaxArea Steiner points are **all exactly on the graded surface** (Z-deviation 0) — not a Z bug.
- **323 of ~1000 MaxArea Steiner points land in graded triangles already < 20 area** — it densifies the
  fine batter that should be untouched.

Mechanism = the recurring one (iteration 3): the remesh **re-triangulates globally (Delaunay)**, which
first coarsens the fine batter, then MaxArea re-subdivides the result — destroy-and-rebuild instead of
leave-alone; the extra points on the folded batter/daylight surface also chord the wrong way → spikes.
The real fix is **local / connectivity-preserving refinement** (keep fine input triangles, only subdivide
the genuinely coarse ones) — the big parked item. Band-aids (despike, crease pin, fallback-preference)
help around the edges but don't address the rebuild itself.

## Iteration 11 — local-refinement remesh shipped ("Local refine" mode)
Built the connectivity-preserving path the diagnosis kept pointing to, behind a new **"Local refine"**
toggle on the Remesh modifier (the global-Delaunay remesh stays as the default mode). Chosen scope
(user): *subdivide + feature flips* — keep flow lines, no vertex motion, no collapse.

`SurfaceRemesher.Engine.LocalMeshRefiner.Refine`:
1. **Adaptive longest-edge subdivision (Rivara).** Split only triangles whose longest edge exceeds the
   target (`EdgeLength`, or derived from `MaxArea`), round by round, at edge **midpoints**. Conformity is
   automatic — midpoints are keyed per edge and shared by both incident triangles (templates handle 1/2/3
   split edges per triangle). **New-vertex Z = endpoint average**, so every added vertex sits *exactly on
   the input surface*: the refined mesh is the identical piecewise-linear surface with more triangles — no
   off-surface Steiner darts (the whole point; kills the iteration-10 MaxArea churn and the spike modes).
   Fine triangles and all input vertices are untouched.
2. **Feature-aware quality flips.** Flip non-feature interior diagonals to raise the pair's minimum angle
   (Lawson / max-min-angle, empty-circumcircle). This **removes the slivers** an irregular input TIN
   carries — a first cut used a *valence* objective (Botsch–Kobbelt) but that only regularizes
   connectivity, not shape, so the slivers survived and the mesh looked shredded. Guard: a crease margin so
   a flip can only act on near-coplanar (near-tangency) diagonals — it cleans up shape without ever
   faceting a smoothly curved area (this is also why it doesn't reintroduce the iteration-3 road-shred).
   The flip machinery (adjacency, stencil lock) was extracted to `MeshFlipGeometry` and reused. Vertices
   never move; requiring a strict angle gain makes the pass converge.

Features (**boundary ∪ auto-detected creases at `CreaseAngle` ∪ constraint breaklines**) are pinned:
never flipped, split children inherit the flag. So batter-toe / slope-break creases stay crisp (no more
spiky blends), and the result is a regular, even mesh — a clean base for the Smooth modifier, and a step
toward a future sculpt modifier. Wired via `RemeshModifierDefinition.LocalRefine` →
`ParameterDescriptor.Bool` → `ApplyRemesh` branch → `RefineMeshLocally`. Tests:
`LocalMeshRefinerTests` (subdivide-to-target, vertices kept in place, on-surface midpoints, watertight,
crease not straddled, valence flips reduce deviation + converge). All 320 Core green.

## Iteration 12 — pivot to field-guided QUAD retopology (Stage 1 shipped)
The triangle direction is the wrong paradigm. Local refine's quality (Lawson) flips still left slivers/fans
on the real terrain — no triangle-flip scheme makes quad flow. What's wanted is **modern retopo**:
quad-dominant, edges flowing along features (creases/road/breaklines), holding loops either side, surviving
Catmull-Clark. That's field-guided quad meshing (Instant Meshes / QuadriFlow family) — a large, new,
multi-stage build; the codebase had none of it. Terrain is 2.5D, so we work in XY and lift Z exactly
(`TerrainFaceGrid`), skipping the hardest parts of general 3-D retopo.

Decisions: **field checkpoint first**; new **"Retopo"** finishing modifier (runs last); quad-**dominant**
output (final goal). **Stage 1 shipped:** `Core/Retopo/CrossFieldSolver.cs` — a 2-D 4-RoSy cross-field,
pinned to feature tangents (boundary ∪ creases via `DetectCreaseEdges` ∪ constraint breaklines) and smoothed
by matrix-free Gauss–Seidel diffusion (no external solver); per-vertex θ∈[0,π/2). The Retopo modifier
(`ShowField` on) leaves geometry untouched and draws a **flow-cross overlay** (decimated crossed segments
along the two quad directions, colored by θ) via a preview-only `FieldOverlayLines` channel
(build result → display state → conduit `DrawLine`), recomputed each build. Features come from the **whole
stack** — boundary ∪ creases ∪ modifier constraint curves ∪ `PersistentHardConstraints` (grade-path road
edges are generated from a centerline, so they only arrive via the stack, not as drawn curves). (First cut
colored the mesh by hue — unreadable for direction; switched to the flow-cross.) Tests:
`CrossFieldSolverTests` (axis/rotated-grid alignment, pins retained, determinism, no-NaN on a creased mesh).
325 Core green. **Roadmap:** Stage 2 = parametrization + integer-isoline quad extraction +
lift-to-Z; Stage 3 = protecting loops, singularity cleanup, quad-dominant Rhino Mesh + quad-aware analysis.

## Iteration 13 — quad retopology Stage 2 shipped (parametrization → quad extraction)
User validated the Stage-1 field (flow-crosses lock onto road/creases) → built the quad mesh. All new Core in
`Core/Retopo/`:
- `GuidedParametrizer.cs` — BFS branch-resolves the 4-RoSy field into a consistent per-face direction, then
  solves two Poisson problems so ∇u≈e1/h, ∇v≈e2/h via a **matrix-free** per-face gradient/divergence operator
  (`L=GᵀMG`, the implicit cotangent Laplacian) with a hand-rolled **Conjugate Gradient** — no external solver.
- `QuadExtractor.cs` — reads the integer (u,v) lattice: per triangle, invert the affine map to find every
  `u=i∧v=j` point inside it; **lattice vertices key by (i,j)** so dedup is exact; emit a quad when all four
  corners exist (gaps at singularities/boundary → quad-**dominant**); Z from the source triangle (exact 2.5D).
- `QuadRemesher.cs` — orchestrates field → parametrize → extract; returns the field too (overlay reuse).

Rhino: Retopo modifier gains a **Quads** toggle (default off) → `ApplyRetopoQuads` replaces the terrain with
the quad mesh, built by new `RhinoGeometryConversions.BuildQuadDominantMesh` (preserves quads — the normal
builder triangulates). Also made `BuildMeshData` **quad-aware** (quad → 2 tris) so any quad mesh read
downstream stays correct. Constraints come through the whole stack (grade-path road edges via
`PersistentHardConstraints`). First cut is **non-seamless** (defects at singularities/boundary expected).
Tests: `QuadRetopoStage2Tests` (parametrizer u≈x/v≈y at unit spacing; extractor flat grid → 16-quad regular
grid + Z-lift; end-to-end quad-dominant + finite on the creased roof). 330 Core green. **Stage 3 roadmap:**
protecting loops, singularity/boundary cleanup, gap-closing, optional seamless param + SubD output.

## Iteration 14 — retaining-wall protection (dedicated wall quad strips)
Stage 2 quads came out jagged over retaining walls: a near-vertical wall is a thin sliver in the 2.5D plan, so
its faces have tiny XY area but a stiff `1/area` gradient → they distort the parametrization and spray garbage
quads. Fix (user chose "dedicated wall quad strips"):
- **Steep-face exclusion** — `QuadRemesher.Options.WallFaceMinSlopeDeg` (~70°) builds a per-face `active` mask
  from the 3-D normal; masked faces are skipped in both `GuidedParametrizer` (so they can't distort u,v) and
  `QuadExtractor` (no quads emitted there). Batters (≤~60°) stay in.
- **Wall quad strips** — new `Core/Retopo/WallQuadStripBuilder.cs`: builds a clean quad band between a wall's
  paired top & toe rails (rows up the wall by edge length), so crest/toe read as loops and the wall stays crisp
  under SubD.
- **Assembly** — `ApplyRetopoQuads` re-derives wall rails from the enabled `RetainingWall` modifiers via the
  existing `RetainingWallPlannerCore.Plan` + `IsWallStripUsable` (correctly cached under the retopo stage),
  quads the terrain with walls excluded, builds the strips, and merges into one quad-dominant mesh.
Non-seamless caveat: the wall↔terrain join can still have small gaps (Stage 3 aligned loops close them). Tests
`WallQuadStripBuilderTests` + steep-exclusion; 333 Core green.

## Earlier candidate directions (remesh-side, parked)
- **A. Preserve good regions:** only refine triangles that are genuinely poor (bad angle/too large);
  pass already-good topology (the graded corridors) through untouched. Best match to the stated goal.
- **B. Connectivity-preserving rebuild:** retain input edges (as constraints/preferred) so the rebuild
  keeps the corridor structure while still inserting constraints / Steiner-refining elsewhere.
- **C. Don't remesh graded regions:** simplest — exclude grade-path/grade-pad corridors from the remesh
  stage entirely.
- Revert the unused `SmoothInteriorDiagonals` code, or leave it parked.

## Iteration 15 — practical Retopo Stage 3 cleanup
Chosen scope: keep the current non-seamless field parametrizer, but make the quad output safer and more
usable in Rhino. Added `QuadRetopoCleanup`: it closes internal lattice hole loops with fan triangles lifted
back to the source terrain and welds the terrain quads, cleanup triangles, and retaining-wall quad strips
before Rhino mesh creation. `ApplyRetopoQuads` now enables cleanup, preserves cleanup triangles in the
quad-dominant mesh, validates topology before accepting output, and falls back to the input mesh on invalid
topology. New Remesh modifiers now default to **Local refine** with a 30° crease angle; old documents that
deserialize without `localRefine` keep the legacy global mode. Tests: `QuadRetopoCleanupTests` and registry
default/legacy-load assertions.

## Iteration 16 — a216 copied case: stop inventing bad topology
The copied case `Terrain-1-20260702-234558-a216f5d9.zip` showed two separate "cleanup made it worse"
failures. **Remesh:** Rhino's Local refine used the Core quality-flip pass and performed thousands of Lawson
flips on a graded corridor/wall stack; even guarded flips can destroy intentional flow, so the Rhino Remesh
path is now subdivision-only (`DoFlips=false`) and reports that it preserved input flow. The Core flip pass
remains available to callers that explicitly opt in. **Retopo:** practical cleanup fan-filled a large
parametrization hole, producing the radial triangle fan visible in the viewport; cleanup now fan-fills only
small holes (derived from target edge length) and leaves large gap loops open instead of fabricating bad
geometry. Test: `QuadRetopoCleanupTests.CloseInternalBoundaryLoops_SkipsLargeHoleInsteadOfFanFilling`.

## Iteration 17 — Sculpt DynTopo hits the same wall (needle spikes on graded fans); lessons applied
The new Sculpt modifier's DynTopo (region-limited `LocalMeshRefiner` under the displacement field) replayed
the whole saga in miniature on a graded road corner (long anisotropic triangle fans): sculpt + smooth
produced dense **needle-spike combs** along the refined band.

**Root cause was NOT the refinement itself** (midpoints sit exactly on the surface — iteration 11 holds).
Two separate mechanisms:
1. **A new one: degenerate slivers break barycentric point location.** The stroke-commit rasterizer
   (`SculptFieldRasterizer`) point-locates each field sample in the working mesh via
   `TerrainFaceGrid.TryInterpolateZ`, which skips triangles with a near-zero barycentric denominator
   (`< 1e-12`). Subdividing an already-degenerate fan yields many such slivers, so samples *inside the
   stroke* silently fail → a comb of stale/zero samples interleaved with written ones → the field itself
   is spiky and replays as needles (smoothing then "reveals" them). Fix:
   `SculptFieldRasterizer.FillPointLocationHoles` — failed samples with ≥ 2 written orthogonal neighbours
   are filled by neighbour average (≤ 4 relaxation passes; the ≥ 2 rule keeps the fill from creeping past
   the mesh boundary — the iteration-16 "don't fabricate geometry far from evidence" principle).
2. **The iteration-16 flip lesson applies verbatim:** the sculpt stage initially ran `DoFlips=true` on the
   refined region. Even region-gated, guarded flips can rewire graded corridor topology, so the sculpt
   stage AND the in-session refine are now both subdivision-only (`DoFlips=false`), and the stage reports
   `"+N vertices under the sculpt, preserved input flow"` like Remesh does.

Perf from the same review: `LocalMeshRefiner.Refine` gained an allocation-free **region early-out**
(`HasCoarseTriangleInRegion`) so the per-stroke-segment DynTopo call costs one scan when there is nothing
to split (previously it copied the whole mesh into Lists per call); the session refines once per mouse
*event* (drag-segment span), not per dab; sculpt DetailSize default is 1 m. Related durable-data fix: the
field's CellSize is **pinned on the definition at first commit** — deriving it from DetailSize meant a
later Detail edit reinterpreted the tiles at a different world scale (rescaling the sculpt toward the
origin). Tests: `LocalMeshRefinerRegionTests`, `SculptFieldRasterizerTests`,
`SculptModifierTests.EffectiveCellSize_PinnedValue_IgnoresLaterDetailChanges`.

Residual (accepted): the DynTopo'd band on an anisotropic fan still *looks* stripy — subdivision honestly
mirrors the base topology. The clean fix remains the parked upstream lever from iteration 4 (light quality
refinement / even boundary resampling in the corridor fill), or sculpting on a Local-refined base.
