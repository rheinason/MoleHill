# Terrain pinches around retaining walls — investigation notes

**Status:** one fix shipped (unmerged, working tree); one problem diagnosed but unfixed; one live bug
unreproduced. Written 2026-09-18.

Reference case: `Trailer loading bay ramp.3dm`, bundle
`Terrain-1-20260917-200440-848a8059`. A 0.2 m wide retaining wall running ~17.7 m across a sparse
39 × 45 m terrain.

## Follow-up experiment — 2026-09-18

New bundle: `Terrain-1-20260917-231313-848a8059`. Remesh is disabled. Its base mesh
matches the existing `RetainingWallPinchCaseTests` fixture (16 vertices, 20 faces).
Measurements below are **XY triangle angles**, so they must not be compared directly to
3D wall-face angles. The exported output has no interior single-use edges, no non-manifold
edges, and no zero-area faces; this bundle still does not reproduce the reported live tear.

Implemented a bounded, opt-in experiment in
`MeshConstraintTopologyInserter.PatchExperiment.cs`. It removes internal edges of the touched
region, keeps the authored vertices and clipped rail segments, and quality-triangulates against
the patch perimeter. Boundary Steiner points are propagated by bisecting adjacent untouched
triangles. It does **not** run in the Rhino build pipeline.

| Variant | Worst XY angle | Faces < 5° / < 1° | Vertices / faces |
|---|---:|---:|---:|
| Base terrain | 5.406° | 0 / 0 | 16 / 20 |
| Existing per-face insertion | 0.081° | 16 / 6 | 37 / 58 |
| Patch + conforming neighbour splits | 0.461° | 14 / 4 | 402 / 775 |
| Patch + one neighbour ring + conforming splits | 0.214° | 13 / 6 | 408 / 779 |
| Patch + two neighbour rings + conforming splits | **20.000°** | **0 / 0** | 382 / 731 |

All three completed patch variants conserve the 1159.994082 plan area and pass checks for
positive triangle area, edge usage no greater than two, and every single-use edge lying on
the original outer rectangle. The two-ring variant also asserts rail edge-chain continuity
and authored rail elevations. A separate analytic wall-band test forces interior Steiner points
and checks their elevations against the expected wall plane.

**What changed in the recommendation:** boundary splitting alone maintains continuity, but
can simply move the slivers into the neighbours. One ring was worse than no ring in this case;
two rings succeeded. A fixed ring count is therefore not a justified general solution. A production
implementation needs a quality-driven transition criterion and a bounded expansion policy.

Two implementation details emerged:

- The shared helper's min-angle-only Steiner cap (10× input vertices) stopped refinement before
  quality converged. The experiment uses an explicit 10,000-point cap and no fallback, and asserts
  the achieved quality instead of treating triangulation success as quality success.
- Re-triangulating a neighbour containing many nominally collinear boundary points produced
  floating-point sliver faces. Direct ordered edge bisection gave conforming positive-area faces.
  Refined points are matched at numerical precision, not merged at the 0.01 model tolerance.

Elevation requires more than lifting the rails afterwards: new points can lie *inside* the wall
band. The experiment samples an elevation reference built by the old inserter with its rails
lifted first, so these interior points follow the wall rather than the original ground.

**Limits:** this is a small-case experiment capped at 4096 input faces, using straightforward
scans and a combined touched region. It has not been validated for disconnected patches, holes,
large terrains, existing hard constraints not supplied as rails, or preservation of nonplanar
terrain between samples. It is not installed or live-tested in Rhino. The successful expanded
region on this tiny sparse mesh does not establish scalability. Keep the current production
inserter until those contracts and adaptive expansion are addressed.

---

## The symptom

Long thin "pinch" triangles fanning out from a single point beside the retaining wall. They were
visible in three different places, and it turned out these were **not all the same problem**:

1. In the raw terrain, before any Remesh — a fan of long edges radiating from the wall corner.
2. After a 1 m Remesh — the terrain is cleanly refined *everywhere except* right at the wall, where a
   sliver fan survives.
3. In the wall's own faces — the wall band itself triangulated as a fan rather than a ladder.

---

## What we found

### There are two independent causes

**Cause A — the Retaining Wall modifier makes the pre-Remesh fan.**

The bundle ships the base mesh separately, so this was directly measurable:

| | worst triangle angle | faces < 5° | faces < 1° |
|---|---|---|---|
| Base TIN, before the wall modifier | **4.33°** | 1 | **0** |
| After the wall is inserted | 0.05° | 15 | 7 |

The triangulation itself is healthy. The damage is done by the insertion step.

**Why:** `MeshConstraintTopologyInserter.TriangulateTouchedFace` retriangulates **one host face at a
time**, confined to that face's own boundary. When the 0.2 m wall corridor crosses a 12 m terrain face,
the local triangulation has no choice but to bridge the corridor to that face's distant corners. It is a
*local stitch* — which runs against the project's own rule, recorded in `CLAUDE.md`:
*"Full-remesh > local stitch: always re-triangulate ALL vertices."* Grading (`PadGrader`, `PathGrader`)
follows that rule; the wall inserter does not.

**Cause B — the remesher's wall freeze made the post-Remesh pinch (fixed).**

Steep wall faces are "frozen" so walls can never be moved or buried. But the freeze also blocked
*refinement*: a wall base line sampled every 6–11 m stayed that coarse while the terrain around it
refined to 1 m. Collapse and relax kept pulling the free side toward the pinned line, and nothing could
refine it back — so the faces bridging that mismatch were squeezed toward zero area.

It got **strictly worse with every iteration**, which is why the bake (5 iterations) looked worst:

| iterations | worst terrain angle |
|---|---|
| 1 | 0.235° |
| 2 | 0.040° |
| 3 (preview) | 0.0025° |
| 5 (bake) | **0.000°** — genuinely degenerate |

Proof it was the freeze: turning wall freezing off produced a perfectly clean mesh (worst 5.18°, zero
slivers).

---

## The fix that shipped

**A frozen wall is frozen against *motion*, not against *refinement*.** Bisecting a wall edge puts the
new point exactly *on* that edge — so nothing moves, and nothing is buried. That one change let the wall
keep pace with the terrain around it.

Two supporting details mattered:

- The midpoint takes the **exact chord**, never the back-projected terrain surface, which would smear a
  near-vertical wall onto the terrain.
- `FrozenFaces` turned out to mean **two different things**: steep walls *and* the quarantine for
  non-manifold geometry. Refining the quarantined faces multiplied the sickness — non-manifold edges
  went 28 → 5383 on a real graded scene. These are now separate masks
  (`FeaturePolylineGraph.QuarantinedFaces`), and quarantined faces are still never subdivided.

**Results:**

| | before | after |
|---|---|---|
| terrain, worst angle | 0.000° | **2.100°** |
| terrain, faces < 5° / < 1° | 62 / 34 | **2 / 0** |
| wall, worst angle | 1.968° | **1.968°** (unchanged) |
| wall, biggest fan hub | 18 faces | **8** |

More iterations now *improve* the result instead of degrading it. Every input wall vertex still comes
through bit-identical, and the mesh stays watertight.

**Files:** `IsotropicRemesher.cs`, `FeaturePolylineGraph.cs`, plus a regression test
(`Remesh_WallBaseCoarserThanTarget_TerrainBesideWallDoesNotDegenerate`) that was confirmed to fail on the
old code and pass on the new. All 1660 tests green. Nothing committed.

---

## Things we tried that did NOT work

These were each implemented, measured, and reverted. Worth recording so they are not retried blind.

| Attempt | Result |
|---|---|
| **Auto-densifying straight breaklines** | Made the fan **worse**: max valence 7 → 12 at the *same* hub, median angle 24.8° → 10.8°, and it introduced a degenerate face. Densifying adds no information and does not fill the empty band; it just gives isolated vertices more distant partners to fan to. |
| **Splitting the longest *non-frozen* edge** when the longest is frozen | 71 → 73 slivers. No effect. |
| **A degeneracy floor in the collapse guard** | 62 → **297** faces under 5°. Much worse — vetoing collapses leaves more junk than it prevents. |
| **Flips between coplanar wall faces** | Worked, but earned almost nothing (wall median 15.4° → 16.7°, worst identical) for the riskiest code in the change. Removed. |

A note on that last one: `MeshFlipGeometry.TriangleNormal` forces `nz >= 0`, and a near-vertical face has
`nz ≈ 0`, so that sign is **rounding noise**. Two opposite-facing panels of a thick wall can therefore
read as "coplanar". Anything that reasons about which way a steep face points must not use that helper.

---

## Still open

### 1. A live tear that was not reproduced

After a rebuild, the wall band appeared torn open in Rhino — and `UnifyMeshNormals` did not help, meaning
it is geometry, not winding. **It could not be reproduced** against the bundle mesh, with or without the
flip code. The flips (the most likely culprit, per the `nz` note above) have been removed, but this is
*not* a confirmed fix. If it recurs, a fresh Copy Case from that exact state is what's needed.

### 2. Patch retriangulation — built and parked

The proposed fix for Cause A: group the cut faces into a connected **patch**, retriangulate it in one
pass, preserve the patch's outer boundary and the wall rails, and let refinement grade from the 0.2 m
corridor out to the coarse terrain.

**It works structurally.** Output is watertight, zero non-manifold edges, plan area conserved exactly
(ratio 1.0000), and the outer boundary is preserved so untouched neighbours still match vertex-for-vertex.

**But quality came out worse than the per-face baseline** — faces under 5°: 7 → 129. This is *not* a bug:
the triangulation succeeded on its first tier with no fallback and no dropped constraints. It is a real
trade-off:

> Preserving the patch boundary means forbidding its splitting. A 20 m boundary edge next to a 0.2 m
> corridor cannot be improved without splitting it. With splitting fully disabled, refinement cannot
> start at all (18 vertices, nothing inserted); with only the boundary protected, the interior refines
> but leaves junk against the frozen boundary.

**The transition has to cross the patch boundary.** Three ways forward:

1. **Grow the patch** outward until its boundary edges are comparable to the corridor scale, so the
   transition happens inside the patch. Simple, but patch size becomes data-dependent.
2. **Allow boundary splits, and bisect the untouched neighbour faces** to stay conforming. Each neighbour
   just gets split at the new boundary point, and the inserter already has edge-point machinery for this.
   *This is the recommended option.*
3. Leave it to Remesh — which now genuinely works, thanks to the shipped fix above.

Working implementation is parked in the session scratchpad as `inserter.patch-wip.cs`.

---

## One-paragraph summary

Pinches beside retaining walls had two separate causes. The Remesh one — the wall freeze blocking
refinement, so the terrain beside a wall was squeezed toward zero area, worse on every iteration — is
**fixed**: a frozen wall may now be refined, because bisecting an edge moves nothing. The pre-Remesh one
is **diagnosed but unfixed**: the Retaining Wall modifier stitches its corridor into one terrain face at a
time, which forces slivers when a 0.2 m corridor crosses a 12 m face. The patch-based fix for it is built
and proven structurally sound, but needs one more decision about how the transition crosses the patch
boundary before it improves anything.
