# Sculpt

Paint height changes onto the terrain by hand with brushes, like sculpting clay. Use it for organic
landform, mounds, swales and last-minute adjustments that don't justify a design curve.

## How it works

Strokes are stored as a sparse height-offset field in world coordinates, not as changes to particular
vertices. That means a Sculpt card stacks with the others: if you change cards below it, the sculpting
re-applies on top of the new result. You can have several Sculpt cards, and they compose.

## Starting a session

Press **Start** on the card to begin a session. A small floating toolbar appears.

### Brushes

| Brush | What it does |
|---|---|
| **Raise** | Raises terrain. Hold **Ctrl** to lower. Hold **Shift** for a temporary Smooth |
| **Lower** | Lowers terrain. **Ctrl** raises |
| **Erase** | Removes sculpt strokes under the brush, restoring the incoming mesh heights |
| **Smooth** | Smooths heights toward the neighbouring terrain |
| **Flatten** | Levels terrain toward the height under the brush at the start of the stroke |
| **Grab** | Drags the terrain under the brush up or down rigidly |
| **Clay** | Builds terrain up toward a plane just above the surface |
| **Noise** | Adds natural height variation |

### Sliders and keys

| Control | Effect |
|---|---|
| **Radius** | Brush size. Press **F** in the viewport to adjust interactively |
| **Strength** | How strongly each dab acts. Press **Shift+F** in the viewport to adjust interactively |
| **Falloff** | The profile of the brush from centre to rim |
| **Ctrl+Z** | Undo the last stroke |
| **Enter / Esc / Done** | End the session |

Drag the grip on the toolbar to move it. Leaving the session records one document undo step. Radius,
strength and falloff are remembered for the session tool, not stored on the card.

Zones and contours update live during strokes; other outputs (such as labels) update when the stroke
finishes.

## Card settings

| Setting | Meaning |
|---|---|
| **Protect** | Curves that protect terrain from sculpting. A closed curve protects everything inside it; an open curve protects the terrain along it. Selecting the source of an earlier Grade Path protects that path's full width |
| **Feather** | Distance beyond each protected area over which sculpting fades back in. `0` uses the sculpt detail size |
| **Stored Sculpt** | Read-only summary of the strokes stored on the card (tile count and approximate size) |
| **Clear** | Deletes every stroke stored on this card (you are asked to confirm) |

Strokes under a protected area are still stored, so adding or removing a Protect curve later is not
destructive.

## Tips

- Sculpting vertices works best on an even mesh. If the card warns that the incoming mesh is sparse or has
  many sharp triangles, add a [Remesh](remesh.md) card below it.
- Protect your graded roads and pads so they keep their design heights.
- Brush strength and size can both be changed mid-stroke-session without leaving it.

## Related

[Smooth](smooth.md) · [Remesh](remesh.md) · [Grade Path](grade-path.md)
