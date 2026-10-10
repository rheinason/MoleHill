# Cut / Fill

Colours the terrain by **how far grading moved the ground**: positive where ground was added (fill),
negative where it was removed (cut). Optionally draws the balance line and contours of the depth itself.

## Settings

| Setting | Meaning |
|---|---|
| **Compare To** | Optional reference ground (meshes, polysurfaces or extrusions). Empty: compare against this terrain's own starting mesh |
| **Boundary** | Optional closed curves limiting the region compared |
| **Colour ramp** | Colours the preview by cut and fill depth. The range stays symmetric about zero, so unchanged ground sits mid-ramp |
| **Balance Line** | Draw the line where the delta crosses zero: where cut meets fill |
| **Balance Color** | Colour for the balance line. Clear to follow the layer |
| **Delta Contours** | Draw contours of the depth itself, so "cut deeper than 1 m" is a line rather than a shade |
| **Delta Interval** | Depth between delta contours. Levels step out from zero both ways, so 0.5 draws at ±0.5, ±1.0 and so on |
| **Contour Color** | Colour for the delta contours. Clear to follow the layer |

## Results

- **Cut / Fill / Net**: volumes from the last build.
- **Drawn** (when delta lines are on): the number of delta contours and balance curves. A balance line only
  exists where the delta changes sign, so a site that is all fill has none.

If there is nothing to compare, the card says so. Either give it a reference or add a card that changes
ground heights.

## Related

[Earthworks](earthworks.md) · [Section Cut](../annotations/section-cut.md) (cut/fill hatches on sections)
