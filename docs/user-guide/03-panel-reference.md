# The MoleHill panel

Open the panel with the `mhPanel` command. It docks like any Rhino panel and adapts to its width, so it
works in a narrow dock too (tab labels become icons and rarely used commands move into an overflow menu).

## The toolbar

The toolbar sits at the top and always acts on the **active terrain**.

**First row: identity**

| Control | What it does |
|---|---|
| **Terrain name / list** | Rename the active terrain (press Enter), or open the list to switch terrains |
| **New** (+) | Create a new terrain |
| **Duplicate** | Copy the selected terrain, including its cards, with its own layers |
| **Delete** | Delete the selected terrain and its layers |

**Second row: building and output**

| Control | What it does |
|---|---|
| **Rebuild** | Force a rebuild now |
| **Reset Build** | Cancel the running build, clear queued rebuilds and drop cached preview state. Use it if a build seems stuck (same as `mhResetTerrainBuild`) |
| **Live update** | Rebuild automatically when referenced Rhino geometry or layers change |
| **Bake** | Turn the terrain and its generated output into real Rhino objects |
| **Visibility** | Hide or show all generated terrain outputs |
| **Lock** | Lock or unlock MoleHill's live outputs in the document. Source geometry and earlier bakes are unaffected |

A status label reports what the terrain is doing: building, up to date, or blocked and why.

Renaming, duplicating and deleting terrains handle the terrain's layers for you. Don't rename or delete
those layers by hand.

## The five tabs

| Tab | Contents | Add with |
|---|---|---|
| **Modifiers** | The modifier stack; Triangulate pinned at the bottom | **Add Modifier** menu |
| **Objects** | Plant, Orient, Scatter | **Add Object** menu |
| **Zones** | Named regions | **Add Zone** (blank, **From Layers…**, **From Selected Layers**) |
| **Analysis** | Measurements and colour overlays | **Add Analysis** |
| **Annotation** | Contours, labels, sections, tables | **Add Annotation** |

Two tabs carry an eye button:

- On **Zones** it switches the viewport between the terrain mesh and the split zone meshes.
- On **Analysis** it shows or hides analysis colours and analysis outputs.

## Anatomy of a card

Every card has:

- A **checkbox** to enable or disable it without deleting it.
- An **icon**, an editable **name** (click to rename) and a one-line **summary** when collapsed.
- A **drag handle** to reorder it. Drop it between cards. Triangulate's handle is disabled.
- A **delete** button.
- A body of **settings**. Settings that only apply in a certain mode appear and disappear as you change
  the mode.
- A **result** area on analysis and annotation cards, showing what the last build measured. If it says
  *Rebuild required*, the figures aren't populated yet.

If a card can't do anything yet it says why, for example *Not applied — no boundaries selected*.

### Setting types

| Setting | How to use it |
|---|---|
| **Source fields** | **Sel** replaces the field with the objects selected in Rhino. **Layers** replaces it with chosen layers. Neither adds to what's there; both replace |
| **Numbers** | Type a value and press Enter, or scrub. Lengths are in model units, and the unit is shown beside the field |
| **Slopes** | Type in any unit: `25%`, `150prom`, `14deg`, `1:3`, `1v:3h`. The field shows your preferred unit. See [Slope units](06-concepts.md#slope-units) |
| **Optional values** | Left blank, they inherit another value (shown greyed), such as Cut Slope inheriting Fill Slope |
| **Sliders** | Scrub for quick changes; you can usually type a value beyond the slider's range |
| **Colours** | Click the swatch. *Clear* returns to the colour of the card's output layer |
| **Colour ramps** | Drag stops, the band interval or the mapped range. See [Concepts](06-concepts.md#colour-ramps) |

## Terrain Settings

The Settings card sits with the terrain. It holds defaults for the whole terrain:

| Setting | Meaning |
|---|---|
| **Output Layers** | The layer template this terrain's output routes and styles through. **Edit…** opens the template editor; **Apply** creates its layers in the document without touching existing ones |
| **Slope Units** | The unit slope fields are *shown* in. A personal preference, not saved in the document. Fields still accept any unit |
| **Detail Size** | The smallest terrain detail to preserve automatically. Smaller keeps more detail; larger merges nearby geometry |
| **Opacity** | Terrain opacity for preview and bake |
| **Terrain Color** | Base display colour (click the swatch; **Reset** restores the default) |
| **Preview Line Weight** | On-screen only. Multiplies the thickness of previewed lines so busy plans read on dense displays. Anything other than 1.0× previews heavier than it will print |
| **Show wires** | Show or hide mesh wires on the terrain mesh |
| **Slow build warning** | Warn before a preview or exact rebuild when recent timings or mesh size suggest it may be slow |
| **Replace previous bakes** | When baking, delete the terrain's earlier baked objects first |
| **Tracking** | **Untrack selected** and **Untrack all** forget baked objects so later replace-bakes leave them alone (nothing is deleted) |

## Text Style (Annotation tab)

The top of the **Annotation** tab shows the Rhino **annotation style** that every label, section and table
on this terrain is drawn in, with its text height and font. Pick another style from the list, or press
**Edit…** to open Rhino's Document Properties and change the style itself; the terrain redraws as soon as
it changes. MoleHill never stores its own text size, and text height and alignment always follow the style,
so a preview and a bake agree.

## Sculpt toolbar

When you start a sculpt session from a [Sculpt](modifiers/sculpt.md) card, a floating toolbar appears with
brushes and sliders. See that page for details.

## Undo

Every edit in the panel is one Rhino undo step. Sculpt strokes undo with Ctrl+Z inside the sculpt session;
leaving the session records one document undo.
