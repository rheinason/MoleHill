# MoleHill.Core/Sculpting

The 2.5D sculpting engine behind the Rhino **Sculpt** modifier — Blender-style brushes that edit
terrain Z. Pure math (no Rhino), unit-tested. The interactive session/UI lives in
`MoleHill.Rhino/Services/SculptSessionController.cs`; the build-stage replay in
`TerrainBuildService.Sculpt.cs`.

**The durable contract is the displacement field, not the mesh.** Brushes edit working-mesh vertex Z
live (fast path); at stroke end the per-vertex delta (Z − BaseZ) is rasterized into a sparse,
world-XY-keyed field that the build stage replays as `z += field.Sample(x, y) * mask(x, y)` onto whatever mesh
arrives from the modifier stack below. Because the field never references vertex indices, sculpt
modifiers are fully stackable: upstream edits (grade pad changes, re-triangulation) flow through and
the sculpt re-applies verbatim, and multiple sculpt modifiers compose. Consequence (intended):
Smooth/Flatten bake a static delta relative to the base surface *at sculpt time* — like Blender's
Displace, they do not re-smooth a later-changed base.

Constraints are a separate world-XY influence mask shared by the live brush engine, stroke
rasterizer, and build replay. Protected polygons/lines evaluate to zero, then feather smoothly back
to one outside their design footprint. The field retains raw displacement beneath the mask; stroke
commits preserve fully protected samples and divide feathered deltas by the mask before storage, so
replay applies attenuation exactly once.

Key files:
- `SculptDisplacementField.cs` — the sparse field: 64×64-sample tiles anchored at the world origin
  (indices stable forever), bilinear `Sample`, `HasInfluenceNear` (the DynTopo region gate),
  `PruneZeroTiles`, and the FieldVersion-1 codec (`EncodeTile`/`DecodeTile`: little-endian float32 +
  Deflate; base64 wrapping is Rhino-side in `SculptFieldCodec`).
- `SculptBrush.cs` — `SculptBrushKind` (Draw/Subtract/Smooth/Flatten/Grab/Clay/Noise) and
  `SculptFalloffs.Evaluate` (Smooth/Linear/Sharp/Constant profiles).
- `SculptConstraintMask.cs` — pure world-XY protected polygons and buffered open/closed polylines,
  returning a smooth 0–1 sculpt influence through the configured outside feather.
- `SculptBrushEngine.cs` — one session's working state: flat arrays + `BaseZ` (recovered exactly as
  `z − field.Sample * mask`), per-dab brush math with `SpatialHashGrid2D` radius queries and
  `MeshSmoother.BuildNeighborGraph` CSR adjacency for the Smooth brush, first-touch undo capture, and
  DynTopo `RefineRegion` (region-limited `LocalMeshRefiner`, split-only during strokes; topology is
  forward-only within a session — undo re-seats created midpoints on the restored surface, which is
  exact because midpoints lie on the piecewise-linear surface).
- `SculptFieldRasterizer.cs` — stroke commit: interpolates the delta surface (a `TerrainFaceGrid`
  over Z = delta) at every field sample in the stroke's dirty bounds; samples off the mesh stay
  untouched, protected samples retain their prior raw displacement, and feathered values are stored
  without double attenuation.
- `SculptStrokeUndo.cs` — per-stroke undo records (sparse old/new Z, replaced tile payloads, created
  midpoints, dirty bounds) + the linear `SculptUndoStack`.
