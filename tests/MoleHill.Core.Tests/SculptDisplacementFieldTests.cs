using MoleHill.Core.Sculpting;
using Xunit;

namespace MoleHill.Core.Tests;

public class SculptDisplacementFieldTests
{
    [Fact]
    public void Sample_EmptyField_ReturnsZero()
    {
        var field = new SculptDisplacementField(0.5);

        Assert.Equal(0.0, field.Sample(3.2, -7.9));
        Assert.True(field.IsEmpty);
    }

    [Fact]
    public void SetSample_ThenSampleAtSamplePoint_ReturnsValue()
    {
        var field = new SculptDisplacementField(0.5);
        field.SetSample(10, -4, 2.5f);

        Assert.Equal(2.5f, field.GetSample(10, -4));
        Assert.Equal(2.5, field.Sample(10 * 0.5, -4 * 0.5), 6);
    }

    [Fact]
    public void Sample_BetweenSamples_BilinearlyInterpolates()
    {
        var field = new SculptDisplacementField(1.0);
        field.SetSample(0, 0, 1f);
        field.SetSample(1, 0, 3f);

        // Halfway between the two samples along X, on the sample row.
        Assert.Equal(2.0, field.Sample(0.5, 0.0), 6);
        // Halfway toward the zero row above blends toward 0.
        Assert.Equal(1.0, field.Sample(0.5, 0.5), 6);
    }

    [Fact]
    public void Sample_AcrossTileBoundary_IsContinuous()
    {
        var field = new SculptDisplacementField(1.0);
        // Sample 63 is the last sample of tile 0; sample 64 is the first of tile 1.
        field.SetSample(63, 0, 4f);
        field.SetSample(64, 0, 8f);

        Assert.Equal(4.0, field.Sample(63.0, 0.0), 6);
        Assert.Equal(8.0, field.Sample(64.0, 0.0), 6);
        Assert.Equal(6.0, field.Sample(63.5, 0.0), 6);
    }

    [Fact]
    public void SetSample_NegativeCoordinates_RoundTripsTileIndexing()
    {
        var field = new SculptDisplacementField(0.25);

        for (int gi = -130; gi <= 2; gi += 33)
        {
            for (int gj = -70; gj <= 70; gj += 37)
            {
                float value = gi * 1000f + gj;
                field.SetSample(gi, gj, value);
                Assert.Equal(value, field.GetSample(gi, gj));
            }
        }
    }

    [Fact]
    public void SetSample_Zero_DoesNotAllocateTile()
    {
        var field = new SculptDisplacementField(1.0);
        field.SetSample(5, 5, 0f);

        Assert.True(field.IsEmpty);
    }

    [Fact]
    public void EncodeTile_ThenDecode_RoundTripsExactly()
    {
        float[] samples = new float[SculptDisplacementField.TileSize * SculptDisplacementField.TileSize];
        var rng = new Random(42);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(rng.NextDouble() * 20.0 - 10.0);

        byte[] payload = SculptDisplacementField.EncodeTile(samples);
        float[] decoded = SculptDisplacementField.DecodeTile(payload);

        Assert.Equal(samples, decoded);
    }

    [Fact]
    public void EncodeTile_SmoothData_CompressesBelowRawSize()
    {
        float[] samples = new float[SculptDisplacementField.TileSize * SculptDisplacementField.TileSize];
        for (int j = 0; j < SculptDisplacementField.TileSize; j++)
        {
            for (int i = 0; i < SculptDisplacementField.TileSize; i++)
                samples[j * SculptDisplacementField.TileSize + i] = (float)Math.Sin(i * 0.1) * 2f;
        }

        byte[] payload = SculptDisplacementField.EncodeTile(samples);

        Assert.True(payload.Length < samples.Length * sizeof(float));
    }

    [Fact]
    public void PruneZeroTiles_AllZeroTile_Removed()
    {
        var field = new SculptDisplacementField(1.0);
        field.SetSample(0, 0, 5f);   // tile (0,0) stays
        field.SetSample(100, 100, 1f);
        field.SetSample(100, 100, 0f); // tile (1,1) becomes all-zero

        field.PruneZeroTiles();

        Assert.Single(field.Tiles);
        Assert.Equal(5f, field.GetSample(0, 0));
    }

    [Fact]
    public void HasInfluenceNear_OutsideOccupiedArea_ReturnsFalse()
    {
        var field = new SculptDisplacementField(1.0);
        field.SetSample(0, 0, 5f);

        Assert.True(field.HasInfluenceNear(0.0, 0.0, 1.0));
        Assert.True(field.HasInfluenceNear(1.5, 0.0, 2.0));
        Assert.False(field.HasInfluenceNear(50.0, 50.0, 2.0));
    }

    [Fact]
    public void TryGetNonZeroBounds_TwoSamples_CoversBothPlusMargin()
    {
        var field = new SculptDisplacementField(2.0);
        field.SetSample(-3, 1, 1f);
        field.SetSample(5, 4, 1f);

        Assert.True(field.TryGetNonZeroBounds(out double minX, out double maxX, out double minY, out double maxY));
        Assert.Equal((-3 - 1) * 2.0, minX, 6);
        Assert.Equal((5 + 1) * 2.0, maxX, 6);
        Assert.Equal((1 - 1) * 2.0, minY, 6);
        Assert.Equal((4 + 1) * 2.0, maxY, 6);
    }

    [Fact]
    public void Resample_HalfCellSize_PreservesSampledSurface()
    {
        var field = new SculptDisplacementField(1.0);
        for (int gi = 0; gi <= 8; gi++)
        {
            for (int gj = 0; gj <= 8; gj++)
                field.SetSample(gi, gj, (float)(Math.Sin(gi * 0.5) + Math.Cos(gj * 0.5)));
        }

        var fine = field.Resample(0.5);

        // The refined field must reproduce the coarse field at arbitrary positions within bilinear accuracy.
        for (double x = 1.0; x <= 7.0; x += 0.73)
        {
            for (double y = 1.0; y <= 7.0; y += 0.61)
                Assert.Equal(field.Sample(x, y), fine.Sample(x, y), 3);
        }
    }

    [Fact]
    public void ReplaceTile_Null_RemovesTile()
    {
        var field = new SculptDisplacementField(1.0);
        field.SetSample(0, 0, 5f);

        field.ReplaceTile(0, 0, null);

        Assert.True(field.IsEmpty);
        Assert.Equal(0f, field.GetSample(0, 0));
    }

    [Fact]
    public void CopyTile_ExistingTile_ReturnsIndependentCopy()
    {
        var field = new SculptDisplacementField(1.0);
        field.SetSample(2, 2, 7f);

        float[]? copy = field.CopyTile(0, 0);
        Assert.NotNull(copy);
        field.SetSample(2, 2, 9f);

        var restored = new SculptDisplacementField(1.0);
        restored.ReplaceTile(0, 0, copy);
        Assert.Equal(7f, restored.GetSample(2, 2));
        Assert.Null(field.CopyTile(5, 5));
    }
}
