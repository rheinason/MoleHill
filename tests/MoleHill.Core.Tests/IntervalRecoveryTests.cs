using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Core.Scan;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class IntervalRecoveryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Recover_Plane_RemovesEveryRedundantContour(double scale)
    {
        var result = TerrainCurveRecovery.Recover(Mesh((x, y) => .25 * x, scale), Options(scale));
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.Empty(result.SpotPoints);
        // A plane is fixed by its own border: every traced contour is redundant.
        Assert.Empty(result.Contours);
        Assert.True(result.PrunedInputCount > 0);
    }

    [Fact]
    public void Recover_Mound_MeetsToleranceWithLevelContoursOnTheInterval()
    {
        var options = Options(1);
        var result = TerrainCurveRecovery.Recover(Mesh((x, y) => 3 * Math.Exp(-((x - 8) * (x - 8) + (y - 8) * (y - 8)) / 20)), options);
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.InRange(result.MaximumDeviation, 0, options.VerticalTolerance);
        Assert.NotEmpty(result.Contours);
        Assert.All(result.Contours, c =>
        {
            Assert.Equal(0, Math.IEEERemainder(c.Level, .5), 9);
            Assert.All(c.PointsXyz.Where((_, i) => i % 3 == 2), z => Assert.Equal(c.Level, z));
        });
    }

    [Fact]
    public void Recover_SecondaryRegion_TakesOnlySpotsAtItsOwnTolerance()
    {
        // Rolling ground on the right is "paving": measured loosely, never contoured.
        var mesh = Mesh((x, y) => .4 * Math.Sin(x * .9) * Math.Cos(y * .7) + .1 * y);
        var options = new TerrainCurveRecovery.Options
        {
            VerticalTolerance = .1, PlanTolerance = .1, LandformLines = false, FitContours = false,
            Policy = new() { ContourInterval = .25, MaximumInputs = 500 },
            Region = new HalfPlane(left: true), SecondaryRegion = new HalfPlane(left: false), SecondaryTolerance = .3,
        };
        var result = TerrainCurveRecovery.Recover(mesh, options);
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.InRange(result.MaximumDeviation, 0, .1);
        Assert.NotNull(result.SecondaryMaximumDeviation);
        Assert.InRange(result.SecondaryMaximumDeviation!.Value, 0, .3);
        Assert.All(result.Contours, c => Assert.All(c.PointsXyz.Where((_, i) => i % 3 == 0), x => Assert.True(x <= 8 + 1e-9)));
    }

    [Fact]
    public void Recover_BudgetSpentOnAStep_ReportsTheMissWhereAndWhichWay()
    {
        // A 3 m block standing on flat "paving" that only spots may describe; one input cannot rebuild it.
        var mesh = Mesh((x, y) => x >= 10 && x <= 12 && y >= 6 && y <= 9 ? 3 : 0);
        var result = TerrainCurveRecovery.Recover(mesh, new TerrainCurveRecovery.Options
        {
            VerticalTolerance = .1, PlanTolerance = .1, LandformLines = false, FitContours = false,
            Policy = new() { ContourInterval = .5, MaximumInputs = 1 },
            Region = new HalfPlane(left: true), SecondaryRegion = new HalfPlane(left: false), SecondaryTolerance = .3,
        });
        Assert.Equal(TerrainCurveRecovery.Termination.CurveBudgetSpent, result.Termination);
        Assert.Equal(result.SecondaryMaximumDeviation!.Value, result.Misses[0].Deviation, 9);
        Assert.True(result.Misses[0].IsSecondary);
        // The block stands above the rebuild; the spot's fan stands above the ground beside it: two regions.
        Assert.Contains(result.Misses, miss => miss.SignedDeviation > 0 && miss.X >= 10 && miss.X <= 12 && miss.Y >= 6 && miss.Y <= 9);
        Assert.Contains(result.Misses, miss => miss.SignedDeviation < 0);
        Assert.Equal(0, result.UncoveredArea);
    }

    [Fact]
    public void Recover_StepOnUnmeasuredGround_CountsNoMissAndSpendsNoSpot()
    {
        // The same block, where the source measured nothing: followed, not chased.
        var mesh = Mesh((x, y) => x >= 10 && x <= 12 && y >= 6 && y <= 9 ? 3 : 0);
        var result = TerrainCurveRecovery.Recover(mesh, new TerrainCurveRecovery.Options
        {
            VerticalTolerance = .1, PlanTolerance = .1, LandformLines = false, FitContours = false,
            Policy = new() { ContourInterval = .5, MaximumInputs = 500 },
            Region = new HalfPlane(left: true), SecondaryRegion = new HalfPlane(left: false), SecondaryTolerance = .3,
            Unmeasured = (x, y) => x >= 9 && x <= 13 && y >= 5 && y <= 10,
        });
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.Empty(result.Misses);
        Assert.Equal(0, result.SpotPointCount);
        Assert.InRange(result.UnmeasuredArea, 20, 30 + 1e-9); // the 4 x 5 m box, counted by vertex on a 1 m grid
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Recover_InputsPerArea_CapsByMeasuredGroundInModelUnits(double scale)
    {
        // 10 inputs over the 16 x 16 unit grid (256 square units): its eight contours, then two spots, not the
        // default 2,000. The contours at the interval are drawn first; the cap stops spot heights.
        var mesh = Mesh((x, y) => .4 * Math.Sin(x * .9) * Math.Cos(y * .7) + .1 * y, scale);
        var result = TerrainCurveRecovery.Recover(mesh, new TerrainCurveRecovery.Options
        {
            VerticalTolerance = .05 * scale, PlanTolerance = .1 * scale, LandformLines = false,
            Policy = new() { ContourInterval = .5 * scale, InputsPerArea = 10.0 / 256 / (scale * scale) },
        });
        Assert.Equal(TerrainCurveRecovery.Termination.CurveBudgetSpent, result.Termination);
        Assert.Equal(10, result.Contours.Count + result.SpotPointCount);
    }

    [Fact]
    public void RecoverBest_IntervalMode_RunsOnceWithoutLandformLines()
    {
        int runs = 0;
        var result = TerrainCurveRecovery.RecoverBest(Mesh((x, y) => Math.Sin(x * .5) + .2 * y), new()
        {
            VerticalTolerance = .2, PlanTolerance = .1, Policy = new() { ContourInterval = .5, ReportActivity = m => { if (m.StartsWith("Tracing")) runs++; } }
        }, TerrainCurveRecovery.LandformMode.Auto);
        Assert.Equal(1, runs);
        Assert.Empty(result.LandformLines);
    }

    private static TerrainCurveRecovery.Options Options(double scale) => new()
    {
        VerticalTolerance = .15 * scale,
        PlanTolerance = .1 * scale,
        LandformLines = false,
        Policy = new() { ContourInterval = .5 * scale, MaximumInputs = 500 }
    };

    private static IndexedTriMesh Mesh(Func<double, double, double> height, double scale = 1)
    {
        const int size = 17; var values = new double[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) values[y * size + x] = height(x, y) * scale;
        return ScanGroundMesher.Mesh(new ScanGrid(0, 0, scale, size, size, values), scale);
    }

    private sealed class HalfPlane(bool left) : ITerrainRecoveryRegion
    {
        public bool Contains(double x, double y) => left ? x < 8 : x >= 8;

        public IReadOnlyList<RegionInputClipper.InputPolyline> Clip(double[] points, bool closed)
        {
            var parts = new List<RegionInputClipper.InputPolyline>(); var run = new List<double>();
            for (int i = 0; i <= points.Length; i += 3)
            {
                if (i < points.Length && Contains(points[i], points[i + 1])) { run.AddRange(points.Skip(i).Take(3)); continue; }
                if (run.Count >= 6) parts.Add(new(run.ToArray(), run.Count / 3, false));
                run.Clear();
            }
            return parts;
        }
    }
}
