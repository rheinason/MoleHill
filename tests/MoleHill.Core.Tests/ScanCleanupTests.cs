using MoleHill.Core.Scan;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ScanCleanupTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Clean_IsolatedSmallBump_RemovesItWithoutMovingThePlane(double scale)
    {
        var ground = Ground(scale); int middle = 20 * 41 + 20;
        ground.Ground.Values[middle] += .08 * scale;
        var cleaned = ScanGroundCleaner.Clean(ground, Array.Empty<double[]>(), .5 * scale, .1 * scale, default);
        Assert.Equal(1, cleaned.ChangedNodes);
        Assert.InRange(cleaned.MaximumDisplacement / scale, .079999, .080001);
        Assert.Equal((.02 * 2 + .03 * 2) * scale, cleaned.Grid.Values[middle], 8);
        Assert.Equal(ground.Ground.Values[middle], cleaned.Grid.Values[middle] + .08 * scale, 8);
    }
    [Fact]
    public void Clean_AcceptedFeatureBand_HoldsItsSmallStep()
    {
        var ground = Ground(); int middle = 20 * 41 + 20; ground.Ground.Values[middle] += .08;
        var cleaned = ScanGroundCleaner.Clean(ground, new[] { new double[] { 2, 0, 0, 2, 4, 0 } }, .5, .1, default);
        Assert.Equal(ground.Ground.Values[middle], cleaned.Grid.Values[middle]);
    }
    [Fact]
    public void Lower_CarOnASlopeBesideATerrace_LowersTheCarAndKeepsTheTerraceEdge()
    {
        const int size = 81; const double cell = .25;
        var values = new double[size * size];
        bool Car(int x, int y) => x * cell is >= 6 and <= 8 && y * cell is >= 6 and <= 10;
        double Plane(int x, int y) => .1 * x * cell + .05 * y * cell;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                values[y * size + x] = Plane(x, y) + (Car(x, y) ? 1.5 : 0) + (x * cell >= 14 ? 1 : 0);
        var grid = new ScanGrid(0, 0, cell, size, size, values);
        var ground = new ScanGround.Result
        {
            Ground = grid, Surface = grid, ObjectCount = 0, StepCount = 0, DomainArea = 400, FilledArea = 0,
            IsGround = Enumerable.Repeat(true, values.Length).ToArray(), Domain = Enumerable.Repeat(true, values.Length).ToArray(),
        };
        var lowered = ScanGroundCleaner.Lower(ground, Array.Empty<double[]>(), 3, .4, 2.5, default);
        Assert.Equal(Enumerable.Range(0, values.Length).Count(i => Car(i % size, i / size)), lowered.ChangedNodes);
        Assert.InRange(lowered.MaximumDisplacement, 1.49, 1.51);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                Assert.Equal(Plane(x, y) + (x * cell >= 14 ? 1 : 0), lowered.Grid.Values[y * size + x], 5);
    }

    [Theory]
    [InlineData(.5, 1)]
    [InlineData(1.5, 2)]
    public void Join_CompatibleMeasuredRuns_RespectsMaximumGapAndRecordsInference(double gap, int expected)
    {
        var pairs = new[] { Pair(0, 1), Pair(1 + gap, 3 + gap) };
        var joined = ScanFeatureBridge.Join(pairs, Ground(), 1, .25, .05, default);
        Assert.Equal(expected, joined.Count);
        if (expected == 1)
        {
            var interval = Assert.Single(joined[0].High.InferredIntervals);
            Assert.InRange(interval.End - interval.Start, .499999, .500001);
            Assert.InRange(joined[0].High.ObservedShare, .8, .99);
            Assert.Equal(3, joined[0].ObservedLength);
        }
    }
    [Fact]
    public void Join_ElevationDisagreement_LeavesTheGap()
    {
        var a = Pair(0, 1); var b = Pair(1.5, 3.5, 1);
        Assert.Equal(2, ScanFeatureBridge.Join(new[] { a, b }, Ground(), 1, .25, .05, default).Count);
    }
    [Fact]
    public void Sampler_CloudGapAndVegetation_DoNotInventGroundEvidence()
    {
        var sampler = new ScanSourceSampler(new ScanPointCloud(new double[] { 0, 0, 1, 2, 0, 8 }, classes: new byte[] { 2, 5 }), 1);
        Assert.Equal(1, sampler.Height(0, 0)); Assert.Null(sampler.Height(1, 0)); Assert.Null(sampler.Height(2, 0));
    }
    [Fact]
    public void Walls_FilledStep_DoesNotBecomeMeasuredEvidence()
    {
        var ground = Ground();
        for (int y = 0; y < 41; y++) for (int x = 20; x < 41; x++) ground.Ground.Values[y * 41 + x] += 2;
        var options = new ScanWalls.Options
        {
            MinimumHeight = .6,
            MinimumLength = 1,
            MaximumWidth = .3,
            PlanTolerance = .25,
            VerticalTolerance = .05
        };
        Assert.NotEmpty(ScanWalls.Trace(ground.Ground, options));
        Array.Fill(ground.IsGround, false);
        Assert.Empty(ScanWalls.Trace(ground, options));
    }

    [Fact]
    public void Join_UnobservedDomainHole_DoesNotBridgeAcrossIt()
    {
        var ground = Ground();
        ground.Domain[12 * 41 + 20] = false;
        Assert.Equal(2, ScanFeatureBridge.Join(new[] { Pair(0, 1), Pair(1.5, 3.5) },
            ground, 1, .25, .05, default).Count);
    }

    [Fact]
    public void Geometry_DisconnectedPieces_AreRejected()
    {
        ScanCurvePiece Line(double start, double end) => new(1, new double[] { 0, 0, 1, 1 },
            new double[] { start, 0, 0, end, 0, 0 });
        Assert.Throws<ArgumentException>(() => new ScanCurveGeometry(new[] { Line(0, 1), Line(2, 3) }, .25, .05));
    }

    private static ScanFeatureBridge.Pair Pair(double start, double end, double dz = 0)
    {
        ScanCurveGeometry Rail(double x, double z) => new(new[] { new ScanCurvePiece(1,
            new double[] { 0, 0, 1, 1 }, new[] { x, start, z, x, end, z }) }, .25, .05);
        return new(Rail(2, 2 + dz), Rail(1.8, dz), end - start, 2);
    }
    private static ScanGround.Result Ground(double scale = 1)
    {
        var values = new double[41 * 41];
        for (int y = 0; y < 41; y++) for (int x = 0; x < 41; x++) values[y * 41 + x] = (.02 * x * .1 + .03 * y * .1) * scale;
        var grid = new ScanGrid(0, 0, .1 * scale, 41, 41, values);
        return new()
        {
            Ground = grid,
            Surface = grid,
            IsGround = Enumerable.Repeat(true, values.Length).ToArray(),
            Domain = Enumerable.Repeat(true, values.Length).ToArray(),
            ObjectCount = 0,
            StepCount = 0,
            DomainArea = 16 * scale * scale,
            FilledArea = 0
        };
    }
}
