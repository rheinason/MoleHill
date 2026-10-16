using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class CurveDrapeTests
{
    private const double Tolerance = 1e-4;

    // A straight curve along X from 0 to 10 at Z = 100, over terrain z = x / 10.
    private static (double, double, double) Line(double s) => (s, 0.0, 100.0);

    private static double? Plane(double x, double y) => x / 10.0;

    private static double? PlaneWithGap(double x, double y) => x > 4.0 && x < 6.0 ? null : x / 10.0;

    private static CurveDrapeResult Drape(
        Func<double, double, double?> probe,
        DrapeMissPolicy policy = DrapeMissPolicy.Split,
        double? tieIn = null,
        IReadOnlyList<double>? extra = null) =>
        CurveDrape.Drape(10.0, Line, probe, new CurveDrapeOptions
        {
            Spacing = 1.0,
            Tolerance = Tolerance,
            MissPolicy = policy,
            TieInLength = tieIn,
            ExtraStations = extra
        });

    [Fact]
    public void Drape_FullCoverage_OneRunOnTheTerrain()
    {
        CurveDrapeResult result = Drape(Plane);

        double[] run = Assert.Single(result.Runs);
        Assert.Equal(11, run.Length / 3);
        for (int i = 0; i < run.Length / 3; i++)
            Assert.Equal(run[i * 3] / 10.0, run[i * 3 + 2], 9);
        Assert.Equal(0, result.MissedStations);
    }

    [Fact]
    public void Drape_SplitPolicy_TwoRunsEndingOnTheTerrainEdge()
    {
        CurveDrapeResult result = Drape(PlaneWithGap, DrapeMissPolicy.Split);

        Assert.Equal(2, result.Runs.Count);
        double[] first = result.Runs[0], second = result.Runs[1];
        Assert.Equal(4.0, first[^3], 3);
        Assert.Equal(6.0, second[0], 3);
        Assert.True(result.DroppedStations > 0);
    }

    [Fact]
    public void Drape_BridgePolicy_CarriesAStraightGradeAcrossTheGap()
    {
        CurveDrapeResult result = Drape(PlaneWithGap, DrapeMissPolicy.Bridge);

        double[] run = Assert.Single(result.Runs);
        int middle = Enumerable.Range(0, run.Length / 3).Single(i => Math.Abs(run[i * 3] - 5.0) < 1e-9);
        Assert.Equal(0.5, run[middle * 3 + 2], 3);
        Assert.True(result.BridgedStations > 0);
        Assert.Equal(0, result.DroppedStations);
    }

    [Fact]
    public void Drape_BridgePolicy_DropsAGapAtTheEnd()
    {
        CurveDrapeResult result = Drape((x, _) => x < 3.0 ? null : x / 10.0, DrapeMissPolicy.Bridge);

        double[] run = Assert.Single(result.Runs);
        Assert.Equal(3.0, run[0], 3);
        Assert.True(result.DroppedStations > 0);
    }

    [Fact]
    public void Drape_KeepOriginalPolicy_HoldsTheCurveElevationOverTheGap()
    {
        CurveDrapeResult result = Drape(PlaneWithGap, DrapeMissPolicy.KeepOriginal);

        double[] run = Assert.Single(result.Runs);
        int middle = Enumerable.Range(0, run.Length / 3).Single(i => Math.Abs(run[i * 3] - 5.0) < 1e-9);
        Assert.Equal(100.0, run[middle * 3 + 2], 9);
        Assert.True(result.KeptStations > 0);
    }

    [Fact]
    public void Drape_Section_TiesBothEndsIntoTheCurve()
    {
        CurveDrapeResult result = Drape(Plane, tieIn: 2.0);

        double[] run = Assert.Single(result.Runs);
        Assert.Equal(100.0, run[2], 9);
        Assert.Equal(100.0, run[^1], 9);
        int middle = Enumerable.Range(0, run.Length / 3).Single(i => Math.Abs(run[i * 3] - 5.0) < 1e-9);
        Assert.Equal(0.5, run[middle * 3 + 2], 9);
    }

    [Fact]
    public void Drape_ExtraStations_AreSampled()
    {
        CurveDrapeResult result = Drape(Plane, extra: new[] { 2.5 });

        double[] run = Assert.Single(result.Runs);
        Assert.Contains(Enumerable.Range(0, run.Length / 3), i => Math.Abs(run[i * 3] - 2.5) < 1e-9);
    }

    [Fact]
    public void Drape_NoCoverage_FailsWithAReason()
    {
        CurveDrapeResult result = Drape((_, _) => null);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 0.5)]
    [InlineData(2.0, 1.0)]
    [InlineData(5.0, 1.0)]
    [InlineData(10.0, 0.0)]
    public void TieInWeight_RampsInFromEachEnd(double station, double expected)
    {
        Assert.Equal(expected, CurveDrape.TieInWeight(station, 10.0, 2.0, smooth: false), 9);
    }
}
