using System.Linq;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The document contract for the aspect analysis and for the cut/fill analysis's drawn outputs. Both are
/// new state on a family that already persists, so what is being checked is that the registry recognises
/// the new kind and that the new fields survive a save and reload.
/// </summary>
public class AspectAndDeltaOutputSerializationTests
{
    private static T RoundTrip<T>(T analysis) where T : AnalysisDefinition
    {
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(analysis);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        return Assert.IsType<T>(Assert.Single(Assert.Single(TerrainSerializer.Deserialize(json)).Analyses));
    }

    /// <summary>
    /// Registering the descriptor is the whole of adding a type: the JSON discriminator is read off the
    /// registry, so a kind that is not registered cannot round-trip at all.
    /// </summary>
    [Fact]
    public void Registry_KnowsTheAspectKind()
    {
        AnalysisTypeDescriptor descriptor = Assert.IsType<AspectAnalysisDescriptor>(
            AnalysisTypeRegistry.ForKind("aspect"));

        Assert.Equal(typeof(AspectAnalysisDefinition), descriptor.DefinitionType);
        Assert.IsType<AspectAnalysisDefinition>(AnalysisTypeRegistry.Create("aspect"));
        Assert.Same(descriptor, AnalysisTypeRegistry.ForType(typeof(AspectAnalysisDefinition)));
    }

    [Fact]
    public void RoundTrip_Aspect_PreservesTheFlatThresholdAndTheWheel()
    {
        var analysis = new AspectAnalysisDefinition { FlatSlopeThresholdDegrees = 3.5 };

        AspectAnalysisDefinition restored = RoundTrip(analysis);

        Assert.Equal(3.5, restored.FlatSlopeThresholdDegrees);
        Assert.Equal("aspect-wheel", restored.PalettePreset);
        Assert.Equal(AnalysisColorMapper.Mode.Constant, restored.ColorMode);
    }

    /// <summary>
    /// The wheel has to close on itself, or a linear sample puts a hard seam at due north — the one place
    /// a reader looks first.
    /// </summary>
    [Fact]
    public void AspectWheelPreset_ClosesOnItself()
    {
        ColorRamp wheel = ColorRampPresets.ResolveRamp("aspect-wheel");
        SlopeAnalyzer.ColorStop first = wheel.Stops.First();
        SlopeAnalyzer.ColorStop last = wheel.Stops.Last();

        Assert.Equal(0.0, first.Position);
        Assert.Equal(1.0, last.Position);
        Assert.Equal((first.R, first.G, first.B), (last.R, last.G, last.B));
    }

    /// <summary>Aspect has no adjustable range: it is the compass, and the card hides the bounds.</summary>
    [Fact]
    public void Aspect_MapsAFullTurnAndDoesNotAutoFit()
    {
        var analysis = new AspectAnalysisDefinition();

        Assert.Equal(RangeShape.Cyclic, TerrainAnalysisPreviewBuilder.GetRangeShape(analysis));
        Assert.False(analysis.AutoColorRange);
        Assert.Equal(0.0, analysis.RangeLow);
        Assert.Equal(AnalysisRange.FullTurnDegrees, analysis.RangeHigh);
    }

    [Fact]
    public void Aspect_ColoursTheTerrainButDrawsNothing()
    {
        var analysis = new AspectAnalysisDefinition();

        Assert.True(TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(analysis));
        Assert.False(TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(analysis));
    }

    [Fact]
    public void RoundTrip_CutFillDrawnOutputs_PreservesEverySetting()
    {
        var analysis = new CutFillAnalysisDefinition
        {
            ShowDeltaContours = true,
            DeltaContourInterval = 0.25,
            ShowBalanceLine = true,
            DeltaContourColorArgb = unchecked((int)0xFF112233),
            BalanceLineColorArgb = unchecked((int)0xFF445566)
        };

        CutFillAnalysisDefinition restored = RoundTrip(analysis);

        Assert.True(restored.ShowDeltaContours);
        Assert.Equal(0.25, restored.DeltaContourInterval);
        Assert.True(restored.ShowBalanceLine);
        Assert.Equal(unchecked((int)0xFF112233), restored.DeltaContourColorArgb);
        Assert.Equal(unchecked((int)0xFF445566), restored.BalanceLineColorArgb);
        Assert.True(restored.DrawsDeltaOutput);
    }

    /// <summary>
    /// Documents written before this existed say nothing about the drawn outputs, and must keep drawing
    /// exactly as they did — which means off.
    /// </summary>
    [Fact]
    public void CutFill_ByDefault_DrawsNothing()
    {
        var analysis = new CutFillAnalysisDefinition();

        Assert.False(analysis.ShowDeltaContours);
        Assert.False(analysis.ShowBalanceLine);
        Assert.False(analysis.DrawsDeltaOutput);
        Assert.False(TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(analysis));
    }

    /// <summary>
    /// With an output on, the layer template joins this analysis's fingerprint — otherwise restyling the
    /// balance-line layer would leave the old curves in place.
    /// </summary>
    [Fact]
    public void CutFill_WithADrawnOutput_CountsAsProducingGeneratedOutput()
    {
        Assert.True(TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(
            new CutFillAnalysisDefinition { ShowBalanceLine = true }));
        Assert.True(TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(
            new CutFillAnalysisDefinition { ShowDeltaContours = true }));
    }

    /// <summary>
    /// The delta lines route by role like every other generated object, and the two roles must resolve to
    /// distinct layers: a depth contour and the balance line mean different things and are restyled apart.
    /// </summary>
    [Fact]
    public void DeltaOutputRoles_ResolveToDistinctLayersUnderAnnotation()
    {
        string contours = LayerRoleTable.Default.Path(LayerRole.CutFillContours);
        string balance = LayerRoleTable.Default.Path(LayerRole.BalanceLine);

        Assert.NotEqual(contours, balance);
        Assert.StartsWith(LayerRoleTable.Default.Path(LayerRole.Annotation), contours);
        Assert.StartsWith(LayerRoleTable.Default.Path(LayerRole.Annotation), balance);
    }
}
