using MoleHill.Core.Scattering;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Poisson scattering keeps its occupancy sparse, so a large extent with a small spacing no longer
/// allocates a dense domain grid before the cap can help. These pin the invariants that must survive
/// that: seed determinism, minimum spacing, cap behaviour, holes and disconnected regions, and
/// cancellation.
/// </summary>
public class ScatterSamplerSparseDomainTests
{
    [Fact]
    public void Sample_HugeExtentWithTinySpacingAndASmallCap_StaysWithinTheCap()
    {
        // Dense occupancy for this domain would be about (200000 / 0.35)^2 cells — hundreds of
        // terabytes. The cap is 500.
        var request = new ScatterRequest
        {
            Boundaries = new[] { Square(0.0, 0.0, 200_000.0) },
            Pattern = ScatterPattern.PoissonDisk,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 0.5,
            MaxSamples = 500,
            Seed = 12
        };

        List<(double X, double Y)> points = ScatterSampler.Sample(request);

        Assert.NotEmpty(points);
        Assert.True(points.Count <= 500);
        AssertMinimumSpacing(points, 0.5);
    }

    [Fact]
    public void Sample_PoissonIsDeterministicForASeed()
    {
        ScatterRequest Build(int seed) => new()
        {
            Boundaries = new[] { Square(0.0, 0.0, 40.0) },
            Pattern = ScatterPattern.PoissonDisk,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 1.5,
            MaxSamples = 5_000,
            Seed = seed
        };

        List<(double X, double Y)> first = ScatterSampler.Sample(Build(7));
        List<(double X, double Y)> second = ScatterSampler.Sample(Build(7));
        List<(double X, double Y)> other = ScatterSampler.Sample(Build(8));

        Assert.Equal(first, second);
        Assert.NotEmpty(first);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Sample_PoissonRespectsMinimumSpacing()
    {
        var request = new ScatterRequest
        {
            Boundaries = new[] { Square(0.0, 0.0, 30.0) },
            Pattern = ScatterPattern.PoissonDisk,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 2.0,
            MaxSamples = 10_000,
            Seed = 3
        };

        List<(double X, double Y)> points = ScatterSampler.Sample(request);

        Assert.True(points.Count > 50, $"Expected a well-filled region, got {points.Count}.");
        AssertMinimumSpacing(points, 2.0);
    }

    [Fact]
    public void Sample_DisconnectedRegions_FillsBothAndNothingBetween()
    {
        var request = new ScatterRequest
        {
            Boundaries = new[] { Square(0.0, 0.0, 5.0), Square(500.0, 0.0, 5.0) },
            Pattern = ScatterPattern.PoissonDisk,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 1.0,
            MaxSamples = 20_000,
            Seed = 21
        };

        List<(double X, double Y)> points = ScatterSampler.Sample(request);

        Assert.Contains(points, p => p.X <= 5.0);
        Assert.Contains(points, p => p.X >= 500.0);
        Assert.DoesNotContain(points, p => p.X > 5.0 && p.X < 500.0);
    }

    [Fact]
    public void Sample_NarrowCorridor_IsStillFilled()
    {
        var request = new ScatterRequest
        {
            Boundaries = new[] { new[] { 0.0, 0.0, 400.0, 0.0, 400.0, 1.0, 0.0, 1.0 } },
            Pattern = ScatterPattern.PoissonDisk,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 0.75,
            MaxSamples = 20_000,
            Seed = 5
        };

        List<(double X, double Y)> points = ScatterSampler.Sample(request);

        Assert.True(points.Count > 100, $"Expected the corridor to be filled, got {points.Count}.");
        Assert.All(points, p => Assert.InRange(p.Y, 0.0, 1.0));
        Assert.Contains(points, p => p.X > 300.0);
    }

    [Fact]
    public void Sample_Cancellation_StopsEarly()
    {
        int calls = 0;
        var request = new ScatterRequest
        {
            Boundaries = new[] { Square(0.0, 0.0, 200.0) },
            Pattern = ScatterPattern.PoissonDisk,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 0.5,
            MaxSamples = 200_000,
            Seed = 9,
            ShouldCancel = () => ++calls > 50
        };

        List<(double X, double Y)> points = ScatterSampler.Sample(request);

        Assert.True(points.Count < 200_000);
    }

    [Fact]
    public void Sample_GridPatternWithAHugeExtent_StillEmitsPoints()
    {
        // The column/row count used to be an int cast that wrapped negative on an extreme extent,
        // silently producing nothing.
        var request = new ScatterRequest
        {
            Boundaries = new[] { Square(0.0, 0.0, 1e9) },
            Pattern = ScatterPattern.Grid,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 0.25,
            MaxSamples = 100,
            Seed = 4
        };

        List<(double X, double Y)> points = ScatterSampler.Sample(request);

        Assert.NotEmpty(points);
        Assert.True(points.Count <= 100);
    }

    private static void AssertMinimumSpacing(List<(double X, double Y)> points, double spacing)
    {
        double minimumSquared = spacing * spacing;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                double dx = points[i].X - points[j].X;
                double dy = points[i].Y - points[j].Y;
                Assert.True(
                    (dx * dx) + (dy * dy) >= minimumSquared - 1e-9,
                    $"Points {i} and {j} are closer than the requested spacing.");
            }
        }
    }

    private static double[] Square(double x, double y, double size)
    {
        return new[] { x, y, x + size, y, x + size, y + size, x, y + size };
    }
}
