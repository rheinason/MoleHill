# Objects

Object cards place Rhino geometry and blocks **on the terrain**. They read ordinary Rhino objects and
layers, move them onto the surface, and keep them on their source layers. Add one from the **Add Object**
menu on the **Objects** tab.

| Card | What it does |
|---|---|
| [Plant](plant.md) | Sets each object's lowest point on the terrain |
| [Orient](orient.md) | Sets objects on the terrain and tilts them to the slope |
| [Scatter](scatter.md) | Scatters a weighted mix of blocks across regions or along curves |

Plant and Orient **move the actual source objects** in your document. MoleHill remembers the move it applied,
so it can adjust the objects when the terrain changes and return them to where they started when the
placement is removed. Scatter works differently: it generates instances that are previewed in the viewport
and become real block instances when you bake.

## Shared settings

Plant, Orient and Scatter share a set of randomisation and offset controls.

| Setting | Meaning |
|---|---|
| **Sources** | Rhino objects and layers whose instances are placed (Plant and Orient) |
| **Rotate Min / Rotate Max** | Range of random rotation, in degrees (0 to 360) |
| **Scale Min / Scale Max** | Range of random uniform scale (about 0.25 to 2) |
| **Seed** | Stable random seed. The same seed gives the same arrangement; change it to reshuffle |
| **Z Offset** | Lift or sink placed objects along their up axis |
| **Bindings** | Read-only count of explicit picks plus watched layers driving the card |
