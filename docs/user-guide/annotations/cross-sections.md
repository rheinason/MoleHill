# Cross-Sections

Draws **unrolled cross-sections at regular stations along an alignment**, arranged in a grid for easy
reading. Standard for roads, paths, channels and any linear design.

## Settings

| Setting | Meaning |
|---|---|
| **Sources** | The alignment curve to sample at regular stations. The first curve resolved is used |
| **Station Interval** | Distance between cross-section stations along the alignment |
| **Cross-Section Width** | Total perpendicular width of each cut, centred on the alignment |
| **Grid Columns** | Number of columns in the unrolled grid layout |
| **Grid Cell Width / Height** | Override the cell size. `0` is automatic |
| **Cut Lines on Terrain** | Draw the perpendicular cut polylines on the terrain at each station |
| **Label Stations** | Print station distance on each cross-section |
| **Show Elevation Grid** | Draw horizontal grid lines on each cross-section |
| **Elevation Grid Interval** | Vertical spacing of grid lines. `0` is automatic |
| **Vertical Exaggeration**, **Elevation Labels**, **Section Title**, **Plan Labels** | See [Section Cut](section-cut.md#settings) |

The Compare, Profiles and Insertion controls are the same as on [Section Cut](section-cut.md#settings-shared-by-all-sections).

## Tips

- A cross-section grid is ideal for a road: pick the centreline, a 20 m interval and a width that covers
  the batters on both sides.
- Show existing against proposed by setting **Compare To Terrain** so cut and fill shade in each section.

## Related

[Section Cut](section-cut.md) · [Grade Path](../modifiers/grade-path.md)
