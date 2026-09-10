using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Loop deviation is measured through an exact nearest-segment index above a loop-size threshold. The
/// distance it reports must equal the all-pairs scan's — including for a point that misses the target
/// loop by a long way, which a fixed-radius query could not answer. The oracle here is the scan.
/// </summary>
public class SeamLoopDeviationIndexTests
{
    [Theory]
    [InlineData(16)]    // below the index threshold: linear scan
    [InlineData(600)]   // above it: indexed
    public void ComputeLoopDeviation_MatchesTheAllPairsScan(int targetCount)
    {
        double[] target = Circle(targetCount, 0.0, 0.0, radius: 20.0);
        double[] source = Circle(240, 0.35, -0.2, radius: 20.05);

        LoopDeviationMetrics actual = SeamValidator.ComputeLoopDeviation(source, target, tolerance: 1.0);
        (double maxDistance, int missCount) = ScanDeviation(source, target, tolerance: 1.0);

        Assert.Equal(maxDistance, actual.MaxDistance, 12);
        Assert.Equal(missCount, actual.MissCount);
    }

    [Fact]
    public void ComputeLoopDeviation_DistantMiss_ReportsTheTrueDistance()
    {
        // The whole point of not using a fixed-radius query: this source sits far outside the target.
        double[] target = Circle(500, 0.0, 0.0, radius: 10.0);
        double[] source = Circle(64, 5_000.0, 5_000.0, radius: 1.0);

        LoopDeviationMetrics actual = SeamValidator.ComputeLoopDeviation(source, target, tolerance: 0.01);
        (double maxDistance, int missCount) = ScanDeviation(source, target, tolerance: 0.01);

        Assert.Equal(maxDistance, actual.MaxDistance, 9);
        Assert.Equal(missCount, actual.MissCount);
        Assert.True(actual.MaxDistance > 6_000.0);
    }

    [Fact]
    public void ComputeLoopDeviation_MixedNearAndFarSources_MatchesTheScan()
    {
        double[] target = Star(400, innerRadius: 8.0, outerRadius: 20.0);

        var random = new Random(515151);
        var source = new List<double>();
        for (int i = 0; i < 500; i++)
        {
            if (i % 5 == 0)
            {
                source.Add((random.NextDouble() * 900.0) - 450.0);
                source.Add((random.NextDouble() * 900.0) - 450.0);
            }
            else
            {
                double angle = random.NextDouble() * Math.PI * 2.0;
                double radius = 8.0 + (random.NextDouble() * 12.0);
                source.Add(Math.Cos(angle) * radius);
                source.Add(Math.Sin(angle) * radius);
            }
        }

        double[] sourceLoop = source.ToArray();
        LoopDeviationMetrics actual = SeamValidator.ComputeLoopDeviation(sourceLoop, target, tolerance: 0.5);
        (double maxDistance, int missCount) = ScanDeviation(sourceLoop, target, tolerance: 0.5);

        Assert.Equal(maxDistance, actual.MaxDistance, 9);
        Assert.Equal(missCount, actual.MissCount);
    }

    [Fact]
    public void ComputeLoopDeviation_SourceExactlyOnTheTarget_ReportsNoDeviation()
    {
        double[] target = Circle(400, 0.0, 0.0, radius: 12.0);

        LoopDeviationMetrics actual = SeamValidator.ComputeLoopDeviation(target, target, tolerance: 1e-9);

        Assert.Equal(0.0, actual.MaxDistance, 12);
        Assert.Equal(0, actual.MissCount);
    }

    [Fact]
    public void ComputeLoopDeviation_ClosingSegmentIsMeasured()
    {
        // The gap between the last and first target vertex is a real segment. A source point sitting on
        // it must report zero, not the distance to the nearest interior segment.
        double[] target = Circle(400, 0.0, 0.0, radius: 12.0);
        int last = (target.Length / 2) - 1;
        double midX = (target[last * 2] + target[0]) / 2.0;
        double midY = (target[(last * 2) + 1] + target[1]) / 2.0;
        var source = new[] { midX, midY, midX, midY, midX, midY };

        LoopDeviationMetrics actual = SeamValidator.ComputeLoopDeviation(source, target, tolerance: 1e-9);
        (double maxDistance, _) = ScanDeviation(source, target, tolerance: 1e-9);

        Assert.Equal(maxDistance, actual.MaxDistance, 12);
        Assert.Equal(0.0, actual.MaxDistance, 12);
    }

    [Fact]
    public void ComputeLoopDeviation_TargetWithFewerThanTwoVertices_IsAllMisses()
    {
        double[] source = Circle(100, 0.0, 0.0, radius: 5.0);

        LoopDeviationMetrics actual = SeamValidator.ComputeLoopDeviation(source, new[] { 0.0, 0.0 }, tolerance: 1.0);

        Assert.Equal(100, actual.MissCount);
    }

    private static (double MaxDistance, int MissCount) ScanDeviation(double[] sourceLoopXy, double[] targetLoopXy, double tolerance)
    {
        int sourceCount = sourceLoopXy.Length / 2;
        int targetCount = targetLoopXy.Length / 2;
        double maxDistance = 0.0;
        int missCount = 0;

        for (int i = 0; i < sourceCount; i++)
        {
            double px = sourceLoopXy[i * 2];
            double py = sourceLoopXy[(i * 2) + 1];
            double best = double.MaxValue;
            for (int j = 0; j < targetCount; j++)
            {
                int next = (j + 1) % targetCount;
                double distance = DistancePointToSegment(
                    px, py,
                    targetLoopXy[j * 2], targetLoopXy[(j * 2) + 1],
                    targetLoopXy[next * 2], targetLoopXy[(next * 2) + 1]);
                if (distance < best)
                    best = distance;
            }

            if (best > maxDistance)
                maxDistance = best;
            if (best > tolerance)
                missCount++;
        }

        return (maxDistance, missCount);
    }

    private static double DistancePointToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-16)
            return Math.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay)));

        double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0.0, 1.0);
        double nx = ax + (t * dx);
        double ny = ay + (t * dy);
        return Math.Sqrt(((px - nx) * (px - nx)) + ((py - ny) * (py - ny)));
    }

    private static double[] Circle(int count, double cx, double cy, double radius)
    {
        var loop = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            double angle = (i * 2.0 * Math.PI) / count;
            loop[i * 2] = cx + (Math.Cos(angle) * radius);
            loop[(i * 2) + 1] = cy + (Math.Sin(angle) * radius);
        }

        return loop;
    }

    private static double[] Star(int count, double innerRadius, double outerRadius)
    {
        var loop = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            double angle = (i * 2.0 * Math.PI) / count;
            double radius = (i % 2 == 0) ? outerRadius : innerRadius;
            loop[i * 2] = Math.Cos(angle) * radius;
            loop[(i * 2) + 1] = Math.Sin(angle) * radius;
        }

        return loop;
    }
}
