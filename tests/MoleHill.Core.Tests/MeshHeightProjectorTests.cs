using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshHeightProjectorTests
{
    [Fact]
    public void TryProjectZ_InsideSingleFace_InterpolatesHeight()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            2.0, 0.0, 2.0,
            0.0, 2.0, 4.0
        };
        int[] faces = { 0, 1, 2 };
        var projector = new MeshHeightProjector(vertices, 3, faces, 1);

        bool projected = projector.TryProjectZ(
            0.5,
            0.5,
            sampleZ: 0.0,
            tolerance: 1e-8,
            out double z,
            out var status);

        Assert.True(projected);
        Assert.Equal(MeshHeightProjector.ProjectionStatus.Projected, status);
        Assert.Equal(1.5, z, precision: 12);
    }

    [Fact]
    public void TryProjectZ_OverlappingDistinctHeights_RequiresFallback()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            0.0, 0.0, 10.0,
            1.0, 0.0, 10.0,
            0.0, 1.0, 10.0
        };
        int[] faces =
        {
            0, 1, 2,
            3, 4, 5
        };
        var projector = new MeshHeightProjector(vertices, 6, faces, 2);

        bool projected = projector.TryProjectZ(
            0.25,
            0.25,
            sampleZ: 9.0,
            tolerance: 1e-6,
            out _,
            out var status);

        Assert.False(projected);
        Assert.Equal(MeshHeightProjector.ProjectionStatus.RequiresFallback, status);
    }

    [Fact]
    public void TryProjectZ_NearVerticalFaceFootprint_RequiresFallback()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            0.0, 0.0, 10.0
        };
        int[] faces = { 0, 1, 2 };
        var projector = new MeshHeightProjector(vertices, 3, faces, 1);

        bool projected = projector.TryProjectZ(
            0.0,
            0.5,
            sampleZ: 5.0,
            tolerance: 1e-6,
            out _,
            out var status);

        Assert.False(projected);
        Assert.Equal(MeshHeightProjector.ProjectionStatus.RequiresFallback, status);
    }

    [Theory]
    [InlineData(0.000000001)]
    [InlineData(0.001)]
    [InlineData(1.0)]
    [InlineData(1000.0)]
    [InlineData(1000000000.0)]
    public void TryProjectZ_UniformlyScaledGeometry_ReturnsScaledHeight(double scale)
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            2.0 * scale, 0.0, 2.0 * scale,
            0.0, 2.0 * scale, 4.0 * scale
        };
        int[] faces = { 0, 1, 2 };
        var projector = new MeshHeightProjector(vertices, 3, faces, 1);

        bool projected = projector.TryProjectZ(
            0.5 * scale,
            0.5 * scale,
            sampleZ: 0.0,
            tolerance: 1e-8 * scale,
            out double z,
            out var status);

        Assert.True(projected);
        Assert.Equal(MeshHeightProjector.ProjectionStatus.Projected, status);
        Assert.Equal(1.5 * scale, z, precision: 10);
    }
}
