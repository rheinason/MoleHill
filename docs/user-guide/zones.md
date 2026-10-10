# Zones

A **zone** is a named, coloured region of the terrain. Zones split the terrain mesh along their boundaries,
so each zone gets its own mesh object, colour and set of measured quantities. Use them for lawn, paving,
planting, hardstand or any area you want to colour, itemise or output separately.

Add zones from the **Zones** tab: **Add Zone** (blank), **From Layers…** (pick layers from a list), or
**From Selected Layers** (one zone per layer selected in Rhino's Layers panel).

## Settings

| Setting | Meaning |
|---|---|
| **Name** | Friendly name shown in the panel and on generated objects |
| **Enabled** | Disable a zone without deleting it |
| **Zone Area** | The closed curves and layers that define the zone's area |
| **Priority by elevation** | When on, a zone whose source geometry is **higher** wins where zones overlap. Useful for stacking zones at different heights (a raised platform on flat ground). When off, **later zones win** |
| **Colour** | Taken from the source layer by default. Click the swatch to set an override colour, or **Use Source Layer** to go back |

Drag a zone's handle to reorder. Later zones win overlaps unless Priority by elevation decides it.

## View toggle

The eye button on the **Zones** tab, or the **Terrain / Zones** buttons, switches the viewport between the
whole terrain mesh and the split zone meshes.

## Last build

After a final build each zone shows its measured quantities:

| Row | Meaning |
|---|---|
| **Plan area** | Projected XY area |
| **Surface area** | 3D surface area |
| **Elevation** | Minimum, area-weighted average and maximum height |
| **Slope** | Minimum, area-weighted average and maximum slope |
| **Mesh output** | Resolved mesh output |
| **Earthworks** | Cut, fill and net volume for the zone. Needs an [Earthworks](analysis/earthworks.md) analysis; otherwise it reads *Unavailable* |

A zone with no resolved output reads *Rebuild required* or *No resolved output*. The
[Report Table](annotations/report-table.md) can draw a zone schedule with a totals row, and
`mhExportTerrainReport` exports the same figures as CSV.

## Related

[Report Table](annotations/report-table.md) · [Scatter](objects/scatter.md)
