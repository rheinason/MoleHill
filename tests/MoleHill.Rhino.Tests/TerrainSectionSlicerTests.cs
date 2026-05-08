using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainSectionSlicerTests
{
    [Fact]
    public void SliceFromPolylineIntersections_FlatProfileAcrossSegment_KeepsAllVertices()
    {
        var cut = new[] { new Point3d(0, 0, 0), new Point3d(10, 0, 0) };
        var slice = new Polyline
        {
            new Point3d(0, 0, 5),
            new Point3d(2.5, 0, 5),
            new Point3d(7.5, 0, 5),
            new Point3d(10, 0, 5)
        };
        var perSegment = new Polyline[]?[] { new[] { slice } };

        var result = TerrainSectionSlicer.SliceFromPolylineIntersections(cut, perSegment, tolerance: 1e-6);

        Assert.Single(result.Segments);
        Assert.Equal(4, result.Segments[0].Vertices.Count);
        Assert.Equal(0.0, result.Segments[0].Vertices[0].Station, 6);
        Assert.Equal(10.0, result.Segments[0].Vertices[3].Station, 6);
        Assert.Equal(10.0, result.TotalStationLength, 6);
        Assert.Equal(5.0, result.MinimumElevation, 6);
        Assert.Equal(5.0, result.MaximumElevation, 6);
    }

    [Fact]
    public void SliceFromPolylineIntersections_SlopedProfile_TracksElevationRange()
    {
        var cut = new[] { new Point3d(0, 0, 0), new Point3d(10, 0, 0) };
        var slice = new Polyline
        {
            new Point3d(0, 0, 0),
            new Point3d(5, 0, 5),
            new Point3d(10, 0, 10)
        };
        var perSegment = new Polyline[]?[] { new[] { slice } };

        var result = TerrainSectionSlicer.SliceFromPolylineIntersections(cut, perSegment, tolerance: 1e-6);

        Assert.Single(result.Segments);
        Assert.Equal(0.0, result.MinimumElevation, 6);
        Assert.Equal(10.0, result.MaximumElevation, 6);
        Assert.Equal(5.0, result.Segments[0].Vertices[1].Station, 6);
        Assert.Equal(5.0, result.Segments[0].Vertices[1].World.Z, 6);
    }

    [Fact]
    public void SliceFromPolylineIntersections_PolylineExtendsBeyondSegmentEnds_ClipsToBoundary()
    {
        var cut = new[] { new Point3d(2, 0, 0), new Point3d(8, 0, 0) };
        var slice = new Polyline
        {
            new Point3d(-5, 0, 1),
            new Point3d(15, 0, 1)
        };
        var perSegment = new Polyline[]?[] { new[] { slice } };

        var result = TerrainSectionSlicer.SliceFromPolylineIntersections(cut, perSegment, tolerance: 1e-6);

        Assert.Single(result.Segments);
        var vertices = result.Segments[0].Vertices;
        Assert.Equal(2, vertices.Count);
        Assert.Equal(0.0, vertices[0].Station, 6);
        Assert.Equal(6.0, vertices[1].Station, 6);
        Assert.Equal(2.0, vertices[0].World.X, 6);
        Assert.Equal(8.0, vertices[1].World.X, 6);
    }

    [Fact]
    public void SliceFromPolylineIntersections_PolylineEntirelyOutsideSegment_ReturnsNoSegments()
    {
        var cut = new[] { new Point3d(0, 0, 0), new Point3d(5, 0, 0) };
        var slice = new Polyline
        {
            new Point3d(20, 0, 1),
            new Point3d(25, 0, 1)
        };
        var perSegment = new Polyline[]?[] { new[] { slice } };

        var result = TerrainSectionSlicer.SliceFromPolylineIntersections(cut, perSegment, tolerance: 1e-6);

        Assert.Empty(result.Segments);
        Assert.Equal(5.0, result.TotalStationLength, 6);
    }

    [Fact]
    public void SliceFromPolylineIntersections_MultiSegmentCut_StationsCumulate()
    {
        var cut = new[]
        {
            new Point3d(0, 0, 0),
            new Point3d(10, 0, 0),
            new Point3d(10, 6, 0)
        };
        var slice0 = new Polyline { new Point3d(0, 0, 3), new Point3d(10, 0, 3) };
        var slice1 = new Polyline { new Point3d(10, 0, 3), new Point3d(10, 6, 3) };
        var perSegment = new Polyline[]?[]
        {
            new[] { slice0 },
            new[] { slice1 }
        };

        var result = TerrainSectionSlicer.SliceFromPolylineIntersections(cut, perSegment, tolerance: 1e-6);

        Assert.Equal(16.0, result.TotalStationLength, 6);
        Assert.Single(result.Segments);
        var vertices = result.Segments[0].Vertices;
        Assert.Equal(0.0, vertices[0].Station, 6);
        Assert.Equal(16.0, vertices[vertices.Count - 1].Station, 6);
    }

    [Fact]
    public void SliceFromPolylineIntersections_NullIntersectionForSegment_TreatedAsNoHit()
    {
        var cut = new[]
        {
            new Point3d(0, 0, 0),
            new Point3d(5, 0, 0),
            new Point3d(10, 0, 0)
        };
        var slice1 = new Polyline { new Point3d(5, 0, 2), new Point3d(10, 0, 2) };
        var perSegment = new Polyline[]?[] { null, new[] { slice1 } };

        var result = TerrainSectionSlicer.SliceFromPolylineIntersections(cut, perSegment, tolerance: 1e-6);

        Assert.Single(result.Segments);
        var vertices = result.Segments[0].Vertices;
        Assert.Equal(5.0, vertices[0].Station, 6);
        Assert.Equal(10.0, vertices[vertices.Count - 1].Station, 6);
    }

    [Fact]
    public void SliceFromPolylineIntersections_DegenerateInput_ReturnsEmpty()
    {
        var cut = new[] { new Point3d(0, 0, 0) };
        var perSegment = Array.Empty<Polyline[]?>();

        var result = TerrainSectionSlicer.SliceFromPolylineIntersections(cut, perSegment, tolerance: 1e-6);

        Assert.True(result.IsEmpty);
        Assert.Equal(0.0, result.TotalStationLength, 6);
    }
}
