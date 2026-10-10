# Grade Path

Grades a **road, path or track corridor** along a centerline, with batters either side out to daylight. The
centerline's Z defines the finished road profile.

## What you need

- A **centerline curve** whose Z values give the finished elevation profile.
- Optionally, curves for the **edges** if the width varies.

## Settings

| Setting | Meaning |
|---|---|
| **Centerlines** | Path curves (objects or layers). Curve Z defines the finished road profile |
| **Width** | Finished path width: the flat or controlled-width core before side grading starts |
| **Variable Width** | Off: the corridor keeps the constant Width. On: nearby plan curves take over each side, and Width is the fallback where no edge is matched |
| **Width Edges** | *Variable Width only.* Roughly parallel plan curves. Each is matched uniquely to one side of a centerline; its Z is ignored and taken from the centerline |
| **Edge Match Distance** | *Variable Width only.* Maximum plan distance for matching an edge to a centerline. `0` uses an automatic four times the Width |
| **Fill Slope** | Slope where terrain is below the road. The main slope; cut inherits it unless overridden |
| **Cut Slope** | Optional override where terrain is above the road |
| **Max Distance** | Maximum reach away from the path. `0` is unlimited |
| **Grade Through Breaklines** | Off: the path stops at breaklines and graded edges from earlier cards. On: it regrades across them |

## Typical use

1. Draw the centerline as a 3D curve with the road's vertical profile. `mhSlopeCurveSection` and
   `mhInspectCurve` help set and check grades.
2. Add a Grade Path card and assign the curve.
3. Set Width and the batter slopes.
4. For a road that widens (a junction or a passing bay), switch on **Variable Width** and assign the edge
   curves.

## Tips

- A path whose centerline crosses itself or another path can be graded by several cards; cards later in the
  stack respect the earlier graded edges unless Grade Through Breaklines is on.
- Select the same centerline in a Sculpt card's **Protect** field to keep sculpting off the finished road.

## Related

[Grade Pad](grade-pad.md) · [Grade Line](grade-line.md) · [Gradient Compliance](../analysis/gradient-compliance.md)
