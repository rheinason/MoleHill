using BitMiracle.LibTiff.Classic;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class GeoTiffElevationReaderTests
{
    [Fact]
    public void TryReadSamples_UInt16Band_PreservesNumericElevationsAboveDisplayRange()
    {
        string path = NewTemporaryTiffPath();
        try
        {
            WriteSingleBand(path, 2, 2, 16, SampleFormat.UINT, [1000, 2000, 3000, 4000]);

            bool read = GeoTiffElevationReader.TryReadSamples(path, 100, out GeoTiffElevationSamples? result, out string? error);

            Assert.True(read, error);
            Assert.NotNull(result);
            Assert.Equal([1000.0, 2000.0, 3000.0, 4000.0], result.Samples.Select(sample => sample.Elevation));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadSamples_Float32Band_PreservesSignedFractionalElevations()
    {
        string path = NewTemporaryTiffPath();
        try
        {
            WriteSingleBand(path, 2, 2, 32, SampleFormat.IEEEFP, [-12.5, 0.25, 1024.75, 8.5]);

            bool read = GeoTiffElevationReader.TryReadSamples(path, 100, out GeoTiffElevationSamples? result, out string? error);

            Assert.True(read, error);
            Assert.NotNull(result);
            Assert.Equal([-12.5, 0.25, 1024.75, 8.5], result.Samples.Select(sample => sample.Elevation));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadSamples_Int16Band_PreservesNegativeElevations()
    {
        string path = NewTemporaryTiffPath();
        try
        {
            WriteSingleBand(path, 2, 2, 16, SampleFormat.INT, [-120, -1, 0, 450]);

            bool read = GeoTiffElevationReader.TryReadSamples(path, 100, out GeoTiffElevationSamples? result, out string? error);

            Assert.True(read, error);
            Assert.NotNull(result);
            Assert.Equal([-120.0, -1.0, 0.0, 450.0], result.Samples.Select(sample => sample.Elevation));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadSamples_RgbImage_IsRejectedAsNonDem()
    {
        string path = NewTemporaryTiffPath();
        try
        {
            using (Tiff output = Tiff.Open(path, "w"))
            {
                SetCommonFields(output, 2, 2, 8, 3, SampleFormat.UINT, Photometric.RGB);
                output.WriteScanline(new byte[] { 1, 2, 3, 4, 5, 6 }, 0);
                output.WriteScanline(new byte[] { 7, 8, 9, 10, 11, 12 }, 1);
                output.WriteDirectory();
            }

            bool read = GeoTiffElevationReader.TryReadSamples(path, 100, out _, out string? error);

            Assert.False(read);
            Assert.Contains("not a DEM", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadSamples_GdalNoDataTag_ExcludesSentinelValue()
    {
        string path = NewTemporaryTiffPath();
        try
        {
            WriteClassicUInt16WithNoData(path);

            bool read = GeoTiffElevationReader.TryReadSamples(path, 100, out GeoTiffElevationSamples? result, out string? error);

            Assert.True(read, error);
            Assert.NotNull(result);
            Assert.Equal([1.0, 2.0, 3.0], result.Samples.Select(sample => sample.Elevation));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MetadataReader_FloatTiff_ReadsPlacementAndLinearUnitsWithoutGdiImage()
    {
        string path = NewTemporaryTiffPath();
        try
        {
            WriteClassicFloatWithGeoTags(path);

            bool read = GeoTiffMetadataReader.TryRead(
                path,
                out RasterGeoreference georeference,
                out _,
                out GeoTiffLinearUnit? units);

            Assert.True(read);
            Assert.Equal(1.0, units?.MetersPerUnit);
            Assert.Equal((100.0, 200.0), georeference.MapRasterPoint(0.5, 0.5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string NewTemporaryTiffPath() => Path.Combine(Path.GetTempPath(), $"MoleHill-{Guid.NewGuid():N}.tif");

    private static void WriteSingleBand(
        string path,
        int width,
        int height,
        int bitsPerSample,
        SampleFormat sampleFormat,
        IReadOnlyList<double> values)
    {
        using Tiff output = Tiff.Open(path, "w");
        SetCommonFields(output, width, height, bitsPerSample, 1, sampleFormat, Photometric.MINISBLACK);
        int bytesPerSample = bitsPerSample / 8;
        for (int y = 0; y < height; y++)
        {
            var scanline = new byte[width * bytesPerSample];
            for (int x = 0; x < width; x++)
                WriteSample(scanline, x * bytesPerSample, values[(y * width) + x], bitsPerSample, sampleFormat);
            output.WriteScanline(scanline, y);
        }
        output.WriteDirectory();
    }

    private static void SetCommonFields(
        Tiff output,
        int width,
        int height,
        int bitsPerSample,
        int samplesPerPixel,
        SampleFormat sampleFormat,
        Photometric photometric)
    {
        output.SetField(TiffTag.IMAGEWIDTH, width);
        output.SetField(TiffTag.IMAGELENGTH, height);
        output.SetField(TiffTag.BITSPERSAMPLE, bitsPerSample);
        output.SetField(TiffTag.SAMPLESPERPIXEL, samplesPerPixel);
        output.SetField(TiffTag.SAMPLEFORMAT, sampleFormat);
        output.SetField(TiffTag.PHOTOMETRIC, photometric);
        output.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        output.SetField(TiffTag.COMPRESSION, Compression.NONE);
        output.SetField(TiffTag.ROWSPERSTRIP, height);
    }

    private static void WriteSample(byte[] buffer, int offset, double value, int bits, SampleFormat format)
    {
        byte[] bytes = (format, bits) switch
        {
            (SampleFormat.UINT, 16) => BitConverter.GetBytes((ushort)value),
            (SampleFormat.INT, 16) => BitConverter.GetBytes((short)value),
            (SampleFormat.IEEEFP, 32) => BitConverter.GetBytes((float)value),
            _ => throw new NotSupportedException()
        };
        bytes.CopyTo(buffer, offset);
    }

    private static void WriteClassicUInt16WithNoData(string path)
    {
        const ushort entryCount = 11;
        const uint ifdOffset = 8;
        const uint noDataOffset = ifdOffset + 2 + (entryCount * 12) + 4;
        const uint pixelOffset = noDataOffset + 6;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write(ifdOffset);
        writer.Write(entryCount);
        WriteIfdEntry(writer, 256, 4, 1, 2); // ImageWidth
        WriteIfdEntry(writer, 257, 4, 1, 2); // ImageLength
        WriteIfdEntry(writer, 258, 3, 1, 16); // BitsPerSample
        WriteIfdEntry(writer, 259, 3, 1, 1); // Compression=None
        WriteIfdEntry(writer, 262, 3, 1, 1); // Photometric=MinIsBlack
        WriteIfdEntry(writer, 273, 4, 1, pixelOffset); // StripOffsets
        WriteIfdEntry(writer, 277, 3, 1, 1); // SamplesPerPixel
        WriteIfdEntry(writer, 278, 4, 1, 2); // RowsPerStrip
        WriteIfdEntry(writer, 279, 4, 1, 8); // StripByteCounts
        WriteIfdEntry(writer, 339, 3, 1, 1); // SampleFormat=unsigned
        WriteIfdEntry(writer, 42113, 2, 6, noDataOffset); // GDAL_NODATA
        writer.Write(0u);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("65535\0"));
        writer.Write((ushort)1);
        writer.Write(ushort.MaxValue);
        writer.Write((ushort)2);
        writer.Write((ushort)3);
    }

    private static void WriteClassicFloatWithGeoTags(string path)
    {
        const ushort entryCount = 13;
        const uint ifdOffset = 8;
        const uint scaleOffset = ifdOffset + 2 + (entryCount * 12) + 4;
        const uint tiepointOffset = scaleOffset + 24;
        const uint geoKeyOffset = tiepointOffset + 48;
        const uint pixelOffset = geoKeyOffset + 24;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write(ifdOffset);
        writer.Write(entryCount);
        WriteIfdEntry(writer, 256, 4, 1, 2);
        WriteIfdEntry(writer, 257, 4, 1, 2);
        WriteIfdEntry(writer, 258, 3, 1, 32);
        WriteIfdEntry(writer, 259, 3, 1, 1);
        WriteIfdEntry(writer, 262, 3, 1, 1);
        WriteIfdEntry(writer, 273, 4, 1, pixelOffset);
        WriteIfdEntry(writer, 277, 3, 1, 1);
        WriteIfdEntry(writer, 278, 4, 1, 2);
        WriteIfdEntry(writer, 279, 4, 1, 16);
        WriteIfdEntry(writer, 339, 3, 1, 3);
        WriteIfdEntry(writer, 33550, 12, 3, scaleOffset);
        WriteIfdEntry(writer, 33922, 12, 6, tiepointOffset);
        WriteIfdEntry(writer, 34735, 3, 12, geoKeyOffset);
        writer.Write(0u);
        foreach (double value in new[] { 2.0, 3.0, 0.0 })
            writer.Write(value);
        foreach (double value in new[] { 0.0, 0.0, 0.0, 100.0, 200.0, 0.0 })
            writer.Write(value);
        foreach (ushort value in new ushort[] { 1, 1, 0, 2, 1025, 0, 1, 2, 3076, 0, 1, 9001 })
            writer.Write(value);
        for (int index = 0; index < 4; index++)
            writer.Write(0.0f);
    }

    private static void WriteIfdEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(value);
    }
}
