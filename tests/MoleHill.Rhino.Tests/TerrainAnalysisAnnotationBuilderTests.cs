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
        var analysis = new CurveSlopeLabelAnnotationDefinition
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
        AssertBlockOrigin(output, 3.0, 5.0, 30.0);
    }

    [RhinoNativeFact]
    public void BuildCurveSlopeSummary_UsesHitFaceNormal_NotSmoothedVertexNormal()
    {
        var analysis = new CurveSlopeLabelAnnotationDefinition
        {
            IsEnabled = true,
            Interval = 100.0,
            Unit = MoleHill.Core.Analysis.SlopeAnalyzer.SlopeUnit.Degrees,
            ValueFormat = "F1"
        };
        var sourceCurve = new LineCurve(
            new Point3d(0.20, 0.20, 10.0),
            new Point3d(0.30, 0.20, 10.0));
        var snapshot = CreateSnapshot(analysis, sourceCurve);

        var build = new TerrainBuildResult();
        var summary = TerrainAnalysisAnnotationBuilder.BuildCurveSlopeSummary(
            snapshot,
            CreateSlopedFaceWithSkewedNeighborMesh(),
            analysis,
            build,
            shouldCancel: null);

        Assert.Equal(1, summary.GeneratedOutputCount);
        Assert.Equal(45.0, summary.SampleMinValue, precision: 3);
        var output = Assert.Single(build.AuxiliaryObjects);
        Assert.Equal("45.0", output.InstanceUserStrings?[GeneratedBlockCatalog.ValueToken]);
    }

    [RhinoNativeFact]
    public void BuildCurveElevationSummary_ProjectsSamplesAlongWorldZ()
    {
        var analysis = new CurveElevationLabelAnnotationDefinition
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
        AssertBlockOrigin(build.AuxiliaryObjects[0], 9.0, 5.0, 90.0);
        AssertBlockOrigin(build.AuxiliaryObjects[1], 9.0, 6.0, 90.0);
    }

    [RhinoNativeFact]
    public void BuildProjectedElevationSummary_ProjectsSamplesAlongWorldZ()
    {
        var analysis = new ProjectedElevationLabelAnnotationDefinition
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
        AssertBlockOrigin(output, 9.0, 5.0, 90.0);
    }

    [RhinoNativeFact]
    public void BuildPointSlopeSummary_ProjectsSamplesAlongWorldZ()
    {
        var analysis = new PointSlopeLabelAnnotationDefinition
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
        AssertBlockOrigin(output, 9.0, 5.0, 90.0);
    }

    [RhinoNativeFact]
    public void BuildSlopeArrowSummary_SamplesGridAndOrientsDownhill()
    {
        var analysis = new SlopeArrowAnnotationDefinition
        {
            IsEnabled = true,
            GridSpacing = 4.0,
            ValueFormat = "F1"
        };
        // The point source is ignored as a boundary (only closed curves clip), so the grid covers the whole mesh.
        var snapshot = CreateSnapshot(analysis, new Point(new Point3d(5.0, 5.0, 0.0)));

        var build = new TerrainBuildResult();
        var summary = TerrainAnalysisAnnotationBuilder.BuildSlopeArrowSummary(
            snapshot,
            CreateSlopedMesh(),
            analysis,
            build,
            shouldCancel: null);

        Assert.True(summary.GeneratedOutputCount > 0);
        Assert.Equal(1000.0, summary.SampleMaxValue, precision: 3);
        Assert.NotEmpty(build.AuxiliaryObjects);
        Assert.All(build.AuxiliaryObjects, output =>
        {
            Assert.Equal(MarkerBlockTemplate.AnnotationSlope, output.MarkerBlockTemplate);
            Assert.Equal("1000.0", output.InstanceUserStrings?[GeneratedBlockCatalog.ValueToken]);
        });
    }

    [RhinoNativeFact]
    public void BuildGradeCalloutSummary_ComputesChordGradeBetweenEndpoints()
    {
        var analysis = new GradeBetweenPointsAnnotationDefinition
        {
            IsEnabled = true,
            ValueFormat = "F1",
            TextHeight = 1.0
        };
        // z = 10x on the test mesh: endpoints project to 20 and 80 over a 6-unit run -> 1000%.
        var sourceLine = new LineCurve(
            new Point3d(2.0, 5.0, 0.0),
            new Point3d(8.0, 5.0, 0.0));
        var snapshot = CreateSnapshot(analysis, sourceLine);

        var build = new TerrainBuildResult();
        var summary = TerrainAnalysisAnnotationBuilder.BuildGradeCalloutSummary(
            snapshot,
            CreateSlopedMesh(),
            analysis,
            build,
            shouldCancel: null);

        Assert.Equal(1, summary.GeneratedOutputCount);
        Assert.Equal(1000.0, summary.SampleMinValue, precision: 3);
        Assert.Equal(3, build.AuxiliaryObjects.Count); // connector + arrowhead + text
        var text = Assert.Single(build.AuxiliaryObjects, output => output.Geometry is TextEntity);
        Assert.Contains("1000.0", ((TextEntity)text.Geometry!).PlainText);
    }

    [RhinoNativeFact]
    public void BuildContourObjects_WithLabels_EmitsTextEntities()
    {
        var withLabels = new ContourAnnotationDefinition
        {
            IsEnabled = true,
            Interval = 20.0,
            StartZ = 0.0,
            ShowLabels = true,
            LabelEveryNth = 1,
            LabelTextHeight = 1.0,
            LabelFormat = "F1"
        };
        var (labelled, _) = TerrainBuildService.BuildContourObjects(CreateSlopedMesh(), withLabels);
        Assert.Contains(labelled, output => output.Geometry is TextEntity);

        var noLabels = new ContourAnnotationDefinition
        {
            IsEnabled = true,
            Interval = 20.0,
            StartZ = 0.0,
            ShowLabels = false
        };
        var (plain, _) = TerrainBuildService.BuildContourObjects(CreateSlopedMesh(), noLabels);
        Assert.DoesNotContain(plain, output => output.Geometry is TextEntity);
    }

    [RhinoNativeFact]
    public void BuildContourObjects_QuadMeshWithUnusedVertex_ContoursWholeSurface()
    {
        // Extraction triangulates the quad (1 face -> 2) and culls the unused vertex (5 -> 4), so the
        // Rhino mesh's own counts describe neither array. Pairing them read past the vertex array and
        // contoured only the first triangle.
        Mesh mesh = CreateSlopedMesh();
        mesh.Vertices.Add(50.0, 50.0, -1000.0);
        var analysis = new ContourAnnotationDefinition
        {
            IsEnabled = true,
            Interval = 20.0,
            StartZ = 0.0,
            ShowLabels = false
        };

        var (objects, _) = TerrainBuildService.BuildContourObjects(mesh, analysis);

        var interiorContours = objects
            .Select(output => output.Geometry)
            .OfType<Curve>()
            .Where(curve => curve.PointAtStart.Z > 1.0 && curve.PointAtStart.Z < 99.0)
            .ToList();
        Assert.NotEmpty(interiorContours);
        Assert.All(interiorContours, curve => Assert.Equal(10.0, curve.GetLength(), precision: 6));
    }

    [RhinoNativeFact]
    public void BuildTerrainSectionSummary_MultipleTerrains_EmitsCutRegion()
    {
        var analysis = new TerrainSectionAnnotationDefinition { IsEnabled = true };
        var cut = new LineCurve(new Point3d(0, 5, 0), new Point3d(10, 5, 0));
        TerrainBuildSnapshot snapshot = CreateSectionSnapshot(analysis, cut, CreateFlatMesh(2.0));
        var build = new TerrainBuildResult();

        TerrainAnalysisSummary summary = TerrainAnalysisAnnotationBuilder.BuildTerrainSectionSummary(
            snapshot, CreateFlatMesh(0.0), analysis, build, shouldCancel: null);

        Assert.Equal(2, summary.SectionTerrainCount);
        Assert.True(summary.SectionCutRegionCount > 0);
        Assert.Equal(0, summary.SectionFillRegionCount);
        Assert.Contains(build.AuxiliaryObjects, output => output.Geometry is Mesh && output.Name.Contains("cut"));
    }

    [RhinoNativeFact]
    public void BuildCrossSectionStationSummary_MultipleTerrains_EmitsCutRegion()
    {
        var analysis = new CrossSectionStationAnnotationDefinition
        {
            IsEnabled = true,
            StationInterval = 5.0,
            CrossSectionWidth = 8.0
        };
        var alignment = new LineCurve(new Point3d(1, 5, 0), new Point3d(9, 5, 0));
        TerrainBuildSnapshot snapshot = CreateSectionSnapshot(analysis, alignment, CreateFlatMesh(2.0));
        var build = new TerrainBuildResult();

        TerrainAnalysisSummary summary = TerrainAnalysisAnnotationBuilder.BuildCrossSectionStationSummary(
            snapshot, CreateFlatMesh(0.0), analysis, build, shouldCancel: null);

        Assert.Equal(2, summary.SectionTerrainCount);
        Assert.True(summary.SectionCutRegionCount > 0);
    }

    [RhinoNativeFact]
    public void BuildLongitudinalSectionSummary_MultipleTerrains_EmitsCutRegion()
    {
        var analysis = new LongitudinalSectionAnnotationDefinition
        {
            IsEnabled = true,
            SampleInterval = 1.0
        };
        var alignment = new LineCurve(new Point3d(1, 5, 0), new Point3d(9, 5, 0));
        TerrainBuildSnapshot snapshot = CreateSectionSnapshot(analysis, alignment, CreateFlatMesh(2.0));
        var build = new TerrainBuildResult();

        TerrainAnalysisSummary summary = TerrainAnalysisAnnotationBuilder.BuildLongitudinalSectionSummary(
            snapshot, CreateFlatMesh(0.0), analysis, build, shouldCancel: null);

        Assert.Equal(2, summary.SectionTerrainCount);
        Assert.True(summary.SectionCutRegionCount > 0);
    }

    private static TerrainBuildSnapshot CreateSnapshot(BlockAttributeAnnotationDefinition analysis, GeometryBase geometry)
    {
        var terrain = new TerrainDefinition
        {
            GlobalTolerance = 0.001,
            Annotations = new List<AnnotationDefinition> { analysis }
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

    private static TerrainBuildSnapshot CreateSectionSnapshot(
        TerrainSectionAnnotationDefinitionBase analysis,
        GeometryBase sourceGeometry,
        Mesh referenceMesh)
    {
        Guid referenceId = Guid.NewGuid();
        var terrain = new TerrainDefinition
        {
            Name = "Proposed",
            GlobalTolerance = 0.001,
            Annotations = new List<AnnotationDefinition> { analysis }
        };
        analysis.ComparisonTerrainIds.Add(referenceId);
        analysis.CutFillReferenceTerrainId = referenceId;
        analysis.HasInsertionPlane = true;
        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };
        Guid sourceId = Guid.NewGuid();
        analysis.Sources.ObjectIds.Add(sourceId);
        BoundingBox bounds = sourceGeometry.GetBoundingBox(true);
        snapshot.SourceObjects[analysis.Sources] = new List<ResolvedSourceObject>
        {
            new()
            {
                ObjectId = sourceId,
                Geometry = sourceGeometry,
                LocalBoundingBox = bounds,
                WorldBoundingBox = bounds
            }
        };
        snapshot.SectionTerrains[referenceId] = new TerrainSectionReferenceSnapshot
        {
            TerrainId = referenceId,
            Name = "Existing",
            ColorArgb = unchecked((int)0xFF808080),
            Mesh = referenceMesh,
            MeshFingerprint = 1
        };
        return snapshot;
    }

    private static void AssertBlockOrigin(GeneratedRhinoObject output, double x, double y, double z)
    {
        Assert.Equal(x, output.InstanceTransform.M03, precision: 6);
        Assert.Equal(y, output.InstanceTransform.M13, precision: 6);
        Assert.Equal(z, output.InstanceTransform.M23, precision: 6);
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

    private static Mesh CreateFlatMesh(double elevation)
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(0.0, 0.0, elevation);
        mesh.Vertices.Add(10.0, 0.0, elevation);
        mesh.Vertices.Add(10.0, 10.0, elevation);
        mesh.Vertices.Add(0.0, 10.0, elevation);
        mesh.Faces.AddFace(0, 1, 2, 3);
        mesh.Normals.ComputeNormals();
        mesh.Compact();
        return mesh;
    }

    private static Mesh CreateSlopedFaceWithSkewedNeighborMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(0.0, 0.0, 0.0);
        mesh.Vertices.Add(1.0, 0.0, 1.0);
        mesh.Vertices.Add(0.0, 1.0, 0.0);
        mesh.Vertices.Add(1.0, 1.0, 100.0);
        mesh.Faces.AddFace(0, 1, 2);
        mesh.Faces.AddFace(1, 3, 2);
        mesh.Normals.ComputeNormals();
        mesh.FaceNormals.ComputeFaceNormals();
        mesh.Compact();
        return mesh;
    }
}
