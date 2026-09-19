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
        GradingResult? result = PathGrader.Grade(
            vertices, vertexCount, faces, faceCount, paths,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            out _, 0.01, false);

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
        double expected = 1.80 + ((SectionX / 11.0) * (2.08 - 1.80));
        Assert.Equal(expected, SampleZ(result!, graded, SectionX, 0.0), precision: 2);
    }

    /// <summary>
    /// At angles steep enough to reach existing ground inside the section search extent, the batter
    /// leaves the rail at exactly the requested slope.
    /// </summary>
    [Theory]
    [InlineData(45.0, 1.0000)]
    [InlineData(60.0, 1.7321)]
    public void Grade_AtSteepAngles_LeavesTheRailAtTheRequestedSlope(double angle, double expectedTangent)
    {
        var (result, graded) = GradeAt(angle);
        Assert.NotNull(result);

        double z0 = SampleZ(result!, graded, SectionX, 0.10);
        double z1 = SampleZ(result!, graded, SectionX, 0.25);
        double measured = (z1 - z0) / 0.15;

        Assert.Equal(expectedTangent, measured, precision: 3);
    }

    /// <summary>
    /// KNOWN DEFECT, pinned so it cannot change unnoticed. A shallow batter needs a longer run to reach
    /// ground than the section search allows, so it is clamped and leaves the rail far steeper than
    /// asked. 20 and 30 degrees produce the same surface as each other, which is the tell: the
    /// requested slope is not governing the result at all.
    ///
    /// Grading reports this ("the slope was clamped to the search extent"), so it is declared rather
    /// than silent -- but a 20 degree wall batter that is built at roughly 46 degrees is still wrong.
    /// When the extent is fixed, these assertions should fail and be replaced by the exact-slope test
    /// above.
    /// </summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(30.0)]
    public void Grade_AtShallowAngles_IsClampedSteeperThanRequested(double angle)
    {
        var (result, graded) = GradeAt(angle);
        Assert.NotNull(result);

        double requestedTangent = Math.Tan(angle * Math.PI / 180.0);
        double z0 = SampleZ(result!, graded, SectionX, 0.10);
        double z1 = SampleZ(result!, graded, SectionX, 0.25);
        double measured = (z1 - z0) / 0.15;

        Assert.True(
            measured > requestedTangent * 1.5,
            $"{angle} deg now grades at tan={measured:F4} against a requested tan={requestedTangent:F4}. " +
            "If the section search extent was fixed, replace this test with an exact-slope assertion.");
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
