using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class AnalysisRangeTests
{
    /// <summary>
    /// The bug this whole type exists for: a terrain of gentle grades plus one retaining wall used to fit
    /// its ramp to the wall, so every real gradient painted the same colour.
    /// </summary>
    [Fact]
    public void Resolve_Auto_TrimsOutliersInsteadOfFittingTheExtreme()
    {
        var values = new double[200];
        for (int i = 0; i < values.Length; i++)
            values[i] = 5.0 + (i % 10);
        values[^1] = 400.0;

        AnalysisRange range = AnalysisRange.Resolve(
            values, ReadOnlySpan<double>.Empty, auto: true, 0.0, 0.0, RangeShape.FromZero);

        Assert.Equal(0.0, range.Low);
        Assert.True(range.High < 100.0, $"Expected the outlier to be trimmed, got {range.High}.");
        Assert.True(range.High >= 14.0, $"Expected the real gradients to stay inside the range, got {range.High}.");
    }

    /// <summary>
    /// A cyclic range is the compass, so it is pinned whatever the data says. Fitting it would be worse
    /// than useless: trim a bearing range and the colours stop naming directions.
    /// </summary>
    [Fact]
    public void Resolve_Cyclic_PinsAFullTurnWhateverTheValues()
    {
        var values = new[] { 92.0, 94.0, 96.0, 98.0 };

        AnalysisRange auto = AnalysisRange.Resolve(
            values, ReadOnlySpan<double>.Empty, auto: true, 0.0, 0.0, RangeShape.Cyclic);
        AnalysisRange requested = AnalysisRange.FromRequested(90.0, 100.0, RangeShape.Cyclic);

        Assert.Equal(0.0, auto.Low);
        Assert.Equal(AnalysisRange.FullTurnDegrees, auto.High);
        Assert.Equal(0.0, requested.Low);
        Assert.Equal(AnalysisRange.FullTurnDegrees, requested.High);
    }

    /// <summary>With no data at all a cyclic range is still the compass, not a 0..1 placeholder.</summary>
    [Fact]
    public void Resolve_Cyclic_WithNoValues_IsStillAFullTurn()
    {
        AnalysisRange range = AnalysisRange.Resolve(
            ReadOnlySpan<double>.Empty, ReadOnlySpan<double>.Empty, auto: true, 0.0, 0.0, RangeShape.Cyclic);

        Assert.Equal(0.0, range.Low);
        Assert.Equal(AnalysisRange.FullTurnDegrees, range.High);
    }

    /// <summary>Values past the fitted range still draw — they clamp, they are not dropped.</summary>
    [Fact]
    public void Normalize_ValuesOutsideTheRange_ClampToTheEnds()
    {
        var range = new AnalysisRange(0.0, 50.0, true);

        Assert.Equal(0.0, range.Normalize(-10.0));
        Assert.Equal(1.0, range.Normalize(400.0));
        Assert.Equal(0.5, range.Normalize(25.0), precision: 9);
    }

    [Fact]
    public void Resolve_Auto_IgnoresNonFiniteValues()
    {
        var values = new[] { 10.0, 12.0, 11.0, double.PositiveInfinity, double.NaN };

        AnalysisRange range = AnalysisRange.Resolve(
            values, ReadOnlySpan<double>.Empty, auto: true, 0.0, 0.0, RangeShape.MinMax);

        Assert.True(double.IsFinite(range.Low));
        Assert.True(double.IsFinite(range.High));
        Assert.True(range.High < 100.0);
    }

    [Fact]
    public void Resolve_Auto_AreaWeightsTheDistribution()
    {
        // One tiny steep sliver against a large flat plate: the weighted fit follows the plate.
        var values = new[] { 2.0, 2.0, 2.0, 90.0 };
        var areas = new[] { 500.0, 500.0, 500.0, 0.01 };

        AnalysisRange weighted = AnalysisRange.Resolve(
            values, areas, auto: true, 0.0, 0.0, RangeShape.FromZero);

        Assert.True(weighted.High < 90.0, $"Expected the sliver to be trimmed, got {weighted.High}.");
    }

    [Fact]
    public void Resolve_SymmetricShape_MirrorsAboutZero()
    {
        var values = new[] { -3.0, -1.0, 0.5, 2.0, 4.0 };

        AnalysisRange range = AnalysisRange.Resolve(
            values, ReadOnlySpan<double>.Empty, auto: true, 0.0, 0.0, RangeShape.SymmetricAboutZero);

        Assert.Equal(-range.High, range.Low, precision: 9);
        Assert.True(range.High > 0.0);
    }

    [Fact]
    public void FromRequested_SymmetricShape_KeepsBoundsMirrored()
    {
        AnalysisRange range = AnalysisRange.FromRequested(-1.0, 4.0, RangeShape.SymmetricAboutZero);

        Assert.Equal(-4.0, range.Low);
        Assert.Equal(4.0, range.High);
        Assert.False(range.IsAuto);
    }

    [Fact]
    public void FromRequested_FromZeroShape_NeverGoesNegative()
    {
        AnalysisRange range = AnalysisRange.FromRequested(-5.0, 30.0, RangeShape.FromZero);

        Assert.Equal(0.0, range.Low);
        Assert.Equal(30.0, range.High);
    }

    [Fact]
    public void SnapOutward_RoundsToReadableNumbersAndNeverInward()
    {
        AnalysisRange range = AnalysisRange.SnapOutward(0.0, 34.7183, RangeShape.FromZero);

        Assert.Equal(0.0, range.Low);
        Assert.True(range.High >= 34.7183, "Snapping must never crop the fitted spread.");
        Assert.Equal(range.High, Math.Round(range.High), precision: 9);
    }

    [Fact]
    public void SnapOutward_DegenerateSpan_StillGivesTheRampRoom()
    {
        AnalysisRange flat = AnalysisRange.SnapOutward(7.0, 7.0, RangeShape.MinMax);
        Assert.True(flat.Span > 0.0);

        AnalysisRange symmetric = AnalysisRange.SnapOutward(0.0, 0.0, RangeShape.SymmetricAboutZero);
        Assert.Equal(-symmetric.High, symmetric.Low, precision: 9);
        Assert.True(symmetric.Span > 0.0);
    }

    [Fact]
    public void Resolve_NoValues_ReturnsAUsableRange()
    {
        AnalysisRange range = AnalysisRange.Resolve(
            ReadOnlySpan<double>.Empty, ReadOnlySpan<double>.Empty, auto: true, 0.0, 0.0, RangeShape.FromZero);

        Assert.True(range.Span > 0.0);
    }

    [Fact]
    public void NiceStep_PicksOneTwoFiveTenDecades()
    {
        Assert.Equal(1.0, AnalysisRange.NiceStep(0.7));
        Assert.Equal(2.0, AnalysisRange.NiceStep(1.5));
        Assert.Equal(5.0, AnalysisRange.NiceStep(4.2));
        Assert.Equal(10.0, AnalysisRange.NiceStep(6.0));
        Assert.Equal(100.0, AnalysisRange.NiceStep(60.0));
        Assert.Equal(1.0, AnalysisRange.NiceStep(double.NaN));
    }

    [Fact]
    public void Histogram_Percentile_IsWeighted()
    {
        var histogram = new AnalysisRange.Histogram();
        histogram.Observe(0.0);
        histogram.Observe(100.0);
        histogram.FreezeBounds();
        histogram.Add(0.0, 90.0);
        histogram.Add(100.0, 10.0);

        // Nine tenths of the weight sits at zero, so the median does too.
        Assert.True(histogram.Percentile(0.5) < 1.0);
        Assert.True(histogram.Percentile(0.99) > 90.0);
    }
}
