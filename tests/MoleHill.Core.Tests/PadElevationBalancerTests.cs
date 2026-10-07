using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class PadElevationBalancerTests
{
    [Fact]
    public void Balance_FlatTerrain_ReturnsActualGradeAndTranslatedBoundary()
    {
        double[] vertices =
        {
            0, 0, 0, 100, 0, 0, 100, 100, 0, 0, 100, 0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };
        var pad = new PadGrader.PadBoundary(
            new[] { 40.0, 40.0, 60.0, 40.0, 60.0, 60.0, 40.0, 60.0 },
            4, targetZ: 0.0, slopeAngleDeg: 45.0, maxDistance: 15.0);

        PadElevationBalanceResult result = PadElevationBalancer.Balance(new IndexedTriMesh(vertices, 4, faces, 2), pad, null, minimumElevation: -1, maximumElevation: 1, targetNet: 0, volumeTolerance: 0.1, iterationCap: 8, modelTolerance: 0.001);

        Assert.NotNull(result.Search.Best);
        Assert.NotNull(result.BestGrading);
        Assert.Equal(-1, result.Search.Samples[0].Elevation);
        Assert.Equal(1, result.Search.Samples[1].Elevation);
        Assert.Equal(result.Search.Best.Value.Elevation, result.AdjustedBoundaryVertices![2], 6);
        Assert.Equal(0, pad.BoundaryVertices[2]);
        Assert.Equal(0, vertices[2]);
    }
}
