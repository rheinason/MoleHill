using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class AspectAnalyzerTests
{
    /// <summary>North as +Y, which is what every plan drawing assumes and what the default is.</summary>
    private const double NorthIsPlusY = 90.0;

    /// <summary>
    /// One triangle in the XY plane tilted so it drains towards <paramref name="descentX"/>,
    /// <paramref name="descentY"/>: the low corner sits in that direction from the high ones.
    /// </summary>
    private static (double[] Vertices, int[] Faces) TiltedTriangle(double descentX, double descentY)
    {
        // A plane z = -(descent · (x, y)) falls fastest in the descent direction, by construction.
        double Z(double x, double y) => -((descentX * x) + (descentY * y));

        var vertices = new[]
        {
            0.0, 0.0, Z(0.0, 0.0),
            10.0, 0.0, Z(10.0, 0.0),
            0.0, 10.0, Z(0.0, 10.0)
        };

        return (vertices, new[] { 0, 1, 2 });
    }

    [Theory]
    // descent direction        expected bearing with north at +Y
    [InlineData(0.0, 1.0, 0.0)]     // drains north
    [InlineData(1.0, 0.0, 90.0)]    // drains east
    [InlineData(0.0, -1.0, 180.0)]  // drains south
    [InlineData(-1.0, 0.0, 270.0)]  // drains west
    [InlineData(1.0, 1.0, 45.0)]    // drains north-east
    public void Analyze_FaceDrainingTowardsACardinal_ReportsThatBearing(
        double descentX, double descentY, double expectedBearing)
    {
        var (vertices, faces) = TiltedTriangle(descentX, descentY);

        AspectAnalyzer.AspectResult result = AspectAnalyzer.Analyze(
            vertices, 3, faces, 1, NorthIsPlusY, flatSlopeRatio: 0.0);

        Assert.Equal(expectedBearing, result.Bearings[0], 6);
    }

    /// <summary>
    /// Winding is not guaranteed anywhere in the pipeline, and the direction water runs cannot depend on
    /// which way a triangle happens to be wound.
    /// </summary>
    [Fact]
    public void Analyze_ReversedWinding_ReportsTheSameBearing()
    {
        var (vertices, faces) = TiltedTriangle(1.0, 0.0);
        var reversed = new[] { faces[0], faces[2], faces[1] };

        double forward = AspectAnalyzer.Analyze(vertices, 3, faces, 1, NorthIsPlusY, 0.0).Bearings[0];
        double backward = AspectAnalyzer.Analyze(vertices, 3, reversed, 1, NorthIsPlusY, 0.0).Bearings[0];

        Assert.Equal(forward, backward, 6);
    }

    /// <summary>North comes from the document, so rotating it rotates every bearing by the same amount.</summary>
    [Fact]
    public void Analyze_RotatingNorth_ShiftsEveryBearingEqually()
    {
        var (vertices, faces) = TiltedTriangle(1.0, 0.5);

        double atDefault = AspectAnalyzer.Analyze(vertices, 3, faces, 1, NorthIsPlusY, 0.0).Bearings[0];
        double atRotated = AspectAnalyzer.Analyze(vertices, 3, faces, 1, NorthIsPlusY + 30.0, 0.0).Bearings[0];

        Assert.Equal(AspectAnalyzer.Normalize360(atDefault + 30.0), atRotated, 6);
    }

    /// <summary>
    /// A level face has no aspect. Giving it one would paint a graded pad — the flattest and largest thing
    /// in most documents — in whatever direction its triangulation noise happened to point.
    /// </summary>
    [Fact]
    public void Analyze_LevelFace_IsFlaggedFlatRatherThanGivenABearing()
    {
        var vertices = new[] { 0.0, 0.0, 4.0, 10.0, 0.0, 4.0, 0.0, 10.0, 4.0 };
        var faces = new[] { 0, 1, 2 };

        AspectAnalyzer.AspectResult result = AspectAnalyzer.Analyze(
            vertices, 3, faces, 1, NorthIsPlusY, flatSlopeRatio: 0.01);

        Assert.True(double.IsNaN(result.Bearings[0]));
        Assert.Equal(1, result.Summary.FlatFaceCount);
        Assert.Null(result.Summary.DominantBearing);
    }

    /// <summary>A face sloping less than the threshold is flat; the same face is not, once the threshold drops.</summary>
    [Fact]
    public void Analyze_FlatThreshold_DecidesWhetherAGentleFaceHasAnAspect()
    {
        // Falls 0.05 over 10 — a 0.5% grade.
        var (vertices, faces) = TiltedTriangle(0.005, 0.0);

        Assert.True(double.IsNaN(
            AspectAnalyzer.Analyze(vertices, 3, faces, 1, NorthIsPlusY, flatSlopeRatio: 0.01).Bearings[0]));
        Assert.False(double.IsNaN(
            AspectAnalyzer.Analyze(vertices, 3, faces, 1, NorthIsPlusY, flatSlopeRatio: 0.001).Bearings[0]));
    }

    /// <summary>A vertical face has an infinite slope and a perfectly well-defined aspect all the same.</summary>
    [Fact]
    public void Analyze_VerticalFace_StillReportsABearing()
    {
        // A wall face in the XZ plane; its normal points along -Y, so it faces south.
        var vertices = new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 0.0, 0.0, 0.0, 5.0 };
        var faces = new[] { 0, 1, 2 };

        double bearing = AspectAnalyzer.Analyze(
            vertices, 3, faces, 1, NorthIsPlusY, flatSlopeRatio: 0.5).Bearings[0];

        Assert.False(double.IsNaN(bearing));
        Assert.True(bearing is 0.0 or 180.0, $"Expected a due north or south wall face, got {bearing}.");
    }

    /// <summary>
    /// The mean of 350° and 10° is north, not south. A plain numeric average gets this exactly backwards,
    /// which is why the summary takes a circular mean.
    /// </summary>
    [Fact]
    public void Summarize_BearingsEitherSideOfNorth_AveragesToNorth()
    {
        var (v1, f1) = TiltedTriangle(Math.Sin(-10.0 * Math.PI / 180.0), Math.Cos(-10.0 * Math.PI / 180.0));
        var (v2, f2) = TiltedTriangle(Math.Sin(10.0 * Math.PI / 180.0), Math.Cos(10.0 * Math.PI / 180.0));

        // Two identical triangles, one per field, averaged by hand through a combined mesh.
        var vertices = v1.Concat(v2).ToArray();
        var faces = new[] { f1[0], f1[1], f1[2], f2[0] + 3, f2[1] + 3, f2[2] + 3 };

        AspectAnalyzer.AspectSummary summary = AspectAnalyzer.Summarize(
            vertices, 6, faces, 2, NorthIsPlusY, flatSlopeRatio: 0.0);

        Assert.NotNull(summary.DominantBearing);
        double dominant = summary.DominantBearing.Value;

        // Signed offset from north, so 359.9 reads as -0.1 rather than as almost a full turn away.
        double signedFromNorth = AspectAnalyzer.Normalize360(dominant + 180.0) - 180.0;
        Assert.Equal(0.0, signedFromNorth, 6);
        Assert.Equal("N", AspectAnalyzer.SectorName(summary.DominantBearing));
    }

    [Theory]
    [InlineData(0.0, "N")]
    [InlineData(10.0, "N")]
    [InlineData(350.0, "N")]
    [InlineData(45.0, "NE")]
    [InlineData(135.0, "SE")]
    [InlineData(180.0, "S")]
    [InlineData(270.0, "W")]
    [InlineData(315.0, "NW")]
    public void SectorName_NamesTheNearestCardinal(double bearing, string expected)
    {
        Assert.Equal(expected, AspectAnalyzer.SectorName(bearing));
    }

    [Fact]
    public void SectorName_NonFiniteBearing_HasNoSector()
    {
        Assert.Equal("—", AspectAnalyzer.SectorName(double.NaN));
    }

    /// <summary>The range is never allowed to land on 360, which is outside [0, 360).</summary>
    [Theory]
    [InlineData(360.0, 0.0)]
    [InlineData(720.0, 0.0)]
    [InlineData(-90.0, 270.0)]
    [InlineData(361.0, 1.0)]
    public void Normalize360_WrapsWithoutLandingOnAFullTurn(double input, double expected)
    {
        Assert.Equal(expected, AspectAnalyzer.Normalize360(input), 9);
    }

    /// <summary>
    /// Flat faces must not colour as though they faced north: they take the flat colour, and every other
    /// face takes a colour off the wheel.
    /// </summary>
    [Fact]
    public void Analyze_FlatAndSlopingFaces_ColourDifferently()
    {
        var (sloping, slopingFaces) = TiltedTriangle(1.0, 0.0);
        var level = new[] { 0.0, 0.0, 4.0, 10.0, 0.0, 4.0, 0.0, 10.0, 4.0 };

        var vertices = sloping.Concat(level).ToArray();
        var faces = new[] { slopingFaces[0], slopingFaces[1], slopingFaces[2], 3, 4, 5 };

        AspectAnalyzer.AspectResult result = AspectAnalyzer.Analyze(
            vertices, 6, faces, 2, NorthIsPlusY, flatSlopeRatio: 0.01);

        Assert.NotEqual(
            (result.FaceColors[0], result.FaceColors[1], result.FaceColors[2]),
            (result.FaceColors[3], result.FaceColors[4], result.FaceColors[5]));
    }
}
