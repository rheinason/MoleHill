# Plant

**Plant** places objects so their **lowest point touches the terrain**. Use it for things that stand on the
ground and shouldn't tilt: trees, bollards, benches, lamp posts, people.

## Settings

| Setting | Meaning |
|---|---|
| **Sources** | The Rhino objects and layers to place |
| **Rotate Min / Max** | Random rotation range (degrees) |
| **Scale Min / Max** | Random uniform scale range |
| **Seed** | Stable random seed for this card |
| **Z Offset** | Lift or sink the objects from the surface |

The objects stay on their original layers. The card moves your source objects, and re-seats them when the
terrain beneath them changes.

## Related

[Orient](orient.md) · [Scatter](scatter.md) · [shared settings](README.md#shared-settings)
