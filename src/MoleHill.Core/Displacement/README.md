# MoleHill.Core/Displacement

The pure half of the Rhino **Displace** modifier: vertical offsets from procedural noise or a greyscale
image, added to the incoming terrain. Host-free and unit-tested; the image is decoded Rhino-side
(`Services/Import/HeightmapImageLoader`) and arrives here as a float grid. See `docs/architecture.md` →
"Core + Rhino: displacement".

**Every pattern is a function of world XY**, through a `DisplacementPlacement` (origin + rotation), never
of vertex indices or UVs. That is what makes a Displace stackable and stable under a Remesh or Retopo
below it — the same contract as Sculpt's field.

Key files:
- `GradientNoise.cs` — 2D Perlin gradient noise (`Sample`, zero on every lattice point, within ±1) and its
  fractal sum (`Fractal`, up to `MaxOctaves`, normalized by the total weight so it stays within ±1). The
  lattice hash is the MurmurHash3 64-bit finalizer over integer-mixed coordinates and the gradients are a
  fixed table of eight unit vectors: no `System.Random`, no transcendental function, so a seed gives
  bit-identical ground on every machine and runtime. `DisplacementSourceTests` pins golden values; change
  them only with a deliberate change of pattern (it changes every saved noise terrain).
- `DisplacementPlacement.cs` — origin and counter-clockwise rotation (a true angle), `ToLocal`/`ToWorld`.
  Its default value is a valid unrotated placement at the world origin.
- `IDisplacementSource.cs` — `Offset(x, y)` plus `DetailSize`, the finest plan distance the source means to
  show (a noise bump, an image pixel), which the stage's sampling check compares the mesh against.
- `NoiseDisplacementSource.cs` — noise at a plan `Size` per coarsest bump; offsets within ±Height/2.
- `HeightmapImage.cs` — the decoded image (0–1, row 0 at the top) and its bilinear `Sample(s, t, wrap)`,
  with pixel centres half a pixel in and `t` measured from the bottom so the image reads upright in plan.
  `DisplacementWrap`: Repeat, Clamp (edge pixels carry on) or Once (the caller leaves the outside alone).
- `ImageDisplacementSource.cs` — the image as one tile of a stated width and height with its lower-left
  corner at the origin; offset = (value − Neutral) × Height, zero outside the tile in Once mode.
- `MappingRectangle.cs` — reads a drawn closed polyline as the image tile: start point = image bottom-left,
  bottom edge counter-clockwise from it (so the drawing direction never mirrors the image), side lengths =
  tile size, bottom bearing = rotation. Refuses, with a reason, anything not level, not four square corners,
  or not starting at a corner.
- `TerrainDisplacer.cs` — applies a source to an `IndexedTriMesh`, Z only. Boundaries scope it (nothing
  outside moves; inside, the offset rises from zero at the drawn line to full strength at the feather).
  Walls (faces at or above 70°), hold lines and, optionally, the border keep their height exactly, and the
  offset fades back in over the feather from them, through `Grading/HeldLines` — the hold and fade Round
  Creases uses, shared rather than copied. Returns the moved/held counts, the offset range, the plan extent
  reached and the median plan length of the edges reached: the resolution the pattern was sampled at.
