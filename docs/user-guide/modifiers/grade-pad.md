# Grade Pad

Creates a **level (or sloping) pad** at the heights of a boundary curve, then **battering** the terrain out
from the pad edge to meet existing ground. It's the standard card for building platforms, car parks, tennis
courts and building pads.

## What you need

- A **closed boundary curve** (or several) whose **Z** values define the finished pad plane.

## Settings

| Setting | Meaning |
|---|---|
| **Boundaries** | Closed curves defining the pad. Z on the curve is the finished level |
| **Fill Slope** | Slope used where existing terrain is **below** the pad. This is the main slope; the cut slope inherits it unless overridden. Flatter slopes extend farther |
| **Cut Slope** | Optional override used where terrain is **above** the pad. Leave blank to use the fill slope |
| **Max Distance** | Maximum grading reach from the pad. `0` is unlimited; smaller values stop the batter sooner |
| **Grade Through Breaklines** | Off: breaklines and graded edges from earlier cards are lines the pad may not cross. On: the pad regrades across them, and the parts it regraded are dropped |

Slopes accept any unit (`1:3`, `33%`, `18deg`).

## Typical use

1. Draw the pad outline as a closed curve at the finished floor level.
2. Add a **Grade Pad** card and assign the curve with **Sel**.
3. Set Fill Slope (for example `1:3`) and, if cuts should be steeper, Cut Slope (for example `1:2`).
4. Add a [Cut / Fill](../analysis/cut-fill.md) or [Earthworks](../analysis/earthworks.md) analysis to see
   the volumes.

## Tips

- Pads at different levels: use one curve per pad, or one card per pad.
- A pad that slopes (for drainage) is just a boundary whose Z varies.
- Remember the order of the stack: a pad graded below a Sculpt card can still be altered by it. Protect the
  pad with a Sculpt Protect curve.

## Related

[Grade Path](grade-path.md) · [Grade Line](grade-line.md) · [Retaining Wall](retaining-wall.md)
