using MoleHill.Core.Analysis;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The card draws the histogram across the ramp, which spans the mapped range, so the bars must be
/// binned over that range — not over the data's own min..max — or they sit under the wrong colours.
/// </summary>
public class AnalysisDistributionBinningTests
{
    [Fact]
    public void BuildRangeDistribution_DataNarrowerThanRange_BinsAgainstMappedRange()
    {
        // All data at 10..20 in a 0..40 range: with 4 bars, only the second bar (10..20) is occupied.
        // Binning over the data extent would have spread it across all four bars.
        double[] values = { 10.0, 12.0, 15.0, 19.9 };

        double[]? bars = TerrainAnalysisPreviewBuilder.BuildRangeDistribution(
            values, ReadOnlySpan<double>.Empty, mask: null, new AnalysisRange(0.0, 40.0, false), barCount: 4);

        Assert.NotNull(bars);
        Assert.Equal(new[] { 0.0, 1.0, 0.0, 0.0 }, bars);
    }

    [Fact]
    public void BuildRangeDistribution_ValuesOutsideRange_FallInEndBars()
    {
        double[] values = { -5.0, 50.0, 50.0 };
        double[] weights = { 1.0, 1.0, 1.0 };

        double[]? bars = TerrainAnalysisPreviewBuilder.BuildRangeDistribution(
            values, weights, mask: null, new AnalysisRange(0.0, 40.0, false), barCount: 4);

        Assert.NotNull(bars);
        Assert.Equal(new[] { 0.5, 0.0, 0.0, 1.0 }, bars);
    }

    [Fact]
    public void BuildRangeDistribution_MaskedAndNonFiniteValues_AreIgnored()
    {
        double[] values = { 5.0, double.NaN, 35.0, double.PositiveInfinity };
        double[] weights = { 1.0, 1.0, 3.0, 1.0 };
        bool[] mask = { true, true, false, true };

        double[]? bars = TerrainAnalysisPreviewBuilder.BuildRangeDistribution(
            values, weights, mask, new AnalysisRange(0.0, 40.0, false), barCount: 4);

        Assert.NotNull(bars);
        Assert.Equal(new[] { 1.0, 0.0, 0.0, 0.0 }, bars);
    }

    [Fact]
    public void BuildRangeDistribution_NoRange_ReturnsNull()
    {
        Assert.Null(TerrainAnalysisPreviewBuilder.BuildRangeDistribution(
            new[] { 1.0 }, ReadOnlySpan<double>.Empty, mask: null, range: null, barCount: 4));
    }
}
