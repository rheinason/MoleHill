using MoleHill.Core.Reporting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Tests;

public sealed class TerrainReportTableBuilderTests
{
    [RhinoNativeFact]
    public void Build_ColorOverride_StampsObjectAppearanceSoItBakes()
    {
        TerrainBuildResult build = BuildTable(colorArgb: unchecked((int)0xFFFF0000));

        Assert.NotEmpty(build.AuxiliaryObjects);
        Assert.All(build.AuxiliaryObjects, generated =>
            Assert.Equal(GeneratedAppearanceSource.Object, generated.AppearanceSource));
    }

    [RhinoNativeFact]
    public void Build_NoColorOverride_FollowsTheLayer()
    {
        TerrainBuildResult build = BuildTable(colorArgb: null);

        Assert.NotEmpty(build.AuxiliaryObjects);
        Assert.All(build.AuxiliaryObjects, generated =>
            Assert.Equal(GeneratedAppearanceSource.Layer, generated.AppearanceSource));
    }

    private static TerrainBuildResult BuildTable(int? colorArgb)
    {
        var report = new ReportDocument("Site");
        report.AddTable("Earthworks", new ReportColumn("Analysis"), new ReportColumn("Cut"))
            .AddRow("Earthworks", "12.00");

        var annotation = new ReportTableAnnotationDefinition
        {
            ColorArgb = colorArgb,
            HasInsertionPlane = true
        };

        var build = new TerrainBuildResult();
        TerrainReportTableBuilder.Build(report, annotation, new RhinoMesh(), 1.0, LayerRoleTable.Default, build);
        return build;
    }
}
