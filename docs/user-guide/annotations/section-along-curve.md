# Section Along Curve

Draws an **unrolled profile that follows a curve**, with distance along the curve across the page and
elevation up it. Use it for a long section of a road, path, stream or pipe run.

## Settings

| Setting | Meaning |
|---|---|
| **Sources** | The curve sampled along its length. Terrain elevation is read at each sample |
| **Sample Interval** | Distance between elevation samples along the curve |
| **Show Baseline** | Draw the horizontal baseline (zero-elevation reference) under the profile |
| **Show Elevation Grid** | Draw horizontal grid lines |
| **Elevation Grid Interval** | Vertical spacing of grid lines. `0` is automatic |
| **Show Station Labels** | Print station distances along the baseline |
| **Station Label Interval** | Spacing between station labels. `0` is automatic (about a quarter of the total length) |
| **Vertical Exaggeration**, **Elevation Labels**, **Section Title**, **Plan Labels** | See [Section Cut](section-cut.md#settings) |

The Compare, Profiles and Insertion controls are the same as on [Section Cut](section-cut.md#settings-shared-by-all-sections).

## Tips

For a quick check of a design curve without drawing anything, use the read-only
[curve inspector](../04-commands.md#curve-inspector) instead.

## Related

[Section Cut](section-cut.md) · [Cross-Sections](cross-sections.md)
