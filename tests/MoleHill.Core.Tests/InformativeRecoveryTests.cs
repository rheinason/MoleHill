using MoleHill.Core.Processing;
using MoleHill.Core.Scan;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class InformativeRecoveryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Recover_Plane_AddsNoFluffOrSeedSpots(double scale)
    {
        var mesh = Mesh((x, y) => .1 * x + .2 * y, scale);
        var result = TerrainCurveRecovery.Recover(mesh, Options(scale));
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.Empty(result.Contours); Assert.Empty(result.SpotPoints);
    }
    [Fact]
    public void Recover_Mound_ExtendsPastPreferredCountAndMeetsTolerance()
    {
        var options = Options(1, 1, 100);
        var result = TerrainCurveRecovery.Recover(Mesh((x, y) => 3 * Math.Exp(-((x - 8) * (x - 8) + (y - 8) * (y - 8)) / 20)), options);
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.InRange(result.MaximumDeviation, 0, options.VerticalTolerance);
        Assert.True(result.Contours.Count + result.SpotPointCount > 1);
        Assert.All(result.Contours, c => Assert.All(c.PointsXyz.Where((_, i) => i % 3 == 2), z => Assert.Equal(c.Level, z)));
    }
    [Fact]
    public void Recover_ComplexGround_CapsSpotsAndCurvesTogether()
    {
        var result = TerrainCurveRecovery.Recover(Mesh((x, y) => Math.Sin(x) * Math.Cos(y)), Options(1, 1, 3));
        Assert.True(result.Contours.Count + result.SpotPointCount + result.LandformLines.Count <= 3);
        Assert.NotEqual(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
    }
    [Fact]
    public void Recover_Cancellation_DoesNotStartTrials()
    {
        var result = TerrainCurveRecovery.Recover(Mesh((x, y) => x * y), new()
        { VerticalTolerance = .1, Policy = new(), ShouldCancel = () => true });
        Assert.Equal(TerrainCurveRecovery.Termination.Cancelled, result.Termination);
    }
    private static TerrainCurveRecovery.Options Options(double scale, int preferred = 20, int cap = 100) => new()
    {
        VerticalTolerance = .15 * scale,
        PlanTolerance = .1 * scale,
        LandformLines = false,
        Policy = new() { PreferredContours = preferred, MaximumInputs = cap, PruneInterval = 5 }
    };
    private static MoleHill.Core.Engine.IndexedTriMesh Mesh(Func<double, double, double> height, double scale = 1)
    {
        const int size = 17; var values = new double[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) values[y * size + x] = height(x, y) * scale;
        return ScanGroundMesher.Mesh(new ScanGrid(0, 0, scale, size, size, values), scale);
    }
}
