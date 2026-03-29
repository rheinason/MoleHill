using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class TriangulationHelperTests
{
    [Fact]
    public void Triangulate_SimpleSquare_SucceedsWithoutFlags()
    {
        var outcome = TriangulationHelper.Triangulate(
            new List<double>
            {
                0.0, 0.0,
                10.0, 0.0,
                10.0, 10.0,
                0.0, 10.0
            },
            vertexCount: 4,
            new List<(int a, int b)>(),
            maxArea: 0.0,
            minAngle: 0.0,
            convex: true);

        Assert.NotNull(outcome.Mesh);
        Assert.Equal(TriangulationWarningFlags.None, outcome.Flags);
        Assert.Null(outcome.WarningMessage);
        Assert.Empty(outcome.FailureDetails);
    }

    [Fact]
    public void Triangulate_AllAttemptsFail_ReturnsFailureDetails()
    {
        var outcome = TriangulationHelper.Triangulate(
            new List<double>
            {
                0.0, 0.0,
                1.0, 0.0,
                2.0, 0.0
            },
            vertexCount: 3,
            new List<(int a, int b)>(),
            maxArea: 0.0,
            minAngle: 0.0,
            convex: true);

        Assert.Null(outcome.Mesh);
        Assert.Contains("All triangulation attempts failed", outcome.WarningMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.NotEmpty(outcome.FailureDetails);
    }
}
