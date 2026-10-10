# Catchments

Finds **which ground drains to which outlet**, dividing the terrain into catchment areas. Catchments are
coloured by category (each basin a different colour), not by a ramp, because a catchment number has no
order. The Legend says so instead of showing a scale.

## Settings

| Setting | Meaning |
|---|---|
| **Flat Below** | Ground flatter than this drains as one region rather than face by face. Raise it if a level pad breaks into slivers; lower it if areas that drain differently are being merged |
| **Merge Below** | Catchments smaller than this share of the terrain are absorbed into the one they spill into. A percentage, so the same setting works on a plot or a quarry. `0` keeps every catchment, which on a survey means hundreds of slivers |
| **Boundaries** | Draw each catchment's boundary as a closed polygon |
| **Boundary Color** | Colour for the boundaries |
| **Flow Paths** | Draw each catchment's longest flow path, from its high point to its outlet |
| **Path Color** | Colour for the flow paths |

## Results

- **Catchments**: how many were resolved (after merging) and the largest area.
- **Closed depressions**: catchments with no outlet. Water reaching one stays there, which is usually a
  grading mistake rather than a design. This is shown here even if you have no Ponding card.
- **Drawn**: how many curves were generated.

## Tips

Start with a **Merge Below** of a few percent, then lower it until real sub-catchments appear.

## Related

[Ponding](ponding.md) · [Waterflow from Points](waterflow.md)
