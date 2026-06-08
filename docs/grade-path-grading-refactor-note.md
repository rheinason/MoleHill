# Grade Path Grading Refactor Note

Date: April 20, 2026

Status: Superseded by the `grading-rebuild` branch. This note is retained as
historical debugging context for the old stitched/remesh fallback ladder. Current
work should use `docs/grading-rebuild-review.md` as the source of truth: path and
pad grading now route through shared constraint-first topology, and the old path
remesh fallback routes described below are no longer the active architecture.

## Summary

`Grade Path` currently has two separate problems:

1. The stitched path topology path is still unreliable.
2. The remesh fallback path was failing on coarse upstream meshes because it inherited an overly large effective tolerance.

The second problem now has a usable workaround in code: the `road-edge fallback` can succeed on coarse terrain again after tightening the path-specific geometry tolerance. The first problem is still unresolved and remains the main architectural gap.

## Update This Round

- Added an in-core structured patch acceptance gate in `PathGrader` before a structured open-path patch is accepted.
- The structured patch gate now checks seam geometry against the split seam using loop-to-segment distance, not exact vertex/segment identity. This avoids false rejection when the patch seam is geometrically correct but differently segmented.
- If the structured patch is rejected by that gate, `PathGrader` now falls through to the alternate triangulated patch builder instead of accepting the structured patch immediately.
- Added explicit diagnostics for:
  - whether the structured or triangulated patch builder was selected
  - when a structured patch was rejected before merge
- Updated seam diagnostics to compare against the authored split seam rather than the patch loop itself.
- Added strip planarity checks in the alternate triangulated patch builder:
  - planar road strips can drop the longitudinal centerline polyline in the simplified attempt
  - planar shoulder strips suppress extra guide rows in the simplified attempt
  - the full triangulated patch graph remains as the fallback attempt if the simplified one fails
- Reordered the remesh fallback ladder on coarse meshes so `Grade Path` now tries the localized substrate road-edge fallback before the whole-mesh road-edge fallback.
- Added an explicit coarse-mesh heuristic for that ordering so localized corridor remesh is preferred when the upstream terrain is already sparse.
- Replaced the localized fallback's shared expanded bounding box with per-path split boundaries.
- Localized fallback now prefers path-following corridor loops built from the path geometry itself, and only falls back to conservative per-path boxes when a corridor loop cannot be built.
- Localized fallback now classifies whole existing mesh faces into the corridor first, and only retries with an exact topology split if the whole-face corridor selection finds nothing.
- Tightened the path-following localized fallback boundary so it uses a capped road-edge support band rather than the full computed shoulder reach.
- Whole-face localized corridor selection now tries bounded bridge-face growth first when the coarse corridor is fragmented, and only falls back to the exact split path if it still cannot reconnect into a single connected patch with a single boundary loop.
- The localized substrate prepass now skips protected-edge subdivision and uses reduced-seed remeshing without interior guide seeds, so the synthetic corridor split boundary does not dominate local refinement before the road-edge pass.
- Full `Grade Path` remesh constraints now boundary-clip the centerline, road edges, and station ribs before remeshing, so paths that run onto or past the terrain perimeter only constrain the in-bounds portion of the corridor.
- When localized fallback already has a coherent whole-face corridor selection, it now uses that selection directly as the local substrate instead of running an extra substrate prepass first.
- Road-edge remesh fallback constraints now resample dense authored path rails down to the fallback segment length before remeshing, so localized corridor rebuilds do not inherit every upstream path sample/control point.
- The localized road-edge fallback remesh now skips extra corridor seed injection after rail resampling, so the local corridor is not re-seeded across already-defined fallback rails.
- Stitched-path rejection now logs the per-path patch-builder and seam diagnostics that `PathGrader` already computed, so stitched failures can be debugged directly instead of only seeing the top-level boundary-loop rejection.
- Structured path validation now rejects no-loop patch meshes only when their seam-adjacent boundary graph is also heavily fragmented, so the obvious `329 vs 179` seam-boundary blow-up falls through to triangulated fallback without regressing smaller boundary-touching path cases.
- Alternate triangulated path patches now go through the same pre-merge stitch validation gate as structured patches, and the builder keeps trying other graph variants when a triangulated candidate still has bad seam deviation or seam-adjacent boundary fragmentation.
- Validation for this round:
  - `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj --filter PathGrader`
  - `dotnet test tests/MoleHill.Core.Tests/MoleHill.Core.Tests.csproj`
  - `dotnet build src/MoleHill.Rhino/MoleHill.Rhino.csproj -p:OutDir=C:\Users\hbxma\Dropbox\TopoTest\.artifacts\build-verify\MoleHill.Rhino\`

## Current State

### What works now

- `Grade Path` no longer routinely degrades to "move existing vertices only" on the coarse `25 verts / 41 faces` case.
- The fallback chain now reaches a successful remesh in the tested coarse case:
  - stitched path rejected
  - full remesh fallback rejected
  - simplified remesh fallback rejected
  - path-only remesh fallback rejected
  - corridor remesh fallback rejected
  - road-edge remesh fallback succeeds
- The successful fallback now produces a useful path topology rebuild on the original coarse mesh:
  - `25 verts / 41 faces -> 452 verts / 854 faces`
- The improvement came from tightening the path topology tolerance, not from a global terrain remesh.
- On coarse upstream meshes, the fallback ladder now prefers a localized corridor-sized road-edge rescue before rebuilding the whole terrain as a final safety net.
- The remaining localized fallback refinement is boundary quality: path-following split loops should reduce corridor spread more than a single axis-aligned owned-region box.

### What still fails

- The stitched path mode still fails first.
- Typical stitched rejection in the coarse case:
  - `Grade Path stitched output produced 16 boundary loops instead of a single terrain boundary.`
  - `Grade Path stitched repair found no removable detached components (components 1, boundary edges 380).`
- This means stitched mode is not failing because of small detached scraps. It is producing one connected mesh with a bad seam/boundary graph.

## Files Involved

- `src/MoleHill.Rhino/Services/TerrainBuildService.cs`
- `src/MoleHill.Core/Grading/PathGrader.cs`
- `src/MoleHill.Core/Engine/SurfaceRemesher.cs`
- `src/MoleHill.Core/Grading/MeshArtifactCleaner.cs`

## What Has Been Tried

### 1. Stitched-first with validation and remesh fallback

Implemented:

- `Grade Path` tries stitched grading patches first.
- Rhino-side validation rejects stitched output when:
  - boundary loop count is not exactly one
  - seam-adjacent naked edges remain near the patch seam

Result:

- Correct safety behavior.
- Did not fix stitched path generation itself.

### 2. Open-path stitched patch rewrite

Implemented:

- structured open-path patch with explicit square end caps
- daylight seam extension at path ends
- unified structured patch instead of fragile strip merging

Result:

- fixed earlier uncapped open-end hole cases
- did not solve the broader stitched seam/topology instability on the failing coarse case

### 3. Section-status and shallow-grade handling

Implemented:

- section status diagnostics (`daylight`, `cap`, `no-grade`, `blocked`, `unresolved`)
- repair of short unresolved runs
- near-road daylight treated as `NoGradeNeeded` instead of unresolved skip

Result:

- improved continuity and diagnostics
- did not solve the current stitched loop explosion

### 4. Remesh fallback ladder expansion

Implemented in sequence:

- full combined constraints
- simplified path constraints
- simplified path constraints without persistent hard constraints in remesh
- corridor-focused reduced-seed fallback
- road-edge-only reduced-seed fallback
- substrate densification fallback
- localized substrate densification fallback

Result:

- Before the tolerance fix, all of these still failed on the coarse case.
- After the tolerance fix, the `road-edge fallback` now succeeds directly, so the later substrate fallbacks are no longer the primary rescue path for this case.

### 5. Stitched-result repair attempt

Implemented:

- keep only the largest connected stitched component before giving up

Result:

- no benefit on the failing case
- diagnostics showed:
  - `components 1`
  - high boundary-edge count
- conclusion: stitched output is topologically wrong as one connected component, not just polluted by detached scraps

### 6. Tolerance-driven investigation

Observed behavior:

- Setting terrain/global tolerance to `0` made `Grade Path` succeed much more reliably.
- With tolerance effectively minimized, the `road-edge fallback` succeeded on a denser upstream mesh and later also on the original coarse case.

Conclusion:

- `Grade Path` topology work was too sensitive to the terrain/global tolerance.
- The fallback path was over-snapping or over-deduping its constraints and/or remesh preparation when using the inherited terrain tolerance.

### 7. Path-specific tolerance clamp

Implemented:

- `GetGradePathGeometryTolerance(...)` in `TerrainBuildService`
- clamped path geometry/remesh tolerance to `1e-6 .. 1e-3`
- threaded this tighter tolerance into:
  - path input resolution
  - path constraint creation
  - Grade Path remesh fallback calls
  - Grade Path localized fallback calls

Result:

- This was the meaningful fix.
- The previously failing coarse case now succeeds through `Grade Path (road-edge fallback)` without needing a user-side global remesh first.

## Important Observations from Logs

### Coarse mesh without the tolerance fix

Typical failure pattern:

- stitched path rejected with `16 boundary loops`
- all remesh fallbacks report:
  - `constraints could not be preserved`
  - `Constraints could not be enforced. Using plain Delaunay.`
- final result stayed on the original topology

### Coarse mesh with the tolerance fix

Observed:

- stitched path still rejected
- `road-edge fallback` now succeeds
- result:
  - `25 verts / 41 faces -> 452 verts / 854 faces`
- runtime remains reasonable:
  - `Grade Path Topology: 0.12 s`
  - `Grade Path: 0.22 s`

### Remesh-before-Grading case

Observed:

- user-side `Remesh` first creates a much denser terrain:
  - about `9,572 verts / 18,835 faces`
- `Grade Path (road-edge fallback)` succeeds there as well
- but this is not the desired long-term workflow because it changes far more terrain topology than needed

Conclusion:

- `Grade Path` only needs a denser local road corridor substrate, not a whole-terrain remesh

## What Needs to Be Tried Next

### Priority 1: Fix stitched path mode properly

This is the main remaining issue.

Specifically:

- Add a patch-level integrity gate inside `PathGrader.TryBuildStructuredPathPatchMesh(...)`
  before the structured patch is accepted.
- If the structured patch does not produce:
  - one valid stitch boundary loop, and
  - a full seam match against the outside mesh,
  then it should fall through to the alternate triangulated patch builder instead of poisoning the whole stitched result.

Reason:

- Right now the structured open-path patch is probably being accepted too early.
- Rhino only sees the failure later, after stitched assembly is already bad.

### Priority 2: Compare path seam merge against pad seam merge directly

Pad stitching is stricter and more successful.

Path should adopt the same local acceptance logic used by pad:

- verify patch boundary loop exists
- build `SeamGraph`
- require full segment match before merge
- reject immediately if seam integrity is already bad

Reason:

- Pad already does this in-core before final merge.
- Path currently records stitch diagnostics, but does not gate early enough at the patch level.

### Priority 3: Reduce fallback topology spread

Now that the fallback works, the next refinement is limiting how much topology it touches.

Try:

- bias `road-edge fallback` to the local corridor split path first when coarse input is detected
- keep whole-mesh fallback only as the final safety net

Reason:

- the current successful `road-edge fallback` still rebuilds a larger region than ideal
- the user explicitly does not want Grade Path to affect more terrain than necessary

### Priority 4: Add diagnostics that explain why stitched mode failed

Current stitched diagnostics are still too high-level at the Rhino level.

Add explicit counts for:

- patch boundary loop count
- seam graph patch segment match count
- outside seam segment match count
- merged seam-adjacent naked edge count
- whether the structured patch or alternate triangulated patch was selected

Reason:

- this will make future stitched failures debuggable without reading code

## What Should Not Be Done Next

- Do not rely on telling users to set terrain tolerance to `0`.
- Do not require a user-side full `Remesh` before `Grade Path`.
- Do not remove the successful `road-edge fallback`; it is currently the safety net that prevents the old "no shoulders added" regression.
- Do not keep expanding the remesh fallback ladder unless a new failure mode appears. The tolerance issue was the real blocker for the current ladder.

## Recommended Forward Plan

1. Keep the current `road-edge fallback` and path-specific tolerance clamp in place.
2. Move effort back to stitched path generation, specifically the structured open-path patch validation and seam acceptance rules.
3. Make path patch acceptance as strict and local as pad patch acceptance.
4. Once stitched mode is stable enough, reduce how often fallback is needed.
5. After that, localize the fallback topology edits more aggressively so fallback only rebuilds a corridor-sized region.

## Practical Success Criteria

The next round of work should be considered successful when all of the following are true:

- stitched path succeeds on the current coarse test case, or fails early and cleanly without producing a broken merged mesh
- fallback remains capable of inserting shoulders on coarse terrain
- Grade Path does not require a user-side full `Remesh` to work
- fallback topology changes are constrained to a local corridor-sized region as much as practical
- logs clearly identify which stitched patch path was attempted and exactly why it was rejected

## Validation Note

This note records observed behavior from live Rhino logs and code changes made during investigation. Full local `dotnet build` / `dotnet test` validation was not executed inside the sandbox environment because NuGet restore is blocked there.
