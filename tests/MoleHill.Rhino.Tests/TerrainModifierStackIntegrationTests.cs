using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainModifierStackIntegrationTests
{
    [RhinoNativeFact]
    public void Build_RetainingWallBeforeGradePad_ProducesWallOutputAndCachesStackStages()
    {
        StackFixture fixture = CreateFixture(wallBeforePad: true);
        var cache = new TerrainRuntimeCache();

        TerrainBuildResult result = new TerrainBuildService().Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        Assert.NotNull(result.PrimaryMesh);
        Assert.Single(result.AuxiliaryObjects, static output => output.Kind == GeneratedObjectKind.RetainingWall);
        Assert.Contains(cache.StageEntries.Keys, key => IsStageKey(key, 1, fixture.Wall.Id, nameof(RetainingWallModifierDefinition)));
        Assert.Contains(cache.StageEntries.Keys, key => IsStageKey(key, 2, fixture.Pad.Id, nameof(GradePadModifierDefinition)));
        Assert.DoesNotContain(result.Timings, static timing => timing.Detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true);
    }

    [RhinoNativeFact]
    public void Build_ReorderedWallAndGradePad_PrunesOldStackStagesAndRebuildsAtNewIndexes()
    {
        StackFixture fixture = CreateFixture(wallBeforePad: true);
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        fixture.Terrain.Modifiers[1] = fixture.Pad;
        fixture.Terrain.Modifiers[2] = fixture.Wall;

        TerrainBuildResult reordered = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        Assert.NotNull(reordered.PrimaryMesh);
        Assert.Single(reordered.AuxiliaryObjects, static output => output.Kind == GeneratedObjectKind.RetainingWall);
        Assert.DoesNotContain(cache.StageEntries.Keys, key => IsStageKey(key, 1, fixture.Wall.Id, nameof(RetainingWallModifierDefinition)));
        Assert.DoesNotContain(cache.StageEntries.Keys, key => IsStageKey(key, 2, fixture.Pad.Id, nameof(GradePadModifierDefinition)));
        Assert.Contains(cache.StageEntries.Keys, key => IsStageKey(key, 1, fixture.Pad.Id, nameof(GradePadModifierDefinition)));
        Assert.Contains(cache.StageEntries.Keys, key => IsStageKey(key, 2, fixture.Wall.Id, nameof(RetainingWallModifierDefinition)));
    }

    [RhinoNativeFact]
    public void Build_DisabledRetainingWall_PrunesWallStageAndDropsWallOutput()
    {
        StackFixture fixture = CreateFixture(wallBeforePad: true);
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        fixture.Wall.IsEnabled = false;

        TerrainBuildResult withoutWall = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        Assert.NotNull(withoutWall.PrimaryMesh);
        Assert.DoesNotContain(withoutWall.AuxiliaryObjects, static output => output.Kind == GeneratedObjectKind.RetainingWall);
        Assert.DoesNotContain(cache.StageEntries.Keys, key => key.Contains(nameof(RetainingWallModifierDefinition), StringComparison.Ordinal));
    }

    [RhinoNativeFact]
    public void Build_CachedRetainingWallStage_RestoresWallOutputFromActiveStackStage()
    {
        StackFixture fixture = CreateFixture(wallBeforePad: true);
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        TerrainBuildResult cached = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        Assert.Single(cached.AuxiliaryObjects, static output => output.Kind == GeneratedObjectKind.RetainingWall);
        Assert.Contains(
            cached.Timings,
            static timing => timing.Stage == "Retaining Wall" &&
                             timing.Detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true);
    }

    [RhinoNativeFact]
    public void Build_RetainingWall_InsertsLocallyBeforeConstrainedRebuild()
    {
        StackFixture fixture = CreateFixture(wallBeforePad: true);
        fixture.Pad.IsEnabled = false;

        TerrainBuildResult result = new TerrainBuildService().Build(
            fixture.Snapshot,
            new TerrainRuntimeCache(),
            TerrainBuildMode.Preview);

        Assert.NotNull(result.PrimaryMesh);
        Assert.NotNull(result.BaseMesh);
        Assert.True(result.PrimaryMesh.Faces.Count >= result.BaseMesh.Faces.Count);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Contains("topology insertion inserted wall breaklines", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Timings, static timing => timing.Stage == "Retaining Wall Topology Insert");
        Assert.DoesNotContain(result.Timings, static timing => timing.Stage == "Retaining Wall Remesh");
    }

    /// <summary>
    /// The wall stage's fingerprint includes the upstream mesh, so moving the terrain's source points
    /// must miss the stage — that is correct, the rails have to be re-inserted into a different mesh.
    /// Rail *planning* reads only the wall curves and tolerances, so it must survive that miss. Before
    /// the plan cache (2026-09-19) it did not, and every upstream Z edit re-planned unchanged walls.
    /// </summary>
    [RhinoNativeFact]
    public void Build_UpstreamZEdit_MissesTheWallStageButReusesTheRailPlan()
    {
        StackFixture fixture = CreateFixture(wallBeforePad: true);
        fixture.Pad.IsEnabled = false;
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        // An upstream Z-only edit: the same points at the same XY, lifted. The wall curves are untouched.
        RaisePointGrid(fixture.Snapshot, fixture.Terrain, 0.75);

        TerrainBuildResult afterEdit = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        Assert.NotNull(afterEdit.PrimaryMesh);
        Assert.Single(afterEdit.AuxiliaryObjects, static output => output.Kind == GeneratedObjectKind.RetainingWall);

        Assert.DoesNotContain(
            afterEdit.Timings,
            static timing => timing.Stage == "Retaining Wall" &&
                             timing.Detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            afterEdit.Timings,
            static timing => timing.Stage == "Retaining Wall Plan" &&
                             timing.Detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>The plan cache must not outlive a change to the wall curves themselves.</summary>
    [RhinoNativeFact]
    public void Build_WallCurveEdit_ReplansRatherThanServingTheCachedPlan()
    {
        StackFixture fixture = CreateFixture(wallBeforePad: true);
        fixture.Pad.IsEnabled = false;
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        // Max Wall Width is a planner input: it changes pairing and acceptance, not just thickness.
        fixture.Wall.MaxWallWidth = 2.5;

        TerrainBuildResult afterEdit = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        Assert.DoesNotContain(
            afterEdit.Timings,
            static timing => timing.Stage == "Retaining Wall Plan" &&
                             timing.Detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void RaisePointGrid(TerrainBuildSnapshot snapshot, TerrainDefinition terrain, double dz)
    {
        var triangulate = terrain.Modifiers.OfType<TriangulateModifierDefinition>().Single();
        List<ResolvedSourceObject> objects = snapshot.SourceObjects[triangulate.Points];
        var raised = new List<ResolvedSourceObject>(objects.Count);
        foreach (ResolvedSourceObject source in objects)
        {
            var point = (Point)source.Geometry;
            var lifted = new Point(new Point3d(point.Location.X, point.Location.Y, point.Location.Z + dz));
            BoundingBox bbox = lifted.GetBoundingBox(true);
            raised.Add(new ResolvedSourceObject
            {
                ObjectId = source.ObjectId,
                Geometry = lifted,
                LocalBoundingBox = bbox,
                WorldBoundingBox = bbox,
                GeometryDataCrc = lifted.DataCRC(0)
            });
        }

        snapshot.SourceObjects[triangulate.Points] = raised;
        snapshot.SourceFingerprints[triangulate.Points] = 102;
    }

    private static bool IsStageKey(string key, int modifierIndex, Guid modifierId, string modifierType)
    {
        return key.StartsWith($"final:modifier:{modifierIndex}:{modifierType}:", StringComparison.Ordinal) &&
               key.EndsWith(modifierId.ToString("N"), StringComparison.Ordinal);
    }

    private static StackFixture CreateFixture(bool wallBeforePad)
    {
        var triangulate = new TriangulateModifierDefinition
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PeelBoundaryTriangles = false,
            Tolerance = 0.01
        };
        var wall = new RetainingWallModifierDefinition
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            MaxWallWidth = 1.0
        };
        var pad = new GradePadModifierDefinition
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SlopeAngle = 25.0,
            MaxDistance = 3.0
        };

        var terrain = new TerrainDefinition
        {
            TerrainId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Name = "Stack Test",
            GlobalTolerance = 0.01,
            Modifiers = wallBeforePad
                ? new List<ModifierDefinition> { triangulate, wall, pad }
                : new List<ModifierDefinition> { triangulate, pad, wall }
        };

        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };

        AddPointGrid(snapshot, triangulate.Points);
        AddCurves(
            snapshot,
            wall.WallCurves,
            CreatePolylineCurve(new Point3d(-0.35, -3.0, 0.0), new Point3d(-0.35, 3.0, 0.0)),
            CreatePolylineCurve(new Point3d(0.35, -3.0, 1.2), new Point3d(0.35, 3.0, 1.2)));
        AddCurves(
            snapshot,
            pad.Boundaries,
            CreateClosedPolylineCurve(
                new Point3d(-1.6, -1.2, 0.8),
                new Point3d(1.6, -1.2, 0.8),
                new Point3d(1.6, 1.2, 0.8),
                new Point3d(-1.6, 1.2, 0.8)));

        return new StackFixture(terrain, snapshot, wall, pad);
    }

    private static void AddPointGrid(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        var objects = new List<ResolvedSourceObject>();
        for (int x = -4; x <= 4; x += 2)
        {
            for (int y = -4; y <= 4; y += 2)
            {
                Guid id = Guid.NewGuid();
                sourceSet.ObjectIds.Add(id);
                objects.Add(CreateSourceObject(id, new Point(new Point3d(x, y, 0.08 * x))));
            }
        }

        snapshot.SourceObjects[sourceSet] = objects;
        snapshot.SourceFingerprints[sourceSet] = 101;
    }

    private static void AddCurves(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet, params Curve[] curves)
    {
        var objects = new List<ResolvedSourceObject>();
        ulong fingerprint = 211;
        foreach (Curve curve in curves)
        {
            Guid id = Guid.NewGuid();
            sourceSet.ObjectIds.Add(id);
            objects.Add(CreateSourceObject(id, curve));
            fingerprint = unchecked((fingerprint * 397) ^ curve.DataCRC(0));
        }

        snapshot.SourceObjects[sourceSet] = objects;
        snapshot.SourceFingerprints[sourceSet] = fingerprint;
    }

    private static ResolvedSourceObject CreateSourceObject(Guid id, GeometryBase geometry)
    {
        BoundingBox bbox = geometry.GetBoundingBox(true);
        return new ResolvedSourceObject
        {
            ObjectId = id,
            Geometry = geometry,
            LocalBoundingBox = bbox,
            WorldBoundingBox = bbox,
            GeometryDataCrc = geometry.DataCRC(0)
        };
    }

    private static PolylineCurve CreatePolylineCurve(params Point3d[] points)
    {
        return new PolylineCurve(points);
    }

    private static PolylineCurve CreateClosedPolylineCurve(params Point3d[] points)
    {
        var closed = new Point3d[points.Length + 1];
        Array.Copy(points, closed, points.Length);
        closed[^1] = points[0];
        return new PolylineCurve(closed);
    }

    private sealed record StackFixture(
        TerrainDefinition Terrain,
        TerrainBuildSnapshot Snapshot,
        RetainingWallModifierDefinition Wall,
        GradePadModifierDefinition Pad);
}
