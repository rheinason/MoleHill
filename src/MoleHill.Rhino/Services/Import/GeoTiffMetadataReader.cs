namespace MoleHill.Rhino.Services;

internal static class GeoTiffMetadataReader
{
    private const int ModelPixelScaleTag = 33550;
    private const int ModelTiepointTag = 33922;
    private const int ModelTransformationTag = 34264;
    private const int GeoKeyDirectoryTag = 34735;
    private const ushort GtRasterTypeGeoKey = 1025;
    private const ushort RasterPixelIsPoint = 2;

    public static bool TryRead(
        string path,
        out RasterGeoreference georeference,
        out string sourceDescription,
        out GeoTiffLinearUnit? linearUnit)
    {
        georeference = default;
        sourceDescription = string.Empty;
        linearUnit = null;
        if (!File.Exists(path))
            return false;

        ushort[] directory = ClassicTiffTagReader.ReadUnsignedShorts(path, GeoKeyDirectoryTag);
        if (GeoTiffLinearUnitReader.TryRead(directory, out GeoTiffLinearUnit detectedUnit))
            linearUnit = detectedUnit;
        bool pixelIsPoint = IsRasterPixelPoint(directory);

        double[] matrix = ClassicTiffTagReader.ReadDoubles(path, ModelTransformationTag);
        if (RasterGeoreference.TryCreateFromModelTransformation(matrix, pixelIsPoint, out georeference))
        {
            sourceDescription = "embedded GeoTIFF ModelTransformation tag";
            return true;
        }

        double[] scale = ClassicTiffTagReader.ReadDoubles(path, ModelPixelScaleTag);
        double[] tiepoints = ClassicTiffTagReader.ReadDoubles(path, ModelTiepointTag);
        if (RasterGeoreference.TryCreateFromPixelScaleAndTiepoint(scale, tiepoints, pixelIsPoint, out georeference))
        {
            sourceDescription = "embedded GeoTIFF ModelPixelScale/ModelTiepoint tags";
            return true;
        }

        return false;
    }

    private static bool IsRasterPixelPoint(IReadOnlyList<ushort> directory)
    {
        if (directory.Count < 4)
            return false;

        int keyCount = directory[3];
        for (int index = 0; index < keyCount; index++)
        {
            int offset = 4 + (index * 4);
            if (offset + 3 >= directory.Count)
                break;

            ushort keyId = directory[offset];
            ushort tagLocation = directory[offset + 1];
            ushort count = directory[offset + 2];
            ushort valueOffset = directory[offset + 3];
            if (keyId == GtRasterTypeGeoKey && tagLocation == 0 && count == 1)
                return valueOffset == RasterPixelIsPoint;
        }

        return false;
    }

}
