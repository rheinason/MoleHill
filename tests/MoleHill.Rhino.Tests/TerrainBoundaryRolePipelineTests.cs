using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainBoundaryRolePipelineTests
{
    [RhinoNativeFact]
    public void Build_OuterHideShow_TrimsPreviewAndBaselineWithFixedPrecedence()
    {
        Fixture fixture = CreateFixture();
        AddBoundary(fixture.Snapshot, fixture.Triangulate.OuterBoundaries, 201, Rectangle(-1.5, -1.5, 1.5, 1.5));
        AddBoundary(fixture.Snapshot, fixture.Triangulate.HideBoundaries, 202, Rectangle(-0.75, -0.75, 0.75, 0.75));
        AddBoundary(fixture.Snapshot, fixture.Triangulate.ShowBoundaries, 203, Rectangle(-0.25, -0.25, 0.25, 0.25));

        TerrainBuildResult result = new TerrainBuildService().Build(
            fixture.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Preview);

        Assert.NotNull(result.PrimaryMesh);
        Assert.NotNull(result.BaseMesh);
        Assert.Equal(result.PrimaryMesh.Faces.Count, result.BaseMesh.Faces.Count);
        BoundingBox bounds = result.PrimaryMesh.GetBoundingBox(true);
        Assert.InRange(bounds.Min.X, -1.50001, -1.49999);
        Assert.InRange(bounds.Max.X, 1.49999, 1.50001);
        Assert.Contains(Enumerable.Range(0, result.PrimaryMesh.Faces.Count), faceIndex =>
        {
            MeshFace face = result.PrimaryMesh.Faces[faceIndex];
            Point3d centroid = (Point3d)result.PrimaryMesh.Vertices[face.A] + (Point3d)result.PrimaryMesh.Vertices[face.B] + (Point3d)result.PrimaryMesh.Vertices[face.C];
            centroid /= 3.0;
            return Math.Abs(centroid.X) < 0.25 && Math.Abs(centroid.Y) < 0.25;
        });
    }

    [RhinoNativeFact]
    public void Build_ChangedOuter_InvalidatesBoundaryStageButKeepsTriangulationCache()
    {
        Fixture fixture = CreateFixture();
        AddBoundary(fixture.Snapshot, fixture.Triangulate.OuterBoundaries, 301, Rectangle(-1.5, -1.5, 1.5, 1.5));
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        service.Build(fixture.Snapshot, cache, TerrainBuildMode.Preview);
        fixture.Snapshot.SourceObjects[fixture.Triangulate.OuterBoundaries] =
            new List<ResolvedSourceObject> { Source(Guid.NewGuid(), Rectangle(-1.0, -1.0, 1.0, 1.0)) };
        fixture.Snapshot.SourceFingerprints[fixture.Triangulate.OuterBoundaries] = 302;
        TerrainBuildResult changed = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Preview);

        Assert.Contains(changed.Timings, timing => timing.Stage == "Triangulate" && timing.Detail!.Contains("cache hit"));
        Assert.DoesNotContain(changed.Timings, timing => timing.Stage == "Boundary Roles" && timing.Detail!.Contains("cache hit"));
    }

    [RhinoNativeFact]
    public void Build_DataClip_FiltersInitialPointGridExactly()
    {
        Fixture fixture = CreateFixture();
        AddBoundary(fixture.Snapshot, fixture.Triangulate.DataClipBoundaries, 401, Rectangle(-1.1, -1.1, 1.1, 1.1));

        TerrainBuildResult result = new TerrainBuildService().Build(
            fixture.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Preview);

        Assert.NotNull(result.PrimaryMesh);
        BoundingBox bounds = result.PrimaryMesh.GetBoundingBox(true);
        Assert.InRange(bounds.Min.X, -1.00001, -0.99999);
        Assert.InRange(bounds.Max.X, 0.99999, 1.00001);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.StartsWith("Data Clip kept 9/25 points", StringComparison.Ordinal));
    }

    [RhinoNativeFact]
    public void Build_DisabledTriangulateWithOuter_DoesNotTrimTerrain()
    {
        Fixture fixture = CreateFixture();
        var disabled = new TriangulateModifierDefinition { IsEnabled = false };
        AddBoundary(fixture.Snapshot, disabled.OuterBoundaries, 501, Rectangle(-0.5, -0.5, 0.5, 0.5));
        fixture.Snapshot.Terrain.Modifiers.Insert(0, disabled);

        TerrainBuildResult result = new TerrainBuildService().Build(
            fixture.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Preview);

        Assert.NotNull(result.PrimaryMesh);
        BoundingBox bounds = result.PrimaryMesh.GetBoundingBox(true);
        Assert.InRange(bounds.Min.X, -2.00001, -1.99999);
        Assert.InRange(bounds.Max.X, 1.99999, 2.00001);
    }

    private static Fixture CreateFixture()
    {
        var triangulate = new TriangulateModifierDefinition { PeelBoundaryTriangles = false, Tolerance = 0.01 };
        var terrain = new TerrainDefinition
        {
            GlobalTolerance = 0.01,
            Modifiers = new List<ModifierDefinition> { triangulate }
        };
        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };

        var points = new List<ResolvedSourceObject>();
        for (int x = -2; x <= 2; x++)
        for (int y = -2; y <= 2; y++)
        {
            Guid id = Guid.NewGuid();
            triangulate.Points.ObjectIds.Add(id);
            points.Add(Source(id, new Point(new Point3d(x, y, x + y))));
        }
        snapshot.SourceObjects[triangulate.Points] = points;
        snapshot.SourceFingerprints[triangulate.Points] = 101;
        return new Fixture(triangulate, snapshot);
    }

    private static void AddBoundary(TerrainBuildSnapshot snapshot, SourceReferenceSet set, ulong fingerprint, Curve curve)
    {
        Guid id = Guid.NewGuid();
        set.ObjectIds.Add(id);
        snapshot.SourceObjects[set] = new List<ResolvedSourceObject> { Source(id, curve) };
        snapshot.SourceFingerprints[set] = fingerprint;
    }

    private static PolylineCurve Rectangle(double minX, double minY, double maxX, double maxY) =>
        new(new[]
        {
            new Point3d(minX, minY, 20), new Point3d(maxX, minY, 20),
            new Point3d(maxX, maxY, 20), new Point3d(minX, maxY, 20),
            new Point3d(minX, minY, 20)
        });

    private static ResolvedSourceObject Source(Guid id, GeometryBase geometry)
    {
        BoundingBox bounds = geometry.GetBoundingBox(true);
        return new ResolvedSourceObject
        {
            ObjectId = id,
            Geometry = geometry,
            LocalBoundingBox = bounds,
            WorldBoundingBox = bounds,
            GeometryDataCrc = geometry.DataCRC(0)
        };
    }

    private sealed record Fixture(TriangulateModifierDefinition Triangulate, TerrainBuildSnapshot Snapshot);
}
