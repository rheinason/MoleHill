using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class RetainingWallMeshGraderTests
{
    [Fact]
    public void GradeSingleWall_InvalidStationCount_ReturnsFailure()
    {
        var strip = new RetainingWallMeshGrader.WallStripDefinition(
            toeXy: new[] { 0.0, 0.0 },
            toeZ: new[] { 0.0 },
            topXy: new[] { 1.0, 0.0 },
            topZ: new[] { 1.0 },
            stationCount: 1);

        var outcome = RetainingWallMeshGrader.GradeSingleWall(
            BuildFlatMeshVertices(),
            4,
            BuildFlatMeshFaces(),
            2,
            strip,
            sharpness: 0.5,
            shoulderWidth: 1.0);

        Assert.False(outcome.GradeApplied);
        Assert.Null(outcome.MeshResult);
        Assert.Contains("at least 2", outcome.WarningOrError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GradeSingleWall_ValidStrip_ChangesVertexElevations()
    {
        var strip = new RetainingWallMeshGrader.WallStripDefinition(
            toeXy: new[] { 0.0, 0.25, 1.0, 0.25 },
            toeZ: new[] { 0.0, 0.0 },
            topXy: new[] { 0.0, 0.75, 1.0, 0.75 },
            topZ: new[] { 2.0, 2.0 },
            stationCount: 2);

        var outcome = RetainingWallMeshGrader.GradeSingleWall(
            BuildFlatMeshVertices(),
            4,
            BuildFlatMeshFaces(),
            2,
            strip,
            sharpness: 0.5,
            shoulderWidth: 1.0);

        Assert.True(outcome.GradeApplied);
        Assert.NotNull(outcome.MeshResult);
        Assert.NotEmpty(outcome.MeshResult!.Vertices);

        bool anyRaised = false;
        for (int i = 0; i < outcome.MeshResult.VertexCount; i++)
        {
            if (outcome.MeshResult.Vertices[i * 3 + 2] > 0.01)
            {
                anyRaised = true;
                break;
            }
        }

        Assert.True(anyRaised);
    }

    [Fact]
    public void GradeSingleWall_SharpnessAffectsProfile()
    {
        var strip = new RetainingWallMeshGrader.WallStripDefinition(
            toeXy: new[] { 0.0, 0.25, 1.0, 0.25 },
            toeZ: new[] { 0.0, 0.0 },
            topXy: new[] { 0.0, 0.75, 1.0, 0.75 },
            topZ: new[] { 2.0, 2.0 },
            stationCount: 2);

        var lowSharp = RetainingWallMeshGrader.GradeSingleWall(
            BuildFlatMeshVertices(), 4, BuildFlatMeshFaces(), 2, strip, 0.0, 0.0);
        var highSharp = RetainingWallMeshGrader.GradeSingleWall(
            BuildFlatMeshVertices(), 4, BuildFlatMeshFaces(), 2, strip, 1.0, 0.0);

        Assert.True(lowSharp.GradeApplied && highSharp.GradeApplied);

        double sumLow = SumZ(lowSharp.MeshResult!);
        double sumHigh = SumZ(highSharp.MeshResult!);
        Assert.NotEqual(sumLow, sumHigh);
    }

    private static double SumZ(GradingResult result)
    {
        double sum = 0;
        for (int i = 0; i < result.VertexCount; i++)
            sum += result.Vertices[i * 3 + 2];
        return sum;
    }

    private static double[] BuildFlatMeshVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0
        };
    }

    private static int[] BuildFlatMeshFaces()
    {
        return new[]
        {
            0, 1, 2,
            0, 2, 3
        };
    }
}
