using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class TerrainCurveRecoveryTests
{
    private static IndexedTriMesh Grid(int size, Func<double, double, double> height)
    {
        int n = size + 1;
        var vertices = new double[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = j * n + i;
                vertices[v * 3] = i;
                vertices[v * 3 + 1] = j;
                vertices[v * 3 + 2] = height(i, j);
            }
        }

        var faces = new List<int>();
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n + 1, d = a + n;
                faces.AddRange(new[] { a, b, c, a, c, d });
            }
        }

        return new IndexedTriMesh(vertices, n * n, faces.ToArray(), faces.Count / 3);
    }

    [Fact]
    public void Recover_TiltedPlane_NeedsOnlyItsBorder()
    {
        TerrainCurveRecovery.Result result = TerrainCurveRecovery.Recover(
            Grid(20, (x, y) => 0.1 * x + 0.05 * y),
            new TerrainCurveRecovery.Options { VerticalTolerance = 0.01 });

        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.Empty(result.Contours);
        Assert.Equal(0, result.SpotPointCount);
        double[] border = Assert.Single(result.Borders);
        Assert.Equal(4, border.Length / 3);
    }

    [Fact]
    public void Recover_Mound_SeedsThePeakAndAddsContoursUntilWithinTolerance()
    {
        IndexedTriMesh mound = Grid(40, (x, y) =>
        {
            double r = Math.Sqrt((x - 20) * (x - 20) + (y - 20) * (y - 20));
            return 10.0 * Math.Exp(-r * r / 120.0);
        });

        TerrainCurveRecovery.Result result = TerrainCurveRecovery.Recover(
            mound, new TerrainCurveRecovery.Options { VerticalTolerance = 0.25, PlanTolerance = 0.2 });

        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.True(result.MaximumDeviation <= 0.25, $"{result.MaximumDeviation}");
        Assert.True(result.SpotPointCount >= 1, "the peak is a spot point");
        Assert.NotEmpty(result.Contours);
        Assert.True(result.Contours.Count < 30, $"{result.Contours.Count} contours");
        foreach (TerrainCurveRecovery.RecoveredContour contour in result.Contours)
        {
            for (int i = 2; i < contour.PointsXyz.Length; i += 3)
                Assert.Equal(contour.Level, contour.PointsXyz[i]);
        }
    }

    [Fact]
    public void Recover_CurveBudget_StopsAndSaysSo()
    {
        IndexedTriMesh mound = Grid(30, (x, y) => Math.Sin(x * 0.4) * Math.Cos(y * 0.4) * 3.0);

        TerrainCurveRecovery.Result result = TerrainCurveRecovery.Recover(
            mound, new TerrainCurveRecovery.Options { VerticalTolerance = 0.001, MaximumCurves = 2 });

        Assert.True(result.Contours.Count <= 2);
        Assert.NotEqual(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
    }

    [Fact]
    public void SimplifyOpen_DropsCollinearPoints()
    {
        double[] points = { 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 1, 0 };

        double[] simplified = TerrainCurveRecovery.SimplifyOpen(points, 0.01, double.PositiveInfinity);

        Assert.Equal(new double[] { 0, 0, 0, 2, 0, 0, 3, 1, 0 }, simplified);
    }

    [Fact]
    public void SimplifyClosed_KeepsAVerticalBreak()
    {
        // A flat square in plan whose one corner-free side carries a 1 m bump at its midpoint.
        double[] loop = { 0, 0, 0, 5, 0, 1, 10, 0, 0, 10, 10, 0, 0, 10, 0 };

        double[] simplified = TerrainCurveRecovery.SimplifyClosed(loop, 0.01, 0.1);

        Assert.Equal(5, simplified.Length / 3);
    }
}
