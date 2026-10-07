# IO

Host-free readers for files the Rhino host imports. Internal to Core (visible to the Rhino plug-in and the
test projects); nothing here references RhinoCommon, LibTiff or a CRS library.

| File | What it is |
|------|------------|
| `ClassicTiffTagReader.cs` | Reads ASCII, short and double tag values from the first IFD of a classic TIFF, including unregistered GeoTIFF/GDAL tags. |
| `GeoTiffMetadataReader.cs` | Resolves a raster's georeference and linear unit from the embedded GeoTIFF tags. |
| `GeoTiffLinearUnitReader.cs` | Maps a projected EPSG linear-unit GeoKey to metres per unit. |
| `RasterGeoreference.cs` | Affine raster placement: from model tags or a six-line world file, scaling and pixel mapping. The RhinoCommon `Transform` for a picture frame is an extension in `MoleHill.Rhino/Services/Import/`. |

`GeoTiffElevationReader` (numeric band decoding through LibTiff.Net) stays in the Rhino plug-in so Core
carries no imaging package.
