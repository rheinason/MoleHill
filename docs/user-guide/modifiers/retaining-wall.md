# Retaining Wall

Makes the terrain honour **wall rails**: curves at the toe (bottom) and top of a wall. Optionally it also
grades the terrain away from the wall, with separate slopes for the lower and upper sides.

## What you need

A pair of rails per wall: one at the **toe** (bottom) and one at the **top**, drawn as open curves with the
correct Z. The `mhCreateWall` command draws one rail and generates the parallel, vertically offset
companion for you.

## Settings

| Setting | Meaning |
|---|---|
| **Wall Curves** | The wall rail curves (objects or layers) |
| **Max Wall Width** | Maximum expected spacing between paired rails. Used to pair rails and clean up the wall |
| **Mode** | **Breaklines only:** the rails are forced into the terrain and nothing else changes. **Grade terrain:** the terrain also batters away from each rail out to daylight. Rail elevations are authoritative in both |
| **Fill Slope** | *Grade terrain only.* Slope where terrain is below the rail |
| **Cut Slope** | *Grade terrain only.* Optional override where terrain is above the rail |
| **Asymmetric Sides** | *Grade terrain only.* Off: both faces use the slopes above. On: the toe side and the top side have their own slopes |
| **Toe Cut Slope**, **Toe Fill Slope** | *Asymmetric.* Slopes below the wall, running away from the lower rail |
| **Top Cut Slope**, **Top Fill Slope** | *Asymmetric.* Slopes above the wall, running away from the upper rail |
| **Max Distance** | *Grade terrain only.* Maximum reach away from a rail. `0` is unlimited |

## Typical use

A wall retaining a pad on one side while daylighting into a bank on the other: use **Grade terrain** and
**Asymmetric Sides** to give each side its own batter.

## Tips

- Rails with rounded and stepped corners are matched automatically, and the generated wall solid follows
  bends cleanly.
- If a graded wall can't be inserted cleanly, MoleHill falls back to a safer insertion; the build log says
  which ran.
- Put walls **below** (earlier than) pads and paths that should respect them.

## Related

[Grade Pad](grade-pad.md) · [Grade Line](grade-line.md) · [Commands: mhCreateWall](../04-commands.md#preparing-input-geometry)
