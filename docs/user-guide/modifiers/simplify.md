# Simplify

Reduces the number of vertices with the error **verified**. Use it to lighten very dense survey meshes
before grading or export.

## Mode

| Mode | You set | Notes |
|---|---|---|
| **Maximum deviation** | The largest vertical difference allowed | The whole triangle overlay is verified against the incoming mesh. If no smaller mesh can meet the bound, the incoming mesh is kept |
| **Target vertex count** | A cap on output vertices | Counts include border, breakline and triangulator-inserted vertices. The achieved error is reported but not limited. If mandatory geometry alone exceeds the cap, the incoming mesh is kept with a diagnostic |
| **Retain percentage** | A percentage of the incoming vertices to keep | Converted to a vertex cap (rounded down). `100%` keeps the input unchanged |

## Settings

| Setting | Applies to | Meaning |
|---|---|---|
| **Maximum Deviation** | Maximum deviation | Largest permitted vertical difference from the terrain just before this card |
| **Target Vertices** | Target vertex count | Maximum output vertex count (minimum 3) |
| **Retain** | Retain percentage | Percentage of vertices to keep |

## What it keeps

The mesh border and grading and wall breaklines are kept. Later cards may change the surface, and an
Earthworks analysis may show a small volume difference, because Simplify does not conserve volume.

## Tips

- Prefer **Maximum deviation** when accuracy matters. It guarantees how far the result can stray.
- Place Simplify above your grading cards so grading is done at full resolution and only the finished
  surface is reduced.

## Related

[Remesh](remesh.md)
