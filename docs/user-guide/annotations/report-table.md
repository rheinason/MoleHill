# Report Table

Draws the **measured quantities of the terrain as a table** on your drawing: terrain overview, a zone
schedule, earthworks, ponding, catchments and gradient compliance. Because it's live, the figures on the
sheet can't be left over from an earlier design.

## Settings

| Setting | Meaning |
|---|---|
| **Terrain** | Terrain name, the date the figures were measured, surface area and elevation range |
| **Zone Schedule** | Area, levels, slope and earthwork per [zone](../zones.md), with a totals row |
| **Earthworks** | Cut, fill and net volume from each [Earthworks](../analysis/earthworks.md) analysis |
| **Ponding** | Pond count, impounded volume, depth and water area from each [Ponding](../analysis/ponding.md) analysis |
| **Catchments** | Basin and closed-depression counts from each [Catchments](../analysis/catchments.md) analysis |
| **Gradient Compliance** | Level area checked, area over the limit and steepest slope from each [Gradient Compliance](../analysis/gradient-compliance.md) analysis, with the standard used |
| **Units** | The slope unit for the drawn slope columns. It's stored with the drawing |
| **Rules** | Draw a rule under each heading row and below each table. Off leaves text only |
| **Column Gap** | Space between columns, as a multiple of the text height |
| **Row Spacing** | Line spacing, as a multiple of the text height |
| **Color** | Override colour for the table. Unset uses the Report Table layer's colour |
| **Insertion** | Where the table's top-left corner goes. **Auto** places it beside the terrain; **Pick** chooses a point |

Sizes are multiples of the annotation style's text height, so the table stays proportioned when you
rescale the style.

## Rules for what it prints

- A quantity that wasn't measured is **blank, never zero**. "No ponding analysis is on" does not read as
  "the terrain holds no water".
- A section that measured nothing isn't drawn at all, so an empty heading never implies an empty result.

The card tells you if nothing is selected to report, or if nothing measures the terrain yet (add a zone
or an analysis). Figures come from the last build, in model units.

## Export

`mhExportTerrainReport` writes the same report as CSV (UTF-8 with BOM, so Excel opens it correctly), in
your Slope Units preference.

## Related

[Zones](../zones.md) · [Earthworks](../analysis/earthworks.md) · [Legend](legend.md)
