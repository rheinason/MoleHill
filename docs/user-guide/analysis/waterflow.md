# Waterflow from Points

Traces **downhill paths** from points you choose, following the terrain surface. Use it to see where a
drip, a downpipe or a pond overflow would go.

## Settings

| Setting | Meaning |
|---|---|
| **Points** | Point objects or layers used as starting points |
| **Max Length** | Maximum plan length of each path. `0` continues to the terrain edge or a local sink |
| **Color** | Colour for the path curves. Clear to follow the layer |

Without start points the card says it needs points to trace from.

## Results

- **Points / Paths**: how many points were traced and how many paths resulted.
- **Ends**: how the paths ended, as counts of *boundary* (reached the terrain edge), *sink* (stopped in a
  local low point) and *outside* (the start point was not over the terrain).

## Tips

- A path that ends in a **local sink** is a depression water cannot leave. Check it with
  [Ponding](ponding.md).
- Place start points at the high point of an area to see where it drains.

## Related

[Catchments](catchments.md) · [Ponding](ponding.md)
