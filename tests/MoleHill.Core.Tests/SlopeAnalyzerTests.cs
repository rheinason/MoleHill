using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class SlopeAnalyzerTests
{
    [Fact]
    public void Analyze_CustomPalette_InterpolatesAcrossStops()
    {
        var result = SlopeAnalyzer.Analyze(
            CreateHalfSlopeVertices(),
            vertexCount: 3,
            new[] { 0, 1, 2 },
            faceCount: 1,
            SlopeAnalyzer.SlopeUnit.Percent,
            colorLow: 0.0,
            colorHigh: 100.0,
            palette: new[]
            {
                new SlopeAnalyzer.ColorStop(0.0, 0, 0, 0),
                new SlopeAnalyzer.ColorStop(1.0, 255, 255, 255)
            });

        Assert.InRange(result.FaceColors[0], 127, 128);
        Assert.InRange(result.FaceColors[1], 127, 128);
        Assert.InRange(result.FaceColors[2], 127, 128);
        Assert.Equal(0.0, result.ColorLow);
        Assert.Equal(100.0, result.ColorHigh);
    }

    [Fact]
    public void Analyze_AutoHigh_UsesComputedMaximumSlope()
    {
        var result = SlopeAnalyzer.Analyze(
            CreateHalfSlopeVertices(),
            vertexCount: 3,
            new[] { 0, 1, 2 },
            faceCount: 1,
            SlopeAnalyzer.SlopeUnit.Percent);

        Assert.InRange(result.Max, 49.999, 50.001);
        Assert.InRange(result.ColorHigh, 49.999, 50.001);
    }

    [Fact]
    public void Analyze_FlatMesh_FallsBackToUnitColorRange()
    {
        var result = SlopeAnalyzer.Analyze(
            new[]
            {
                0.0, 0.0, 0.0,
                1.0, 0.0, 0.0,
                0.0, 1.0, 0.0
            },
            vertexCount: 3,
            new[] { 0, 1, 2 },
            faceCount: 1,
            SlopeAnalyzer.SlopeUnit.Percent);

        Assert.Equal(0.0, result.ColorLow);
        Assert.Equal(1.0, result.ColorHigh);
        Assert.Equal(0, result.FaceColors[0]);
        Assert.Equal(200, result.FaceColors[1]);
        Assert.Equal(0, result.FaceColors[2]);
    }

    [Fact]
    public void Analyze_VerticalFace_ClampsToHighEndColor()
    {
        var result = SlopeAnalyzer.Analyze(
            new[]
            {
                0.0, 0.0, 0.0,
                0.0, 1.0, 0.0,
                0.0, 0.0, 1.0
            },
            vertexCount: 3,
            new[] { 0, 1, 2 },
            faceCount: 1,
            SlopeAnalyzer.SlopeUnit.Percent,
            colorLow: 0.0,
            colorHigh: 100.0,
            palette: new[]
            {
                new SlopeAnalyzer.ColorStop(0.0, 10, 20, 30),
                new SlopeAnalyzer.ColorStop(1.0, 200, 210, 220)
            });

        Assert.Equal(200, result.FaceColors[0]);
        Assert.Equal(210, result.FaceColors[1]);
        Assert.Equal(220, result.FaceColors[2]);
    }

    [Fact]
    public void ConvertRatioToUnit_AndBack_PreservesSlopeAcrossUnits()
    {
        const double slopeRatio = 0.5;

        foreach (var unit in new[]
                 {
                     SlopeAnalyzer.SlopeUnit.Ratio,
                     SlopeAnalyzer.SlopeUnit.Percent,
                     SlopeAnalyzer.SlopeUnit.Promille,
                     SlopeAnalyzer.SlopeUnit.Degrees
                 })
        {
            double unitValue = SlopeAnalyzer.ConvertRatioToUnit(slopeRatio, unit);
            double roundTrip = SlopeAnalyzer.ConvertUnitToRatio(unitValue, unit);
            Assert.InRange(roundTrip, slopeRatio - 1e-9, slopeRatio + 1e-9);
        }
    }

    [Fact]
    public void Analyze_OneToOneSlope_ReportsEquivalentSlopeUnits()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 1.0,
            0.0, 1.0, 0.0
        };
        int[] faces = { 0, 1, 2 };

        var percent = SlopeAnalyzer.Analyze(vertices, 3, faces, 1, SlopeAnalyzer.SlopeUnit.Percent);
        var promille = SlopeAnalyzer.Analyze(vertices, 3, faces, 1, SlopeAnalyzer.SlopeUnit.Promille);
        var degrees = SlopeAnalyzer.Analyze(vertices, 3, faces, 1, SlopeAnalyzer.SlopeUnit.Degrees);
        var ratio = SlopeAnalyzer.Analyze(vertices, 3, faces, 1, SlopeAnalyzer.SlopeUnit.Ratio);

        Assert.InRange(percent.Slopes[0], 99.999, 100.001);
        Assert.InRange(promille.Slopes[0], 999.999, 1000.001);
        Assert.InRange(degrees.Slopes[0], 44.999, 45.001);
        Assert.InRange(ratio.Slopes[0], 0.999, 1.001);
    }

    [Fact]
    public void Summarize_MatchesAnalyzeSummaryValues()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 1.0,
            0.0, 1.0, 0.0,
            1.0, 1.0, 0.5
        };
        int[] faces =
        {
            0, 1, 2,
            1, 3, 2
        };

        var summary = SlopeAnalyzer.Summarize(vertices, 4, faces, 2, SlopeAnalyzer.SlopeUnit.Percent);
        var analyze = SlopeAnalyzer.Analyze(vertices, 4, faces, 2, SlopeAnalyzer.SlopeUnit.Percent);

        Assert.Equal(analyze.Min, summary.Min, precision: 12);
        Assert.Equal(analyze.Max, summary.Max, precision: 12);
        Assert.Equal(analyze.Average, summary.Average, precision: 12);
        Assert.Equal(analyze.ColorLow, summary.ColorLow, precision: 12);
        Assert.Equal(analyze.ColorHigh, summary.ColorHigh, precision: 12);
        Assert.Equal(2, summary.FaceCount);
    }

    [Fact]
    public void Summarize_DoesNotExposeFaceColors()
    {
        var summary = SlopeAnalyzer.Summarize(
            CreateHalfSlopeVertices(),
            vertexCount: 3,
            new[] { 0, 1, 2 },
            faceCount: 1,
            SlopeAnalyzer.SlopeUnit.Percent);

        Assert.DoesNotContain(
            summary.GetType().GetProperties(),
            property => string.Equals(property.Name, nameof(SlopeAnalyzer.SlopeResult.FaceColors), StringComparison.Ordinal));
    }

    private static double[] CreateHalfSlopeVertices()
    {
        return new[]
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.5
        };
    }
}
