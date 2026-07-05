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
}
