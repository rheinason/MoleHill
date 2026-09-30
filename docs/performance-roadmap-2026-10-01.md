# Performance roadmap (2026-10-01)

Where large-terrain edit performance stands after the incremental-rebuild work
(`incremental-rebuild-design-2026-09-29.md`), what is left, and what each remaining lever is worth. Written to
park the performance track: the numbers below are measured, and each lever lists the traps already found, so
whoever picks it up does not rediscover them.

## Where it stands

Measured with the park probe (`docs/validation-lanes.md`, "park-stress"): a synthetic 4 x 0.8 km park at 1 m
spacing (3.2 million survey points, about 6 million faces through the grading stages), full stack: Triangulate,
Grade Pad, Grade Path, Retaining Wall, Remesh, six analyses and contours. The edit is one survey point raised
by 5 cm, away from every pad and road.

| Milestone | Survey-point edit at 1 m |
|---|---|
| Before the incremental work (2026-09-29) | 144 s (every edit equalled a cold build) |
| Tiled, memoized Remesh (fbef579) | ~94 s |
| Windowed Grade Pad (b102ee1) | 76 s |
| Windowed Grade Path, reach-shaped windows (0084544) | 54 s |
| Cut / Fill fallback through the grid cell (a88728a) | ~44 s |
| Windowed wall rail insertion (e5c7c6c) | 37.5 s |
| Stage meshes normalized in managed code (bdc2842) | **30.6 s** |

Structural edits (an empty card, a rename, a rebuild with nothing changed) cost 0.5 to 1.2 s. The hosted-lane
interactive warm edit at 100k faces is 60 ms, inside the 66 ms input-to-visible target on evaluation time.

Where the 30.6 s goes (1 m, survey-point edit, bdc2842):

| Stage | Time | Main cost |
|---|---|---|
| Analyses | 8.3 s | slope 2.2, catchments 1.8, ponding 1.8, cut/fill 1.1, waterflow 1.1 |
| Grade Path | 6.8 s | constraint setup ~5 s (`PathGrader.CreateConstraints` over the whole mesh) |
| Remesh | 4.5 s | incremental; mostly per-tile keying and the surface hash |
| Grade Pad | 3.8 s | constraint setup ~1.8 s, output mesh 0.7 s |
| Triangulate | 3.1 s | full re-triangulation on any survey edit |
| Retaining Wall | 2.7 s | window overhead and building the Rhino mesh |

Every grading window was reused in that edit. What remains is whole-mesh work around the windows, and the
analyses, which were never made incremental.

## Remaining levers, by expected gain

### 1. Tiled, memoized analyses: ~6-7 s at 1 m

Each analysis recomputes over the whole final mesh on any edit. Slope, aspect and elevation are per face, so
they tile exactly like Remesh (`TiledIsotropicRemesher`): world-anchored tiles, each keyed by a content hash of
its faces, results memoized in `TerrainRuntimeCache`. Cut / Fill is also per face but reads the reference
surface; its tile key must include the reference faces under the tile (Remesh's `SurfaceHasher` does exactly
this). Catchments, ponding and waterflow are **not** local: a change in one place can reroute flow across the
site. They need a different approach, e.g. memoizing the depression-filled surface per tile and recomputing
only the downstream flow graph, and should come after the local ones.

Traps: summary values (areas, volumes) summed from tiles must be summed in a fixed tile order, or they change in
the last bits between cold and incremental runs. Analysis summaries are persisted state (see CLAUDE.md, "A
terrain-level summary is persisted state").

### 2. Constraint setup per window: ~5-6 s at 1 m

`PathGrader.CreateConstraints` / `PadGrader.CreateConstraints` run over the whole mesh on every build, even when
every grading window is reused: a topology validation, the terrain's boundary loop (~1 s at 6M faces), a
`TerrainFaceGrid` over all faces (~0.2 s), and the per-path section work (~1 s per 25 paths). Measured on a
3.2M-face grid: 1.6-2.1 s in total.

Two routes:
- **Cheap:** memoize the constraint set per item by a key of the item plus the faces within its reach (the
  window key machinery already computes this), and reuse it when the key matches.
- **Complete:** compute the constraints inside each window, from the window mesh. The trap: path sections clip at
  the terrain's boundary loop, and a window's rim is not the terrain border. It is only equivalent where the
  window rim lies beyond the section search distance, which the margin guarantees except where the real border is
  within reach.

### 3. Lazy Rhino meshes between stages (the rest of P1): ~2.5 s at 1 m

bdc2842 removed Rhino's normalization from stage outputs. What is left per stage is Rhino filling the mesh (about
0.35 s at 6M faces) and computing normals (~0.13 s). Stages that only read arrays could skip the Rhino mesh
entirely: a `StageMesh` carrying the normalized arrays and building the Rhino mesh on first access. It touches
the stage signatures, the stage cache (which stores and duplicates Rhino meshes), the controller and every stage,
so it is a large, mechanical change for a small gain. Worth doing only as part of a larger pipeline refactor.

Traps, already found:
- Stage arrays are **float-rounded** (the historic read-back went through the single-precision `Point3f`
  indexer). A lazy mesh must keep handing out float-rounded arrays, or every downstream stage sees different
  numbers.
- Only a mesh with consistent winding may skip Rhino (`MeshArrayNormalizer` returns false otherwise).

### 4. Incremental Triangulate: ~2.5-3 s at 1 m, more for contour surveys

Any survey change re-triangulates everything (3.1 s at 1 m for points; ~5.4 s for 1M constrained contour
vertices). `TinEngine` already has a Z-only shortcut and a single-point incremental edit, but the incremental
edit is capped at 250k vertices and does not cover constrained contours. A tiled or locally re-triangulated CDT
would make a moved contour vertex cost in proportion to its neighbourhood. This matters most for contour surveys,
where a contour edit at 1 m costs 29 s today, close to a cold build.

### 5. Smaller items

- Grade Pad falls back to grading the whole terrain on the contour park at 2 m ("a pad's window changed a face it
  shares with the rest"). Find which window does not weld and why.
- `GradingWindows` per-window overhead (face assignment, interface edges, stitch) is ~1-2 s per stage at 6M
  faces. It is O(n) and parallelisable further.
- `DaylightReach` scans every vertex per round; fine for a few pads, quadratic in spirit for many.

## How to measure

- **Hosted lane** (`./validate.ps1 hosted-perf`): small and medium scenes, 5 samples, judged against
  `tests/perf-baselines/hosted-perf.json`. Record a new baseline in the same commit as a deliberate speed change,
  from a run made without a baseline path, on a quiet machine: right after a park run, every stage reads
  20-50 % slow. Single-stage flags that flip between runs are noise; compare minimum times.
- **Park probe** (`ParkScaleStress`): the 1 m rung needs ~18 GB peak. Close the Rhino slot between runs and run
  nothing heavy alongside. Useful request fields: `Spacings`, `SurveyMode: "contours"`, `ContourMode`, `WallMode`,
  `GradeThroughBreaklines`, `RemeshMode`. Each step logs the earthwork to full precision, so a speed change can be
  shown not to move the result, and each windowed stage reports its phase timings and why a window was re-graded.
- **Exactness:** a performance change should leave results bit-identical, or say which results change and why. The
  wall sweep (`WallGradeProbe`, 1,152 cases) compares face counts, loops and batter accuracy case by case.
