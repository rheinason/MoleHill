using System.Globalization;
using System.Xml.Linq;
using BitMiracle.LibTiff.Classic;

namespace MoleHill.Rhino.Services;

internal readonly record struct GeoTiffElevationSample(int PixelX, int PixelY, double Elevation);

internal sealed class GeoTiffElevationSamples
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Stride { get; init; }

    public required IReadOnlyList<GeoTiffElevationSample> Samples { get; init; }
}

/// <summary>Reads numeric, single-band TIFF samples without passing them through display RGB conversion.</summary>
internal static class GeoTiffElevationReader
{
    private const ushort SampleFormatUnsigned = 1;
    private const ushort SampleFormatSigned = 2;
    private const ushort SampleFormatFloat = 3;
    private const ushort PlanarConfigurationContiguous = 1;
    private const int GdalMetadataTag = 42112;
    private const int GdalNoDataTag = 42113;

    public static bool TryReadSamples(
        string path,
        int maximumSampleCount,
        out GeoTiffElevationSamples? result,
        out string? error)
    {
        result = null;
        error = null;
        using Tiff? image = Tiff.Open(path, "r");
        if (image == null)
        {
            error = "The TIFF raster could not be opened.";
            return false;
        }

        int width = GetRequiredInt(image, TiffTag.IMAGEWIDTH);
        int height = GetRequiredInt(image, TiffTag.IMAGELENGTH);
        int samplesPerPixel = GetDefaultedInt(image, TiffTag.SAMPLESPERPIXEL, 1);
        int bitsPerSample = GetDefaultedInt(image, TiffTag.BITSPERSAMPLE, 1);
        int sampleFormat = GetDefaultedInt(image, TiffTag.SAMPLEFORMAT, SampleFormatUnsigned);
        int planarConfiguration = GetDefaultedInt(image, TiffTag.PLANARCONFIG, PlanarConfigurationContiguous);
        int photometric = GetDefaultedInt(image, TiffTag.PHOTOMETRIC, -1);

        if (width <= 0 || height <= 0)
        {
            error = "The TIFF raster has invalid dimensions.";
            return false;
        }
        if (samplesPerPixel != 1 || photometric == (int)Photometric.RGB)
        {
            error = "Terrain creation requires a single numeric elevation band; RGB/multi-band imagery is not a DEM.";
            return false;
        }
        if (planarConfiguration != PlanarConfigurationContiguous)
        {
            error = "The TIFF raster uses an unsupported planar sample layout.";
            return false;
        }
        if (!IsSupportedFormat(sampleFormat, bitsPerSample))
        {
            error = $"Unsupported DEM sample format: SampleFormat={sampleFormat}, BitsPerSample={bitsPerSample}.";
            return false;
        }

        int bytesPerSample = bitsPerSample / 8;
        int scanlineSize = image.ScanlineSize();
        if (scanlineSize < width * bytesPerSample)
        {
            error = "The TIFF scanline is smaller than its declared sample layout.";
            return false;
        }

        int targetCount = Math.Max(1, maximumSampleCount);
        int stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((width * (double)height) / targetCount)));
        double? noData = TryReadAsciiDoubleTag(path, GdalNoDataTag);
        ReadScaleOffset(path, out double scale, out double offset);
        var samples = new List<GeoTiffElevationSample>(Math.Min(targetCount, width * height));
        var scanline = new byte[scanlineSize];
        for (int y = 0; y < height; y += stride)
        {
            if (!image.ReadScanline(scanline, y, 0))
            {
                error = $"Could not decode TIFF scanline {y}.";
                return false;
            }

            for (int x = 0; x < width; x += stride)
            {
                double raw = ReadSample(scanline, x * bytesPerSample, sampleFormat, bitsPerSample);
                if (!double.IsFinite(raw) || IsNoData(raw, noData))
                    continue;
                double elevation = (raw * scale) + offset;
                if (double.IsFinite(elevation))
                    samples.Add(new GeoTiffElevationSample(x, y, elevation));
            }
        }

        if (samples.Count < 3)
        {
            error = "The DEM contains fewer than three finite, non-NoData samples.";
            return false;
        }

        result = new GeoTiffElevationSamples
        {
            Width = width,
            Height = height,
            Stride = stride,
            Samples = samples
        };
        return true;
    }

    private static bool IsSupportedFormat(int sampleFormat, int bitsPerSample)
    {
        return sampleFormat switch
        {
            SampleFormatUnsigned or SampleFormatSigned => bitsPerSample is 8 or 16 or 32 or 64,
            SampleFormatFloat => bitsPerSample is 32 or 64,
            _ => false
        };
    }

    private static double ReadSample(byte[] bytes, int offset, int sampleFormat, int bitsPerSample)
    {
        ReadOnlySpan<byte> value = bytes.AsSpan(offset, bitsPerSample / 8);
        return (sampleFormat, bitsPerSample) switch
        {
            (SampleFormatUnsigned, 8) => value[0],
            (SampleFormatUnsigned, 16) => BitConverter.ToUInt16(value),
            (SampleFormatUnsigned, 32) => BitConverter.ToUInt32(value),
            (SampleFormatUnsigned, 64) => BitConverter.ToUInt64(value),
            (SampleFormatSigned, 8) => unchecked((sbyte)value[0]),
            (SampleFormatSigned, 16) => BitConverter.ToInt16(value),
            (SampleFormatSigned, 32) => BitConverter.ToInt32(value),
            (SampleFormatSigned, 64) => BitConverter.ToInt64(value),
            (SampleFormatFloat, 32) => BitConverter.ToSingle(value),
            (SampleFormatFloat, 64) => BitConverter.ToDouble(value),
            _ => double.NaN
        };
    }

    private static bool IsNoData(double value, double? noData)
    {
        if (!noData.HasValue)
            return false;
        return double.IsNaN(noData.Value) ? double.IsNaN(value) : value.Equals(noData.Value);
    }

    private static int GetRequiredInt(Tiff image, TiffTag tag)
    {
        FieldValue[]? field = image.GetField(tag);
        return field is { Length: > 0 } ? field[0].ToInt() : 0;
    }

    private static int GetDefaultedInt(Tiff image, TiffTag tag, int fallback)
    {
        FieldValue[]? field = image.GetFieldDefaulted(tag);
        return field is { Length: > 0 } ? field[0].ToInt() : fallback;
    }

    private static void ReadScaleOffset(string path, out double scale, out double offset)
    {
        scale = 1.0;
        offset = 0.0;
        string? metadata = TryReadAsciiTag(path, GdalMetadataTag);
        if (string.IsNullOrWhiteSpace(metadata))
            return;

        try
        {
            XElement root = XElement.Parse(metadata);
            foreach (XElement item in root.Descendants().Where(element => element.Name.LocalName == "Item"))
            {
                string name = ((string?)item.Attribute("name") ?? (string?)item.Attribute("role") ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();
                if (!double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                    !double.IsFinite(value))
                    continue;
                if (name is "SCALE" or "SCALEOFFSET")
                    scale = value;
                else if (name == "OFFSET")
                    offset = value;
            }
        }
        catch (System.Xml.XmlException)
        {
            // Malformed optional GDAL metadata does not invalidate otherwise readable samples.
        }
    }

    private static double? TryReadAsciiDoubleTag(string path, int tag)
    {
        string? text = TryReadAsciiTag(path, tag);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    /// <summary>Reads optional classic-TIFF ASCII tags that LibTiff does not register by default.</summary>
    private static string? TryReadAsciiTag(string path, int requestedTag)
    {
        return requestedTag is >= 0 and <= ushort.MaxValue
            ? ClassicTiffTagReader.ReadAscii(path, (ushort)requestedTag)
            : null;
    }
}
