using MoleHill.Core.Sculpting;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// <see cref="SculptConstraintMask.EvaluateInfluence"/> skips a region whose bounding-box lower bound is
/// positive and at or beyond the feather distance. Such a region can neither pin the point nor lower the
/// nearest distance below the feather threshold, so the answer must be unchanged. These check that
/// against a per-region reference: influence is 0 if any region pins the point, otherwise the smallest
/// per-region influence (influence is monotone in the nearest distance).
/// </summary>
public class SculptConstraintMaskBoundsRejectionTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(3.0)]
    public void EvaluateInfluence_ManyScatteredRegions_MatchesThePerRegionReference(double featherDistance)
    {
        var random = new Random(1234);
        var shapes = new List<(double[] Xy, int Count, bool IsPolygon, double HalfWidth, bool IsClosed)>();
        for (int i = 0; i < 25; i++)
        {
            double cx = random.NextDouble() * 60.0;
            double cy = random.NextDouble() * 60.0;
            if (i % 2 == 0)
            {
                shapes.Add((new[] { cx, cy, cx + 2.0, cy, cx + 2.0, cy + 1.5, cx, cy + 1.5 }, 4, true, 0.0, true));
            }
            else
            {
                shapes.Add((new[] { cx, cy, cx + 3.0, cy + 1.0, cx + 5.0, cy - 1.0 }, 3, false, 0.4, false));
            }
        }

        var combined = new SculptConstraintMask(featherDistance);
        var singles = new List<SculptConstraintMask>();
        foreach ((double[] xy, int count, bool isPolygon, double halfWidth, bool isClosed) in shapes)
        {
            var single = new SculptConstraintMask(featherDistance);
            if (isPolygon)
            {
                combined.AddPolygon(xy, count);
                single.AddPolygon(xy, count);
            }
            else
            {
                combined.AddPolyline(xy, count, halfWidth, isClosed);
                single.AddPolyline(xy, count, halfWidth, isClosed);
            }

            singles.Add(single);
        }

        for (int trial = 0; trial < 3000; trial++)
        {
            double x = (random.NextDouble() * 80.0) - 10.0;
            double y = (random.NextDouble() * 80.0) - 10.0;

            double expected = 1.0;
            foreach (SculptConstraintMask single in singles)
                expected = Math.Min(expected, single.EvaluateInfluence(x, y));

            Assert.Equal(expected, combined.EvaluateInfluence(x, y), 12);
        }
    }

    [Fact]
    public void EvaluateInfluence_InsideAFarPolygon_IsStillPinned()
    {
        // The rejection must never skip a region that actually contains the point, however far that
        // region sits from the others.
        var mask = new SculptConstraintMask(featherDistance: 1.0);
        mask.AddPolygon(new double[] { 0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0 }, 4);
        mask.AddPolygon(new double[] { 500.0, 500.0, 502.0, 500.0, 502.0, 502.0, 500.0, 502.0 }, 4);

        Assert.Equal(0.0, mask.EvaluateInfluence(501.0, 501.0));
        Assert.Equal(0.0, mask.EvaluateInfluence(0.5, 0.5));
        Assert.Equal(1.0, mask.EvaluateInfluence(250.0, 250.0));
    }

    [Fact]
    public void EvaluateInfluence_ZeroFeather_StillPinsExactlyOnTheBoundary()
    {
        var mask = new SculptConstraintMask(featherDistance: 0.0);
        mask.AddPolyline(new double[] { 0.0, 0.0, 10.0, 0.0 }, 2, halfWidth: 1.0);

        Assert.Equal(0.0, mask.EvaluateInfluence(5.0, 1.0));
        Assert.Equal(1.0, mask.EvaluateInfluence(5.0, 1.0001));
    }

    [Fact]
    public void EvaluateInfluence_InTheFeatherRamp_RisesWithDistance()
    {
        var mask = new SculptConstraintMask(featherDistance: 2.0);
        mask.AddPolygon(new double[] { 0.0, 0.0, 4.0, 0.0, 4.0, 4.0, 0.0, 4.0 }, 4);

        double near = mask.EvaluateInfluence(4.5, 2.0);
        double far = mask.EvaluateInfluence(5.5, 2.0);

        Assert.InRange(near, 0.0, 1.0);
        Assert.InRange(far, 0.0, 1.0);
        Assert.True(far > near);
        Assert.Equal(1.0, mask.EvaluateInfluence(6.5, 2.0));
    }
}
