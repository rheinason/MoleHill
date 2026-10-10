# Annotations

Annotation cards **describe** the terrain by drawing it: contour lines, labels, sections, tables. They
produce real drawing geometry (curves, text, block instances, hatches) on the terrain's output layers.
Add them from the **Add Annotation** menu on the **Annotation** tab. (Cards that *measure* the terrain are
[analyses](../analysis/README.md).)

| Card | Draws |
|---|---|
| [Contours](contours.md) | Contour lines with major/minor levels and labels |
| [Spot Heights (Curve)](spot-heights-curve.md) | Elevation labels at stations along curves |
| [Spot Slope (Curve)](spot-slope-curve.md) | Slope labels along curves |
| [Spot Heights (Points)](spot-heights-points.md) | Elevation labels at points |
| [Spot Slope (Points)](spot-slope-points.md) | Slope labels at points |
| [Flow Arrows](flow-arrows.md) | Downhill arrows on a grid |
| [Grade Callout](grade-callout.md) | Grade between two points |
| [Section Cut](section-cut.md) | A section along a cut line |
| [Cross-Sections](cross-sections.md) | Unrolled cross-sections at stations |
| [Section Along Curve](section-along-curve.md) | A profile that follows a curve |
| [Report Table](report-table.md) | Measured quantities as a table |
| [Legend](legend.md) | A key to the colours on the terrain |

## How annotations behave

- **The checkbox is the only visibility control.** Annotations have no tab-level visibility switch.
  Turn a card off to hide its drawing.
- **Text follows the annotation style.** At the top of the Annotation tab, **Text Style** names the Rhino
  annotation style used for every label, section and table, with its text height and font. Change the
  style (or **Edit…** it in Rhino's Document Properties) and everything redraws. Height and alignment
  always come from the style, so preview and bake agree.
- **Drawn live, baked on demand.** Annotations are produced by the final build and shown in the viewport.
  **Bake** makes them real objects.
- **Colour.** Colour fields override the layer colour; *Clear* returns to the layer.
- **Block annotations.** Spot-height, spot-slope and callout cards place a **block** at each label. See
  [below](#block-annotation-settings).
- **Results area.** Each card shows what the last build produced, for example *12 curve(s) across 8 levels*.
  *Rebuild required* means nothing has been generated yet.

## Block annotation settings

Spot Heights, Spot Slope and Flow Arrows cards that place blocks share these settings:

| Setting | Meaning |
|---|---|
| **Block** | The block drawn at each label. Leave the built-in block, pick any block in the document, or copy the built-in one to make your own and edit it in Rhino's block editor |
| **Decimals** | Decimal places shown in the value |
| **Prefix / Suffix** | Text before and after the value in the block's DISPLAY attribute |
| **Block Scale** | Scale factor for inserted blocks |
| **Color** | Colour for the blocks. Clear to follow the layer |

Slope annotations also have **Units** (Percent, Promille, Ratio or Degrees) and **Flip Arrow** (rotate
arrows 180° to match other office conventions). The slope unit here is stored with the drawing, unlike the
personal Slope Units preference used for typing.

## Sections

[Section Cut](section-cut.md), [Cross-Sections](cross-sections.md) and
[Section Along Curve](section-along-curve.md) share most of their settings. See
[Section settings](section-cut.md#settings-shared-by-all-sections).
