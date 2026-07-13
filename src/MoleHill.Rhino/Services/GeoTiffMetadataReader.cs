using System.Drawing;
using System.Drawing.Imaging;

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
        Image image,
        out RasterGeoreference georeference,
        out string sourceDescription)
    {
        georeference = default;
        sourceDescription = string.Empty;
        bool pixelIsPoint = TryReadRasterPixelIsPoint(image);

        if (TryReadDoubles(image, ModelTransformationTag, out double[] matrix) &&
            RasterGeoreference.TryCreateFromModelTransformation(matrix, pixelIsPoint, out georeference))
        {
            sourceDescription = "embedded GeoTIFF ModelTransformation tag";
            return true;
        }

        if (TryReadDoubles(image, ModelPixelScaleTag, out double[] scale) &&
            TryReadDoubles(image, ModelTiepointTag, out double[] tiepoints) &&
            RasterGeoreference.TryCreateFromPixelScaleAndTiepoint(scale, tiepoints, pixelIsPoint, out georeference))
        {
            sourceDescription = "embedded GeoTIFF ModelPixelScale/ModelTiepoint tags";
            return true;
        }

        return false;
    }

    private static bool TryReadRasterPixelIsPoint(Image image)
    {
        if (!TryReadUnsignedShorts(image, GeoKeyDirectoryTag, out ushort[] directory) || directory.Length < 4)
            return false;

        int keyCount = directory[3];
        for (int index = 0; index < keyCount; index++)
        {
            int offset = 4 + (index * 4);
            if (offset + 3 >= directory.Length)
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

    private static bool TryReadDoubles(Image image, int tagId, out double[] values)
    {
        values = Array.Empty<double>();
        if (!TryGetPropertyItem(image, tagId, out PropertyItem? property))
            return false;
        if (property == null || property.Type != 12 || property.Value == null || property.Value.Length % sizeof(double) != 0)
            return false;

        values = new double[property.Value.Length / sizeof(double)];
        for (int index = 0; index < values.Length; index++)
            values[index] = BitConverter.ToDouble(property.Value, index * sizeof(double));
        return values.All(double.IsFinite);
    }

    private static bool TryReadUnsignedShorts(Image image, int tagId, out ushort[] values)
    {
        values = Array.Empty<ushort>();
        if (!TryGetPropertyItem(image, tagId, out PropertyItem? property))
            return false;
        if (property == null || property.Type != 3 || property.Value == null || property.Value.Length % sizeof(ushort) != 0)
            return false;

        values = new ushort[property.Value.Length / sizeof(ushort)];
        for (int index = 0; index < values.Length; index++)
            values[index] = BitConverter.ToUInt16(property.Value, index * sizeof(ushort));
        return true;
    }

    private static bool TryGetPropertyItem(Image image, int tagId, out PropertyItem? property)
    {
        property = null;
        try
        {
            property = image.PropertyItems.FirstOrDefault(item => item.Id == tagId);
            return property != null;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }
}
