using MoleHill.Core.Analysis;

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
