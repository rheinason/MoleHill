using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Measures the graded surface instead of counting its faces. Topology counts cannot tell a correct
/// batter from a wrong one, and every bug in this area has been a plausible-looking wrong answer
/// rather than a crash.
///
/// The section is taken across rail 2 (wall 1's toe, running along y=0 and grading outward to +y) at
/// x = 5, sampling the graded surface directly.
/// </summary>
public class RetainingWallRailBatterSectionTests
{
    private const double SectionX = 5.0;

    private static (GradingResult? result, double[] gradedVertices) GradeAt(double angle)
    {
        var (vertices, vertexCount, faces, faceCount, paths) = RetainingWallRailCaseData.Build(angle);
        GradeOutcome gradeOutcome = PathGrader.Grade(new PathGradeRequest
        {
            Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
            Paths = paths,
            HardConstraints = Array.Empty<ConstraintPolyline>(),
            ModelTolerance = 0.01,
            PreferSplitKeep = false,
        });
        GradingResult? result = gradeOutcome.Result;

        if (result == null)
            return (null, Array.Empty<double>());

        var graded = new double[result.VertexCount * 3];
        Array.Copy(result.Vertices, graded, graded.Length);
        return (result, graded);
    }

    private static double SampleZ(GradingResult result, double[] graded, double x, double y)
        => new TerrainFaceGrid(graded, result.VertexCount, result.Faces, result.FaceCount).InterpolateZ(x, y);

    /// <summary>
    /// The rail must sit at the elevation it was authored with. If a grade quietly welds the rail down
    /// to existing ground the batter above it can still look perfect, so this is checked on its own.
    /// </summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(45.0)]
    public void Grade_PinsTheRailToItsAuthoredElevation(double angle)
    {
        var (result, graded) = GradeAt(angle);
        Assert.NotNull(result);

        // Rail 2 runs (0,0,z=1.80) -> (11,0,z=2.08); at x=5 that interpolates to 1.9273.
        // Compared against the model tolerance, not a decimal place: xUnit's precision overload rounds
        // both values (to even), so a value 0.002 away can still fail when it straddles a .xx5 boundary.
        double expected = 1.80 + ((SectionX / 11.0) * (2.08 - 1.80));
        double measured = SampleZ(result!, graded, SectionX, 0.0);
        Assert.True(
            Math.Abs(measured - expected) <= 0.01,
            $"{angle} deg put the rail at {measured:F4} instead of its authored {expected:F4}.");
    }

    /// <summary>
    /// The batter leaves the rail at exactly the requested slope, shallow angles included. 30 degrees
    /// joined once one-sided rails were stationed between their authored vertices (it had measured tan
    /// 1.0116); 10 and 20 degrees once a single line's elevation sections were spaced by its batter reach
    /// rather than left at its drawn vertices (they had measured .5330 and .4491, cut short of daylight).
    /// </summary>
    [Theory]
    [InlineData(10.0, 0.1763)]
    [InlineData(20.0, 0.3640)]
    [InlineData(30.0, 0.5774)]
    [InlineData(45.0, 1.0000)]
    [InlineData(60.0, 1.7321)]
    public void Grade_AtAnyAngle_LeavesTheRailAtTheRequestedSlope(double angle, double expectedTangent)
    {
        var (result, graded) = GradeAt(angle);
        Assert.NotNull(result);

        double z0 = SampleZ(result!, graded, SectionX, 0.10);
        double z1 = SampleZ(result!, graded, SectionX, 0.25);
        double measured = (z1 - z0) / 0.15;

        Assert.Equal(expectedTangent, measured, precision: 3);
    }

    /// <summary>The batter must actually return to existing ground, not stop in mid-air.</summary>
    [Theory]
    [InlineData(20.0)]
    [InlineData(45.0)]
    public void Grade_BatterDaylightsBackToExistingGround(double angle)
    {
        var (vertices, vertexCount, faces, faceCount, _) = RetainingWallRailCaseData.Build(angle);
        double existing = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount).InterpolateZ(SectionX, 2.0);

        var (result, graded) = GradeAt(angle);
        Assert.NotNull(result);

        Assert.Equal(existing, SampleZ(result!, graded, SectionX, 2.0), precision: 3);
    }
}
