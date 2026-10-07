using System.Globalization;

namespace MoleHill.Core.IO;

/// <summary>
/// Lightweight affine raster georeferencing. Supports standard embedded GeoTIFF model tags and
/// six-line world files without taking a dependency on a CRS/reprojection library.
/// </summary>
internal readonly record struct RasterGeoreference(
    double XPixel,
    double XLine,
    double YPixel,
    double YLine,
    double XUpperLeft,
    double YUpperLeft)
{
    public bool IsValid =>
        double.IsFinite(XPixel) &&
        double.IsFinite(XLine) &&
        double.IsFinite(YPixel) &&
        double.IsFinite(YLine) &&
        double.IsFinite(XUpperLeft) &&
        double.IsFinite(YUpperLeft) &&
        Math.Abs((XPixel * YLine) - (XLine * YPixel)) > 1e-15;

    internal static bool TryCreateFromModelTransformation(
        IReadOnlyList<double> matrix,
        bool pixelIsPoint,
        out RasterGeoreference georeference)
    {
        georeference = default;
        if (matrix.Count < 16)
            return false;

        georeference = NormalizePixelOrigin(new RasterGeoreference(
            matrix[0], matrix[1], matrix[4], matrix[5], matrix[3], matrix[7]), pixelIsPoint);
        return georeference.IsValid;
    }

    internal static bool TryCreateFromPixelScaleAndTiepoint(
        IReadOnlyList<double> scale,
        IReadOnlyList<double> tiepoints,
        bool pixelIsPoint,
        out RasterGeoreference georeference)
    {
        georeference = default;
        if (scale.Count < 2 || tiepoints.Count < 6)
            return false;

        double xPixel = scale[0];
        double yLine = -Math.Abs(scale[1]);
        double rasterX = tiepoints[0];
        double rasterY = tiepoints[1];
        double modelX = tiepoints[3];
        double modelY = tiepoints[4];
        georeference = NormalizePixelOrigin(new RasterGeoreference(
            xPixel,
            0.0,
            0.0,
            yLine,
            modelX - (xPixel * rasterX),
            modelY - (yLine * rasterY)), pixelIsPoint);
        return georeference.IsValid;
    }

    public static bool TryReadWorldFile(
        string path,
        out RasterGeoreference georeference,
        out string? error)
    {
        georeference = default;
        error = null;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            error = $"Could not read world file: {ex.Message}";
            return false;
        }

        if (lines.Length != 6)
        {
            error = "World file must contain exactly six values.";
            return false;
        }

        var values = new double[6];
        for (int index = 0; index < values.Length; index++)
        {
            if (!double.TryParse(lines[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]) ||
                !double.IsFinite(values[index]))
            {
                error = $"World file value {index + 1} is not a finite number.";
                return false;
            }
        }

        // World files store A,D,B,E,C,F and define C/F at the centre of the upper-left pixel.
        georeference = NormalizePixelOrigin(new RasterGeoreference(
            values[0], values[2], values[1], values[3], values[4], values[5]), pixelIsPoint: true);
        if (!georeference.IsValid)
        {
            error = "World file contains a degenerate affine transform.";
            return false;
        }

        return true;
    }

    public RasterGeoreference ScaleCoordinates(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(scale));

        return new RasterGeoreference(
            XPixel * scale,
            XLine * scale,
            YPixel * scale,
            YLine * scale,
            XUpperLeft * scale,
            YUpperLeft * scale);
    }

    internal (double X, double Y) MapRasterPoint(double pixel, double line)
    {
        return (
            XUpperLeft + (pixel * XPixel) + (line * XLine),
            YUpperLeft + (pixel * YPixel) + (line * YLine));
    }

    private static RasterGeoreference NormalizePixelOrigin(RasterGeoreference value, bool pixelIsPoint)
    {
        if (!pixelIsPoint)
            return value;

        return value with
        {
            XUpperLeft = value.XUpperLeft - (0.5 * (value.XPixel + value.XLine)),
            YUpperLeft = value.YUpperLeft - (0.5 * (value.YPixel + value.YLine))
        };
    }
}
