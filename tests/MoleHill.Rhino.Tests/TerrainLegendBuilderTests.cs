using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The Legend annotation keys whatever colours the terrain, from the range the mesh was coloured with.
/// These pin which colouring it keys, when it refuses to draw, and what it draws.
/// </summary>
public class TerrainLegendBuilderTests
{
    private static TerrainDefinition TerrainWith(params AnalysisDefinition[] analyses)
    {
        var terrain = new TerrainDefinition { ShowAnalysisOutputs = true };
        terrain.Analyses.AddRange(analyses);
        return terrain;
    }

    private static TerrainDisplayState ColouredBy(AnalysisDefinition analysis, AnalysisRange? range) => new()
    {
        ActiveAnalysisId = analysis.Id,
        ActiveAnalysisRange = range
    };

    [Fact]
    public void DescribeUnavailable_NothingColoursTheTerrain_SaysSo()
    {
        Assert.Contains("Nothing colours the terrain", TerrainLegendBuilder.DescribeUnavailable(TerrainWith()));
    }

    [Fact]
    public void DescribeUnavailable_ColoursHidden_SaysSo()
    {
        TerrainDefinition terrain = TerrainWith(new SlopeAnalysisDefinition());
        terrain.ShowAnalysisOutputs = false;

        Assert.Contains("hidden", TerrainLegendBuilder.DescribeUnavailable(terrain));
    }

    [Fact]
    public void DescribeUnavailable_KeysTheFirstEnabledColouring_SoACatchmentAboveASlopeBlocksIt()
    {
        var catchment = new CatchmentAnalysisDefinition();
        var slope = new SlopeAnalysisDefinition();

        Assert.Contains("no scale", TerrainLegendBuilder.DescribeUnavailable(TerrainWith(catchment, slope)));
        Assert.Null(TerrainLegendBuilder.DescribeUnavailable(TerrainWith(slope, catchment)));

        catchment.IsEnabled = false;
        Assert.Null(TerrainLegendBuilder.DescribeUnavailable(TerrainWith(catchment, slope)));
    }

    [Fact]
    public void ResolveContent_BeforeTheTerrainIsColoured_DrawsNothing()
    {
        var slope = new SlopeAnalysisDefinition();
        var state = new TerrainDisplayState { ActiveAnalysisId = null };

        TerrainLegendContent? content = TerrainLegendBuilder.ResolveContent(
            TerrainWith(slope), state, UnitSystem.Meters, new LegendAnnotationDefinition(), out string? reason);

        Assert.Null(content);
        Assert.NotNull(reason);
    }

    [Fact]
    public void ResolveContent_SteppedSlope_KeysTheBandsFromTheColouredRange()
    {
        var slope = new SlopeAnalysisDefinition
        {
            Label = "Slope",
            ColorMode = AnalysisColorMapper.Mode.Stepped,
            ColorInterval = 5
        };
        var range = new AnalysisRange(0, 20, false);

        TerrainLegendContent? content = TerrainLegendBuilder.ResolveContent(
            TerrainWith(slope), ColouredBy(slope, range), UnitSystem.Meters, new LegendAnnotationDefinition(), out _);

        Assert.NotNull(content);
        Assert.Equal(slope.Id, content!.AnalysisId);
        Assert.Equal("Slope", content.Title);
        Assert.Equal(AnalysisLegendStyle.Swatches, content.Legend.Style);
        Assert.Equal(4, content.Legend.Entries.Count);
    }

    [Fact]
    public void ResolveContent_LengthAnalysis_TitleCarriesTheModelUnit_AndAnExplicitTitleWins()
    {
        var elevation = new ElevationAnalysisDefinition { Label = "Elevation" };
        TerrainDefinition terrain = TerrainWith(elevation);
        TerrainDisplayState state = ColouredBy(elevation, new AnalysisRange(10, 30, false));

        TerrainLegendContent? auto = TerrainLegendBuilder.ResolveContent(
            terrain, state, UnitSystem.Meters, new LegendAnnotationDefinition(), out _);
        TerrainLegendContent? named = TerrainLegendBuilder.ResolveContent(
            terrain, state, UnitSystem.Meters, new LegendAnnotationDefinition { Title = "  Ground levels " }, out _);

        Assert.Equal("Elevation (m)", auto!.Title);
        Assert.Equal("Ground levels", named!.Title);
        Assert.Equal(AnalysisLegendStyle.Gradient, auto.Legend.Style);
    }

    [Fact]
    public void ResolveContent_Compliance_KeysOnlyTheColoursItsRulesCanPaint()
    {
        var compliance = new GradientComplianceAnalysisDefinition();
        compliance.Rules.LevelAreaMode = GradientRuleMode.Warn;
        compliance.Rules.RouteMode = GradientRuleMode.Off;

        TerrainLegendContent? content = TerrainLegendBuilder.ResolveContent(
            TerrainWith(compliance), ColouredBy(compliance, null), UnitSystem.Meters, new LegendAnnotationDefinition(), out _);

        Assert.Equal(
            new[] { "Within limit", "Over limit", "Not checked" },
            content!.Legend.Entries.Select(entry => entry.Label));
    }

    [Fact]
    public void Descriptor_EveryRowIsRefreshOnly_BecauseTheBuildNeverDrawsALegend()
    {
        var descriptor = AnnotationTypeRegistry.ForType(typeof(LegendAnnotationDefinition));

        Assert.NotNull(descriptor);
        Assert.All(descriptor!.Parameters, parameter => Assert.True(parameter.RefreshOnly, parameter.Key));
    }

    [RhinoNativeFact]
    public void Build_VerticalSwatches_DrawsOneColouredMeshPerBandOnTheLegendRole_BesideTheTerrain()
    {
        var legend = new LegendAnnotationDefinition();
        var content = new TerrainLegendContent(
            AnalysisLegendBuilder.ForRamp(
                new AnalysisRange(0, 20, false),
                AnalysisColorMapper.Mode.Stepped,
                5,
                ColorRampPresets.ResolveRamp(null).Stops,
                value => value.ToString("0")),
            "Slope",
            Guid.NewGuid());
        var bounds = new BoundingBox(new Point3d(0, 0, 0), new Point3d(100, 50, 5));

        List<GeneratedRhinoObject> objects = TerrainLegendBuilder.Build(
            legend, content, bounds, textHeight: 2.0, LayerRoleTable.Default);

        Assert.All(objects, item =>
        {
            Assert.Equal(LayerRole.Legend, item.Role);
            Assert.Equal(legend.Id, item.AnalysisId);
        });

        var swatches = objects.Select(item => item.Geometry).OfType<Mesh>().ToList();
        Assert.Equal(4, swatches.Count);
        Assert.All(swatches, mesh =>
        {
            Assert.Equal(mesh.Vertices.Count, mesh.VertexColors.Count);
            BoundingBox box = mesh.GetBoundingBox(true);
            Assert.True(box.Min.X > bounds.Max.X, "auto placement sits to the right of the terrain");
            Assert.True(box.Min.Y >= bounds.Min.Y - 1e-6, "and no lower than the terrain's foot");
        });
        Assert.Equal(5, objects.Count(item => item.Geometry is TextEntity)); // title + 4 labels
    }
}
