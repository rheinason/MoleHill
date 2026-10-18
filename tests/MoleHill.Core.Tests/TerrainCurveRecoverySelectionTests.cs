using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public class TerrainCurveRecoverySelectionTests
{
    private static IndexedTriMesh Valley()
    {
        const int n = 31;
        var xyz = new double[n * n * 3]; var faces = new List<int>();
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            int i = y * n + x; xyz[i * 3] = x; xyz[i * 3 + 1] = y;
            xyz[i * 3 + 2] = .1 * y + .4 * Math.Abs(x - 15) + .04 * Math.Sin(y * .5);
            if (x + 1 < n && y + 1 < n) faces.AddRange(new[] { i, i + 1, i + n + 1, i, i + n + 1, i + n });
        }
        return IndexedTriMesh.FromArrays(xyz, faces.ToArray());
    }

    [Theory]
    [InlineData(TerrainCurveRecovery.LandformMode.Yes, true)]
    [InlineData(TerrainCurveRecovery.LandformMode.No, false)]
    public void RecoverBest_ExplicitMode_OverridesLegacyFlagWithoutMutatingIt(TerrainCurveRecovery.LandformMode mode, bool enabled)
    {
        var options = new TerrainCurveRecovery.Options { VerticalTolerance = .1, PlanTolerance = .1, MaximumCurves = 100, LandformLines = !enabled, FitContours = true };
        var mesh = Valley();
        var result = TerrainCurveRecovery.RecoverBest(mesh, options, mode);
        var expected = TerrainCurveRecovery.Recover(mesh, new() { VerticalTolerance = .1, PlanTolerance = .1, MaximumCurves = 100, LandformLines = enabled, FitContours = true });
        Assert.Equal(!enabled, options.LandformLines);
        Assert.Equal(expected.MaximumDeviation, result.MaximumDeviation);
        Assert.Equal(expected.Contours.SelectMany(c => c.PointsXyz), result.Contours.SelectMany(c => c.PointsXyz));
        Assert.Equal(expected.LandformLines.SelectMany(line => line), result.LandformLines.SelectMany(line => line));
    }

    [Fact]
    public void RecoverBest_Auto_KeepsToleranceAndNeverUsesMoreCurvesThanEitherSuccessfulMode()
    {
        var mesh = Valley();
        TerrainCurveRecovery.Options Options(bool lines) => new() { VerticalTolerance = .1, PlanTolerance = .1, MaximumCurves = 100, LandformLines = lines, FitContours = true };
        var auto = TerrainCurveRecovery.RecoverBest(mesh, Options(false), TerrainCurveRecovery.LandformMode.Auto);
        var yes = TerrainCurveRecovery.Recover(mesh, Options(true)); var no = TerrainCurveRecovery.Recover(mesh, Options(false));
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, auto.Termination);
        Assert.InRange(auto.MaximumDeviation, 0, .1);
        int Count(TerrainCurveRecovery.Result r) => r.Contours.Count + r.LandformLines.Count;
        foreach (var alternative in new[] { yes, no }.Where(r => r.Termination == TerrainCurveRecovery.Termination.ToleranceSatisfied))
            Assert.True(Count(auto) <= Count(alternative));
        Assert.All(auto.Contours, contour => Assert.NotNull(contour.Curve));
    }

    [Fact]
    public void RecoverBest_CancelledDuringAuto_DoesNotStartAnotherPass()
    {
        bool cancelled = false; int rounds = 0;
        var result = TerrainCurveRecovery.RecoverBest(Valley(), new() { VerticalTolerance = .001, MaximumCurves = 100,
            ShouldCancel = () => cancelled, ReportProgress = (_, _) => { rounds++; cancelled = true; } }, TerrainCurveRecovery.LandformMode.Auto);
        Assert.Equal(TerrainCurveRecovery.Termination.Cancelled, result.Termination);
        Assert.Equal(1, rounds);
    }
}
