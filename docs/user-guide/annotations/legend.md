# Legend

Draws a **key to the colours on the terrain**. It describes whichever analysis is currently colouring the
terrain, so the key on the drawing always matches what you see.

## How it works

The Legend has no analysis picker. It keys the first enabled analysis that colours the mesh, because the
terrain shows one colouring at a time. Edit that analysis's ramp and the legend redraws immediately without a rebuild. With nothing
colouring the terrain (colours hidden), the legend is removed rather than left describing colours that
are gone.

What it draws depends on the colouring:

- **Gradient** colouring: a colour strip with round tick values. End swatches are labelled open (`< 5`,
  `≥ 20`) because values past the range take the end colours.
- **Stepped** colouring: one swatch per band.
- **Constant** colouring: one swatch per stop, with the stops as thresholds.
- **Gradient Compliance**: its categories (only those its rules can paint).
- **Catchments**: basins are coloured apart and have no scale, so the card says so.

## Settings

| Setting | Meaning |
|---|---|
| **Title** | Heading above the key. Leave empty to use the analysis's name and unit |
| **Layout** | **Vertical** stacks the key with the highest values at the top; **Horizontal** runs it left to right, for a key along the foot of a sheet |
| **Swatch Size** | Size of each swatch and the width of a gradient strip, as a multiple of the text height |
| **Strip Length** | Length of a gradient strip, as a multiple of the text height. Stepped and threshold colourings draw one swatch per band instead |
| **Color** | Override colour for text and outlines. Unset uses the Legend layer's colour. Swatches always show the analysis's own colours |
| **Insertion** | Top-left corner of the key. **Auto** places it beside the terrain, level with its foot; **Pick** chooses a point |

## Related

[Slope](../analysis/slope.md) · [Elevation](../analysis/elevation.md) · [Cut / Fill](../analysis/cut-fill.md)
