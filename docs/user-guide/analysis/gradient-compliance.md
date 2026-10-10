# Gradient Compliance

Checks **accessible landings and routes** against a gradient standard: level areas must be nearly flat in
every direction, and routes must stay within walk, ramp and cross-slope limits, with landings at suitable
intervals. Colours are verdicts (pass, reported, failed), not a ramp.

## Choosing a standard

| Setting | Meaning |
|---|---|
| **Standard** | The accessibility standard the limits come from. Choosing one **copies its limits into the card**, so a later plug-in update can't change an old project's verdict. Every limit stays editable; an edited standard is marked *(modified)*. Choose **Custom** for your own limits |

Built-in standards:

- **ADA 2010 (US)**: landings and turning spaces no steeper than 1:48; walking surfaces 1:20 running and
  1:48 cross; ramps 1:12; ramp runs rise 760 mm; landings 1525 mm long.
- **Approved Doc M Vol 2 (England)**: landings level to 1:60; approaches less steep than 1:20 with
  1:40 cross-fall; limits on flight length and rise; ramp goings of 10 m at 1:20, 5 m at 1:15 and 2 m at
  1:12 (interpolated); intermediate landings 1.5 m long.

The card's basis line says which standard applies and whether you have edited it. Check the current text of
the standard for your project; MoleHill's limits are a convenience, not a certification.

## Level areas

| Setting | Meaning |
|---|---|
| **Level Areas** | Closed curves around landings, turning spaces and other areas that must be level in every direction. A curve inside another makes a hole |
| **Level Rule** | **Off** ignores level areas. **Report** shows ground over the limit in amber, as information. **Warn** shows it in red, as a failure |
| **Level Limit** | The steepest a level area may be in any direction, and the slope below which a stretch of route counts as a landing. Any slope unit: `1:48`, `2.08%` and `1.19deg` are the same limit |

## Routes

| Setting | Meaning |
|---|---|
| **Routes** | Curves along the centre of accessible routes. Slope along the route is its *running slope*, allowed up to the ramp limit; slope across is the *cross slope*. Drawing direction doesn't matter |
| **Route Width** | Width of the corridor checked along each route, centred on the curve |
| **Route Rule** | **Off**, **Report** (amber) or **Warn** (red). Ramps within the limit show blue either way: allowed, but not a walk |
| **Walk Limit** | The steepest running slope that is still a walk. Steeper, up to the ramp limit, is a ramp |
| **Ramp Limit** | The steepest running slope allowed at all. Steeper fails |
| **Cross Limit** | The steepest a route may fall across its direction of travel |
| **Landing Min** | The shortest level stretch that counts as a landing. Landings are found along the routes, not drawn |
| **Walk Max Rise** | The most a walk may climb between landings. `0` means no limit |
| **Ramp Max Rise** | The most a ramp run may climb between landings. `0` means no limit |
| **Ramp Goings** | A table: the longest ramp run allowed at each gradient. Add or remove rows. Leave empty for no going limit. With **Interpolate** on, gradients between two rows get a going between theirs |
| **Measure Over** | The length the gradient is averaged over, like a level laid on the ground. `0` measures each triangle alone, which fails survey-derived landings a level would pass. Not part of any standard |

The route settings appear only when the Route Rule is on.

## Results

- **Level Result** and **Level Steepest**: how much level-area ground is over the limit, and the steepest
  gradient found.
- **Route Result**: running and cross slope failures in the corridor.
- **Route Ramps**: ground steeper than a walk but within the ramp limit.
- **Route Steepest**: the steepest running and cross slopes.
- **Route Runs**: runs too high or long, landings found (shown teal), and the largest rise.

## Tips

- Draw landings as closed curves and routes as centrelines, then switch both rules to **Warn**.
- Adjust the design (usually with [Grade Pad](../modifiers/grade-pad.md) or
  [Grade Path](../modifiers/grade-path.md)) until the verdict clears.
- If an old survey fails everywhere, raise **Measure Over** to the length of a spirit level.

## Related

[Slope](slope.md) · [Inspect Curve](../04-commands.md#curve-inspector) · [Report Table](../annotations/report-table.md)
