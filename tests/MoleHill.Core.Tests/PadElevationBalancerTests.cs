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
    [Fact]
    public void TrySuggestRange_SlopedTerrain_BracketsAllCutToAllFill()
    {
        // Terrain rises 1 in 10 along X: z = x / 10 over a 100 x 100 square.
        double[] vertices = { 0, 0, 0, 100, 0, 10, 100, 100, 10, 0, 100, 0, 50, 50, 5 };
        int[] faces = { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4 };
        var pad = new PadGrader.PadBoundary(
            new[] { 40.0, 40.0, 60.0, 40.0, 60.0, 60.0, 40.0, 60.0 }, 4, targetZ: 100.0);

        bool found = PadElevationBalancer.TrySuggestRange(
            new IndexedTriMesh(vertices, 5, faces, 4), pad, out double minimum, out double maximum, marginFraction: 0);

        Assert.True(found);
        // Under the footprint the terrain runs from 4 to 6.
        Assert.Equal(4.0, minimum, 3);
        Assert.Equal(6.0, maximum, 3);
    }

    [Fact]
    public void TrySuggestRange_PadOffTheTerrain_ReturnsFalse()
    {
        double[] vertices = { 0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0 };
        int[] faces = { 0, 1, 2, 0, 2, 3 };
        var pad = new PadGrader.PadBoundary(
            new[] { 40.0, 40.0, 60.0, 40.0, 60.0, 60.0, 40.0, 60.0 }, 4, targetZ: 0.0);

        Assert.False(PadElevationBalancer.TrySuggestRange(
            new IndexedTriMesh(vertices, 4, faces, 2), pad, out _, out _));
    }
}
