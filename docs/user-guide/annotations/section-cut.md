# Section Cut

Draws a **section (profile) along a cut line**. Each cut curve you give it produces one profile showing
the terrain, with optional ticks, grid, labels and shaded cut and fill against existing ground.

## Settings

| Setting | Meaning |
|---|---|
| **Sources** | Curves used as cut lines through the terrain. Each curve produces one profile |
| **Station Tick Interval** | Spacing of station ticks along the baseline. `0` disables ticks |
| **Elevation Grid Interval** | Vertical spacing of horizontal grid lines. `0` is automatic |
| **Show Station Ticks** | Draw tick marks at each station |
| **Show Elevation Grid** | Draw horizontal grid lines |
| **Show Station Labels** | Print station distances below each tick |
| **Vertical Exaggeration** | Vertical scale relative to horizontal. `1` is true shape; `5` or `10` stretches heights so gentle ground reads. Affects the drawing only, never the terrain |
| **Elevation Labels** | Real elevations up the left edge of each section, one per grid step |
| **Section Title** | A title beneath each section: its mark (A-A', B-B', …) or, for cross-sections, its station |
| **Plan Labels** | Mark each section in plan: A and A' at the ends of the cut line, or the station beside each cross-section's cut |

## Settings shared by all sections

Section Cut, [Cross-Sections](cross-sections.md) and [Section Along Curve](section-along-curve.md) share
the Vertical Exaggeration, Elevation Labels, Section Title and Plan Labels settings above, and these
**Compare** and **Layout** controls:

| Setting | Meaning |
|---|---|
| **Compare To Terrain** | Another MoleHill terrain to draw on the section as existing ground and to shade cut and fill against. *None* shades against this terrain's own starting mesh: the ground before any card moved it |
| **Compare To** | Existing ground as Rhino geometry (a survey mesh or surface) sliced along the same cut line. It takes precedence over Compare To Terrain for cut/fill |
| **Cut / Fill** | Shade cut and fill between this terrain and the ground it's compared to |
| **Profiles** | Every other terrain in the document, ticked to draw its profile on the same section. Click a swatch to choose that profile's colour on this card (right-click to go back to the terrain's own colour). The compared terrain is always drawn. Long lists get a filter box |
| **Insertion** | Where the drawing is placed. **Auto** places it beside the terrain. **Pick** lets you choose a point in the viewport |

Cut and fill hatches take their pattern, scale and angle from the layer template (the *Sections ▸ Cut* and
*Fill* roles), so edit them there for consistent sheets. Existing ground is drawn in the colour of its
layer role (*Sections ▸ Existing*). A section with nothing to compare against tells you so.

## Tips

- Draw cut lines as open curves across the site. Each curve produces one profile, marked A-A', B-B' and so on.
- Several sections along different curves stack without overlapping.
- Use Vertical Exaggeration of 2–5 on gentle sites.
- To show existing against proposed: duplicate the terrain, grade the copy, and set the original as
  **Compare To Terrain**.

## Related

[Cross-Sections](cross-sections.md) · [Section Along Curve](section-along-curve.md) · [Cut / Fill](../analysis/cut-fill.md)
