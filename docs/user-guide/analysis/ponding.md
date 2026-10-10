# Ponding

Finds the **depressions that would hold standing water** and measures them: depth, volume and wet area.

## Settings

| Setting | Meaning |
|---|---|
| **Colour ramp** | Colours the preview by how deep water stands. Ground that drains is left alone |
| **Ignore Below** | Depressions shallower than this are not reported. Survey surfaces always have millimetre dimples; reporting those makes a useful check one nobody reads |
| **Flat Below** | Ground flatter than this drains as one region. Shared with Catchments; set them alike and the terrain is only routed once |
| **Shorelines** | Draw each pond's edge at the level it overflows at |
| **Shoreline Color** | Colour for shorelines |
| **Spill Points** | Mark where each pond overflows. That's where to cut a channel, so it is usually the first thing you want after finding a pond |
| **Spill Color** | Colour for spill markers |

## Results

- **Standing water**: *None — everywhere drains*, or the number of depressions.
- **Deepest / Volume**: the deepest pond and the total held across all ponds.
- **Wet area**: total water-surface area at the overflow level.

## Tips

Find a pond, switch on **Spill Points**, then grade a channel through the spill point with
[Grade Line](../modifiers/grade-line.md) and watch the pond disappear.

## Related

[Catchments](catchments.md) · [Waterflow from Points](waterflow.md)
