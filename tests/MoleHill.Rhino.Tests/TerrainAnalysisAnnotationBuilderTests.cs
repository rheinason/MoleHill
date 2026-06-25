using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainAnalysisAnnotationBuilderTests
{
    [RhinoNativeFact]
    public void BuildCurveSlopeSummary_ProjectsSamplesAlongWorldZ()
    {
        var analysis = new CurveSlopeLabelAnalysisDefinition
        {
            IsEnabled = true,
            Interval = 100.0,
            ValueFormat = "F1"
        };
        var sourceCurve = new LineCurve(
            new Point3d(2.0, 5.0, 200.0),
            new Point3d(4.0, 5.0, 200.0));
        var snapshot = CreateSnapshot(analysis, sourceCurve);

        var build = new TerrainBuildResult();
        var summary = TerrainAnalysisAnnotationBuilder.BuildCurveSlopeSummary(
            snapshot,
            CreateSlopedMesh(),
            analysis,
            build,
            shouldCancel: null);

        Assert.Equal(1, summary.GeneratedOutputCount);
        Assert.Equal(1000.0, summary.SampleMinValue, precision: 6);
        var output = Assert.Single(build.AuxiliaryObjects);
        Assert.Equal("1000.0", output.InstanceUserStrings?[GeneratedBlockCatalog.ValueToken]);
    }

    [RhinoNativeFact]
    public void BuildCurveElevationSummary_ProjectsSamplesAlongWorldZ()
    {
        var analysis = new CurveElevationLabelAnalysisDefinition
        {
            IsEnabled = true,
            Interval = 100.0,
            ValueFormat = "F2"
        };
        var sourceCurve = new LineCurve(
            new Point3d(9.0, 5.0, 200.0),
            new Point3d(9.0, 6.0, 200.0));
        var snapshot = CreateSnapshot(analysis, sourceCurve);

        var build = new TerrainBuildResult();
        var summary = TerrainAnalysisAnnotationBuilder.BuildCurveElevationSummary(
            snapshot,
            CreateSlopedMesh(),
            analysis,
            build,
            shouldCancel: null);

        Assert.Equal(2, summary.GeneratedOutputCount);
        Assert.Equal(90.0, summary.SampleMinValue, precision: 6);
        Assert.All(build.AuxiliaryObjects, output =>
            Assert.Equal("90.00", output.InstanceUserStrings?[GeneratedBlockCatalog.ValueToken]));
    }

    [RhinoNativeFact]
    public void BuildProjectedElevationSummary_ProjectsSamplesAlongWorldZ()
    {
        var analysis = new ProjectedElevationLabelAnalysisDefinition
        {
            IsEnabled = true,
            ValueFormat = "F2"
        };
        var sourcePoint = new Point(new Point3d(9.0, 5.0, 200.0));
        var snapshot = CreateSnapshot(analysis, sourcePoint);

        var build = new TerrainBuildResult();
        var summary = TerrainAnalysisAnnotationBuilder.BuildProjectedElevationSummary(
            snapshot,
            CreateSlopedMesh(),
            analysis,
            build,
            shouldCancel: null);

        Assert.Equal(1, summary.GeneratedOutputCount);
        Assert.Equal(90.0, summary.SampleMinValue, precision: 6);
        var output = Assert.Single(build.AuxiliaryObjects);
        Assert.Equal("90.00", output.InstanceUserStrings?[GeneratedBlockCatalog.ValueToken]);
    }

    [RhinoNativeFact]
    public void BuildPointSlopeSummary_ProjectsSamplesAlongWorldZ()
    {
        var analysis = new PointSlopeLabelAnalysisDefinition
        {
            IsEnabled = true,
            ValueFormat = "F1"
        };
        var sourcePoint = new Point(new Point3d(9.0, 5.0, 200.0));
        var snapshot = CreateSnapshot(analysis, sourcePoint);

        var build = new TerrainBuildResult();
        var summary = TerrainAnalysisAnnotationBuilder.BuildPointSlopeSummary(
            snapshot,
            CreateSlopedMesh(),
            analysis,
            build,
            shouldCancel: null);

        Assert.Equal(1, summary.GeneratedOutputCount);
        Assert.Equal(1000.0, summary.SampleMinValue, precision: 6);
        var output = Assert.Single(build.AuxiliaryObjects);
        Assert.Equal("1000.0", output.InstanceUserStrings?[GeneratedBlockCatalog.ValueToken]);
    }

    private static TerrainBuildSnapshot CreateSnapshot(BlockAttributeAnalysisDefinition analysis, GeometryBase geometry)
    {
        var terrain = new TerrainDefinition
        {
            GlobalTolerance = 0.001,
            Analyses = new List<AnalysisDefinition> { analysis }
        };
        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };

        Guid sourceId = Guid.NewGuid();
        analysis.Sources.ObjectIds.Add(sourceId);
        BoundingBox bounds = geometry.GetBoundingBox(true);
        snapshot.SourceObjects[analysis.Sources] = new List<ResolvedSourceObject>
        {
            new()
            {
                ObjectId = sourceId,
                Geometry = geometry,
                LocalBoundingBox = bounds,
                WorldBoundingBox = bounds
            }
        };
        return snapshot;
    }

    private static Mesh CreateSlopedMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(0.0, 0.0, 0.0);
        mesh.Vertices.Add(10.0, 0.0, 100.0);
        mesh.Vertices.Add(0.0, 10.0, 0.0);
        mesh.Vertices.Add(10.0, 10.0, 100.0);
        mesh.Faces.AddFace(0, 1, 3, 2);
        mesh.Normals.ComputeNormals();
        mesh.Compact();
        return mesh;
    }
}
