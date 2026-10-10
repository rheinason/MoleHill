# Concepts

## Slope units

Slope is written differently in every discipline: percent, ratios, promille, degrees. MoleHill lets you
choose how slopes are **shown** and accept any unit when you **type**.

- **Slope Units** in the [Settings card](03-panel-reference.md#terrain-settings) sets the display unit. It's
  a personal preference and doesn't change the document, so a colleague opening the same file sees their
  own unit.
- Any slope field accepts any unit you type, and converts:

| You type | Meaning |
|---|---|
| `25%` | 25 percent |
| `150prom` | 150 per mille (15%) |
| `14deg` | 14 degrees |
| `1:3` or `1v:3h` | 1 vertical to 3 horizontal |
| `4` under the *Ratio* unit | The 1:4 slope. A bare number is the run *n* of 1:n |

- **`a:b` is always vertical:horizontal.** So `1:3` is the *flatter* batter and `1:1` is 45 degrees.
- A value that can't be read is reverted rather than guessed. A good one is shown back in your display
  unit.
- Fields labelled **Degrees** with no slope unit (such as Crease Angle) are true angles between faces and are
  never converted.
- Annotation cards that print slopes (slope labels, flow arrows, the report table) have their own
  **Units** setting, because that unit is part of the drawing and is saved with the terrain.

## Model units

MoleHill uses your document's unit system. Lengths shown in cards (Width, Interval, Max Distance…) are in
model units and defaults are scaled to suit. If you change the document's model units, volumes, areas and
default values follow.

## Output layers

Every generated object lands on a layer chosen by its **role** (for example *Contours (Major)*, *Waterflow*,
*Legend*, *Report Table*). Colour, print width, linetype, text style and hatch come from that role's layer,
so a preview and a bake always look the same.

- Layers are **per terrain**. The shipped template puts each terrain under its own root, `MoleHill
  {terrain}`, with *Input* and *Output* branches.
- Edit the template with **Output Layers → Edit…** (or `mhEditLayerTemplates`). **Apply** creates the
  layers (`mhApplyLayerTemplate`); `mhResetLayerStyles` re-applies a template's appearance to layers that
  already exist, discarding hand edits made in the Layers panel.
- A card's **Color** field overrides the layer colour for that card. *Clear* it to follow the layer again.
- Never rename, copy or delete a terrain's layers by hand. Use the panel's rename, duplicate and delete.
- Survey import writes to its own layer tree named after the file, outside every terrain.

## Preview, final and bake

- **Preview.** While editing, MoleHill can show a faster approximation (for example, remeshing at twice
  the edge length). Generated outputs such as contours, labels and sections are only produced by a final
  build.
- **Final.** The exact result, including all analyses, annotations, zones, objects and scatter.
- **Render.** Final terrain meshes appear in render engines without baking.
- **Bake.** Creates real Rhino objects: the terrain mesh, curves, text, block instances and hatches.
  Scatter creates real block instances only at bake time (the preview shows points, shapes or capped
  instances).

Baked objects are tracked per terrain. **Replace previous bakes** swaps the old set for the new. Untrack
objects you want to keep. To detach a terrain entirely, run `mhConvertToRhino`.

## Breaklines, boundaries, creases and protect curves

| Word | Meaning |
|---|---|
| **Breakline** | A curve whose edges the mesh keeps; its Z sets the height along the edge |
| **Boundary** | A closed plan curve that scopes a region |
| **Border** | The mesh's own outer edge |
| **Crease** | A fold detected from the mesh, kept during Remesh/Retopo but never saved as a breakline |
| **Incoming mesh** | The mesh a card receives from the card before it |
| **Protect** | A curve that shields terrain from an edit (Sculpt, Smooth) without adding an edge |
| **Batter** | The graded slope between a design edge and where it meets existing ground |

By default **breaklines are hard**: a grading card may not cross breaklines or graded edges from earlier
cards unless you turn on **Grade Through Breaklines**.

## Colour ramps

Analyses that colour the terrain (Slope, Elevation, Cut / Fill, Ponding) use a colour ramp. You can drag the
ramp's stops, change the band interval or the mapped range. The ramp can be *gradient* (smooth), *stepped*
(bands) or *constant* (thresholds). Aspect uses a closed compass wheel. Catchments and Gradient Compliance
use categorical colours instead, because their colours are labels, not values. Only one analysis colours the
terrain at a time; a [Legend](annotations/legend.md) keys whichever is showing.

## Project base and coordinates

If your data are in real-world (georeferenced) coordinates far from the origin, precision suffers. MoleHill
supports a **project base**: a saved horizontal plane that maps between project-local and real-world
coordinates.

- `mhOrientToOrigin` moves the project to the origin and remembers how to get back.
- `mhConvertToProjectCoordinates` / `mhConvertToRealWorldCoordinates` switch the selection between the two.
- `mhImportWithGeoref` / `mhExportWithGeoref` apply the transform on import and export.
- `mhClearProjectBase` removes the saved base without moving geometry.

LandXML and GeoTIFF imports use the saved transform. CRS reprojection is not supported.
