using System.Linq;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The document and registry contract for the ponding analysis, plus the decisions that separate it from
/// its sibling on the Catchments card.
/// </summary>
public class PondingAnalysisTests
{
    private static T RoundTrip<T>(T analysis) where T : AnalysisDefinition
    {
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(analysis);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        return Assert.IsType<T>(Assert.Single(Assert.Single(TerrainSerializer.Deserialize(json)).Analyses));
    }

    [Fact]
    public void Registry_KnowsThePondingKind()
    {
        AnalysisTypeDescriptor descriptor = Assert.IsType<PondingAnalysisDescriptor>(
            AnalysisTypeRegistry.ForKind("ponding"));

        Assert.Equal(typeof(PondingAnalysisDefinition), descriptor.DefinitionType);
        Assert.IsType<PondingAnalysisDefinition>(AnalysisTypeRegistry.Create("ponding"));
        Assert.Same(descriptor, AnalysisTypeRegistry.ForType(typeof(PondingAnalysisDefinition)));
    }

    [Fact]
    public void RoundTrip_PreservesEverySetting()
    {
        var analysis = new PondingAnalysisDefinition
        {
            FlatSlopeThresholdDegrees = 0.6,
            MinimumDepth = 0.12,
            ShowOutlines = false,
            ShowSpillPoints = false,
            OutlineColorArgb = unchecked((int)0xFF112233),
            SpillPointColorArgb = unchecked((int)0xFF445566)
        };

        PondingAnalysisDefinition restored = RoundTrip(analysis);

        Assert.Equal(0.6, restored.FlatSlopeThresholdDegrees);
        Assert.Equal(0.12, restored.MinimumDepth);
        Assert.False(restored.ShowOutlines);
        Assert.False(restored.ShowSpillPoints);
        Assert.Equal(unchecked((int)0xFF112233), restored.OutlineColorArgb);
        Assert.Equal(unchecked((int)0xFF445566), restored.SpillPointColorArgb);
    }

    /// <summary>
    /// The depth threshold is a model length, and the flat threshold a slope. Neither may become a bare
    /// number: this is the card that has to be trusted, and an unlabelled "0.05" tells a reader nothing
    /// about whether it is millimetres or metres of standing water being ignored.
    /// </summary>
    [Fact]
    public void EveryNumericParameter_DeclaresAUnit()
    {
        var descriptor = AnalysisTypeRegistry.ForKind("ponding")!;

        var unitless = descriptor.Parameters
            .Where(p => p.Kind is ParameterKind.Number or ParameterKind.Slider)
            .Where(p => p.Unit == ParameterUnit.None)
            .Select(p => p.Key)
            .ToList();

        Assert.Empty(unitless);
        Assert.Equal(
            ParameterUnit.ModelLength,
            Assert.Single(descriptor.Parameters, p => p.Key == "MinimumDepth").Unit);
        Assert.Equal(
            ParameterUnit.Slope,
            Assert.Single(descriptor.Parameters, p => p.Key == "FlatSlopeThreshold").Unit);
    }

    /// <summary>
    /// Ponding carries a ramp and catchments deliberately does not. Ponded depth is a measurement on a
    /// continuum; a catchment index is a name. Pinned because the two cards sit next to each other and
    /// "make them consistent" is the obvious wrong tidy-up.
    /// </summary>
    [Fact]
    public void Ponding_OffersARamp_WhereCatchmentsDoesNot()
    {
        Assert.Contains(
            AnalysisTypeRegistry.ForKind("ponding")!.Parameters,
            p => p.Kind == ParameterKind.ColorRamp);
        Assert.DoesNotContain(
            AnalysisTypeRegistry.ForKind("catchment")!.Parameters,
            p => p.Kind == ParameterKind.ColorRamp);
    }

    /// <summary>Depth runs from zero upward, so the ramp's low end is the shoreline, not the deepest water.</summary>
    [Fact]
    public void RangeShape_RunsFromZero()
    {
        Assert.Equal(
            MoleHill.Core.Analysis.RangeShape.FromZero,
            TerrainAnalysisPreviewBuilder.GetRangeShape(new PondingAnalysisDefinition()));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void ProducesGeneratedOutput_TracksBothDrawnOutputs(bool outlines, bool spills, bool expected)
    {
        var analysis = new PondingAnalysisDefinition
        {
            ShowOutlines = outlines,
            ShowSpillPoints = spills
        };

        Assert.Equal(expected, TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(analysis));
    }

    [Fact]
    public void SupportsTerrainPreview_IncludesPonding()
    {
        Assert.True(TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(new PondingAnalysisDefinition()));
    }

    /// <summary>
    /// Both drainage cards derive from one base, which is what lets the build stage recognise that two
    /// cards agreeing on their routing share a basin graph instead of routing the terrain twice.
    /// </summary>
    [Fact]
    public void BothDrainageAnalyses_ShareTheDrainageBase()
    {
        Assert.IsAssignableFrom<DrainageAnalysisDefinition>(new PondingAnalysisDefinition());
        Assert.IsAssignableFrom<DrainageAnalysisDefinition>(new CatchmentAnalysisDefinition());
    }

    [Theory]
    [InlineData(LayerRole.Ponding)]
    [InlineData(LayerRole.PondingSpillPoints)]
    public void PondingRoles_ResolveToALayer(LayerRole role)
    {
        Assert.False(string.IsNullOrWhiteSpace(LayerRoleTable.Default.Path(role)));
    }

    /// <summary>
    /// Ponding is the loudest thing on the sheet because it reports a fault rather than describing the
    /// design, so it must not be seeded the same colour as the flow lines beside it.
    /// </summary>
    [Fact]
    public void PondingAndWaterflow_AreNeverSeededTheSameColour()
    {
        var table = LayerRoleTable.Default;

        Assert.NotEqual(
            table.Appearance(LayerRole.Ponding).ColorArgb,
            table.Appearance(LayerRole.Waterflow).ColorArgb);
    }
}
