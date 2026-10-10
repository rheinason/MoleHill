# Flow Arrows

Draws **downhill arrows on a regular grid** across the terrain, each labelled with the slope. They show
at a glance which way water will run.

## Settings

| Setting | Meaning |
|---|---|
| **Sources** | Optional closed boundary curves limiting where arrows are placed. Leave empty to cover the whole terrain |
| **Grid Spacing** | Spacing of the sampling grid. Smaller spacing gives more arrows |
| **Units** | Show slopes as percent, promille, ratio or degrees |
| **Flip Arrow** | Rotate arrows 180° so they point **uphill** instead of downhill |
| **Decimals** | Decimal places on the slope labels |
| Block settings | Other [block settings](README.md#block-annotation-settings) |

## Results

- **Arrows**: how many were placed.
- **Min / Avg / Max**: slope magnitudes sampled across the grid.

## Related

[Slope](../analysis/slope.md) · [Waterflow from Points](../analysis/waterflow.md) · [Catchments](../analysis/catchments.md)
