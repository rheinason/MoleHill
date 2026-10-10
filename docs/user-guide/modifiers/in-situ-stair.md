# In-Situ Stair

Generates **stair solids** on a walkable reference surface. You supply a surface that represents the
walkable slope; MoleHill derives the tread depth from your riser height and builds the steps.

## Settings

| Setting | Meaning |
|---|---|
| **Reference** | The walkable surfaces: meshes, surfaces, polysurfaces or extrusions |
| **Riser Height** | Vertical rise per step. Tread depth is derived from the surface and this riser |
| **Min Tread Depth** | Minimum acceptable derived tread depth. Values above `0` colour undersized stair solids bright red; `0` disables the warning |
| **Max Distance** | Maximum reach away from the stair. `0` is unlimited |
| **Show Tread Labels** | Show one viewport tread-depth label per interpreted stair surface |
| **Tread Depth** | *Read-only.* Derived horizontal tread depth across the surfaces |
| **Step Count** | *Read-only.* Generated tread count across the surfaces |

The two read-only values populate after a build ("build to compute" until then). With no reference
surface the card shows *Not applied — no reference surface selected*.

## Related

[Grade Pad](grade-pad.md) · [Grade Line](grade-line.md)
