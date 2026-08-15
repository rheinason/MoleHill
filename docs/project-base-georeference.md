# Project base and real-world coordinates

MoleHill keeps modelling geometry near Rhino's XY origin while retaining a reversible mapping to the
project's real-world Cartesian coordinates. The authoritative mapping is stored as the named construction
plane `MoleHill_ProjectBase`. Every read validates that the plane is finite, horizontal, orthonormal,
right-handed, and has no vertical offset; a manually tilted or elevated plane is rejected rather than
silently changing elevations.

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

The X-axis pick uses the project base as its Rhino getter base point and draws a dynamic guide line. Its
XY separation must exceed the document's model tolerance, preventing a nearly coincident pick from
creating an unstable rotation.

## Legacy Python and C# migration

The former Python `OrientToOrigin.py` workflow normally stored a named CPlane called `FOTM`. That plane
represented the opposite convention: **real-world to local** (`G^-1`). Older C# builds could instead
store `Georef` using the current **local to real-world** (`G`) convention.

When no modern plane exists, commands detect either legacy name and ask before migrating it. The prompt
can also ignore the candidate once or disable legacy fallback for the document:

- `FOTM` is inverted from its Python real-world-to-local convention.
- `Georef` is retained in its legacy C# local-to-real-world convention.

Migration validates the result, writes `MoleHill_ProjectBase`, and leaves the legacy named CPlane
unchanged. This avoids deleting an unrelated user CPlane that happens to use a legacy name. Clearing the
project base suppresses legacy fallback in document user text without deleting `FOTM` or `Georef`; a new
orientation or successful migration re-enables the modern mapping.

## Import and export behavior

`mhImportWithGeoref` is a Rhino script-runner command, so its nested `Paste` and `Import` operations finish
before remapping begins. It snapshots the active object table, then applies `G^-1` to every newly added
ModelSpace object, including hidden and individually locked objects; PageSpace layout geometry is left
unchanged. Selection is only a fallback when an importer produces no detectable new ids. If import or
the coordinate transform fails, every newly added object is removed with locked/hidden modes ignored and
the prior selection is restored. Objects on locked layers still fail the transform with an explicit
unlock-and-retry message rather than overriding the user's layer protection.

`mhExportWithGeoref` applies `G` through Rhino's file-write transform. It never moves source geometry,
never changes the active CPlane or document path, suppresses file-format input, and restores selection.

## GeoTIFF import

`mhImportGeoTiff` first looks for standard embedded GeoTIFF placement tags:

- `ModelTransformation` (full affine), or
- `ModelPixelScale` plus `ModelTiepoint`.

If neither is available, it looks beside the image for `.tfw`, `.tifw`, or `.wld`, then offers a file
picker. The full six-value affine is applied, including rotation/shear and the world-file pixel-centre
offset. If a project base exists, real-world raster placement is mapped through `G^-1` before the picture
frame is added.

Projected-coordinate linear-unit GeoKeys are read when present (including metres, international feet,
US survey feet, Clarke's feet, fathoms, and nautical miles) and coordinates are converted into current
Rhino model units. Geographic coordinate systems are not inferred as linear because their raster XY
values are normally angular. A world file carries no unit metadata; if no supported projected GeoTIFF
unit key exists, the command asks for source units and defaults to the document's units.

This is deliberately lightweight and does **not** perform CRS reprojection. Raster and saved project-base
coordinates must already use the same projected coordinate system. Use GIS software to reproject the
raster first when they differ.
