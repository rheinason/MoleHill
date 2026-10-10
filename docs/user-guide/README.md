# MoleHill User Guide

MoleHill is a terrain modelling toolkit for **Rhino 8**. You give it survey points, contours and
breaklines; it builds a terrain mesh, lets you shape it with grading and editing *cards*, measures it, and
draws the results (contours, labels, sections, tables) as ordinary Rhino geometry.

This guide is for people who **use** MoleHill. It does not cover building or extending the plug-in.
Developer documentation lives one level up in [`docs/`](../architecture.md).

## Start here

| If you want to… | Read |
|---|---|
| Understand what MoleHill is and how it thinks | [Product overview](01-overview.md) |
| Go from survey data to a graded, drawn-up terrain | [A typical workflow](02-typical-workflow.md) |
| Learn the panel, toolbar and settings | [The MoleHill panel](03-panel-reference.md) |
| Look up one specific card | The card lists below |
| Prepare input geometry, import/export, or use Rhino commands | [Command reference](04-commands.md) |
| Use MoleHill from Grasshopper | [Grasshopper components](05-grasshopper.md) |
| Understand slope units, layers and baking | [Concepts](06-concepts.md) |

## Cards

Everything you add to a terrain is a **card** in one of five tabs of the MoleHill panel.

### Modifiers: shape the terrain

Modifiers form a stack. The pinned **Triangulate** card at the bottom builds the base mesh; every card
above it works on the result of the one before.

| Card | Purpose |
|---|---|
| [Triangulate](modifiers/triangulate.md) | Build the base mesh from points, breaklines, contours, a DEM or an existing mesh |
| [Add Geometry](modifiers/add-geometry.md) | Add more points, breaklines or contours part-way up the stack |
| [Smooth](modifiers/smooth.md) | Smooth heights while keeping the plan layout |
| [Remesh](modifiers/remesh.md) | Even out triangles and keep creases |
| [Retopo](modifiers/retopo.md) | Rebuild as quads that follow creases and breaklines |
| [Simplify](modifiers/simplify.md) | Use fewer vertices within a verified error |
| [Sculpt](modifiers/sculpt.md) | Paint height changes by hand |
| [Project To](modifiers/project-to.md) | Blend heights toward another mesh or terrain |
| [Grade Pad](modifiers/grade-pad.md) | Level pad with cut and fill batters out to daylight |
| [Grade Path](modifiers/grade-path.md) | Road or path corridor with batters |
| [Grade Line](modifiers/grade-line.md) | Batter away from a design line |
| [Retaining Wall](modifiers/retaining-wall.md) | Wall rails that the terrain must honour, optionally with batters |
| [In-Situ Stair](modifiers/in-situ-stair.md) | Generate stair solids on a walkable surface |

### Objects: place things on the terrain

| Card | Purpose |
|---|---|
| [Plant](objects/plant.md) | Drop Rhino objects so their lowest point touches the terrain |
| [Orient](objects/orient.md) | Drop and tilt objects to follow the terrain slope |
| [Scatter](objects/scatter.md) | Scatter weighted blocks across regions or along curves |

### Zones

| Page | Purpose |
|---|---|
| [Zones](zones.md) | Split the terrain into named, coloured regions with their own quantities |

### Analysis: measure the terrain

| Card | Purpose |
|---|---|
| [Earthworks](analysis/earthworks.md) | Cut, fill and net volume |
| [Slope](analysis/slope.md) | Colour by steepness |
| [Aspect](analysis/aspect.md) | Colour by the direction the ground faces |
| [Elevation](analysis/elevation.md) | Colour by height |
| [Cut / Fill](analysis/cut-fill.md) | Colour by how far grading moved the ground |
| [Waterflow from Points](analysis/waterflow.md) | Trace downhill paths from chosen points |
| [Catchments](analysis/catchments.md) | Which ground drains to which outlet |
| [Ponding](analysis/ponding.md) | Where water would stand |
| [Gradient Compliance](analysis/gradient-compliance.md) | Check accessibility gradients against a standard |

### Annotation: draw the terrain

| Card | Purpose |
|---|---|
| [Contours](annotations/contours.md) | Contour lines with major/minor levels and labels |
| [Spot Heights (Curve)](annotations/spot-heights-curve.md) | Elevation labels along curves |
| [Spot Slope (Curve)](annotations/spot-slope-curve.md) | Slope labels along curves |
| [Spot Heights (Points)](annotations/spot-heights-points.md) | Elevation labels at points |
| [Spot Slope (Points)](annotations/spot-slope-points.md) | Slope labels at points |
| [Flow Arrows](annotations/flow-arrows.md) | Downhill arrows on a grid |
| [Grade Callout](annotations/grade-callout.md) | Grade between two points as 1:n and % |
| [Section Cut](annotations/section-cut.md) | Profile at a cut line |
| [Cross-Sections](annotations/cross-sections.md) | Unrolled cross-sections at stations |
| [Section Along Curve](annotations/section-along-curve.md) | Profile that follows a curve |
| [Report Table](annotations/report-table.md) | Measured quantities drawn as a table |
| [Legend](annotations/legend.md) | Key to the colours on the terrain |

## Conventions used in this guide

- **Bold** names are labels you will see in the panel.
- *Model units* means the length unit of your Rhino document. MoleHill follows it everywhere.
- A **breakline** is a curve whose edges the mesh keeps. A **boundary** is a closed plan curve that limits
  a region. The mesh's own outer edge is its **border**. A fold detected from the mesh is a **crease**.
  **Protect** curves shield terrain from an edit without adding mesh edges.
- Slope fields accept any unit; see [Slope units](06-concepts.md#slope-units).
