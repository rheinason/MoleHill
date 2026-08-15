using MoleHill.Rhino.Services;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class ProjectBaseGeoreferenceTests
{
    [Fact]
    public void ConvertLegacyFotmPlane_InvertsPythonWorldToLocalConvention()
    {
        Point3d basePoint = new(1000.0, 2000.0, 0.0);
        Plane legacyFotm = Plane.WorldXY;
        legacyFotm.Origin = new Point3d(-basePoint.Y, basePoint.X, 0.0);
        legacyFotm.XAxis = new Vector3d(0.0, -1.0, 0.0);
        legacyFotm.YAxis = new Vector3d(1.0, 0.0, 0.0);
        legacyFotm.ZAxis = Vector3d.ZAxis;

        Assert.True(ProjectBaseCPlaneService.TryConvertLegacyPlaneToLocalToWorld(
            legacyFotm,
            LegacyProjectBaseConvention.WorldToLocal,
            out Plane migrated,
            out string? error), error);

        Assert.Equal(basePoint.X, migrated.OriginX, 10);
        Assert.Equal(basePoint.Y, migrated.OriginY, 10);
        Assert.Equal(new Vector3d(0.0, 1.0, 0.0), migrated.XAxis);
        Assert.Equal(new Vector3d(-1.0, 0.0, 0.0), migrated.YAxis);
    }

    [Fact]
    public void ConvertLegacyGeorefPlane_PreservesCSharpLocalToWorldConvention()
    {
        Plane legacyGeoref = Plane.WorldXY;
        legacyGeoref.Origin = new Point3d(500.0, 700.0, 0.0);

        Assert.True(ProjectBaseCPlaneService.TryConvertLegacyPlaneToLocalToWorld(
            legacyGeoref,
            LegacyProjectBaseConvention.LocalToWorld,
            out Plane migrated,
            out string? error), error);

        Assert.Equal(500.0, migrated.OriginX, 10);
        Assert.Equal(700.0, migrated.OriginY, 10);
        Assert.Equal(Vector3d.XAxis, migrated.XAxis);
        Assert.Equal(Vector3d.YAxis, migrated.YAxis);
    }

    [Fact]
    public void ValidateProjectBasePlane_RejectsVerticalOffsetAndTilt()
    {
        Plane elevated = Plane.WorldXY;
        elevated.OriginZ = 1.0;
        Assert.False(ProjectBaseCPlaneService.TryValidateProjectBasePlane(elevated, out string? elevatedError));
        Assert.Contains("origin", elevatedError, StringComparison.OrdinalIgnoreCase);

        Plane tilted = Plane.WorldXY;
        tilted.YAxis = new Vector3d(0.0, Math.Sqrt(0.5), Math.Sqrt(0.5));
        tilted.ZAxis = new Vector3d(0.0, -Math.Sqrt(0.5), Math.Sqrt(0.5));
        Assert.False(ProjectBaseCPlaneService.TryValidateProjectBasePlane(tilted, out string? tiltedError));
        Assert.Contains("horizontal", tiltedError, StringComparison.OrdinalIgnoreCase);

        Plane leftHanded = Plane.WorldXY;
        leftHanded.YAxis = -Vector3d.YAxis;
        Assert.False(ProjectBaseCPlaneService.TryValidateProjectBasePlane(leftHanded, out string? handednessError));
        Assert.Contains("right-handed", handednessError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnitizeXAxisDirection_RequiresMoreThanModelToleranceInXy()
    {
        Assert.False(ProjectBaseCPlaneService.TryUnitizeXAxisDirection(
            new Vector3d(0.0005, 0.0, 0.0),
            modelTolerance: 0.001,
            out _));

        Assert.True(ProjectBaseCPlaneService.TryUnitizeXAxisDirection(
            new Vector3d(3.0, 4.0, 0.0),
            modelTolerance: 0.001,
            out Vector3d direction));
        Assert.Equal(0.6, direction.X, 12);
        Assert.Equal(0.8, direction.Y, 12);
    }

    [Fact]
    public void ImportPlanner_UsesCompleteIdDifferenceAndOnlyTransformsModelSpace()
    {
        Guid existing = Guid.NewGuid();
        Guid visibleModel = Guid.NewGuid();
        Guid hiddenOrLockedModel = Guid.NewGuid();
        Guid pageObject = Guid.NewGuid();
        var before = new[]
        {
            new GeoreferenceObjectState(existing, ActiveSpace.ModelSpace)
        };
        var after = new[]
        {
            new GeoreferenceObjectState(existing, ActiveSpace.ModelSpace),
            new GeoreferenceObjectState(visibleModel, ActiveSpace.ModelSpace),
            new GeoreferenceObjectState(hiddenOrLockedModel, ActiveSpace.ModelSpace),
            new GeoreferenceObjectState(pageObject, ActiveSpace.PageSpace)
        };

        Guid[] allNew = GeoreferenceImportPlanner.FindNewObjectIds(before, after);
        Guid[] modelNew = GeoreferenceImportPlanner.FindNewModelObjectIds(before, after);

        Assert.Equal(3, allNew.Length);
        Assert.Equal(new[] { visibleModel, hiddenOrLockedModel }, modelNew);
    }

    [RhinoNativeFact]
    public void MigrateAndClearLegacyFotm_CreatesModernPlaneWithoutDeletingLegacy()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        Plane legacyFotm = Plane.WorldXY;
        legacyFotm.Origin = new Point3d(-2000.0, 1000.0, 0.0);
        legacyFotm.XAxis = new Vector3d(0.0, -1.0, 0.0);
        legacyFotm.YAxis = new Vector3d(1.0, 0.0, 0.0);
        legacyFotm.ZAxis = Vector3d.ZAxis;
        Assert.True(doc.NamedConstructionPlanes.Add(ProjectBaseCPlaneService.LegacyFotmCPlaneName, legacyFotm) >= 0);

        Assert.True(ProjectBaseCPlaneService.TryGetLegacyCandidate(doc, out LegacyProjectBaseCandidate candidate));
        Assert.Equal(LegacyProjectBaseConvention.WorldToLocal, candidate.Convention);
        Assert.True(ProjectBaseCPlaneService.TryMigrateLegacyProjectBase(doc, candidate, out string message), message);
        Assert.True(ProjectBaseCPlaneService.TryGetProjectBasePlane(doc, out Plane modern, out string? error), error);
        Assert.Equal(1000.0, modern.OriginX, 10);
        Assert.Equal(2000.0, modern.OriginY, 10);
        Assert.True(doc.NamedConstructionPlanes.Find(ProjectBaseCPlaneService.LegacyFotmCPlaneName) >= 0);

        Assert.True(ProjectBaseCPlaneService.ClearProjectBasePlane(doc));
        Assert.False(ProjectBaseCPlaneService.HasProjectBasePlane(doc));
        Assert.True(doc.NamedConstructionPlanes.Find(ProjectBaseCPlaneService.LegacyFotmCPlaneName) >= 0);
        Assert.False(ProjectBaseCPlaneService.TryGetLegacyCandidate(doc, out _));
    }

    [RhinoNativeFact]
    public void SaveAndClearModernPlane_DoesNotDeleteUnrelatedLegacyNames()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        Assert.True(doc.NamedConstructionPlanes.Add(
            ProjectBaseCPlaneService.LegacyFotmCPlaneName,
            Plane.WorldXY) >= 0);
        Assert.True(doc.NamedConstructionPlanes.Add(
            ProjectBaseCPlaneService.LegacyProjectBaseCPlaneName,
            Plane.WorldXY) >= 0);

        Plane modern = Plane.WorldXY;
        modern.Origin = new Point3d(500.0, 600.0, 0.0);
        Assert.True(ProjectBaseCPlaneService.SaveProjectBasePlane(doc, modern));
        Assert.True(ProjectBaseCPlaneService.ClearProjectBasePlane(doc));

        Assert.True(doc.NamedConstructionPlanes.Find(ProjectBaseCPlaneService.LegacyFotmCPlaneName) >= 0);
        Assert.True(doc.NamedConstructionPlanes.Find(ProjectBaseCPlaneService.LegacyProjectBaseCPlaneName) >= 0);
    }

    [RhinoNativeFact]
    public void OrientDocument_PreservesElevationAndSavesInverseRoundTrip()
    {
        using RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        Assert.NotEqual(Guid.Empty, doc.Objects.AddPoint(new Point3d(100.0, 200.0, 7.0)));

        Assert.Equal(global::Rhino.Commands.Result.Success,
            ProjectBaseCPlaneService.OrientDocument(
                doc,
                new Point3d(100.0, 200.0, 999.0),
                new Point3d(100.0, 201.0, -500.0),
                out string message));
        Assert.Contains("Created", message);

        PointObject pointObject = Assert.Single(doc.Objects
            .GetObjectList(new ObjectEnumeratorSettings
            {
                ActiveObjects = true,
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = true
            })
            .OfType<PointObject>());
        Point3d local = pointObject.PointGeometry.Location;
        Assert.Equal(0.0, local.X, 10);
        Assert.Equal(0.0, local.Y, 10);
        Assert.Equal(7.0, local.Z, 10);

        Assert.True(ProjectBaseCPlaneService.TryGetTransform(
            toProjectCoordinates: false,
            doc,
            out Transform localToWorld,
            out string? error), error);
        local.Transform(localToWorld);
        Assert.Equal(100.0, local.X, 10);
        Assert.Equal(200.0, local.Y, 10);
        Assert.Equal(7.0, local.Z, 10);
    }
}
