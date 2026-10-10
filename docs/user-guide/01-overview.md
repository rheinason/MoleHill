# Product overview

## What MoleHill does

MoleHill turns survey and design geometry into a **live terrain model** inside a Rhino 8 document. You
point it at ordinary Rhino objects, such as points, curves and meshes, and it keeps a terrain mesh up to
date as those objects change. You then add cards that grade, edit, measure and draw the terrain.

You can:

- Build a terrain mesh from spot heights, breaklines, contour curves, a GeoTIFF elevation image, a LandXML
  surface or an existing mesh.
- Design earthworks: level pads, road corridors, ditches and retaining walls, each with cut and fill
  batters that run out to where they meet existing ground (daylight).
- Sculpt and smooth the surface, and reduce or regularise the mesh.
- Place objects and scatter blocks (trees, rocks, furniture) onto the terrain.
- Split the terrain into zones with their own quantities.
- Measure cut/fill volumes, slope, aspect, drainage, ponding and accessibility gradients.
- Draw contours, spot heights, sections and tables as real Rhino geometry for your sheets.

## The ideas to know

### A terrain is a recipe, not a mesh

A MoleHill **terrain** stores a *definition*: which Rhino objects and layers feed it, which cards modify
it, and how it should be measured and drawn. The mesh you see is rebuilt from that recipe. Change a
source curve, a slope or a boundary and the terrain updates. The definition is saved inside your `.3dm`
file, so it travels with the document.

A document can hold several terrains, for example *Existing* and *Proposed*. Some cards can compare one
against another.

### Cards

Everything you add is a **card**, in one of five tabs:

| Tab | What lives there |
|---|---|
| **Modifiers** | Cards that build and change the terrain mesh |
| **Objects** | Cards that place Rhino objects and blocks onto the terrain |
| **Zones** | Named regions of the terrain |
| **Analysis** | Cards that *evaluate* the terrain: a measurement, usually shown as colour |
| **Annotation** | Cards that *describe* the terrain: contours, labels, sections, tables |

The analysis/annotation split is a simple test. If the result is a **measurement** (slope, cut/fill,
ponding), it's an analysis. If the result is **drawing** (contour lines, text, a section), it's an
annotation.

Every card can be switched on and off with its checkbox, renamed, collapsed to a one-line summary and
reordered by dragging. A card that can't change anything yet, such as a grading card with no curves, tells
you why instead of failing quietly.

### The modifier stack

Modifiers run as a stack. **Triangulate** is pinned at the bottom and builds the base mesh. Each card above
it takes the *incoming mesh* from the card before and passes its result on. Order matters. Smoothing
before grading gives a different answer from grading before smoothing. Drag cards to reorder them; the
terrain updates.

### Sources: how cards find geometry

Most cards take **sources**: the Rhino objects or layers they read. Each source field has two buttons:

- **Sel** replaces the field with the objects currently selected in Rhino.
- **Layers** replaces it with layers (not additive). A layer source is *watched*: add or remove objects
  on the layer and the terrain follows.

Heights come from the geometry itself. A breakline's Z values set the height along the line. A point's Z is
its height. Curves used as plan boundaries have their Z ignored.

### Preview, final and bake

While you edit, MoleHill can show a quick **preview** build so the viewport stays responsive, then runs
the exact **final** build. Generated output (contours, labels, sections, scatter) is drawn in the
viewport and can be rendered, but it isn't in your document yet. **Bake** turns it into real Rhino objects
on the terrain's layers. See [Concepts](06-concepts.md#preview-final-and-bake).

### Layers are organised for you

Each terrain gets its own layer tree (named `MoleHill {terrain}`), with each kind of output routed to its
own layer. Colour, print width and linetype come from those layers. Edit the layer template once and every
terrain follows it. See [Concepts](06-concepts.md#output-layers).

## What you need

- Rhino 8 for Windows.
- Install **MoleHill** from Rhino's Package Manager (`_PackageManager`, search for *MoleHill*). This
  installs the Rhino plug-in and the optional Grasshopper components together.
- Run `mhPanel` to open the panel, or `mhCreateTerrain` to start a terrain from the objects you have
  selected.

If Rhino refuses to load the plug-in after a manual install, unblock the files in Windows (file
**Properties** → **Unblock**).

## Rhino plug-in and Grasshopper

The **Rhino plug-in** is the main product: the panel, the cards and the commands described in this guide.
The **Grasshopper components** are an optional companion for procedural workflows. See
[Grasshopper components](05-grasshopper.md).

## Where next

- Follow [A typical workflow](02-typical-workflow.md) end to end.
- Learn the [panel](03-panel-reference.md).
