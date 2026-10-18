# MoleHill.Core/Processing

Input preparation before triangulation — cleaning, deduplicating, and discretizing raw points/curves.
Pure, unit-tested.

`TerrainCurveRecovery.Breaklines.cs` prepares a scan reference with measured wall rails before recovering
its curves: original borders remain, and coarse averages on a rail are excluded instead of becoming
conflicting heights along it. `FixedBreaklines` on recovery options holds the same lines in every
rebuild and landform trial. The root `TerrainCurveRecovery.cs` partial owns recovery and simplification.
`ITerrainRecoveryRegion` scopes deviation and spot selection. `.Region.cs` clips each contour and picks
the fragment nearest its selected error; `.Fitting.cs` fits light curves before rebuilding, checking
the region and reference height and retaining an unfitted fallback when necessary. Ordinary terrain
recovery keeps its existing full-terrain polyline behavior unless these options are supplied.

- `PointMerge.cs` / `PointMergeMode.cs` — ordered merge targets without removing points: center,
  first/last, or transitive 3D proximity clusters. Uses flat XYZ arrays and compensated centers;
  pairwise checks are intended for manually selected sets. The Rhino host applies targets to grips.

- `PointCloudProcessor.cs` — merges spot points + breakline vertices with Z-aware spatial-hash dedup
  (breakline-to-breakline merges require XY **and** Z proximity, preserving parallel retaining walls).
  Occupied cells use one shared linked-index store instead of one allocated list per cell, keeping sparse
  multi-million-point surveys fast and memory-bounded.
- `BreaklineDiscretizer.cs` — polylines → flat vertex array + segment index pairs.
- `TerrainConstraintPreprocessor.cs` — conditions breakline and contour spacing before meshing using
  observed source-segment medians. Segment lengths are collected once per source class and straight
  runs are detected in linear time, so multi-million-station polylines retain the panel's spacing
  normalization without a quadratic preprocessing stall. Breaklines are then stationed to their
  **clearance** — the plan distance to the nearest contour, spot sample or other breakline vertex — so a
  sparse breakline between dense contours no longer becomes a few hub vertices fanning into slivers
  under 1°, while a breakline in a void (large clearance) gains nothing. Contours are never restationed.
  `ProcessSeparately` returns the two classes aligned with their inputs, and **whatever persists a line
  as a constraint must persist this processed form**, not the raw one: a later constrained rebuild seeds
  every mesh vertex, and a raw long segment passing within rounding of the stations leaves a zero-area
  cap at each one (~11,000 on one terrain, which downstream cleanup then deleted into holes).
- `TinInputCleaner.cs` — input topology repair: degenerate/duplicate segments, tiny spikes, crossings split
  at one shared vertex, and a count of crossings whose lines disagree in elevation. Triangulate and Add
  Geometry's rebuild run it on every build with `collapseCollinearVertices: false`; collinear collapse
  (which would delete deliberate breakline stations) is used only by the failure retry.
- `RegionInputClipper.cs` — exact union clipping for Data Clip: keeps points inside/on closed World-XY
  loops and splits crossing polylines while linearly interpolating Z. The Rhino host applies it to raw
  Triangulate and Add Geometry sources, never to the carried mesh.
- `RegionInputFilter.cs` — legacy margin-capable point-region helper retained for compiled callers.
- `SurfaceConformer.cs` — Z-only blend from an incoming terrain to a target 2.5D mesh. Optional closed
  loops use even-odd nesting for islands and donut holes, with a smooth feather contained inside every
  boundary edge (or, with no loops, inside the target footprint's edge). Wall-face vertices are left
  in place.
- `SurfaceDeviationEvaluator.cs` — indexed, streaming XY-overlay comparison for two 2.5D triangle
  meshes. It certifies maximum vertical deviation at overlay vertices, verifies domain area in both
  directions (including holes/islands), records a worst-error witness, and supports cancellation.
  Degeneracy uses an adaptive robust orientation predicate rather than treating model tolerance as a
  minimum face width, so narrow but valid grading-fan triangles remain supported.
- `SurfaceConstraintEdgeResolver.cs` — maps persistent constraint polylines onto actual mesh edges
  with indexed XY lookup and elevation-coherence checks, keeping reusable geometry validation out of
  the Rhino host.
- `SurfaceSimplifier.cs` — constrained surface reduction by certified maximum deviation or deterministic
  target vertex count. It protects every boundary
  and required-chain vertex/edge, refines from certified overlay-error witnesses and source errors,
  and returns the unchanged input with an explicit reason when it cannot prove a smaller result.
- `CurveDrape.cs` — the drape kernel behind `mhDrapeCurve` / `mhDrapeCurveSection`, Rhino-free: geometry
  arrives as a point-at-distance callback and a terrain probe. Stations every spacing plus the source's
  corners; each change between hit and miss is bisected so a run ends on the terrain's edge;
  `DrapeMissPolicy` splits, bridges (linear Z between hits; an end gap is dropped) or keeps the curve's Z;
  a `TieInLength` makes it a section drape whose ends stay on the curve and ramp in. `DrapeEditPoints` is the
  loose drape: only the given edit points move (Bridge grades a gap between hits, an end gap keeps its Z).
- `TerrainCurveRecovery.cs` — B25, terrain to editable input: simplified border loops, spot points on
  landform-scale peaks and pits, then a greedy loop that rebuilds a TIN from what is chosen, finds the
  terrain vertex it misses by most and adds the contour through it (or a spot point when the contour
  collapses), until within the vertical tolerance or out of budget. Deviation is vertex-sampled. Before
  contours it offers ridge and valley lines (`ExtractLandformLines`: priority-flood filling, then steepest-descent
  flow accumulation on the terrain and on its inverse) and keeps each only if it earns its length. Several
  contours are traced per round on larger terrains, and contours are simplified against the terrain under
  each chord so they stay within the vertical tolerance and do not cross.
- `ZonePriorityResolver.cs` — deterministic boundary ordering before a shared area split, using zone
  stack index, source boundary index, input elevation, and elevation-priority flag. Stack-priority zones
  separate elevation-sorted runs, avoiding the old mixed-mode comparator cycle. Rhino's zone stage calls
  this Core resolver; GH zone metadata and resolver use remain B7c work.
