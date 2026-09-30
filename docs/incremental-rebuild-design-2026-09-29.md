# Incremental terrain rebuilds — design (2026-09-29)

**Status:** proposal. Nothing here changes the current architecture until a phase lands; each phase
updates `architecture.md` when it does.

**Scope:** the *authoritative* build — the exact terrain and everything derived from it — made to cost
in proportion to what an edit changed rather than to the size of the terrain. It is the other half of
`interactive-terrain-plan-2026-09-16.md`, which covers gesture latency (sessions, previews,
cancellation). That plan's Step 6 ("extend local evaluation only where dependency locality is
proven") is where this design plugs in, and it keeps that plan's rules: stage-local contracts rather than a
universal dependency graph, and exact fallback whenever a precondition is not proven.

## 1. The problem, measured

Park-scale probe (`validation-lanes.md`, "park-stress"), 4 × 0.8 km park, full stack:
Triangulate → Grade Pad → Grade Path → Retaining Wall → Remesh → 5 analyses → contours.

**A local edit costs a cold build.** Raising one survey point by 5 cm at 1 m spacing (a change to about
2 m² of a 3.2 km² terrain):

| Stage | Time | Why it re-ran |
|---|---|---|
| Triangulate | 3.3 s | Z-only shortcut already; the rest is conversion and hashing |
| Grade Pad | 20.3 s | input fingerprint changed |
| Grade Path | 26.8 s | input fingerprint changed |
| Retaining Wall | 11.0 s | input fingerprint changed |
| Remesh | 36.5 s | input fingerprint changed (58 s before the collapse work) |
| Analyses | 23.4 s | input fingerprint changed; Cut / Fill alone 11.4 s |
| **Total** | **~124 s** | |

**Edits that cannot change the terrain still cost a rebuild.** New park-probe steps, run 2026-09-29
after the survey-point edit on the full stack:

| Step | 2 m (1.3M faces) | 1 m (5.1M faces) | What ran |
|---|---|---|---|
| Rebuild, nothing changed | 0.1 s | 0.5 s | every stage hit its cache |
| Add an empty Grade Pad card below Triangulate | 21.7 s | 92.4 s | Grade Pad, Grade Path, Wall and Remesh re-ran on identical input; the analyses hit |
| Rename the Remesh card | 8.6 s | 37.0 s | Remesh re-ran; the analyses hit |

Cache hits are already cheap (0.5 s for the whole stack at 1 m), so the cost is entirely in *misses that
should have been hits* and in *whole-terrain work for local changes*.

## 2. How the stage cache works today

Facts from `TerrainBuildService` (`.cs`, `.Cache.cs`, `.Fingerprints.cs`, `.ModifierStages.cs`) and
`TerrainRuntimeCache`:

1. **Key.** Each modifier stage is cached under `{mode}modifier:{index}:{type}:{id}`
   (`TerrainStageKey.CreateModifier`). After each build, `PruneUnused` drops every entry whose key was not
   used.
2. **Hit test.** A stage hits when its stored `PreResolutionFingerprint` equals
   `ComputeModifierStageFingerprint`: the upstream stage's *output* fingerprint, the modifier type, the
   tolerances, the whole definition serialized to JSON (including `Label`), and each source set's
   fingerprint.
3. **Output fingerprint is content.** `StoreMeshStageCache` hashes the normalized output mesh plus the
   hard and elevation constraint lists. Every mesh stage stores through it, so a stage that re-runs and
   produces an identical result gives downstream stages an unchanged input: **early cut-off already works.**
   That is why the rename above cost only Remesh and not the analyses.
4. **Grading overlap invalidation.** Grade Pad / Path / Line record `GradingPatch` bounds in
   `GradingTopologyEntries`. `FindIntersectingGradingStageKeys` invalidates later grading stages whose
   patches overlap; it reads the later stage's position by parsing the index out of its key.
5. **Partial reuse that exists.** Grade Pad caches its graded topology separately from the stage (keyed by
   upstream fingerprint, pads and locks). The retaining-wall rail plan is cached independently of the
   terrain. `TinEngine` updates Z only, or inserts/removes a single point, without re-triangulating.
6. **Representation.** Stages hand each other Rhino meshes. Each stage extracts flat arrays
   (`TryExtractMeshData` normalizes a copy), works on them, builds a Rhino mesh back, and
   `StoreMeshStageCache` normalizes, hashes and clones it.

## 3. Why an empty card rebuilds everything below it

Four independent causes, all confirmed in code:

1. **Position is in the cache key.** Putting a card anywhere but the bottom shifts the index of every card
   below it. Their keys change, they miss, and `PruneUnused` then deletes the old entries. The miss is
   spurious: their inputs are unchanged, because content cut-off (§2.3) would have handed them the same
   upstream fingerprint. (`AddModifier` appends at the bottom, so the rebuild starts when the card is moved
   up into place — which is what a user does next.)
2. **Position is also in two content fingerprints.** `ComputeZoneGradePathFingerprint`
   (`Fingerprints.cs`) and Smooth's `ComputeSelectedGradePathRoadBreaklinesFingerprint` (`Tin.cs`) hash
   the matching Grade Path's *index*, so they change when a card is inserted above that path.
3. **An inert card still does whole-terrain bookkeeping.** An empty Grade Pad finds it has no boundaries
   quickly (its own row reads 0 ms at 1 m), but it still extracts the mesh first, and `StoreMeshStageCache`
   then normalizes, hashes and clones the full 6.4M-face mesh. That is under 0.6 s in the 92 s above, so
   this is the smallest cause. The stage timing rows do not show it, because `StoreMeshStageCache` stops
   the stage timer before it normalizes, hashes and clones. That is a measurement gap to close in P0.
4. **Presentation is fingerprinted as input.** `Label` is part of the serialized definition, so renaming a
   card re-runs it (8.6 s for Remesh at 2 m, ~37 s at 1 m).

## 4. Principles

- **History independence.** The authoritative terrain is a function of the definition alone. An
  incremental build must produce exactly what a cold build of the same definition produces. It is
  checked by an oracle (§8), not argued. Without this a reopened file can grade differently, and a
  quantity report can change between two opens of the same document. (Decision D1.)
- **Stage-local contracts.** Each stage type declares what it can prove about its own locality. There is
  no framework that infers dependencies.
- **Fallback is always a full re-run of that stage.** An unproven precondition costs time, never
  correctness.
- **Measured on the park probe and the hosted-perf lane**, with finished-mesh hash equality as the gate
  for every phase that claims exactness.

## 5. Layer 0 — structural edits cost nothing

This layer alone fixes "adding a card rebuilds the terrain". It is small, independent of everything
below, and ships first.

**0.1 Stable stage keys.** The key becomes `{mode}modifier:{type}:{id}`. Order needs no representation in
the key, because the upstream fingerprint chain already carries it: moving a card changes the inputs of
exactly the stages whose inputs really changed, and content cut-off stops the invalidation where results
converge again. Changes:
- `TerrainStageKey.CreateModifier` drops the index. Its four callers are `TerrainBuildService.Build`,
  `TerrainController.Diagnostics`, `SculptSessionController` and the sculpt stage-mesh lookup.
- `FindIntersectingGradingStageKeys` takes the current build's id → position map instead of parsing the
  key. `TryParseModifierIndex` is deleted.
- The zone and Smooth fingerprints hash the Grade Path's `Id`, not its index.
- Test: insert, move and remove a card at every position of a five-stage stack. Every stage whose input is
  unchanged reports a cache hit.

**0.2 Inert cards pass through.** Each modifier descriptor declares when an instance cannot affect the
terrain: `IsInert(definition, snapshot)`, evaluated on *resolved* sources, so a set whose objects were
deleted counts as empty. An inert stage returns the incoming mesh and fingerprint untouched. It publishes
no constraints or outputs, writes no cache entry, and its card says so ("Not applied — no boundaries
selected").

| Modifier | Inert when |
|---|---|
| Grade Pad / Grade Path / Grade Line | no resolved boundaries / paths / lines |
| Retaining Wall, In-Situ Stair | no resolved curves |
| Add Geometry | no resolved geometry |
| Project To | no target terrain |
| Sculpt | empty displacement field |
| Remesh, Retopo, Smooth, Simplify | never: they act on the whole mesh without sources |

**0.3 Presentation is not input.** The stage fingerprint serializes the definition through a
fingerprint-only contract that skips presentation fields (`Label`, and any row the parameter schema
declares presentation-only). A guard test, in the style of `ParameterSchemaGuardTests`, requires every
property of every definition to be classified as a build input or as presentation. This is the
interactive plan's "appearance only" capability, applied to the authoritative build.

**Expected result:** adding, moving up, or renaming a card that has no effect costs well under a second
at 1 m. Verified by the three probe steps in §1.

## 6. Layer 1 — flat, canonical stage outputs

Layers 2–4 need to *compare* and *patch* stage outputs cheaply. Today's representation makes both
expensive. This layer is the foundation; it is also the "flat arrays between stages" step of the
earlier perf plan, and it pays for itself in removed conversions.

- **Flat data between stages.** Stages exchange an immutable `TerrainMeshData`: XYZ `double[]`, faces
  `int[]`, and the hard and elevation constraints. Rhino meshes are built only for display and bake.
  This removes the extract / normalize / rebuild round trip every stage makes now.
- **Canonical order.** Vertices are sorted by a world-anchored spatial key: tile, then Morton order within
  the tile, then exact coordinates. Faces are rotated to start at their smallest vertex, then sorted. Two
  meshes with the same geometry and connectivity then have identical arrays. Equality becomes hash
  equality, and the difference between two meshes is well defined and cheap to compute. As a side effect
  every pass gets spatial memory locality; the collapse phase was bound by exactly that latency.
- **An updatable content hash.** The fingerprint becomes a multiset hash: the sum of a strong 128-bit mix
  of each face together with its three vertices. Removing and adding faces updates it in O(changed faces)
  instead of rehashing 6M faces.
- **Cost.** Canonicalizing is a sort, about 0.3–0.6 s at 3M vertices, in place of today's
  normalize, hash and clone per stage.
- **Risk.** Every stored hash changes once, so hosted-perf must be re-baselined. A Core algorithm that
  quietly depends on input vertex order will produce a new, but again deterministic, cold result.

## 7. Layer 2 — dirty regions and splicing

### The contract a stage declares

- **Input change Δin:** faces removed and added, and constraints changed, relative to the input the cached
  output was built from. Its **dirty bounds** B(Δ) are the plan bounds of those faces and constraints,
  held as a set of world tiles.
- **Envelope Env(definition, input):** a conservative plan region with two guarantees.
  - Outside Env, the output equals the input face for face.
  - Inside Env, the output depends only on input within **Env ⊕ halo**. The halo is how far the stage
    reads beyond where it writes: daylight rays, ring patches, and so on.
- For an edited definition, Env is the union of the old and new envelopes. That way moving a feature
  restores the ground it leaves behind; the interactive plan has the same rule.

| Stage | Env | Halo |
|---|---|---|
| Grade Pad | each pad boundary grown by MaxDistance | the daylight-ray corridor |
| Grade Path / Line | each corridor grown by MaxDistance | the ray corridor |
| Retaining Wall | rail strips | the local-triangulation ring (crossed faces plus their vertex ring) |
| Add Geometry | geometry bounds | one face ring |
| Smooth (with boundary) | boundary | smoothing radius × iterations |
| Project To | the target's footprint | one face ring |
| Sculpt | occupied field tiles | brush falloff |
| Triangulate | produces Δ; see below | — |
| Remesh | whole terrain today (§7.4) | — |

### Rules

- **R1 Splice.** The definition is unchanged and B(Δin) does not touch Env ⊕ halo. Then
  `out' = cachedOut − removedFaces + addedFaces`, and Δout = Δin. This is exact: outside Env the cached
  output *is* the old input, so the same faces can be exchanged in it. It costs O(|Δ|) plus an O(n) array
  shift, milliseconds rather than seconds.
- **R2 Re-run and diff.** Otherwise the stage runs. Δout is then `diff(out', cachedOut)` in canonical form,
  so downstream stages still see a local change: a pad slope edit only dirties that pad's envelope.
- **R3 Per item (later).** Stages made of independent items (pads, paths, walls) re-run only the items
  whose Env ⊕ halo touches B(Δ), or whose definition changed. Items that blend where they overlap (pad
  overlap) are grouped and re-run together.
- **Constraints** are stage input and output like faces: a changed constraint adds its bounds to B(Δ).
  Stage artefacts (daylight lines, overlays, diagnostics) are reused under R1, because under R1 they depend
  only on unchanged input.

### Triangulate as the source of Δ

- **Z-only edits** (`WithUpdatedZ`): Δ is the faces incident to the changed vertices.
- **Single point added or removed** (`TryApplyIncrementalEdit`): Δ is the old and new cavity.
- **Anything else:** a full rebuild, then `diff` against the cached output. Delaunay is local, so the
  difference is still small when the edit is.
- **Snapshot cost.** The interactive plan notes that capturing a snapshot fingerprints every source
  set. With 3.2M survey points that capture runs on every edit and is not in the probe's numbers. It has
  to be measured through the controller before claiming end-to-end edit times.

### 7.4 Remesh

Remesh is the one global stage. Every operator pass is ordered over the whole mesh, so in principle a
local change can move vertices anywhere, and at 36.5 s it is the largest stage at 1 m. Two options
(decision D2):

- **(a) Keep it global.** Any upstream Δ re-runs Remesh. This is correct and simple, and it still benefits
  from Layers 0–1.
- **(b) Tile-deterministic Remesh.** World-anchored tiles about 64 × the target edge. Tile boundary lines
  are sampled deterministically at the target length and pinned as features. Each tile is remeshed on its
  own.
  - Output inside a tile then depends only on input in that tile, so an incremental result equals a cold
    one by construction. Δout is the dirty tiles.
  - Cold Remesh parallelizes across cores: 24 threads here, so an estimated few seconds instead of 36.
  - The cost is straight rows of edges along the tile lines. They lie on the surface, so contours and
    shading are unaffected, but they show in wireframe.
  - Breaklines and frozen walls that cross a tile line get an intersection vertex. The
    frozen-wall-bisection lesson in `remesh-wall-pinch` applies.

**Recommendation:** prototype (b) at 1 m and compare against (a) on minimum angle, edge-length histogram,
maximum deviation from the input surface, and a visual check of the tile lines, before either is chosen.

## 8. Layer 3 — analyses and outputs

- **Per-face analyses** (slope, aspect, elevation, Cut / Fill volumes and delta contours, contour
  annotation) decompose by tile. Per-tile partial results (sums, generated curve pieces) are cached, and
  only tiles in B(Δ) are recomputed. Contour pieces are per-face local; joining them into polylines is
  global but cheap. The base mesh Cut / Fill compares against gets its Δ from Triangulate in the same way.
- **Flow analyses** (waterflow, catchments, ponding) are not local: flow crosses any region. They re-run
  on any Δ, at 2–4 s each at 1 m. Later they can be bounded by basin: a change can only alter flow in the
  basins containing B(Δ) and those downstream of them. Per the interactive plan, they never inherit local
  invalidation by assumption.
- **Zones, markers, objects, scatter:** per-item envelope tests. **Display:** per-tile preview chunks
  (interactive plan Step 6, "dirty display chunks").

## 9. Correctness: the equivalence oracle

- **An oracle for every incremental path.** Random edit sequences on fixtures, at Core level where
  possible, compare the incremental result with a cold build of the final definition by canonical hash.
  The generator must include the adversarial cases:
  - an edit exactly on an envelope edge, and one inside the halo;
  - a boundary moved into or out of an envelope;
  - a constraint change from an upstream stage;
  - card insert, remove, enable and reorder.
- **A settle check** (developer lane first): after an incremental final build, an idle-time cold build
  compares hashes and reports any divergence as a diagnostic.
- **Lanes.** hosted-perf gains an edit scenario per layer, and the park probe keeps its structural and local
  edit steps.

## 10. Phasing

| Phase | Content | Expected at 1 m |
|---|---|---|
| **P0** | Layer 0: stable keys, inert cards, presentation fields | empty or renamed card: under 1 s (from 92 s / 37 s) |
| **P1** | Layer 1: flat canonical data between stages | fewer conversions on every build; enables P2 |
| **P2** | Layer 2 R1/R2 for graders, walls, Add Geometry, Project To, Sculpt, bounded Smooth; Triangulate as Δ source | survey-point edit: Remesh + analyses remain (~60 s) |
| **P3** | Layer 3, per-face analyses by tile | analyses: from 23 s to the flow analyses (~8 s) |
| **P4** | Remesh decision (D2) and, if (b), tile Remesh | survey-point edit: a few seconds |
| **P5** | R3 per-item re-runs; flow analyses bounded by basin | edits near features stay local |

Each phase is shippable alone, measured on the park probe and hosted-perf, and carries its own oracle
tests.

## 11. Decisions (answered 2026-09-29)

- **D1:** exact. An incremental result must equal a cold build.
- **D2:** prototype the tiled Remesh, but its topology must be very good: Remesh is the intermediate step
  before sculpting and smoothing. Straight pinned tile lines are therefore out. The prototype uses two
  passes on grids offset by half a tile, so every region is remeshed freely in one pass and the second pass
  holds only edges the first already remeshed.
- **D3:** canonical vertex order, as recommended.
- **D4:** accepted ("Not applied — no boundaries selected").

## 12. Progress

- **P0 done** (2026-09-29): stable keys, inert cards, `[NotBuildInput]`, and a `{stage} Cache Store`
  timing row. At 1 m an empty card below Triangulate takes 1.2 s (was 92 s), and renaming Remesh 0.5 s
  (was 37 s); every stage below reports a cache hit. The stair's `Computed*` fields were found to be
  fingerprinted outputs, which cost one spurious re-run after every build; they are no longer.
- **P4 prototype done** (2026-09-30): `TiledIsotropicRemesher`, behind the Remesh card's hidden `"tiled"`
  mode (architecture.md, "Remesh: tiled prototype"). On the 1 m park it is 2.3× faster than the global
  remesh and better on every quality measure, with no vertex off the terrain and no seam; an edit changes
  output within one tile.
- **P4 done** (2026-09-30): the tiled remesh is the default, and incremental. Tiles are canonically ordered
  and keyed by content, so the incremental path needed no global canonical order (P1) after all: an unchanged
  tile is found by its key whatever the upstream numbering. On the 1 m park a one-vertex edit remeshes 10 of
  2,134 tiles (3.5 s against 11.7 s cold), bit-identical to a cold remesh of the edit.
- **Windowed Grade Pad done** (2026-09-30; replaces P2's splice for pads). Each group of overlapping pads
  is graded on the sub-mesh inside its window and stitched back, keyed and memoized like tiles. Cold and
  incremental run the same windowed computation, so exactness needs no proof about what a grader reads
  globally. On the 1 m park Grade Pad is 7.4 s cold (was ~19 s) and a survey edit reuses every window. A
  window that cannot weld falls back to grading the whole mesh (architecture.md, "Grading windows").
- **Windows by shape, and windowed Grade Path** (2026-09-30). A window is the faces within an item's reach of its
  plan shape (a pad's filled outline, a path's centreline), not its bounding box: the park's path network is
  one connected group, whose box is the whole park, but whose band leaves the land between the roads out.
  With a margin read only from faces within each item's reach, the network splits into 7 windows at 1 m;
  a survey edit away from roads and pads reuses them all and costs 54 s (was 76 s). Grade Path is 17 s cold (was 27 s).
- **Next:** the Retaining Wall stage, per-tile analyses (P3), and flat data between stages (P1) for the
  per-stage O(n) plumbing that remains.

## Original questions for the owner

- **D1 — History independence.** Incremental results must equal a cold build exactly (recommended), or may
  differ within tolerance. The second ships sooner, but a reopened file can then report different volumes.
- **D2 — Remesh.** Tile-deterministic, with visible tile-line edge rows (recommended to prototype), or
  global and re-run on every upstream change.
- **D3 — Canonical vertex order.** It changes every stored hash once and needs a re-baseline. It is
  required by D1, if exactness is chosen.
- **D4 — Inert-card message.** An inert card says why it is not applied yet. Is that the wording and place
  you want?
