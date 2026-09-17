# Retaining Wall Generator

## Overview

The retaining-wall workflow accepts unordered wall-rail curves, pairs them into walls, builds solid wall Breps, and inserts the accepted toe/top rails as hard terrain breaklines.

**Rail Z values are authoritative in every mode.** When a wall is accepted the terrain is forced to the toe/top rail elevations during breakline insertion, and nothing downstream moves a rail.

## Modes

The modifier has one **Mode** row with two settings. It defaults to `Breaklines only`, so a document saved before grading existed reads back with exactly the result it had.

| Mode | What it does |
|---|---|
| `Breaklines only` (default) | Pairs, solids and breakline insertion, and nothing else. The terrain meets the rails and is otherwise untouched. |
| `Grade terrain` | Everything above, plus a batter running away from each rail out to daylight. |

In `Grade terrain` each rail grades **one way only — away from its partner rail**, so the wall face is never buried and terrain is never pushed through it. That outward direction is not the rail curve's own plan normal, so it is computed from the pairing and handed to the grader explicitly (`RetainingWallGradePlanner` in `src/MoleHill.Shared/`, shared by the Rhino modifier and the Grasshopper component).

The card carries a shared Fill Slope and an optional Cut Slope, and an **Asymmetric Sides** toggle that reveals a cut and a fill override for the toe side and for the top side. A blank override inherits the shared pair, so the symmetric case costs nothing.

There is no per-side "off" switch, for the same reason Grade Line has none: a rail sits at an authored elevation, so its side always resolves to some slope. Where the terrain already meets the rail the batter measures no difference and emits nothing — that is what "this side needs no grading" looks like. A side that should stay a bare face is that side set to the vertical maximum.

Grading reuses the ordinary corridor cascade at width zero (see `docs/architecture.md` → "Core: grading"), so the watertight invariant, the barrier handling and the daylight diagnostics are the same ones Grade Pad and Grade Path use. If grading fails, the accepted breaklines are kept and the failure is reported.

**Grading runs before the rails are inserted, and that ordering is load-bearing.** Insertion forces the terrain to the rail elevations; a batter measured after it therefore starts with zero height difference at its own foot, reports `Flat`, and emits nothing. The failure is silent — the build reports a clean conform and hands back an ungraded mesh — so it was found only by measuring a section across a live 4 m wall. Grading the incoming mesh first also means the wall's own rails are not yet hard constraints, so a rail never becomes a barrier standing on its own batter's foot. Other walls and road edges still act as barriers, exactly as they do for Grade Pad's lock curves.

## Public Inputs

| Name | Type | Description |
|---|---|---|
| Mesh (Grasshopper) | Mesh | Triangle terrain mesh to receive accepted wall breaklines |
| Wall Curves | List<Curve> | Unordered open or closed 3D wall rail curves |
| Max Wall Width | double | Maximum expected spacing between paired wall rails |
| Wall Layer (Rhino) | Layer path | Optional output layer for wall Breps |

Removed retaining-wall concepts: `Sharpness`, `Shoulder Width`, fallback wall meshes, and wall-strip Z grading. Daylighting returned in 2026-09 as the opt-in `Grade terrain` mode above — built on the shared grading cascade rather than the bespoke wall-strip code that was removed.

## Outputs

| Name | Type | Description |
|---|---|---|
| Mesh | Mesh | Terrain mesh with accepted wall rails inserted as breaklines |
| Wall Breps | List<Brep> | Solid retaining-wall Breps |
| Pairs | List<Line> | Preview connectors for successful pairs |
| Report | List<string> | Typed info / warning / error diagnostics |

## Pairing And Diagnostics

Curves are paired by conservative mutual nearest matching. `W` below means the wall modifier's Max Wall Width value, while `T` means terrain/model tolerance. Max Wall Width primarily controls pairing/search; bounded repair also uses `0.05W` as a safety cap. Tessellation, solid generation, and remesh operations use `T`.

- open curves pair only with open curves; closed loops pair only with closed loops
- candidate cost is `meanDistance + 0.5 * iqrDistance + 0.25 * maxDistance`
- both rails must mutually select each other as best candidate
- mean distance must be `<= W`
- interquartile distance must be `<= W`
- max distance must be `<= 1.5W`
- open candidates must map monotonically from one end of each rail to the other in both directions
- each side's second-best candidate must be at least `1.5x` the best cost

Diagnostics carry a reason enum, all related curve ids, and exact focus geometry where applicable,
including ambiguous pairs, mixed open/closed candidates, self-intersecting rails, sub-tolerance width,
mapping rejection, crossing walls, and solid failure.

Self-intersection validation places segment XY bounds into spatial grid buckets and compares only segments
that share a bucket. It uses the exact intersection predicate and adjacency exclusions without the
quadratic all-pairs scan on long rails. A self-intersecting rail that cannot be repaired within the
bounded cleanup limit is rejected because it has no unique ordered path for wall stationing.

Before rejecting an open rail for a self-crossing or non-monotonic station mapping, the planner may
try a bounded repair candidate. Rhino sets the repair limit to
`max(T, min(0.1 * detailSize, 0.05W))`. The candidate preserves both endpoints and global Z extrema,
stays within that 3D deviation, and may remove no more than `2 * repairLimit` of path detour. This
second bound prevents a long collinear retrace from disappearing merely because its points lie on the
replacement chord. The repaired rails must then pass the normal bidirectional station, distance,
width, and solid checks. Already-valid rails always retain their authored geometry. Closed rails are
not auto-repaired.

## Stationing And Corners

Wall width is the minimum synchronized toe/top rail spacing. A pair is rejected when this width is below `max(0.1W, 0.1T, 1e-6)`.

Stations preserve authored polyline vertices on ordinary rails. Open rails above 512 cleaned points are
simplified in 3D within `T` before pairing/solid creation, preserving endpoints and Z/shape to tolerance
while avoiding thousands of redundant loft stations. Non-polyline curves are tessellated using
terrain/model tolerance and angle tolerance `5 degrees`.

For open rails, sampled points are projected to closest partner-rail stations. The station sequence must
be monotonic (allowing a small numerical slack) and cover both ends. This bidirectional gate rejects
partial overlaps, loops, and retracing rails before distance-only pairing can accept them.

Open-wall endpoint junctions within one wall width use a bounded miter only when each participating end
has exactly one candidate (a deterministic degree-two corner). The extension budget is
`max(2 * minWallWidth, 4T)`. XY can be mitered, but authored Z is retained unless both elevations already
agree within `T`. Crossings and multi-way/T junctions are reported and left unresolved.

Closed-loop pairs use seam alignment as initialization, then the same station mapping rules; the final wall interval wraps to the first station.

Crossing diagnostics test finite centerline segments, not their infinite line extensions. A crossing
whose interpolated wall-height ranges are separate is informational (`Walls cross in plan`); a
crossing whose vertical ranges overlap remains a warning (`Walls may overlap`). Both walls stay
enabled, and the diagnostic carries the exact crossing, both centerline segments, and all four rail
ids.

## Wall Solid

The wall solid uses shared rail stations and four continuous side bands:

- each station spans the two rail XY positions
- vertical range is `min(toeZ, topZ)` to `max(toeZ, topZ)`
- open walls get start/end caps
- closed walls wrap and do not get caps
- joined output is accepted only when it is one valid watertight solid
- if Rhino cannot join the surface bands, the same stations build one closed mesh and convert it to a
  Brep; the result is still accepted only when valid and solid

The builder never chooses the largest fragment from a multi-piece join. If solid generation alone fails,
the valid toe/top rails can still be inserted as terrain breaklines and the missing solid is reported.
Invalid rails (self-intersection or station-mapping failure) produce neither a solid nor breaklines.

## Runtime Diagnostics

Rhino translates planner and insertion reports into runtime overlay items owned by the Retaining Wall
modifier. The expanded card shows error/warning counts and a **Show Issues** checkbox. Ambiguous pairs,
invalid stationing, self-intersections, rejected corners/crossings, solid failures, and topology fallbacks
are highlighted directly on the source rails. Successful bounded cleanup and vertically separated plan
crossings are softer information items; unresolved geometry that suppresses output remains a warning.
Visibility is session-only, defaults off, triggers redraw without rebuilding, and never changes JSON,
undo state, fingerprints, synced output, or bake output.

## Terrain Insertion

Rhino tries direct topology insertion first. It splits only terrain faces crossed by the accepted wall
rails, preserving untouched vertices and faces verbatim. The result is accepted only when the terrain
boundary remains safe and every wall constraint is represented; accepted rails then persist as hard
constraints for later modifiers. If direct insertion fails, the workflow falls back to
`SurfaceRemesher.Remesh` over the combined terrain and wall constraint stack.

For retaining-wall fallback, the reduced boundary/guide seed is prepared first. When that reduced seed
passes the remesh acceptance checks, it is returned before attempting the more expensive carried-interior
seed path.

Grasshopper uses the shared `SurfaceRemesher` breakline insertion path for accepted wall pairs.
