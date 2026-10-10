# Earthworks

Measures **cut, fill and net volume** by comparing the finished terrain to a baseline.

## Settings

| Setting | Meaning |
|---|---|
| **Compare To** | An optional reference: Rhino meshes, polysurfaces or extrusions standing for the existing ground. Leave empty to compare against this terrain's own starting mesh (what Triangulate produced), so the result tells you how much your grading cards moved the ground |
| **Boundary** | Optional closed curves limiting where volumes are counted |

You can also compare against another MoleHill terrain in the document.

## Results

The **Summary** field shows, from the last build:

- **Cut**: volume of ground removed.
- **Fill**: volume of ground added.
- **Net**: fill minus cut.
- **Mode**: *Exact*, or *Estimated from terrain delta*.

You can click into the field to select and copy the values.

If nothing could differ (no reference, and no card above Triangulate changes heights), the card tells you
there is nothing to compare.

## Tips

- To report a volume for one area, use **Boundary**, or use [zones](../zones.md); each zone shows its own
  earthwork when an Earthworks analysis is on.
- Simplify does not conserve volume, so a small difference can appear if you simplify.
- For a picture of where cut and fill occur, use [Cut / Fill](cut-fill.md).

## Related

[Cut / Fill](cut-fill.md) · [Report Table](../annotations/report-table.md)
