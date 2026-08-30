using System.Collections.Generic;
using System.Linq;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The document contract for edited colour ramps. The load path has to be forgiving — a colour preference
/// must never stop a terrain from drawing — and, most importantly, documents written before the ramp
/// editor existed must keep drawing exactly as they did.
/// </summary>
public class AnalysisColorRampSerializationTests
{
    private static TerrainDefinition TerrainWith(AnalysisDefinition analysis)
    {
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(analysis);
        return terrain;
    }

    private static AnalysisDefinition RoundTrip(AnalysisDefinition analysis)
    {
        string json = TerrainSerializer.Serialize(new[] { TerrainWith(analysis) });
        return Assert.Single(Assert.Single(TerrainSerializer.Deserialize(json)).Analyses);
    }

    [Fact]
    public void RoundTrip_EditedStops_PreservesPositionsAndColors()
    {
        var analysis = new SlopeAnalysisDefinition();
        analysis.SetRamp(new ColorRamp(new SlopeAnalyzer.ColorStop[]
        {
            new(0.0, 10, 20, 30),
            new(0.4, 40, 50, 60),
            new(1.0, 70, 80, 90)
        }));

        ColorRamp restored = RoundTrip(analysis).ResolveRamp();

        Assert.Equal(3, restored.Count);
        Assert.Equal(new[] { 0.0, 0.4, 1.0 }, restored.Stops.Select(stop => stop.Position));
        Assert.Equal(40, restored[1].R);
        Assert.Equal(50, restored[1].G);
        Assert.Equal(60, restored[1].B);
    }

    [Fact]
    public void ResolveRamp_NoOverride_UsesTheNamedPreset()
    {
        // The pre-ramp-editor state: a preset key and nothing else. This is what every existing document
        // says, so it has to resolve to exactly the preset's own stops.
        var analysis = new ElevationAnalysisDefinition { PalettePreset = "viridis" };

        Assert.Empty(analysis.PaletteStops);
        Assert.True(analysis.ResolveRamp().Matches(ColorRampPresets.ResolveRamp("viridis")));
    }

    [Fact]
    public void ApplyPreset_DropsTheOverride()
    {
        var analysis = new ElevationAnalysisDefinition();
        analysis.SetRamp(ColorRampPresets.ResolveRamp("magma").Reverse());
        Assert.NotEmpty(analysis.PaletteStops);

        analysis.ApplyPreset("turbo");

        Assert.Empty(analysis.PaletteStops);
        Assert.Equal("turbo", analysis.PalettePreset);
        Assert.True(analysis.ResolveRamp().Matches(ColorRampPresets.ResolveRamp("turbo")));
    }

    [Fact]
    public void ApplyPreset_UnknownKey_FallsBackToTheDefault()
    {
        var analysis = new SlopeAnalysisDefinition();

        analysis.ApplyPreset("no-such-ramp");

        Assert.Equal(ColorRampPresets.DefaultKey, analysis.PalettePreset);
    }

    [Fact]
    public void SetRamp_StoresOpaqueColors()
    {
        // Alpha belongs to the terrain's transparency, not to a stop. A stop that round-tripped with
        // alpha 0 would come back invisible.
        var analysis = new SlopeAnalysisDefinition();
        analysis.SetRamp(new ColorRamp(new SlopeAnalyzer.ColorStop[] { new(0.0, 1, 2, 3), new(1.0, 4, 5, 6) }));

        Assert.All(analysis.PaletteStops, stop => Assert.Equal(0xFF, (stop.ColorArgb >> 24) & 0xFF));
    }

    [Fact]
    public void Deserialize_TooFewStops_FallsBackToThePreset()
    {
        // A one-stop list is not a ramp; treating it as "no override" is the only reading that leaves the
        // analysis able to draw.
        var analysis = new SlopeAnalysisDefinition
        {
            PalettePreset = "magma",
            PaletteStops = new List<AnalysisColorStopState>
            {
                new() { Position = 0.5, ColorArgb = unchecked((int)0xFF808080) }
            }
        };

        AnalysisDefinition restored = RoundTrip(analysis);

        Assert.Empty(restored.PaletteStops);
        Assert.True(restored.ResolveRamp().Matches(ColorRampPresets.ResolveRamp("magma")));
    }

    [Fact]
    public void Deserialize_UnsortedOrOutOfRangeStops_AreNormalized()
    {
        var analysis = new ElevationAnalysisDefinition
        {
            PaletteStops = new List<AnalysisColorStopState>
            {
                new() { Position = 2.0, ColorArgb = unchecked((int)0xFF112233) },
                new() { Position = -1.0, ColorArgb = unchecked((int)0xFF445566) },
                new() { Position = 0.5, ColorArgb = unchecked((int)0xFF778899) }
            }
        };

        AnalysisDefinition restored = RoundTrip(analysis);

        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, restored.PaletteStops.Select(stop => stop.Position));
    }

    [Fact]
    public void Deserialize_StopWithZeroAlpha_ComesBackOpaque()
    {
        var analysis = new ElevationAnalysisDefinition
        {
            PaletteStops = new List<AnalysisColorStopState>
            {
                new() { Position = 0.0, ColorArgb = 0x00123456 },
                new() { Position = 1.0, ColorArgb = 0x00654321 }
            }
        };

        AnalysisDefinition restored = RoundTrip(analysis);

        Assert.All(restored.PaletteStops, stop => Assert.Equal(0xFF, (stop.ColorArgb >> 24) & 0xFF));
        Assert.Equal(0x12, restored.ResolveRamp()[0].R);
    }

    [Fact]
    public void Deserialize_LegacyPaletteKey_StillResolves()
    {
        // These four keys are what documents in the wild contain.
        foreach (string key in new[] { "terrain-spectrum", "viridis", "magma", "cool-warm" })
        {
            AnalysisDefinition restored = RoundTrip(new SlopeAnalysisDefinition { PalettePreset = key });
            Assert.Equal(key, restored.PalettePreset);
        }
    }
}
