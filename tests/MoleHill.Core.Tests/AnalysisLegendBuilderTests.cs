using System.Globalization;
using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The drawn legend must describe the colours the mesh is painted with. Each mode's key is checked
/// against the sampler the mesh uses, not only against expected labels.
/// </summary>
public sealed class AnalysisLegendBuilderTests
{
    private static readonly SlopeAnalyzer.ColorStop[] BlackToWhite =
    {
        new(0, 0, 0, 0),
        new(1, 255, 255, 255)
    };

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    [Fact]
    public void ForRamp_Gradient_SamplesThePaletteEndToEndAndTicksSitAtTheirValues()
    {
        var range = new AnalysisRange(0, 10, false);

        AnalysisLegend legend = AnalysisLegendBuilder.ForRamp(range, AnalysisColorMapper.Mode.Gradient, 0, BlackToWhite, Format);

        Assert.Equal(AnalysisLegendStyle.Gradient, legend.Style);
        Assert.Empty(legend.Entries);
        Assert.Equal(AnalysisLegendBuilder.GradientSamples, legend.Gradient.Count);
        Assert.Equal(0, legend.Gradient[0].R);
        Assert.Equal(255, legend.Gradient[^1].R);
        Assert.Equal(0.0, legend.Gradient[0].Position);
        Assert.Equal(1.0, legend.Gradient[^1].Position);

        Assert.NotEmpty(legend.Ticks);
        foreach (AnalysisLegendTick tick in legend.Ticks)
        {
            double value = double.Parse(tick.Label, CultureInfo.InvariantCulture);
            Assert.Equal(range.Normalize(value), tick.Position, 9);
        }
    }

    [Fact]
    public void ForRamp_Stepped_ListsTheBandsTheMeshIsPaintedWith()
    {
        var range = new AnalysisRange(0, 10, false);
        var bands = AnalysisColorMapper.ResolveBands(range, 2.5, BlackToWhite);

        AnalysisLegend legend = AnalysisLegendBuilder.ForRamp(range, AnalysisColorMapper.Mode.Stepped, 2.5, BlackToWhite, Format);

        Assert.Equal(AnalysisLegendStyle.Swatches, legend.Style);
        Assert.Equal(bands.Count, legend.Entries.Count);
        for (int i = 0; i < bands.Count; i++)
        {
            SlopeAnalyzer.ColorStop painted = AnalysisColorMapper.SampleResolved(
                bands[i].Center, range, AnalysisColorMapper.Mode.Stepped, bands, BlackToWhite);
            Assert.Equal(painted.R, legend.Entries[i].Color.R);
        }
    }

    [Fact]
    public void ForRamp_Stepped_EndLabelsAreOpenBecauseValuesPastTheRangeTakeTheEndColours()
    {
        var range = new AnalysisRange(0, 10, false);

        AnalysisLegend legend = AnalysisLegendBuilder.ForRamp(range, AnalysisColorMapper.Mode.Stepped, 2.5, BlackToWhite, Format);

        Assert.Equal(new[] { "< 2.5", "2.5 – 5", "5 – 7.5", "≥ 7.5" }, legend.Entries.Select(entry => entry.Label));
    }

    [Fact]
    public void ForRamp_Constant_StopsAreTheThresholds()
    {
        var palette = new SlopeAnalyzer.ColorStop[]
        {
            new(0.0, 10, 0, 0),
            new(0.5, 20, 0, 0),
            new(0.8, 30, 0, 0)
        };
        var range = new AnalysisRange(0, 100, false);

        AnalysisLegend legend = AnalysisLegendBuilder.ForRamp(range, AnalysisColorMapper.Mode.Constant, 0, palette, Format);

        Assert.Equal(new[] { "< 50", "50 – 80", "≥ 80" }, legend.Entries.Select(entry => entry.Label));
        Assert.Equal(new byte[] { 10, 20, 30 }, legend.Entries.Select(entry => entry.Color.R));
    }

    [Fact]
    public void ForRamp_Constant_EverySwatchMatchesTheColourPaintedInsideIt()
    {
        // First stop off zero and two coincident stops: the cases where a naive reading of the stops
        // would key a colour the mesh never shows.
        var palette = new SlopeAnalyzer.ColorStop[]
        {
            new(0.2, 10, 0, 0),
            new(0.4, 20, 0, 0),
            new(0.4, 30, 0, 0),
            new(0.9, 40, 0, 0)
        };
        var range = new AnalysisRange(-5, 5, false);

        AnalysisLegend legend = AnalysisLegendBuilder.ForRamp(range, AnalysisColorMapper.Mode.Constant, 0, palette, Format);

        // 10 below 0.4, 30 (the later coincident stop) to 0.9, then 40. 20 is never painted.
        Assert.Equal(new byte[] { 10, 30, 40 }, legend.Entries.Select(entry => entry.Color.R));
        foreach ((double t, byte expected) in new[] { (0.05, (byte)10), (0.3, (byte)10), (0.6, (byte)30), (0.95, (byte)40) })
            Assert.Equal(expected, AnalysisColorMapper.SampleConstant(t, palette).R);
    }

    [Fact]
    public void ForRamp_SingleBand_IsLabelledClosed()
    {
        var range = new AnalysisRange(0, 1, false);

        AnalysisLegend legend = AnalysisLegendBuilder.ForRamp(range, AnalysisColorMapper.Mode.Stepped, 5, BlackToWhite, Format);

        Assert.Equal("0 – 1", Assert.Single(legend.Entries).Label);
    }

    [Fact]
    public void ForCategories_KeepsTheGivenOrder()
    {
        var entries = new[]
        {
            new AnalysisLegendEntry(new SlopeAnalyzer.ColorStop(0, 1, 2, 3), "Within limit"),
            new AnalysisLegendEntry(new SlopeAnalyzer.ColorStop(0, 4, 5, 6), "Over limit")
        };

        AnalysisLegend legend = AnalysisLegendBuilder.ForCategories(entries);

        Assert.Equal(AnalysisLegendStyle.Swatches, legend.Style);
        Assert.Equal(new[] { "Within limit", "Over limit" }, legend.Entries.Select(entry => entry.Label));
    }

    [Fact]
    public void BuildTicks_AreRoundValuesInsideTheRange()
    {
        IReadOnlyList<double> ticks = AnalysisRange.BuildTicks(new AnalysisRange(-3, 17, false));

        Assert.Equal(new[] { 0.0, 5.0, 10.0, 15.0 }, ticks);
    }
}
