# Smooth

Smooths terrain **heights** while leaving the plan layout (X and Y) alone. Use it to take noise out of
survey data or to soften a sculpted or graded surface.

## Settings

| Setting | Meaning |
|---|---|
| **Boundaries** | Closed curves that limit smoothing to the terrain inside them. Leave empty to smooth the whole terrain |
| **Protect** | Curves that keep their height while the terrain around them is smoothed. They add no mesh edge |
| **Iterations** | How many smoothing passes to run. Scrub for quick changes, or type a larger value than the slider allows |
| **Strength** | How strongly each pass moves vertex heights (usually 0 to 1) |
| **Protect Hold** | How firmly Protect curves hold their height. `1` keeps them exactly; `0` smooths them like any other vertex |

## Tips

- Protect road edges, kerbs and walls so the smoothing doesn't round them off.
- Smoothing works on vertices, so very sparse or badly shaped meshes smooth poorly. If the card warns that
  the incoming mesh is sparse or has many sharp triangles, put a [Remesh](remesh.md) card below it.
- Smooth keeps closed terrain edges in place.
- Several light passes (a low Strength with more Iterations) behave more gently than one heavy pass.

## Related

[Remesh](remesh.md) · [Sculpt](sculpt.md) (its Smooth brush)
