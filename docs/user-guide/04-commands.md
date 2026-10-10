# Command reference

MoleHill adds commands that start with `mh`. Type them in Rhino's command line, or use the **MoleHill**
toolbar. The toolbar holds the geometry, block and document utilities. Creating a terrain and editing its
cards happens in the [panel](03-panel-reference.md). Some commands are variants of another and sit on that
button's right-click.

## Terrain

| Command | What it does |
|---|---|
| `mhPanel` | Open the MoleHill panel |
| `mhCreateTerrain` | Create a terrain from the current selection and open the panel |
| `mhConvertToRhino` | Detach the selected terrain from its sources and convert it into standard Rhino objects |
| `mhResetTerrainBuild` | Force-reset the selected terrain's build: cancels the running build and clears queued rebuilds. Preview state is discarded |
| `mhExportTerrainReport` | Write the selected terrain's measured quantities to a CSV file (UTF-8 with BOM, so Excel opens it correctly) |

## Preparing input geometry

| Command | What it does |
|---|---|
| `mhValidateTerrainInputs` | Clean selected points and curves in a dialog, including duplicate joined segments. Objects are replaced in place so ids and attributes survive |
| `mhSplitAtIntersections` | Split selected curves at their pairwise intersections |
| `mhDrapeCurve` | Select curves, then sample them onto a selected mesh or surface along World Z (or onto the active MoleHill terrain) |
| `mhTrimBoundary` | Trim curves against closed boundary curves, choosing which side to trim away |
| `mhCreateWall` | Draw a wall rail and generate its parallel companion rail at a plan and elevation offset, for [Retaining Wall](modifiers/retaining-wall.md) |
| `mhLiftCurvesWithLine` | Pick a line; curves crossing it are lifted. Options: **LiftFactor**, **AddOrderDots** |

These commands create ordinary Rhino geometry for you to assign as points, breaklines, contours or
boundaries. They never change a terrain definition.

## Working with heights and grades

| Command | What it does |
|---|---|
| `mhTwoPointInterpolation` | Pick a low point and a high point, then place points whose height is interpolated between them. Press Enter to finish |
| `mhGradientInterpolation` | Pick a base point and a slope, then place points whose heights follow that gradient from the base |
| `mhSlopeCurve` | Apply a slope to a selected curve. Option **ReplaceInput** |
| `mhSlopeCurveSection` | Set or interpolate the grade over a section of a curve (pick two points; the grade runs toward the second), including blending to the active terrain. Live preview. **Anchor** picks which end holds its elevation, **Transition** eases the moved end back into the rest of the curve. Reports how far the result strays from the prescribed grade when control points can't express it exactly |
| `mhReplaceCurveSection` | Replace a section of a base curve with a replacement curve |
| `mhSoftEditCurves` | Move curves with a soft falloff around a base point. Options **Falloff**, **FixEnds**, **Movement**, **Output** |
| `mhOffsetFeature` | Offset a 3D polyline in plan (mitred corners), keeping its grade, then apply a vertical change. Options **Distance**, a vertical mode (**Elevation**, **Percent**, **Degrees**, **Ratio**) and **Layer** |
| `mhInspectCurve` | Open the read-only curve inspector (see below) |

### Curve inspector

`mhInspectCurve` opens a panel with an elevation profile of the selected curve, plus checks, measurements
and an event list. It never edits geometry.

- Colour the profile by grade, elevation, cut/fill or plan radius. The same colouring is drawn on the curve in
  the viewport; hovering over the profile moves a marker along the curve.
- **Checks** for maximum grade, minimum plan radius, vertical grade change and terrain coverage. Each check
  can be Off, Report or Warn, and thresholds are kept between sessions.
- **Label** drops elevation, grade, station or cut/fill text dots along the curve.

## Survey and terrain import

| Command | What it does |
|---|---|
| `mhImportSurveyPoints` | Read a coded survey file into points and breaklines on a layer tree named after the file |
| `mhEditFieldCodes` | Edit the field-code table that decides how survey codes become points and lines (the right-click variant of the importer) |
| `mhImportLandXml` | Import every TIN surface in a LandXML file, converting to document units and swapping Northing/Easting into Y/X |
| `mhExportLandXml` | Export the completed terrain mesh as a metric LandXML TIN surface |
| `mhImportGeoTiff` | Place a georeferenced GeoTIFF as a picture-frame surface you can use as a DEM Surface on [Triangulate](modifiers/triangulate.md) |
| `mhImportGeoTiffTerrain` | Import a GeoTIFF as a terrain in one step |

## Project base and coordinates

| Command | What it does |
|---|---|
| `mhOrientToOrigin` | Move the project to the origin, saving how to get back |
| `mhClearProjectBase` | Remove the saved project base without moving geometry |
| `mhConvertToProjectCoordinates` | Convert to project-local coordinates |
| `mhConvertToRealWorldCoordinates` | Convert to real-world coordinates |
| `mhImportWithGeoref` / `mhExportWithGeoref` | Paste/File import and export applying the saved transform |

See [Concepts](06-concepts.md#project-base-and-coordinates).

## Layers and blocks

| Command | What it does |
|---|---|
| `mhEditLayerTemplates` | Open the layer template editor |
| `mhApplyLayerTemplate` | Create a template's layers in the document, leaving existing layers as they are |
| `mhResetLayerStyles` | Re-apply a template's appearance to existing layers. This overwrites hand edits made in the Layers panel |
| `mhExternalizeBlock` | Move a block definition out to its own file |
| `mhUpdateAllLinkedBlocks` | Update all linked blocks |
| `mhSetSunNorth` | Set the sun north direction |
| `mhAddMarkerParentheses` / `mhRemoveMarkerParentheses` | Add or remove parentheses on marker text |

## Diagnostics

`mhBenchmarkLargeTin`, `mhLatencyTrace` and `mhDebugAnnotationBlock` are for troubleshooting and support.
They are command-line only.
