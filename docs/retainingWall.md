# Retaining Wall Generator

## Overview

The retaining-wall workflow accepts unordered wall-rail curves, pairs them into walls, builds solid wall Breps, and inserts the accepted toe/top rails as hard terrain breaklines.

This is not a daylighting or grading tool. Rail Z values are authoritative: when a wall is accepted, the terrain is forced to the toe/top rail elevations during breakline insertion.

## Public Inputs

| Name | Type | Description |
|---|---|---|
| Mesh (Grasshopper) | Mesh | Triangle terrain mesh to receive accepted wall breaklines |
| Wall Curves | List<Curve> | Unordered open or closed 3D wall rail curves |
| Max Wall Width | double | Maximum expected spacing between paired wall rails |
| Wall Layer (Rhino) | Layer path | Optional output layer for wall Breps |

Removed retaining-wall concepts: `Sharpness`, `Shoulder Width`, daylight outputs, fallback wall meshes, and wall-strip Z grading.

## Outputs

| Name | Type | Description |
|---|---|---|
| Mesh | Mesh | Terrain mesh with accepted wall rails inserted as breaklines |
| Wall Breps | List<Brep> | Solid retaining-wall Breps |
| Pairs | List<Line> | Preview connectors for successful pairs |
| Report | List<string> | Typed info / warning / error diagnostics |

## Pairing And Diagnostics

Curves are paired by conservative mutual nearest matching. `W` below means the wall modifier's Max Wall Width value, while `T` means terrain/model tolerance. Max Wall Width is only a pairing/search limit; cleanup, tessellation, corner proximity, solid generation, and remesh operations use `T`.

- open curves pair only with open curves; closed loops pair only with closed loops
- candidate cost is `meanDistance + 0.5 * iqrDistance + 0.25 * maxDistance`
- both rails must mutually select each other as best candidate
- mean distance must be `<= W`
- interquartile distance must be `<= W`
- max distance must be `<= 1.5W`
- each side's second-best candidate must be at least `1.5x` the best cost

Diagnostics carry a reason enum and curve ids where applicable, including ambiguous pairs, mixed open/closed candidates, self-intersecting rails, sub-tolerance width, mapping rejection, and solid failure.

## Stationing And Corners

Wall width is the minimum synchronized toe/top rail spacing. A pair is rejected when this width is below `max(0.1T, 1e-6)`.

Stations preserve authored polyline vertices. Non-polyline curves are sparsely tessellated using terrain/model tolerance and angle tolerance `5 degrees`.

Station mapping uses local normal projection first and closest-point fallback second. Open mappings are rejected when more than two backward outliers occur or any backward jump exceeds `5%` of the partner rail length.

Open-wall endpoint junctions within terrain/model tolerance use a bounded miter. The extension budget is `max(2 * minWallWidth, 4T)`. If the miter collapses or inverts rail spacing, the corner join is skipped with a warning.

Closed-loop pairs use seam alignment as initialization, then the same station mapping rules; the final wall interval wraps to the first station.

## Wall Solid

The wall solid uses the fast rectangular-section builder:

- each station spans the two rail XY positions
- vertical range is `min(toeZ, topZ)` to `max(toeZ, topZ)`
- open walls get start/end caps
- closed walls wrap and do not get caps
- only valid solid Breps are accepted

If solid generation fails, the entire pair is skipped and no terrain breaklines are inserted for that pair.

## Terrain Insertion

Rhino tries direct topology insertion first. The result is accepted only when the terrain boundary remains safe and wall constraints are represented. If direct insertion fails, the workflow falls back to `SurfaceRemesher.Remesh`.

Grasshopper uses the shared `SurfaceRemesher` breakline insertion path for accepted wall pairs.
