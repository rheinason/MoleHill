# Retopo

Replaces the terrain with a **quad-dominant mesh** whose edges flow along creases and breaklines. It's
useful when you want a clean, regular mesh for modelling, rendering or export. Retopo can be placed
anywhere in the stack. Retaining walls pass through untouched, and the result has no holes.

## Settings

| Setting | Meaning |
|---|---|
| **Breaklines** | Curves (road edges, ridges) the quad flow follows, in addition to creases detected from the mesh and the mesh border |
| **Quads** | On: build the quad-dominant mesh. Off: keep the input mesh and only preview the flow field |
| **Show Field** | Draw the computed cross field as small crossed segments, coloured by direction, so you can check that the flow follows your creases and breaklines |
| **Crease Angle** | Align the flow to interior creases that fold by at least this many degrees. About 20–35° catches batter toes; `0` aligns to the mesh border and breaklines only |
| **Edge Length** | Target quad size. `0` derives it from the mesh's extent and density. Previews run at twice this size |

## Tips

- Turn **Show Field** on first, with **Quads** off, to confirm the flow is right before generating quads.
- Quads that follow creases give clean batters; a larger Edge Length gives a lighter mesh.

## Related

[Remesh](remesh.md)
