using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class RasterGeoreferenceTests
{
    [Fact]
    public void TryReadWorldFile_PreservesAffineTermsAndConvertsPixelCenterToCorner()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, new[] { "2", "1", "0.5", "-3", "100", "200" });

            Assert.True(RasterGeoreference.TryReadWorldFile(path, out var georef, out string? error), error);
            Assert.Equal(2.0, georef.XPixel, 12);
            Assert.Equal(0.5, georef.XLine, 12);
            Assert.Equal(1.0, georef.YPixel, 12);
            Assert.Equal(-3.0, georef.YLine, 12);
            Assert.Equal(98.75, georef.XUpperLeft, 12);
            Assert.Equal(201.0, georef.YUpperLeft, 12);

            var mapped = georef.MapRasterPoint(10.0, 20.0);
            Assert.Equal(128.75, mapped.X, 12);
            Assert.Equal(151.0, mapped.Y, 12);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryCreateFromModelTransformation_ReadsFullAffine()
    {
        double[] matrix =
        {
            2.0, 0.5, 0.0, 100.0,
            1.0, -3.0, 0.0, 200.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0
        };

        Assert.True(RasterGeoreference.TryCreateFromModelTransformation(
            matrix, pixelIsPoint: false, out var georef));
        Assert.Equal((100.0, 200.0), georef.MapRasterPoint(0.0, 0.0));
        Assert.Equal((130.0, 150.0), georef.MapRasterPoint(10.0, 20.0));
    }

    [Fact]
    public void TryCreateFromPixelScaleAndTiepoint_ResolvesTiepointOffset()
    {
        Assert.True(RasterGeoreference.TryCreateFromPixelScaleAndTiepoint(
            new[] { 2.0, 3.0, 0.0 },
            new[] { 10.0, 20.0, 0.0, 1000.0, 2000.0, 0.0 },
            pixelIsPoint: false,
            out var georef));

        Assert.Equal((1000.0, 2000.0), georef.MapRasterPoint(10.0, 20.0));
        Assert.Equal((980.0, 2060.0), georef.MapRasterPoint(0.0, 0.0));
    }

    [Fact]
    public void CreatePictureFrameToWorldTransform_MapsLowerLeftToBottomRasterEdge()
    {
        var georef = new RasterGeoreference(2.0, 0.5, 1.0, -3.0, 98.75, 201.0);

        Transform transform = georef.CreatePictureFrameToWorldTransform(imageHeight: 100);

        Assert.Equal(148.75, transform.M03, 12);
        Assert.Equal(-99.0, transform.M13, 12);
        Assert.Equal(2.0, transform.M00, 12);
        Assert.Equal(-0.5, transform.M01, 12);
        Assert.Equal(1.0, transform.M10, 12);
        Assert.Equal(3.0, transform.M11, 12);
    }

    [Fact]
    public void TryReadWorldFile_DegenerateAffineFailsCleanly()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path, new[] { "1", "2", "2", "4", "100", "200" });

            Assert.False(RasterGeoreference.TryReadWorldFile(path, out _, out string? error));
            Assert.Contains("degenerate", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComposeUpdatedLocalToWorld_PostComposesNewLocalInverse()
    {
        Transform existing = Transform.Identity;
        existing.M00 = 0.0;
        existing.M01 = -1.0;
        existing.M03 = 1000.0;
        existing.M10 = 1.0;
        existing.M11 = 0.0;
        existing.M13 = 2000.0;

        Transform inverseNewLocal = Transform.Identity;
        inverseNewLocal.M03 = 20.0;
        inverseNewLocal.M13 = -10.0;

        Transform composed = ProjectBaseCPlaneService.ComposeUpdatedLocalToWorld(existing, inverseNewLocal);

        Assert.Equal(1010.0, composed.M03, 12);
        Assert.Equal(2020.0, composed.M13, 12);
        Assert.Equal(0.0, composed.M00, 12);
        Assert.Equal(-1.0, composed.M01, 12);
    }
}
