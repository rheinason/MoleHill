# Grade Line

Battering away from a **single design line**, which is useful for swales, ditches, embankment crests,
kerb lines and any feature that is an edge rather than a surface. The curve's Z is the finished elevation
along the line.

## Settings

| Setting | Meaning |
|---|---|
| **Design Lines** | Curves (objects or layers). Z is the finished elevation along the line |
| **Fill Slope** | Slope where terrain is below the line. Main slope; cut inherits it unless overridden |
| **Cut Slope** | Optional override where terrain is above the line |
| **Asymmetric Sides** | Off: both sides use the slopes above. On: each side of the line has its own cut and fill slopes |
| **Left Cut Slope**, **Left Fill Slope**, **Right Cut Slope**, **Right Fill Slope** | *Asymmetric Sides only.* Per-side slopes. Blank inherits the shared slope |
| **Max Distance** | Maximum reach away from the line. `0` is unlimited |
| **Grade Through Breaklines** | Off: the grading can't cross breaklines or graded edges from earlier cards. On: it can |

Left and right are taken looking along the curve's direction.

## Typical use

A ditch with a steep back and a flat front: draw the ditch line, switch on **Asymmetric Sides**, and give
the two sides different slopes.

## Related

[Grade Pad](grade-pad.md) · [Grade Path](grade-path.md) · [Retaining Wall](retaining-wall.md)
