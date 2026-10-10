# A typical workflow

This walkthrough takes a site from survey data to a graded terrain with quantities and drawings. Every
step links to the card that does the work. You don't need all of them on every project.

## 1. Prepare your input geometry

MoleHill reads ordinary Rhino objects, so tidy ones give a better terrain.

- **Survey points and breaklines.** If you have a coded survey file, import it with `mhImportSurveyPoints`.
  It creates points and curves on a layer tree named after the file. See [Commands](04-commands.md#survey-and-terrain-import).
- **Contours drawn flat?** Lift them to their heights, or use `mhDrapeCurve` to sample curves onto a mesh.
- **Clean the data.** `mhValidateTerrainInputs` removes duplicate points and joins duplicate segments.
  `mhSplitAtIntersections` splits crossing curves so breaklines meet at a vertex.
- **Georeferenced data?** Set up the project base first so real-world coordinates stay manageable. See
  [Concepts](06-concepts.md#project-base-and-coordinates).

Put inputs on their own layers (for example *Survey Points*, *Breaklines*). Layer sources are watched, so
you can add geometry later without touching the card.

## 2. Create the terrain

1. Select your points and curves in Rhino.
2. Run `mhCreateTerrain`. The panel opens with a new terrain.

Or open the panel with `mhPanel` and click the **+** button, then assign sources on the
[Triangulate](modifiers/triangulate.md) card yourself:

- **Points** for spot heights.
- **Breaklines** for edges the mesh must keep (kerbs, ridges, streams, building edges).
- **Contours** for contour curves.
- **Outer**, **Hide**, **Show** boundaries to trim the finished mesh.

Or import an existing surface instead: a mesh as **Exact TIN Mesh**, a GeoTIFF as **DEM Surface**, or a
LandXML file with `mhImportLandXml`.

The terrain appears in the viewport. Name it in the toolbar: *Existing*.

## 3. Check and refine the base mesh

- Look for spikes and gaps. Fix the source data or use **Peel Border** on the Triangulate card to remove
  thin triangles from the edge.
- If triangles are long and thin, add a [Remesh](modifiers/remesh.md) card above Triangulate.
- For a very dense survey, add [Simplify](modifiers/simplify.md) to cut the vertex count within an error you
  choose.
- To clean noisy survey heights, add [Smooth](modifiers/smooth.md), protecting curves that must keep their
  height.

## 4. Design the proposal

To compare proposed against existing, duplicate the terrain (toolbar **Duplicate**), rename the copy
*Proposed*, and add grading cards to the copy. Or keep one terrain and compare its final result against its
own starting mesh. Cut / Fill and Earthworks do that automatically.

Draw your design as ordinary Rhino curves with the correct Z values, then add cards:

| You are designing | Card |
|---|---|
| A level building platform or car park | [Grade Pad](modifiers/grade-pad.md) |
| A road, path or track | [Grade Path](modifiers/grade-path.md) |
| A swale, bank or any single design line | [Grade Line](modifiers/grade-line.md) |
| A retaining wall | [Retaining Wall](modifiers/retaining-wall.md) |
| Steps on a slope | [In-Situ Stair](modifiers/in-situ-stair.md) |
| A rough hand-shaped change | [Sculpt](modifiers/sculpt.md) |
| Heights that should match another surface | [Project To](modifiers/project-to.md) |

Order matters. Cards above run after cards below. Typical order, bottom to top: Triangulate → Smooth /
Remesh → walls → pads and paths → Sculpt for final touches.

Each grading card has a **Fill Slope** and an optional **Cut Slope**. Type them in whatever unit you like
(`1:3`, `33%`, `18deg`). The batter runs out from the design edge until it meets the existing ground.

`mhSlopeCurveSection` and `mhOffsetFeature` help draw design curves with the right grade.
`mhInspectCurve` checks a curve's grade and radius before you build on it.

## 5. Check the result

Add analyses to see what you've done:

- [Slope](analysis/slope.md) to find steep batters.
- [Cut / Fill](analysis/cut-fill.md) to see where ground was added and removed, with a balance line.
- [Earthworks](analysis/earthworks.md) for cut, fill and net volumes.
- [Catchments](analysis/catchments.md), [Ponding](analysis/ponding.md) and
  [Waterflow](analysis/waterflow.md) to check drainage.
- [Gradient Compliance](analysis/gradient-compliance.md) for accessible routes and landings.

Only analyses that colour the terrain show one at a time on the mesh; use the eye on the **Analysis** tab to
show or hide analysis colours and outputs.

## 6. Split into zones (optional)

Add [zones](zones.md) from layers (for example *Lawn*, *Paving*, *Planting*). Each gets its own mesh,
colour and measured plan area, surface area, elevation range, slope and cut/fill.

## 7. Place objects (optional)

- [Plant](objects/plant.md) or [Orient](objects/orient.md) to sit furniture, bollards or buildings on the
  ground.
- [Scatter](objects/scatter.md) to distribute trees or rocks across a planting area or along a path.

## 8. Draw it up

Add annotations: [Contours](annotations/contours.md) (major/minor, labelled), spot heights, slope arrows,
grade callouts, [sections](annotations/section-cut.md), a [legend](annotations/legend.md) and a
[report table](annotations/report-table.md). Configure the annotation style and the output layers once in
the Settings card; they apply to every annotation.

## 9. Bake

Click **Bake** in the toolbar. The terrain mesh and all generated output become real Rhino objects on the
terrain's layers, ready to print or export. With **Replace previous bakes** on, baking again replaces the
last set.

To detach a terrain from its recipe and keep only plain Rhino geometry, use `mhConvertToRhino`.

## 10. Hand over data

- `mhExportLandXml` writes the final mesh as a LandXML surface.
- `mhExportTerrainReport` writes measured quantities to CSV.
- With Rhino.Inside.Revit, Grasshopper's *Prepare Toposolid* and *Write Toposolids* send the terrain to
  Revit. See [Grasshopper](05-grasshopper.md).

## Tips

- Leave **Live update** on while you work; switch it off for very large terrains and press **Rebuild** when
  you're ready.
- Collapse cards you are not working on. The one-line summary shows the key settings.
- Turning a card off (its checkbox) is the quickest way to see what it contributes.
- If a result looks wrong, check the card's status line first. Cards say why they are doing nothing
  ("no boundaries selected") or why the build is blocked.
