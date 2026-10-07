using MoleHill.Core.IO;
using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class RasterGeoreferenceTests
{
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
