using MoleHill.Core.Displacement;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The two Displace sources as pure functions of world XY: noise must be identical on every machine and
/// runtime (golden values), stay inside its stated range and turn rigidly with its rotation; an image must
/// sample bilinearly between pixel centres, wrap or clamp at its edges, and map pixels to the ground through
/// its origin, rotation and tile size.
/// </summary>
public class DisplacementSourceTests
{
    // ---- Noise ----

    [Fact]
    public void GradientNoise_IsZeroOnEveryLatticePoint()
    {
        for (int x = -5; x <= 5; x++)
        {
            for (int y = -5; y <= 5; y++)
                Assert.Equal(0.0, GradientNoise.Sample(x, y, seed: 3));
        }
    }

    [Theory]
    [InlineData(0.25, 0.75, 1, 0.5, 7, GoldenA)]
    [InlineData(-3.6, 12.2, 4, 0.5, 7, GoldenB)]
    [InlineData(123456.789, -98765.4321, 6, 0.65, 42, GoldenC)]
    [InlineData(0.5, 0.5, 8, 1.0, -1, GoldenD)]
    public void Fractal_GoldenValues_NeverChange(double x, double y, int octaves, double roughness, int seed, double expected)
    {
        // A saved terrain must rebuild to the same ground on any machine and any .NET: the noise uses only
        // integer hashing and IEEE arithmetic, so these are exact, not approximate.
        Assert.Equal(expected, GradientNoise.Fractal(x, y, octaves, roughness, seed), 15);
    }

    private const double GoldenA = 0.14731244404544944;
    private const double GoldenB = -0.20660702980910037;
    private const double GoldenC = -0.1994146845783957;
    private const double GoldenD = 0.02897244342886822;

    [Fact]
    public void Fractal_StaysWithinUnitRange_AndUsesIt()
    {
        double extreme = 0.0;
        for (int octaves = 1; octaves <= GradientNoise.MaxOctaves; octaves += 3)
        {
            foreach (double roughness in new[] { 0.0, 0.5, 1.0 })
            {
                for (int i = 0; i < 4000; i++)
                {
                    double x = (i * 0.37) - 700.0;
                    double y = (i * 0.61) % 97.0;
                    double value = GradientNoise.Fractal(x, y, octaves, roughness, seed: 11);
                    Assert.InRange(value, -1.0, 1.0);
                    extreme = Math.Max(extreme, Math.Abs(value));
                }
            }
        }

        Assert.True(extreme > 0.4, $"the noise barely moves: largest magnitude {extreme}");
    }

    [Fact]
    public void Fractal_DifferentSeeds_GiveDifferentGround()
    {
        int differing = 0;
        for (int i = 0; i < 100; i++)
        {
            double x = i * 0.73, y = i * 0.29;
            if (GradientNoise.Fractal(x, y, 4, 0.5, 1) != GradientNoise.Fractal(x, y, 4, 0.5, 2))
                differing++;
        }

        Assert.True(differing > 90, $"{differing} of 100 samples differ between seeds");
    }

    [Fact]
    public void NoiseSource_OffsetStaysWithinHalfTheHeight()
    {
        var source = new NoiseDisplacementSource(size: 5.0, height: 2.0, octaves: 5, roughness: 0.7, seed: 9, new DisplacementPlacement(0, 0, 0));
        double highest = double.NegativeInfinity, lowest = double.PositiveInfinity;
        for (int i = 0; i < 20000; i++)
        {
            double offset = source.Offset((i % 200) * 0.31, (i / 200) * 0.29);
            highest = Math.Max(highest, offset);
            lowest = Math.Min(lowest, offset);
        }

        Assert.InRange(highest, 0.0, 1.0);
        Assert.InRange(lowest, -1.0, 0.0);
        Assert.True(highest - lowest > 0.5, $"range used {highest - lowest} of 2");
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(-115.0)]
    [InlineData(90.0)]
    public void NoiseSource_Rotation_TurnsThePatternRigidlyAboutTheOrigin(double degrees)
    {
        var straight = new NoiseDisplacementSource(4.0, 1.0, 4, 0.5, 5, new DisplacementPlacement(30.0, -20.0, 0.0));
        var turned = new NoiseDisplacementSource(4.0, 1.0, 4, 0.5, 5, new DisplacementPlacement(30.0, -20.0, degrees));
        double radians = degrees * Math.PI / 180.0;
        for (int i = 0; i < 50; i++)
        {
            double dx = (i * 0.83) - 20.0, dy = (i * 0.47) - 11.0;
            double rx = (dx * Math.Cos(radians)) - (dy * Math.Sin(radians));
            double ry = (dx * Math.Sin(radians)) + (dy * Math.Cos(radians));
            Assert.Equal(straight.Offset(30.0 + dx, -20.0 + dy), turned.Offset(30.0 + rx, -20.0 + ry), 9);
        }
    }

    [Fact]
    public void NoiseSource_MovingTheOrigin_MovesThePattern()
    {
        var atOrigin = new NoiseDisplacementSource(3.0, 1.0, 3, 0.5, 1, new DisplacementPlacement(0.0, 0.0, 0.0));
        var moved = new NoiseDisplacementSource(3.0, 1.0, 3, 0.5, 1, new DisplacementPlacement(100.0, 50.0, 0.0));
        for (int i = 0; i < 20; i++)
            Assert.Equal(atOrigin.Offset(i * 0.7, i * 0.3), moved.Offset(100.0 + (i * 0.7), 50.0 + (i * 0.3)), 12);
    }

    [Fact]
    public void NoiseSource_FinestSize_HalvesPerOctave()
    {
        var source = new NoiseDisplacementSource(8.0, 1.0, 4, 0.5, 1, new DisplacementPlacement(0, 0, 0));
        Assert.Equal(1.0, source.FinestSize, 12);
        Assert.Equal(8.0, source.DetailSize, 12);
    }

    // ---- Image ----

    /// <summary>A 2 x 2 image: top row 0.0, 0.2; bottom row 0.6, 1.0.</summary>
    private static HeightmapImage TwoByTwo() => new(2, 2, new[] { 0.0f, 0.2f, 0.6f, 1.0f });

    [Fact]
    public void Image_AtPixelCentres_ReturnsThePixel()
    {
        HeightmapImage image = TwoByTwo();

        // s from the left, t from the bottom: the bottom-left pixel is row 1, column 0.
        Assert.Equal(0.6, image.Sample(0.25, 0.25, DisplacementWrap.Clamp), 6);
        Assert.Equal(1.0, image.Sample(0.75, 0.25, DisplacementWrap.Clamp), 6);
        Assert.Equal(0.0, image.Sample(0.25, 0.75, DisplacementWrap.Clamp), 6);
        Assert.Equal(0.2, image.Sample(0.75, 0.75, DisplacementWrap.Clamp), 6);
    }

    [Fact]
    public void Image_BetweenCentres_IsBilinear()
    {
        HeightmapImage image = TwoByTwo();

        Assert.Equal((0.6 + 1.0) / 2.0, image.Sample(0.5, 0.25, DisplacementWrap.Clamp), 6);
        Assert.Equal((0.0 + 0.2 + 0.6 + 1.0) / 4.0, image.Sample(0.5, 0.5, DisplacementWrap.Clamp), 6);
        Assert.Equal(0.6 + (0.25 * (1.0 - 0.6)), image.Sample(0.375, 0.25, DisplacementWrap.Clamp), 6);
    }

    [Fact]
    public void Image_AtTheEdge_RepeatBlendsWithTheFarSide_ClampHoldsTheEdge()
    {
        HeightmapImage image = TwoByTwo();

        // The left edge of the bottom row lies halfway between its first pixel and (repeating) its last.
        Assert.Equal((0.6 + 1.0) / 2.0, image.Sample(0.0, 0.25, DisplacementWrap.Repeat), 6);
        Assert.Equal(0.6, image.Sample(0.0, 0.25, DisplacementWrap.Clamp), 6);

        // A whole tile further on, a repeating image reads the same; a clamped one keeps its edge.
        Assert.Equal(image.Sample(0.25, 0.25, DisplacementWrap.Repeat), image.Sample(3.25, -1.75, DisplacementWrap.Repeat), 9);
        Assert.Equal(1.0, image.Sample(7.0, 0.25, DisplacementWrap.Clamp), 6);
    }

    [Fact]
    public void ImageSource_NeutralValue_MeansNoOffset()
    {
        var grey = new HeightmapImage(1, 1, new[] { 0.5f });
        var source = new ImageDisplacementSource(grey, 10.0, 10.0, 2.0, 0.5, DisplacementWrap.Repeat, new DisplacementPlacement(0, 0, 0));
        Assert.Equal(0.0, source.Offset(3.0, 4.0), 12);

        var white = new HeightmapImage(1, 1, new[] { 1.0f });
        var black = new HeightmapImage(1, 1, new[] { 0.0f });
        Assert.Equal(1.0, new ImageDisplacementSource(white, 10, 10, 2.0, 0.5, DisplacementWrap.Repeat, default).Offset(1, 1), 12);
        Assert.Equal(-1.0, new ImageDisplacementSource(black, 10, 10, 2.0, 0.5, DisplacementWrap.Repeat, default).Offset(1, 1), 12);
        Assert.Equal(2.0, new ImageDisplacementSource(white, 10, 10, 2.0, 0.0, DisplacementWrap.Repeat, default).Offset(1, 1), 12);
    }

    [Fact]
    public void ImageSource_OriginRotationAndTileSize_PlaceThePixelsOnTheGround()
    {
        // A 20 x 10 tile whose lower-left corner is at (100, 50), turned 90 degrees: the image's X axis
        // points along world +Y and its top towards world -X.
        var source = new ImageDisplacementSource(TwoByTwo(), 20.0, 10.0, 1.0, 0.0, DisplacementWrap.Once, new DisplacementPlacement(100.0, 50.0, 90.0));

        // Bottom-left pixel centre: local (5, 2.5) -> world (100 - 2.5, 50 + 5).
        Assert.Equal(0.6, source.Offset(97.5, 55.0), 6);
        // Bottom-right pixel centre: local (15, 2.5) -> world (97.5, 65).
        Assert.Equal(1.0, source.Offset(97.5, 65.0), 6);
        // Top-left pixel centre: local (5, 7.5) -> world (92.5, 55).
        Assert.Equal(0.0, source.Offset(92.5, 55.0), 6);
        Assert.Equal(5.0, source.PixelSize, 12);
    }

    [Fact]
    public void ImageSource_Once_LeavesTheGroundAloneOutsideTheTile()
    {
        var white = new HeightmapImage(1, 1, new[] { 1.0f });
        var once = new ImageDisplacementSource(white, 10.0, 10.0, 1.0, 0.5, DisplacementWrap.Once, new DisplacementPlacement(0, 0, 0));
        var clamp = new ImageDisplacementSource(white, 10.0, 10.0, 1.0, 0.5, DisplacementWrap.Clamp, new DisplacementPlacement(0, 0, 0));

        Assert.Equal(0.5, once.Offset(5.0, 5.0), 12);
        Assert.Equal(0.0, once.Offset(-1.0, 5.0), 12);
        Assert.Equal(0.0, once.Offset(5.0, 10.5), 12);
        Assert.Equal(0.5, clamp.Offset(-1.0, 5.0), 12);
    }
}
