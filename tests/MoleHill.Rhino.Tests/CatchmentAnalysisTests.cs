using System.Linq;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The document and registry contract for the catchment analysis, and the two decisions about it that a
/// later change could plausibly undo without any other test noticing.
/// </summary>
public class CatchmentAnalysisTests
{
    private static T RoundTrip<T>(T analysis) where T : AnalysisDefinition
    {
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(analysis);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        return Assert.IsType<T>(Assert.Single(Assert.Single(TerrainSerializer.Deserialize(json)).Analyses));
    }

    [Fact]
    public void Registry_KnowsTheCatchmentKind()
    {
        AnalysisTypeDescriptor descriptor = Assert.IsType<CatchmentAnalysisDescriptor>(
            AnalysisTypeRegistry.ForKind("catchment"));

        Assert.Equal(typeof(CatchmentAnalysisDefinition), descriptor.DefinitionType);
        Assert.IsType<CatchmentAnalysisDefinition>(AnalysisTypeRegistry.Create("catchment"));
        Assert.Same(descriptor, AnalysisTypeRegistry.ForType(typeof(CatchmentAnalysisDefinition)));
    }

    [Fact]
    public void RoundTrip_PreservesEveryRoutingAndDrawingSetting()
    {
        var analysis = new CatchmentAnalysisDefinition
        {
            FlatSlopeThresholdDegrees = 0.75,
            MinimumBasinAreaPercent = 2.5,
            ShowBoundaries = false,
            ShowFlowPaths = true,
            BoundaryColorArgb = unchecked((int)0xFF123456),
            FlowPathColorArgb = unchecked((int)0xFF654321)
        };

        CatchmentAnalysisDefinition restored = RoundTrip(analysis);

        Assert.Equal(0.75, restored.FlatSlopeThresholdDegrees);
        Assert.Equal(2.5, restored.MinimumBasinAreaPercent);
        Assert.False(restored.ShowBoundaries);
        Assert.True(restored.ShowFlowPaths);
        Assert.Equal(unchecked((int)0xFF123456), restored.BoundaryColorArgb);
        Assert.Equal(unchecked((int)0xFF654321), restored.FlowPathColorArgb);
    }

    /// <summary>
    /// Every numeric row carries a unit, because this product does not show unlabelled numbers. The merge
    /// threshold is the interesting one: it is a *share* rather than an area precisely so it can be
    /// labelled, since there is no model-area unit to label an area with. Turning it back into an area
    /// would quietly reintroduce a bare number.
    /// </summary>
    [Fact]
    public void EveryNumericParameter_DeclaresAUnit()
    {
        var descriptor = AnalysisTypeRegistry.ForKind("catchment")!;

        var unitless = descriptor.Parameters
            .Where(p => p.Kind is ParameterKind.Number or ParameterKind.Slider)
            .Where(p => p.Unit == ParameterUnit.None)
            .Select(p => p.Key)
            .ToList();

        Assert.Empty(unitless);
        Assert.Equal(
            ParameterUnit.Percent,
            Assert.Single(descriptor.Parameters, p => p.Key == "MinimumBasinArea").Unit);
        Assert.Equal(
            ParameterUnit.Slope,
            Assert.Single(descriptor.Parameters, p => p.Key == "FlatSlopeThreshold").Unit);
    }

    /// <summary>
    /// A catchment index is a name, not a magnitude, so the card must not offer a ramp: stretched across
    /// the basins it would give the picture a "near values, near colours" relationship the data does not
    /// have, and a legend naming its ends would be meaningless.
    /// </summary>
    [Fact]
    public void Card_OffersNoColourRamp()
    {
        var descriptor = AnalysisTypeRegistry.ForKind("catchment")!;

        Assert.DoesNotContain(descriptor.Parameters, p => p.Kind == ParameterKind.ColorRamp);
    }

    /// <summary>
    /// Both drawn outputs have to make the analysis count as producing output, or an edit to the layer
    /// template will not invalidate its cached curves and the boundaries keep the old layer's colour.
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void ProducesGeneratedOutput_TracksBothDrawnOutputs(
        bool boundaries, bool flowPaths, bool expected)
    {
        var analysis = new CatchmentAnalysisDefinition
        {
            ShowBoundaries = boundaries,
            ShowFlowPaths = flowPaths
        };

        Assert.Equal(expected, TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(analysis));
    }

    [Fact]
    public void SupportsTerrainPreview_IncludesCatchments()
    {
        Assert.True(TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(new CatchmentAnalysisDefinition()));
    }

    /// <summary>
    /// The colouring's whole job is that adjacent catchments look different. A palette that repeated
    /// within a handful of indices would put two identical greens side by side on the common case.
    /// </summary>
    [Fact]
    public void CategoricalPalette_GivesConsecutiveIndicesDistinctColours()
    {
        for (int index = 1; index < CategoricalPalette.Count; index++)
            Assert.NotEqual(CategoricalPalette.ColorAt(index - 1), CategoricalPalette.ColorAt(index));

        // Unclassified is neutral grey and must not collide with a real category.
        for (int index = 0; index < CategoricalPalette.Count; index++)
            Assert.NotEqual(CategoricalPalette.ColorAt(-1), CategoricalPalette.ColorAt(index));
    }

    /// <summary>Both catchment roles resolve to a real layer, so nothing can bake onto the current layer.</summary>
    [Theory]
    [InlineData(LayerRole.Catchments)]
    [InlineData(LayerRole.CatchmentFlowPaths)]
    public void CatchmentRoles_ResolveToALayer(LayerRole role)
    {
        Assert.False(string.IsNullOrWhiteSpace(LayerRoleTable.Default.Path(role)));
    }
}
