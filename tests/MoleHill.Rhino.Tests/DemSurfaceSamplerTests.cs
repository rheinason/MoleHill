using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class DemSurfaceSamplerTests
{
    [RhinoNativeFact]
    public void TrySample_PlanarSurface_MapsRasterCentresAndAddsElevationFromSurface()
    {
        var plane = new Plane(new Point3d(1000.0, 2000.0, -50.0), Vector3d.XAxis, Vector3d.YAxis);
        var surface = new PlaneSurface(plane, new Interval(0.0, 100.0), new Interval(0.0, 50.0));
        var raster = new GeoTiffElevationSamples
        {
            Width = 100,
            Height = 50,
            Stride = 1,
            Samples =
            [
                new GeoTiffElevationSample(0, 0, 60.0),
                new GeoTiffElevationSample(99, 49, 80.0),
                new GeoTiffElevationSample(50, 25, 70.0)
            ]
        };

        bool sampled = DemSurfaceSampler.TrySample(surface, raster, 1.0, out List<Point3d> points, out string? error);

        Assert.True(sampled, error);
        Assert.Equal(new Point3d(1000.5, 2049.5, 10.0), points[0]);
        Assert.Equal(new Point3d(1099.5, 2000.5, 30.0), points[1]);
    }

    [RhinoNativeFact]
    public void TrySample_NonSurfaceInput_FailsCleanly()
    {
        var raster = new GeoTiffElevationSamples
        {
            Width = 1,
            Height = 3,
            Stride = 1,
            Samples =
            [
                new GeoTiffElevationSample(0, 0, 1.0),
                new GeoTiffElevationSample(0, 1, 2.0),
                new GeoTiffElevationSample(0, 2, 3.0)
            ]
        };

        Assert.False(DemSurfaceSampler.TrySample(new Point(Point3d.Origin), raster, 1.0, out _, out string? error));
        Assert.Contains("surface", error, StringComparison.OrdinalIgnoreCase);
    }
}
