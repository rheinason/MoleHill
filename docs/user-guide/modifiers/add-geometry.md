# Add Geometry

Adds extra **points, breaklines and contours** to the terrain part-way up the stack, without changing
Triangulate. Use it to inject design detail after other cards have run, for example spot heights or a
kerb line that should only exist from this point on.

## Settings

| Setting | Meaning |
|---|---|
| **Points** | Points that become terrain vertices at their own height |
| **Breaklines** | Curves whose edges the mesh keeps. Each vertex sets the height along the edge |
| **Contours** | Contour curves. Their vertices set terrain height |
| **Peel Border** | The same border-peeling controls as [Triangulate](triangulate.md#peel-border): **Enabled**, **Max Edge**, **Max Angle**, **Slope Limit** |

## Behaviour

- With no geometry selected the card shows *Not applied — no geometry selected* and is skipped.
- It works on the incoming mesh, so geometry added here sits on top of what earlier cards produced.

## Related

[Triangulate](triangulate.md)
