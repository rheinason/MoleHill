using MoleHill.Core.Scan;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ScanAlignmentFitterTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Fit_CircularPlanWithRollingHeights_PreservesRadiusAtEveryParameter(double scale)
    {
        var raw = new List<double>();
        for (int i = 0; i <= 120; i++)
        {
            double a = .2 + i / 120.0 * 1.2;
            raw.Add(12 * Math.Cos(a) * scale); raw.Add(12 * Math.Sin(a) * scale);
            raw.Add((.3 * a + .1 * Math.Sin(a * 8)) * scale);
        }
        var curve = ScanAlignmentFitter.Fit(raw.ToArray(), false, .1 * scale, .05 * scale, .005 * scale)!;
        Assert.NotEmpty(curve.Pieces);
        Assert.True(curve.ControlPointCount < 40);
        foreach (var piece in curve.Pieces)
            for (int i = 0; i <= 40; i++)
            {
                var p = piece.PointAt(i / 40.0); double a = Math.Atan2(p.Y, p.X);
                Assert.InRange(Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) / scale - 12), 0, 1e-8);
                Assert.InRange(Math.Abs(p.Z / scale - (.3 * a + .1 * Math.Sin(a * 8))), 0, .0055);
            }
        Assert.Contains(curve.Pieces, p => p.Weights.Any(w => Math.Abs(w - 1) > 1e-5));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void FitPair_RasterReturnsOnStraightRails_ProducesCollinearNonCrossingPlans(double scale)
    {
        var top = new List<double>(); var toe = new List<double>();
        for (int i = 0; i <= 100; i++)
        {
            double y = i * .1;
            top.AddRange(new[] { (5 + (i % 4 == 0 ? .15 : 0)) * scale, y * scale, (2 + .05 * y) * scale });
            toe.AddRange(new[] { (4.5 + (i % 4 == 0 ? .1 : 0)) * scale, y * scale, .05 * y * scale });
        }
        Assert.True(ScanAlignmentFitter.FitPair(top.ToArray(), toe.ToArray(), false, .1 * scale,
            .25 * scale, .02 * scale, default, out var high, out var low));
        Assert.True(high.ControlPointCount <= 8); Assert.True(low.ControlPointCount <= 8);
        foreach (var rail in new[] { high, low })
        {
            var p = rail.PointsXyz; double dx = p[^3] - p[0], dy = p[^2] - p[1];
            Assert.All(rail.Pieces.SelectMany(s => Enumerable.Range(0, s.ControlPointCount).Select(i => (s.ControlPoints[i * 3], s.ControlPoints[i * 3 + 1]))),
                q => Assert.InRange(Math.Abs((q.Item1 - p[0]) * dy - (q.Item2 - p[1]) * dx) / (scale * scale), 0, 1e-8));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void FitPair_ClosedCircularTerrace_KeepsConcentricArcsAndCloses(double scale)
    {
        double[] Ring(double radius, double z) => Enumerable.Range(0, 121).SelectMany(i =>
        {
            double a = i * Math.PI * 2 / 120; return new[] { radius * Math.Cos(a) * scale, radius * Math.Sin(a) * scale, z * scale };
        }).ToArray();
        var top = Ring(10, 2); var toe = Ring(10.3, 0);
        Array.Copy(top, 0, top, top.Length - 3, 3); Array.Copy(toe, 0, toe, toe.Length - 3, 3);
        Assert.True(ScanAlignmentFitter.FitPair(top, toe, true, .1 * scale, .1 * scale, .02 * scale, default, out var high, out var low));
        foreach (var pair in new[] { (high, 10.0), (low, 10.3) })
            foreach (var piece in pair.Item1.Pieces)
                for (int i = 0; i <= 20; i++) { var p = piece.PointAt(i / 20.0); Assert.InRange(Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) / scale - pair.Item2), 0, 1e-7); }
    }

    [Fact]
    public void FitPair_IntersectingRails_IsRejected()
    {
        Assert.False(ScanAlignmentFitter.FitPair(new double[] { 0, 0, 2, 10, 10, 2 },
            new double[] { 0, 10, 0, 10, 0, 0 }, false, .1, .25, .05, default, out _, out _));
    }
}
