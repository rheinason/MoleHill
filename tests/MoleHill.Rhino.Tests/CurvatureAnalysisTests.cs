using System.Reflection;
using System.Text.RegularExpressions;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Reporting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The curvature analysis as document state and as a card: its kind, its saved fields, its units, its
/// summary, its report section and its legend. The measurement itself is pinned in Core's
/// <c>CurvatureAnalyzerTests</c>.
/// </summary>
public class CurvatureAnalysisTests
{
    private static T RoundTrip<T>(T analysis) where T : AnalysisDefinition
    {
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(analysis);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        return Assert.IsType<T>(Assert.Single(Assert.Single(TerrainSerializer.Deserialize(json)).Analyses));
    }

    /// <summary>A paraboloid dome of curvature 1/ρ on a regular grid.</summary>
    private static IndexedTriMesh Dome(double half, double spacing, double rho)
    {
        int cells = (int)Math.Round(2.0 * half / spacing);
        int side = cells + 1;
        var vertices = new double[side * side * 3];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                double x = -half + (i * spacing);
                double y = -half + (j * spacing);
                int v = (j * side) + i;
                vertices[v * 3] = x;
                vertices[(v * 3) + 1] = y;
                vertices[(v * 3) + 2] = -((x * x) + (y * y)) / (2.0 * rho);
            }
        }

        var faces = new List<int>();
        for (int j = 0; j < cells; j++)
        {
            for (int i = 0; i < cells; i++)
            {
                int a = (j * side) + i;
                faces.AddRange(new[] { a, a + 1, a + side + 1, a, a + side + 1, a + side });
            }
        }

        return IndexedTriMesh.FromArrays(vertices, faces.ToArray());
    }

    [Fact]
    public void Registry_KnowsTheCurvatureKind()
    {
        AnalysisTypeDescriptor descriptor = Assert.IsType<CurvatureAnalysisDescriptor>(AnalysisTypeRegistry.ForKind("curvature"));

        Assert.Equal(typeof(CurvatureAnalysisDefinition), descriptor.DefinitionType);
        Assert.IsType<CurvatureAnalysisDefinition>(AnalysisTypeRegistry.Create("curvature"));
        Assert.Same(descriptor, AnalysisTypeRegistry.ForType(typeof(CurvatureAnalysisDefinition)));
    }

    [Fact]
    public void Defaults_ShowLandforms_OnADivergingRampFittedAboutZero()
    {
        var analysis = new CurvatureAnalysisDefinition();

        Assert.Equal(CurvatureDisplay.Landforms, analysis.Display);
        Assert.Equal(CurvatureMeasure.Mean, analysis.Measure);
        Assert.Equal("cool-warm", analysis.PalettePreset);
        Assert.True(analysis.AutoColorRange);
        Assert.Equal(5.0, analysis.FeatureSize);
        Assert.Equal(100.0, analysis.PlanarRadius);
        Assert.Equal(RangeShape.SymmetricAboutZero, TerrainAnalysisPreviewBuilder.GetRangeShape(analysis));
        Assert.True(TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(analysis));
    }

    [Fact]
    public void RoundTrip_PreservesEverySetting()
    {
        var analysis = new CurvatureAnalysisDefinition
        {
            Display = CurvatureDisplay.Curvature,
            Measure = CurvatureMeasure.Plan,
            FeatureSize = 12.5,
            PlanarRadius = 40.0,
            AutoColorRange = false,
            RangeLow = -0.05,
            RangeHigh = 0.05,
        };

        CurvatureAnalysisDefinition restored = RoundTrip(analysis);

        Assert.Equal(CurvatureDisplay.Curvature, restored.Display);
        Assert.Equal(CurvatureMeasure.Plan, restored.Measure);
        Assert.Equal(12.5, restored.FeatureSize);
        Assert.Equal(40.0, restored.PlanarRadius);
        Assert.False(restored.AutoColorRange);
        Assert.Equal(-0.05, restored.RangeLow);
        Assert.Equal(0.05, restored.RangeHigh);
    }

    /// <summary>What is shown is presentation: switching it must not re-run the measurement.</summary>
    [Theory]
    [InlineData(nameof(CurvatureAnalysisDefinition.Display))]
    [InlineData(nameof(CurvatureAnalysisDefinition.Measure))]
    public void DisplaySettings_AreNotBuildInputs(string property)
    {
        PropertyInfo info = typeof(CurvatureAnalysisDefinition).GetProperty(property)!;

        Assert.NotNull(info.GetCustomAttribute<NotBuildInputAttribute>());
    }

    /// <summary>
    /// Sizes are lengths; the colour range is a curvature, 1/length, so it shrinks by the factor lengths grow.
    /// </summary>
    [Fact]
    public void Scale_SizesScaleAsLengths_AndTheRangeAsAnInverseLength()
    {
        var analysis = new CurvatureAnalysisDefinition
        {
            FeatureSize = 5.0,
            PlanarRadius = 100.0,
            RangeLow = -0.04,
            RangeHigh = 0.04,
            ColorInterval = 0.01,
        };

        TerrainUnitScaler.Scale(analysis, 1000.0);

        Assert.Equal(5000.0, analysis.FeatureSize, 9);
        Assert.Equal(100000.0, analysis.PlanarRadius, 9);
        Assert.Equal(-0.00004, analysis.RangeLow, 12);
        Assert.Equal(0.00004, analysis.RangeHigh, 12);
        Assert.Equal(0.00001, analysis.ColorInterval, 12);
    }

    [Fact]
    public void Create_InAMillimetreDocument_SizesAreInMillimetres()
    {
        var analysis = (CurvatureAnalysisDefinition)AnalysisTypeRegistry.Create(
            "curvature", ModelUnitContext.FromUnitSystem(UnitSystem.Millimeters))!;

        Assert.Equal(5000.0, analysis.FeatureSize, 9);
        Assert.Equal(100000.0, analysis.PlanarRadius, 9);
    }

    [Fact]
    public void Scale_Summary_RangeIsInverse_AreaIsSquared_SharesAreUnitFree()
    {
        var curvature = new CurvatureAnalysisDefinition();
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(curvature);
        var summary = new TerrainAnalysisSummary
        {
            AnalysisId = curvature.Id,
            DisplayRangeLow = -0.02,
            DisplayRangeHigh = 0.02,
            LandformMeasuredArea = 3.0,
            LandformClassShares = new[] { 0.25, 0.75, 0, 0, 0, 0, 0, 0 },
        };
        terrain.LastAnalysisResults.Add(summary);

        TerrainUnitScaler.Scale(new[] { terrain }, 10.0);

        Assert.Equal(-0.002, summary.DisplayRangeLow!.Value, 12);
        Assert.Equal(0.002, summary.DisplayRangeHigh!.Value, 12);
        Assert.Equal(300.0, summary.LandformMeasuredArea!.Value, 9);
        Assert.Equal(new[] { 0.25, 0.75, 0, 0, 0, 0, 0, 0 }, summary.LandformClassShares);
    }

    [Fact]
    public void BuildSummary_Dome_SharesCoverTheMeasuredArea_AndPeakDominates()
    {
        var analysis = new CurvatureAnalysisDefinition { FeatureSize = 3.0, PlanarRadius = 200.0 };

        TerrainAnalysisSummary summary = TerrainBuildService.BuildCurvatureSummary(Dome(10.0, 0.5, 50.0), analysis, 400.0, null);

        Assert.Equal(analysis.Id, summary.AnalysisId);
        Assert.Equal(400.0, summary.LandformMeasuredArea!.Value, 6);
        Assert.Equal(1.0, summary.LandformClassShares!.Sum(), 9);
        Assert.True(summary.LandformClassShares![(int)LandformClass.Peak] > 0.95);
    }

    /// <summary>Nothing measurable leaves the landform fields null: "too sparse" must not read as "all planar".</summary>
    [Fact]
    public void BuildSummary_SingleTriangle_LeavesTheLandformFieldsNull()
    {
        var mesh = IndexedTriMesh.FromArrays(new[] { 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0 }, new[] { 0, 1, 2 });

        TerrainAnalysisSummary summary = TerrainBuildService.BuildCurvatureSummary(mesh, new CurvatureAnalysisDefinition(), 0.5, null);

        Assert.Null(summary.LandformMeasuredArea);
        Assert.Null(summary.LandformClassShares);
    }

    [Fact]
    public void Report_ListsEveryClassForEachMeasuredCard_AndSkipsUnmeasuredOnes()
    {
        var terrain = new TerrainDefinition { Name = "Site" };
        var measured = new CurvatureAnalysisDefinition { Label = "Landforms", FeatureSize = 5.0 };
        var unmeasured = new CurvatureAnalysisDefinition { Label = "Detail" };
        terrain.Analyses.Add(measured);
        terrain.Analyses.Add(unmeasured);
        var shares = new double[LandformClassifier.ClassCount];
        shares[(int)LandformClass.Ridge] = 0.25;
        shares[(int)LandformClass.Planar] = 0.75;
        var summaries = new[]
        {
            new TerrainAnalysisSummary { AnalysisId = measured.Id, LandformMeasuredArea = 200.0, LandformClassShares = shares },
            new TerrainAnalysisSummary { AnalysisId = unmeasured.Id },
        };

        ReportDocument document = TerrainReportBuilder.Build(
            terrain,
            Array.Empty<ZoneAnalysisSummary>(),
            summaries,
            ModelUnitContext.FromUnitSystem(UnitSystem.Meters),
            SlopeAnalyzer.SlopeUnit.Percent,
            new DateTime(2026, 10, 15),
            TerrainReportSections.Landforms);
        ReportTable table = Assert.Single(document.Tables);

        Assert.Equal(LandformClassifier.ClassCount, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal("Landforms", row[0]));
        IReadOnlyList<string> ridge = table.Rows.Single(row => row[2] == "Ridge");
        Assert.Equal(CsvWriter.Number(5.0), ridge[1]);
        Assert.Equal(CsvWriter.Number(50.0), ridge[3]);
        Assert.Equal(CsvWriter.Number(25.0), ridge[4]);
    }

    [Fact]
    public void ReportTable_HasALandformsSwitch_OnByDefault()
    {
        Assert.True(new ReportTableAnnotationDefinition().IncludeLandforms);
        Assert.Contains(
            AnnotationTypeRegistry.ForType(typeof(ReportTableAnnotationDefinition))!.Parameters,
            parameter => parameter.Key == "IncludeLandforms");
    }

    [Fact]
    public void Legend_Landforms_KeysEveryClassAndUnmeasuredGround()
    {
        var curvature = new CurvatureAnalysisDefinition();
        var terrain = new TerrainDefinition { ShowAnalysisOutputs = true };
        terrain.Analyses.Add(curvature);

        TerrainLegendContent? content = TerrainLegendBuilder.ResolveContent(
            terrain, new TerrainDisplayState { ActiveAnalysisId = curvature.Id }, UnitSystem.Meters,
            new LegendAnnotationDefinition(), out _);

        Assert.NotNull(content);
        Assert.Equal(AnalysisLegendStyle.Swatches, content!.Legend.Style);
        Assert.Equal(
            new[] { "Peak", "Ridge", "Shoulder", "Planar", "Saddle", "Toe", "Valley", "Hollow", "Not measured" },
            content.Legend.Entries.Select(entry => entry.Label));
    }

    [Fact]
    public void Legend_CurvatureValue_IsARampTitledInInverseModelUnits()
    {
        var curvature = new CurvatureAnalysisDefinition { Display = CurvatureDisplay.Curvature };
        var terrain = new TerrainDefinition { ShowAnalysisOutputs = true };
        terrain.Analyses.Add(curvature);
        var state = new TerrainDisplayState { ActiveAnalysisId = curvature.Id, ActiveAnalysisRange = new AnalysisRange(-0.04, 0.04, true) };

        TerrainLegendContent? content = TerrainLegendBuilder.ResolveContent(
            terrain, state, UnitSystem.Meters, new LegendAnnotationDefinition(), out _);

        Assert.NotNull(content);
        Assert.Equal("Curvature (1/m)", content!.Title);
        Assert.Equal(AnalysisLegendStyle.Gradient, content.Legend.Style);
    }

    [Theory]
    [InlineData(0.0213, "+0.0213")]
    [InlineData(-0.004, "-0.004")]
    [InlineData(0.0, "0")]
    public void FormatCurvature_IsSignedWithThreeSignificantFigures(double value, string expected)
    {
        Assert.Equal(expected, AnalysisFormatting.FormatCurvature(value).Replace(',', '.'));
    }

    /// <summary>The card speaks the shared vocabulary: "feature" is retired, so the size is a landform size.</summary>
    [Fact]
    public void CardText_AvoidsRetiredTerms()
    {
        var retired = new Regex(@"\b(constraints?|features?|pinned|fixity|upstream|TIN boundary)\b", RegexOptions.IgnoreCase);
        AnalysisTypeDescriptor descriptor = AnalysisTypeRegistry.ForKind("curvature")!;

        Assert.DoesNotMatch(retired, descriptor.Subtitle);
        foreach (var parameter in descriptor.Parameters)
        {
            Assert.DoesNotMatch(retired, parameter.Label);
            Assert.DoesNotMatch(retired, parameter.Help ?? string.Empty);
        }
    }

    [Fact]
    public void Descriptor_DisplayAndMeasure_RecolourWithoutARebuild_AndTheRampOnlyShowsForValues()
    {
        AnalysisTypeDescriptor descriptor = AnalysisTypeRegistry.ForKind("curvature")!;
        var landforms = new CurvatureAnalysisDefinition();
        var values = new CurvatureAnalysisDefinition { Display = CurvatureDisplay.Curvature };

        Assert.True(descriptor.Parameters.Single(p => p.Key == "Display").RefreshOnly);
        Assert.True(descriptor.Parameters.Single(p => p.Key == "Measure").RefreshOnly);
        var ramp = descriptor.Parameters.Single(p => p.Kind == ParameterKind.ColorRamp);
        Assert.False(ramp.VisibleWhen!(landforms));
        Assert.True(ramp.VisibleWhen!(values));
    }
}
