using System.Linq;
using MoleHill.Core.Scattering;
using Xunit;

namespace MoleHill.Core.Tests;

public class ScatterSamplerTests
{
    // A 10x10 square at the origin (area 100).
    private static double[] Square(double size = 10.0) =>
        new[] { 0.0, 0.0, size, 0.0, size, size, 0.0, size };

    private static bool InSquare((double X, double Y) p, double size = 10.0) =>
        p.X >= -1e-9 && p.X <= size + 1e-9 && p.Y >= -1e-9 && p.Y <= size + 1e-9;

    [Fact]
    public void Sample_RandomCount_ProducesRequestedCountInsideBoundary()
    {
        var points = ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = new[] { Square() },
            Pattern = ScatterPattern.Random,
            DensityMode = ScatterDensityMode.Count,
            Count = 200,
            Seed = 7
        });

        Assert.Equal(200, points.Count);
        Assert.All(points, p => Assert.True(InSquare(p), $"({p.X},{p.Y}) outside boundary"));
    }

    [Fact]
    public void Sample_SameSeed_IsDeterministic_DifferentSeed_Differs()
    {
        ScatterRequest Make(int seed) => new()
        {
            Boundaries = new[] { Square() },
            Pattern = ScatterPattern.Random,
            DensityMode = ScatterDensityMode.Count,
            Count = 100,
            Seed = seed
        };

        var a1 = ScatterSampler.Sample(Make(3));
        var a2 = ScatterSampler.Sample(Make(3));
        var b = ScatterSampler.Sample(Make(4));

        Assert.Equal(a1.Count, a2.Count);
        for (int i = 0; i < a1.Count; i++)
        {
            Assert.Equal(a1[i].X, a2[i].X, 12);
            Assert.Equal(a1[i].Y, a2[i].Y, 12);
        }

        // Overwhelmingly likely to differ for a different seed.
        bool anyDifferent = a1.Count != b.Count ||
            Enumerable.Range(0, System.Math.Min(a1.Count, b.Count))
                .Any(i => System.Math.Abs(a1[i].X - b[i].X) > 1e-9 || System.Math.Abs(a1[i].Y - b[i].Y) > 1e-9);
        Assert.True(anyDifferent);
    }

    [Fact]
    public void Sample_PerArea_ScalesWithArea()
    {
        var small = ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = new[] { Square(10.0) }, // area 100
            Pattern = ScatterPattern.Random,
            DensityMode = ScatterDensityMode.PerArea,
            PerAreaDensity = 0.5,
            Seed = 1
        });
        var large = ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = new[] { Square(20.0) }, // area 400
            Pattern = ScatterPattern.Random,
            DensityMode = ScatterDensityMode.PerArea,
            PerAreaDensity = 0.5,
            Seed = 1
        });

        Assert.Equal(50, small.Count);   // 0.5 * 100
        Assert.Equal(200, large.Count);  // 0.5 * 400
    }

    [Fact]
    public void Sample_PoissonSpacing_RespectsMinimumDistance()
    {
        const double spacing = 1.5;
        var points = ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = new[] { Square(20.0) },
            Pattern = ScatterPattern.PoissonDisk,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = spacing,
            Seed = 11
        });

        Assert.True(points.Count > 20, $"expected a filled region, got {points.Count}");
        Assert.All(points, p => Assert.True(InSquare(p, 20.0)));

        double minSq = double.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                double dx = points[i].X - points[j].X;
                double dy = points[i].Y - points[j].Y;
                minSq = System.Math.Min(minSq, dx * dx + dy * dy);
            }
        }

        // No pair closer than the spacing radius (blue-noise guarantee), allowing a tiny epsilon.
        Assert.True(minSq >= spacing * spacing - 1e-6, $"closest pair {System.Math.Sqrt(minSq):F4} < spacing {spacing}");
    }

    [Fact]
    public void Sample_Grid_IsEvenlySpacedAndInside()
    {
        var points = ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = new[] { Square(10.0) },
            Pattern = ScatterPattern.Grid,
            DensityMode = ScatterDensityMode.Spacing,
            Spacing = 2.0,
            Seed = 0
        });

        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.True(InSquare(p)));
        // 10/2 = 5 columns x 5 rows of cell centres.
        Assert.Equal(25, points.Count);
    }

    [Fact]
    public void Sample_MultipleBoundaries_FillsEachRegion()
    {
        double[] left = { 0, 0, 5, 0, 5, 5, 0, 5 };
        double[] right = { 20, 0, 25, 0, 25, 5, 20, 5 };
        var points = ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = new[] { left, right },
            Pattern = ScatterPattern.Random,
            DensityMode = ScatterDensityMode.PerArea,
            PerAreaDensity = 1.0,
            Seed = 5
        });

        Assert.Contains(points, p => p.X <= 5.0 + 1e-9);
        Assert.Contains(points, p => p.X >= 20.0 - 1e-9);
        // No points land in the gap between the two squares.
        Assert.DoesNotContain(points, p => p.X > 6.0 && p.X < 19.0);
    }

    [Fact]
    public void Sample_EmptyOrDegenerateBoundary_ReturnsNothing()
    {
        Assert.Empty(ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = System.Array.Empty<double[]>(),
            DensityMode = ScatterDensityMode.Count,
            Count = 50
        }));

        Assert.Empty(ScatterSampler.Sample(new ScatterRequest
        {
            Boundaries = new[] { new[] { 0.0, 0.0, 1.0, 1.0 } }, // 2 points, not a polygon
            DensityMode = ScatterDensityMode.Count,
            Count = 50
        }));
    }
}
