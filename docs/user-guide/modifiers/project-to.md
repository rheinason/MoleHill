# Project To

Blends the terrain's heights toward a **target** mesh or another MoleHill terrain. Use it to match a
proposed surface to a design model, to pull existing ground toward a reference, or to tie a terrain into a
neighbour.

## Settings

| Setting | Meaning |
|---|---|
| **Target Mesh** | One Rhino mesh to project toward. Assigning it clears the terrain target |
| **Target terrain** | Choose another MoleHill terrain in the document as the target instead of a mesh |
| **Boundaries** | Closed curves limiting the effect to the terrain inside them. Nested loops alternate between included and excluded areas, so an inner loop makes a donut hole. Leave empty to affect the whole terrain |
| **Strength** | How far each covered vertex moves toward the target elevation. `1` lands on the target; `0.5` goes half way |
| **Feather** | Distance inside every boundary edge over which the effect rises smoothly from zero to full strength |

With no target the card shows *Not applied — no target selected*.

## Tips

- Use a Strength below 1 to soften a change rather than replace the surface.
- Add a Feather so the edge of the effect doesn't leave a step.
- Use Boundaries to restrict the projection to a single site.

## Related

[Sculpt](sculpt.md) · [Grade Pad](grade-pad.md)
