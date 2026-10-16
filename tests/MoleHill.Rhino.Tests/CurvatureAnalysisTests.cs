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
        Assert.Equal(0.0, analysis.FeatureSize);
        Assert.Equal(0.05, analysis.FlatnessTolerance);
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
            FlatnessTolerance = 0.02,
            AutoColorRange = false,
            RangeLow = -0.05,
            RangeHigh = 0.05,
        };

        CurvatureAnalysisDefinition restored = RoundTrip(analysis);

        Assert.Equal(CurvatureDisplay.Curvature, restored.Display);
        Assert.Equal(CurvatureMeasure.Plan, restored.Measure);
        Assert.Equal(12.5, restored.FeatureSize);
        Assert.Equal(0.02, restored.FlatnessTolerance);
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
            FlatnessTolerance = 0.05,
            RangeLow = -0.04,
            RangeHigh = 0.04,
            ColorInterval = 0.01,
        };

        TerrainUnitScaler.Scale(analysis, 1000.0);

        Assert.Equal(5000.0, analysis.FeatureSize, 9);
        Assert.Equal(50.0, analysis.FlatnessTolerance, 9);
        Assert.Equal(-0.00004, analysis.RangeLow, 12);
        Assert.Equal(0.00004, analysis.RangeHigh, 12);
        Assert.Equal(0.00001, analysis.ColorInterval, 12);
    }

    [Fact]
    public void Create_InAMillimetreDocument_SizesAreInMillimetres()
    {
        var analysis = (CurvatureAnalysisDefinition)AnalysisTypeRegistry.Create(
            "curvature", ModelUnitContext.FromUnitSystem(UnitSystem.Millimeters))!;

        Assert.Equal(0.0, analysis.FeatureSize);
        Assert.Equal(50.0, analysis.FlatnessTolerance, 9);
    }

    [Fact]
    public void Scale_AutoSize_StaysZero()
    {
        var analysis = new CurvatureAnalysisDefinition();

        TerrainUnitScaler.Scale(analysis, 1000.0);

        Assert.Equal(0.0, analysis.FeatureSize);
    }

    /// <summary>A 200 m site with an 8 m, 60 m wide mound at its centre.</summary>
    private static IndexedTriMesh Mound()
    {
        const double half = 100.0, spacing = 2.5;
        int cells = (int)Math.Round(2.0 * half / spacing);
        int side = cells + 1;
        var vertices = new double[side * side * 3];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                double x = -half + (i * spacing);
                double y = -half + (j * spacing);
                double u = Math.Max(0.0, 1.0 - (((x * x) + (y * y)) / (30.0 * 30.0)));
                int v = (j * side) + i;
                vertices[v * 3] = x;
                vertices[(v * 3) + 1] = y;
                vertices[(v * 3) + 2] = 8.0 * u * u;
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

    private static LandformClass PeakClass(IndexedTriMesh mesh, double landformSize, double tolerance)
    {
        CurvatureField field = CurvatureAnalyzer.Measure(mesh, landformSize);
        sbyte[] classes = LandformClassifier.Classify(field, tolerance);
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int f = 0; f < field.FaceCount; f++)
        {
            int a = mesh.Faces[f * 3], b = mesh.Faces[(f * 3) + 1], c = mesh.Faces[(f * 3) + 2];
            double cx = (mesh.Vertices[a * 3] + mesh.Vertices[b * 3] + mesh.Vertices[c * 3]) / 3.0;
            double cy = (mesh.Vertices[(a * 3) + 1] + mesh.Vertices[(b * 3) + 1] + mesh.Vertices[(c * 3) + 1]) / 3.0;
            double d = (cx * cx) + (cy * cy);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = f;
            }
        }

        return (LandformClass)classes[best];
    }

    [Fact]
    public void BuildSummary_Auto_ResolvesFromTheExtent_AndTheMoundPeakReadsConvex()
    {
        IndexedTriMesh mound = Mound();
        var analysis = new CurvatureAnalysisDefinition();

        TerrainAnalysisSummary summary = TerrainBuildService.BuildCurvatureSummary(mound, analysis, 40000.0, null);

        Assert.Equal(20.0, summary.LandformSize!.Value, 9);
        Assert.Equal(0.0, analysis.FeatureSize);
        Assert.Equal(LandformClass.Convex, PeakClass(mound, summary.LandformSize.Value, analysis.FlatnessTolerance));
    }

    [Fact]
    public void BuildSummary_TypedSize_OverridesAuto()
    {
        var analysis = new CurvatureAnalysisDefinition { FeatureSize = 5.0 };

        TerrainAnalysisSummary summary = TerrainBuildService.BuildCurvatureSummary(Mound(), analysis, 40000.0, null);

        Assert.Equal(5.0, summary.LandformSize!.Value, 9);
    }

    [Fact]
    public void Threshold_UsesTheResolvedSize_NotTheRawZero()
    {
        double resolved = LandformSize.FromExtent(200.0, 200.0);

        Assert.Equal(0.001, LandformClassifier.CurvatureThreshold(0.05, resolved), 12);
        Assert.Equal(0.0, LandformClassifier.CurvatureThreshold(0.05, new CurvatureAnalysisDefinition().FeatureSize));
    }

    [Fact]
    public void LandformSize_IsCarriedThroughTheRuntimeCacheClone_AndScalesAsALength()
    {
        var curvature = new CurvatureAnalysisDefinition();
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(curvature);
        var summary = new TerrainAnalysisSummary { AnalysisId = curvature.Id, LandformSize = 20.0 };
        terrain.LastAnalysisResults.Add(summary);

        TerrainUnitScaler.Scale(new[] { terrain }, 10.0);

        Assert.Equal(200.0, summary.LandformSize!.Value, 9);
    }

    [Fact]
    public void DescribeResult_Auto_ShowsTheResolvedSize_AndATypedSizeShowsNone()
    {
        AnalysisTypeDescriptor descriptor = AnalysisTypeRegistry.ForKind("curvature")!;
        var format = new ResultFormatter(a => a.ToString("0"), a => a.ToString("0"), l => $"{l:0} m", a => a.ToString("0"));
        var shares = new[] { 0.5, 0.5, 0.0 };
        var summary = new TerrainAnalysisSummary
        {
            LandformMeasuredArea = 100.0, LandformClassShares = shares, LandformSize = 20.0,
        };

        var auto = descriptor.DescribeResult(new CurvatureAnalysisDefinition(), summary, format);
        var typed = descriptor.DescribeResult(new CurvatureAnalysisDefinition { FeatureSize = 20.0 }, summary, format);

        Assert.Contains(auto, row => row.Label == "Landform Size" && row.Value == "Auto (20 m)");
        Assert.DoesNotContain(typed, row => row.Label == "Landform Size");
    }

    /// <summary>
    /// The tolerance replaced an unreleased PlanarRadius. A document that still stores one loads without
    /// error, ignores it, and takes the default tolerance.
    /// </summary>
    [Fact]
    public void Load_LegacyPlanarRadius_IsDropped_AndTheDefaultToleranceApplies()
    {
        var analysis = new CurvatureAnalysisDefinition { FeatureSize = 7.0 };
        var terrain = new TerrainDefinition();
        terrain.Analyses.Add(analysis);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        string withLegacy = Regex.Replace(json, "\"[Ff]latnessTolerance\":\\s*0\\.05", "\"planarRadius\": 40");
        Assert.NotEqual(json, withLegacy);

        var restored = Assert.IsType<CurvatureAnalysisDefinition>(
            Assert.Single(Assert.Single(TerrainSerializer.Deserialize(withLegacy)).Analyses));

        Assert.Equal(7.0, restored.FeatureSize);
        Assert.Equal(0.05, restored.FlatnessTolerance);
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
            LandformClassShares = new[] { 0.25, 0.75, 0.0 },
        };
        terrain.LastAnalysisResults.Add(summary);

        TerrainUnitScaler.Scale(new[] { terrain }, 10.0);

        Assert.Equal(-0.002, summary.DisplayRangeLow!.Value, 12);
        Assert.Equal(0.002, summary.DisplayRangeHigh!.Value, 12);
        Assert.Equal(300.0, summary.LandformMeasuredArea!.Value, 9);
        Assert.Equal(new[] { 0.25, 0.75, 0.0 }, summary.LandformClassShares);
    }

    [Fact]
    public void BuildSummary_Dome_SharesCoverTheMeasuredArea_AndConvexDominates()
    {
        var analysis = new CurvatureAnalysisDefinition { FeatureSize = 3.0, FlatnessTolerance = 0.005 };

        TerrainAnalysisSummary summary = TerrainBuildService.BuildCurvatureSummary(Dome(10.0, 0.5, 50.0), analysis, 400.0, null);

        Assert.Equal(analysis.Id, summary.AnalysisId);
        Assert.Equal(400.0, summary.LandformMeasuredArea!.Value, 6);
        Assert.Equal(1.0, summary.LandformClassShares!.Sum(), 9);
        Assert.True(summary.LandformClassShares![(int)LandformClass.Convex] > 0.95);
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
        shares[(int)LandformClass.Convex] = 0.25;
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
        IReadOnlyList<string> ridge = table.Rows.Single(row => row[2] == "Convex");
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
            new[] { "Convex", "Planar", "Concave", "Not measured" },
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
