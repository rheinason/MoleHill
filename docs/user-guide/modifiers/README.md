# Modifiers

Modifier cards build and change the terrain mesh. They form a **stack**: the pinned
[Triangulate](triangulate.md) card sits at the bottom and builds the base mesh, and each card above it
works on the *incoming mesh* produced by the one before. Order matters, so drag cards to reorder them.

Use the **Add Modifier** menu to add a card. Each card has an enable checkbox, an editable name and a
one-line summary when collapsed.

| Group | Cards |
|---|---|
| Build the mesh | [Triangulate](triangulate.md), [Add Geometry](add-geometry.md) |
| Clean and reshape the mesh | [Smooth](smooth.md), [Remesh](remesh.md), [Retopo](retopo.md), [Simplify](simplify.md) |
| Edit by hand or by target | [Sculpt](sculpt.md), [Project To](project-to.md) |
| Design earthworks | [Grade Pad](grade-pad.md), [Grade Path](grade-path.md), [Grade Line](grade-line.md), [Retaining Wall](retaining-wall.md), [In-Situ Stair](in-situ-stair.md) |

## Things that apply to many cards

- **Cards that do nothing say why.** A grading card with no curves shows *Not applied — no … selected* and
  is skipped.
- **Grading and breaklines.** Grade Pad, Grade Path, Grade Line and Retaining Wall have **Grade Through
  Breaklines** (walls grade only in their grade mode). Off: breaklines and graded edges from cards that ran
  earlier are lines the grading may not cross. On: the new grading is allowed to regrade across them.
- **Max Distance** on grading cards limits how far the batter reaches. `0` means unlimited.
- **Fill Slope and Cut Slope.** *Fill* applies where existing ground is *below* the design (you add soil);
  *Cut* applies where ground is *above* it (you remove soil). Cut inherits Fill unless you set it.
  Steeper slopes finish sooner; flatter slopes spread farther. Type any slope unit: `1:3`, `33%`, `18deg`.
- **Heights come from the curves.** On grading cards the design curve's Z is the finished elevation.
- **Reordering is cheap.** Cards are cached individually; moving or renaming a card does not rebuild the
  cards that did not change.
