using System;
using System.Linq;
using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Pins the ramp-editing semantics the panel card depends on. These are the operations behind the card's
/// handles and its +/−/⇉/⇄ toolbar; keeping them here means the rules can be checked without a UI.
/// </summary>
public sealed class ColorRampTests
{
    private static ColorRamp BlackToWhite() => new(new SlopeAnalyzer.ColorStop[]
    {
        new(0.0, 0, 0, 0),
        new(1.0, 255, 255, 255)
    });

    [Fact]
    public void Constructor_UnsortedStops_OrdersByPosition()
    {
        var ramp = new ColorRamp(new SlopeAnalyzer.ColorStop[]
        {
            new(0.8, 10, 10, 10),
            new(0.2, 20, 20, 20),
            new(0.5, 30, 30, 30)
        });

        Assert.Equal(new[] { 0.2, 0.5, 0.8 }, ramp.Stops.Select(stop => stop.Position));
    }

    [Fact]
    public void Constructor_EmptyStops_ProducesUsableRamp()
    {
        var ramp = new ColorRamp(Array.Empty<SlopeAnalyzer.ColorStop>());

        Assert.Equal(ColorRamp.MinimumStops, ramp.Count);
    }

    [Fact]
    public void Constructor_SingleStop_WidensToBothEnds()
    {
        var ramp = new ColorRamp(new SlopeAnalyzer.ColorStop[] { new(0.4, 12, 34, 56) });

        Assert.Equal(2, ramp.Count);
        Assert.Equal(0.0, ramp[0].Position);
        Assert.Equal(1.0, ramp[1].Position);
        Assert.Equal(12, ramp[1].R);
    }

    [Fact]
    public void Insert_ChoosesWidestGap()
    {
        var ramp = new ColorRamp(new SlopeAnalyzer.ColorStop[]
        {
            new(0.0, 0, 0, 0),
            new(0.1, 0, 0, 0),
            new(1.0, 0, 0, 0)
        });

        var inserted = ramp.Insert();

        Assert.Equal(4, inserted.Count);
        Assert.Equal(0.55, inserted[2].Position, 6);
    }

    [Fact]
    public void Insert_TakesTheColorAlreadyShownThere()
    {
        // Adding a stop must give you a handle without changing a single pixel.
        var inserted = BlackToWhite().Insert();

        Assert.Equal(0.5, inserted[1].Position, 6);
        Assert.Equal(128, inserted[1].R);
        Assert.Equal(128, inserted[1].G);
        Assert.Equal(128, inserted[1].B);
    }

    [Fact]
    public void RemoveAt_AtMinimumStops_LeavesRampUnchanged()
    {
        var ramp = BlackToWhite();

        Assert.Equal(2, ramp.RemoveAt(0).Count);
        Assert.Equal(2, ramp.RemoveAt(1).Count);
    }

    [Fact]
    public void RemoveAt_AboveMinimum_DropsTheStop()
    {
        var ramp = BlackToWhite().Insert();

        var removed = ramp.RemoveAt(1);

        Assert.Equal(2, removed.Count);
        Assert.Equal(new[] { 0.0, 1.0 }, removed.Stops.Select(stop => stop.Position));
    }

    [Fact]
    public void RemoveAt_OutOfRangeIndex_LeavesRampUnchanged()
    {
        var ramp = BlackToWhite().Insert();

        Assert.Equal(3, ramp.RemoveAt(-1).Count);
        Assert.Equal(3, ramp.RemoveAt(99).Count);
    }

    [Fact]
    public void Distribute_SpacesStopsEvenly()
    {
        var ramp = new ColorRamp(new SlopeAnalyzer.ColorStop[]
        {
            new(0.0, 1, 1, 1),
            new(0.05, 2, 2, 2),
            new(0.9, 3, 3, 3),
            new(1.0, 4, 4, 4)
        });

        var distributed = ramp.Distribute();

        Assert.Equal(new[] { 0.0, 1.0 / 3.0, 2.0 / 3.0, 1.0 }, distributed.Stops.Select(stop => stop.Position));
        // Colours ride along with their stop; only the spacing changes.
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, distributed.Stops.Select(stop => stop.R));
    }

    [Fact]
    public void Reverse_SwapsColorsAndKeepsPositions()
    {
        var ramp = new ColorRamp(new SlopeAnalyzer.ColorStop[]
        {
            new(0.0, 1, 1, 1),
            new(0.25, 2, 2, 2),
            new(1.0, 3, 3, 3)
        });

        var reversed = ramp.Reverse();

        Assert.Equal(new[] { 0.0, 0.25, 1.0 }, reversed.Stops.Select(stop => stop.Position));
        Assert.Equal(new byte[] { 3, 2, 1 }, reversed.Stops.Select(stop => stop.R));
    }

    [Fact]
    public void Reverse_Twice_RestoresOriginal()
    {
        var ramp = ColorRampPresets.ResolveRamp("turbo");

        Assert.True(ramp.Reverse().Reverse().Matches(ramp));
    }

    [Fact]
    public void WithStopAt_MovingPastNeighbour_ReSortsAndIsFoundByPosition()
    {
        var ramp = BlackToWhite().Insert();

        // Drag the middle stop past the top one; the array re-sorts, so the index it was grabbed by is
        // no longer the stop the user is holding. This is why the drag loop re-finds it by position.
        var moved = ramp.WithStopAt(1, 1.0);

        Assert.Equal(3, moved.Count);
        Assert.Equal(128, moved[moved.IndexNearest(1.0)].R);
    }

    [Fact]
    public void WithStopAt_CoincidentPosition_ReportsExactMovedStopIndex()
    {
        var ramp = BlackToWhite().Insert();

        // Move the upper endpoint onto the middle stop. Nearest-position lookup resolves this tie to the
        // middle stop, but a drag must keep hold of the white endpoint that was actually moved.
        var moved = ramp.WithStopAt(2, 0.5, out int movedIndex);

        Assert.Equal(2, movedIndex);
        Assert.Equal(255, moved[movedIndex].R);
        Assert.Equal(128, moved[moved.IndexNearest(0.5)].R);
    }

    [Fact]
    public void WithStopAt_PositionOutsideUnitRange_Clamps()
    {
        var ramp = BlackToWhite().Insert().WithStopAt(1, 5.0);

        Assert.All(ramp.Stops, stop => Assert.InRange(stop.Position, 0.0, 1.0));
    }

    [Fact]
    public void WithStopColor_ChangesColorOnly()
    {
        var ramp = BlackToWhite().WithStopColor(0, 10, 20, 30);

        Assert.Equal(0.0, ramp[0].Position);
        Assert.Equal(10, ramp[0].R);
        Assert.Equal(30, ramp[0].B);
    }

    [Fact]
    public void Resample_ProducesEvenlySpacedStopsAlongTheSameCurve()
    {
        var resampled = BlackToWhite().Resample(5);

        Assert.Equal(5, resampled.Count);
        Assert.Equal(new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }, resampled.Stops.Select(stop => stop.Position));
        Assert.Equal(128, resampled[2].R);
    }

    [Fact]
    public void Resample_ClampsToTheAllowedStopCount()
    {
        Assert.Equal(ColorRamp.MinimumStops, BlackToWhite().Resample(0).Count);
        Assert.Equal(ColorRamp.MaximumStops, BlackToWhite().Resample(1000).Count);
    }

    [Fact]
    public void Sample_MatchesTheMapperTheMeshIsColouredWith()
    {
        // The card and the terrain must never disagree about what a value looks like.
        var ramp = ColorRampPresets.ResolveRamp("viridis");
        var direct = AnalysisColorMapper.SamplePalette(0.37, ramp.Stops);
        var viaRamp = ramp.Sample(0.37);

        Assert.Equal(direct.R, viaRamp.R);
        Assert.Equal(direct.G, viaRamp.G);
        Assert.Equal(direct.B, viaRamp.B);
    }

    [Fact]
    public void Matches_DetectsColorAndPositionDifferences()
    {
        var ramp = BlackToWhite();

        Assert.True(ramp.Matches(BlackToWhite()));
        Assert.False(ramp.Matches(ramp.WithStopColor(0, 1, 1, 1)));
        Assert.False(ramp.Matches(ramp.Insert()));
        Assert.False(ramp.Matches(null));
    }

    [Fact]
    public void Constant_HoldsEachStopsColourUntilTheNextStop()
    {
        // The "one wide acceptable band, then a few narrow bad ones" case: thresholds at 0, 0.7, 0.85.
        var ramp = new ColorRamp(new SlopeAnalyzer.ColorStop[]
        {
            new(0.00, 0, 200, 0),
            new(0.70, 255, 200, 0),
            new(0.85, 255, 0, 0)
        });

        Assert.Equal(200, AnalysisColorMapper.SampleConstant(0.0, ramp.Stops).G);
        Assert.Equal(200, AnalysisColorMapper.SampleConstant(0.69, ramp.Stops).G);
        // Exactly on a stop takes that stop, and one step past it stays there — no blending anywhere.
        Assert.Equal(255, AnalysisColorMapper.SampleConstant(0.70, ramp.Stops).R);
        Assert.Equal(200, AnalysisColorMapper.SampleConstant(0.84, ramp.Stops).G);
        Assert.Equal(0, AnalysisColorMapper.SampleConstant(0.86, ramp.Stops).G);
        Assert.Equal(0, AnalysisColorMapper.SampleConstant(1.0, ramp.Stops).G);
    }

    [Fact]
    public void Constant_NeverBlends_UnlikeGradient()
    {
        var ramp = BlackToWhite();

        Assert.Equal(128, AnalysisColorMapper.SamplePalette(0.5, ramp.Stops).R);
        Assert.Equal(0, AnalysisColorMapper.SampleConstant(0.5, ramp.Stops).R);
    }

    [Fact]
    public void Constant_ClampsOutsideTheUnitRange()
    {
        var ramp = BlackToWhite();

        Assert.Equal(0, AnalysisColorMapper.SampleConstant(-5.0, ramp.Stops).R);
        Assert.Equal(255, AnalysisColorMapper.SampleConstant(5.0, ramp.Stops).R);
    }

    [Fact]
    public void SampleResolved_MatchesTheModeItIsGiven()
    {
        // Every colouring path funnels through SampleResolved; this is the guard that a mode added to
        // Core cannot quietly colour the mesh differently from the card.
        var ramp = BlackToWhite();
        var range = new AnalysisRange(0.0, 10.0, false);

        var gradient = AnalysisColorMapper.SampleResolved(
            5.0, range, AnalysisColorMapper.Mode.Gradient, null, ramp.Stops);
        var constant = AnalysisColorMapper.SampleResolved(
            5.0, range, AnalysisColorMapper.Mode.Constant, null, ramp.Stops);

        Assert.Equal(128, gradient.R);
        Assert.Equal(0, constant.R);
    }

    [Fact]
    public void ResolveBandsFor_OnlySteppedNeedsBands()
    {
        var ramp = BlackToWhite();
        var range = new AnalysisRange(0.0, 10.0, false);

        Assert.Null(AnalysisColorMapper.ResolveBandsFor(range, AnalysisColorMapper.Mode.Gradient, 0, ramp.Stops));
        Assert.Null(AnalysisColorMapper.ResolveBandsFor(range, AnalysisColorMapper.Mode.Constant, 0, ramp.Stops));
        Assert.NotNull(AnalysisColorMapper.ResolveBandsFor(range, AnalysisColorMapper.Mode.Stepped, 2, ramp.Stops));
    }

    [Fact]
    public void Presets_ResolveEveryLegacyKeyThatDocumentsMayStore()
    {
        // These four are a persistence contract: documents written before the ramp editor store one of
        // them in PalettePreset, and renaming a key would silently change how they draw.
        foreach (string key in new[] { "terrain-spectrum", "viridis", "magma", "cool-warm" })
            Assert.Equal(key, ColorRampPresets.Resolve(key).Key);
    }

    [Fact]
    public void Presets_UnknownKey_FallsBackToDefaultRatherThanThrowing()
    {
        Assert.Equal(ColorRampPresets.DefaultKey, ColorRampPresets.Resolve("from-a-newer-build").Key);
        Assert.Equal(ColorRampPresets.DefaultKey, ColorRampPresets.Resolve(null).Key);
        Assert.Equal(ColorRampPresets.DefaultKey, ColorRampPresets.Resolve("  ").Key);
    }

    [Fact]
    public void Presets_KeyLookupIsCaseInsensitive()
    {
        Assert.Equal("viridis", ColorRampPresets.Resolve("Viridis").Key);
        Assert.True(ColorRampPresets.IsBuiltIn("TURBO"));
        Assert.False(ColorRampPresets.IsBuiltIn("user:mine"));
    }

    [Fact]
    public void Presets_AllHaveUsableRamps()
    {
        Assert.All(ColorRampPresets.All, preset =>
        {
            Assert.True(preset.Ramp.Count >= ColorRamp.MinimumStops);
            Assert.Equal(0.0, preset.Ramp[0].Position, 6);
            Assert.Equal(1.0, preset.Ramp[^1].Position, 6);
        });
    }
}
