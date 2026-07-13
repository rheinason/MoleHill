# Project base and real-world coordinates

MoleHill keeps modelling geometry near Rhino's XY origin while retaining a reversible mapping to the
project's real-world Cartesian coordinates. The mapping is stored as the named construction plane
`MoleHill_ProjectBase`; the older `Georef` name is read as a migration fallback.

## Coordinate contract

The saved plane represents one rigid transform `G` from **local project coordinates** to **real-world
coordinates**. Its inverse `G^-1` maps real-world data into the local model.

- `mhOrientToOrigin` selects a project XY base and optional project X direction, moves the entire active
  document into local coordinates, and saves the inverse mapping as `G`.
- `mhConvertToRealWorldCoordinates` applies `G` to selected geometry.
- `mhConvertToProjectCoordinates` applies `G^-1` to selected geometry.
- `mhExportWithGeoref` writes selected local geometry through `G` without moving the source document.
- `mhImportWithGeoref` imports/pastes real-world geometry and applies `G^-1`.
- `mhClearProjectBase` deletes the saved mapping without moving geometry.

Orienting an already-oriented document composes the additional local transform on the right:
`G_new = G_existing * inverse(newLocalTransform)`. Elevation is deliberately preserved: the selected
project base controls XY translation and horizontal rotation only. MoleHill does not currently model a
vertical-datum offset.

Rhino replaces transformed objects with new object ids. `TerrainController` completes each replacement
mapping on the subsequent Add/Undelete event and updates terrain sources, placement state, managed output
ids, and baked-object ids together.

## GeoTIFF import

`mhImportGeoTiff` first looks for standard embedded GeoTIFF placement tags:

- `ModelTransformation` (full affine), or
- `ModelPixelScale` plus `ModelTiepoint`.

If neither is available, it looks beside the image for `.tfw`, `.tifw`, or `.wld`, then offers a file
picker. The full six-value affine is applied, including rotation/shear and the world-file pixel-centre
offset. If a project base exists, real-world raster placement is mapped through `G^-1` before the picture
frame is added.

This is deliberately lightweight and does **not** perform CRS reprojection. Raster coordinates, Rhino
model units, and the saved project-base coordinates must already use the same projected coordinate system
and units. Use GIS software to reproject the raster first when they differ.
