# Contours

Draws **contour lines** at a fixed interval, with major (index) contours and optional elevation labels.

## Settings

| Setting | Meaning |
|---|---|
| **Interval** | Vertical spacing between contour levels |
| **Start Z** | Base elevation offset from which levels are stepped |
| **Major Every Nth** | Every Nth level is a major (index) contour. 5 is the usual survey convention; 1 makes every contour major |
| **Split Major / Minor** | Send major and minor contours to separate `Contours::Major` and `Contours::Minor` sublayers, so print width and linetype are controlled per layer in Rhino |
| **Color** | Colour for the contour curves. Clear to take the colour from the layer |
| **Label Contours** | Place elevation text along the contours |
| **Label Interval** | Spacing between repeated labels along each contour. `0` places one label per contour curve |
| **Label Every Nth** | Label only every Nth level (index contours). 1 labels every level |
| **Label Decimals** | Decimal places in the labels |

## Results

- **Curves**: the number of curves and the number of levels.
- **Levels**: the first and last contour elevations.

## Tips

- Set **Interval** to suit the drawing scale, **Major Every Nth** to 5, and **Label Every Nth** to 5 to label
  just the index contours.
- With **Split Major / Minor**, give the major layer a heavier print width in the layer template.
- Contours update quickly: changing the interval or labels doesn't need a full rebuild, and contours
  follow sculpt strokes live.
- Contour curves are real Rhino curves once baked. You can also feed them back as the **Contours** source of
  a [Triangulate](../modifiers/triangulate.md) card.

## Related

[Elevation](../analysis/elevation.md) · [Spot Heights (Curve)](spot-heights-curve.md)
