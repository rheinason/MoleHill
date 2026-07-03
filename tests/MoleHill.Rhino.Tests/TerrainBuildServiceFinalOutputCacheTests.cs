using MoleHill.Core.Scattering;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainBuildServiceFinalOutputCacheTests
{
    [RhinoNativeFact]
    public void Build_RepeatedFinalBuild_RestoresGeneratedOutputStagesFromCache()
    {
        FinalOutputFixture fixture = CreateFixture();
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        TerrainBuildResult first = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        TerrainBuildResult second = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        Assert.NotNull(first.PrimaryMesh);
        Assert.NotNull(second.PrimaryMesh);
        Assert.Equal(first.MarkerObjects.Count, second.MarkerObjects.Count);
        Assert.Equal(first.ObjectPlacements.Sum(static group => group.Placements.Count), second.ObjectPlacements.Sum(static group => group.Placements.Count));
        Assert.Equal(first.ScatterObjects.Count, second.ScatterObjects.Count);
        Assert.True(second.ScatterObjects.Count > 0);

        AssertCacheHit(second, "Markers");
        AssertCacheHit(second, "Objects");
        AssertCacheHit(second, "Scatter");

        Assert.Equal(
            first.ScatterObjects.Select(static output => output.InstanceTransform.M03).ToArray(),
            second.ScatterObjects.Select(static output => output.InstanceTransform.M03).ToArray());
    }

    [RhinoNativeFact]
    public void Build_ChangedScatterDefinition_InvalidatesOnlyScatterOutputStage()
    {
        FinalOutputFixture fixture = CreateFixture();
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        fixture.Scatter.Count += 1.0;
        TerrainBuildResult changed = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        AssertCacheHit(changed, "Markers");
        AssertCacheHit(changed, "Objects");
        AssertNoCacheHit(changed, "Scatter");
        Assert.Equal((int)fixture.Scatter.Count, changed.ScatterObjects.Count);
    }

    private static void AssertCacheHit(TerrainBuildResult result, string stage)
    {
        Assert.Contains(
            result.Timings,
            timing => timing.Stage == stage &&
                      timing.Detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void AssertNoCacheHit(TerrainBuildResult result, string stage)
    {
        Assert.DoesNotContain(
            result.Timings,
            timing => timing.Stage == stage &&
                      timing.Detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static FinalOutputFixture CreateFixture()
    {
        var triangulate = new TriangulateModifierDefinition
        {
            Id = Guid.Parse("11111111-aaaa-4444-aaaa-111111111111"),
            PeelBoundaryTriangles = false,
            Tolerance = 0.01
        };
        var marker = new ElevationMarkerDefinition
        {
            Id = Guid.Parse("22222222-aaaa-4444-aaaa-222222222222")
        };
        var placedObject = new LowestPointObjectDefinition
        {
            Id = Guid.Parse("33333333-aaaa-4444-aaaa-333333333333")
        };
        var scatter = new ScatterObjectDefinition
        {
            Id = Guid.Parse("44444444-aaaa-4444-aaaa-444444444444"),
            SourceMode = ScatterSourceMode.Region,
            DensityMode = ScatterDensityMode.Count,
            Count = 3,
            RandomSeed = 17,
            Blocks =
            {
                new ScatterBlockEntry
                {
                    BlockDefinitionName = "Tree",
                    Weight = 1.0
                }
            }
        };

        var terrain = new TerrainDefinition
        {
            TerrainId = Guid.Parse("aaaaaaaa-bbbb-4444-aaaa-aaaaaaaaaaaa"),
            Name = "Final Output Cache Test",
            GlobalTolerance = 0.01,
            Modifiers = new List<ModifierDefinition> { triangulate },
            Markers = new List<MarkerDefinition> { marker },
            Objects = new List<TerrainObjectDefinition> { placedObject, scatter }
        };

        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };

        AddPointGrid(snapshot, triangulate.Points);
        AddObjects(snapshot, marker.Sources, 301, CreateSourceObject(Guid.Parse("55555555-aaaa-4444-aaaa-555555555555"), new Point(new Point3d(0.0, 0.0, 0.0))));
        AddObjects(snapshot, placedObject.Sources, 401, CreateSourceObject(Guid.Parse("66666666-aaaa-4444-aaaa-666666666666"), new Point(new Point3d(1.0, 1.0, 0.0))));
        AddObjects(
            snapshot,
            scatter.Boundaries,
            501,
            CreateSourceObject(
                Guid.Parse("77777777-aaaa-4444-aaaa-777777777777"),
                CreateClosedPolylineCurve(
                    new Point3d(-1.0, -1.0, 0.0),
                    new Point3d(1.0, -1.0, 0.0),
                    new Point3d(1.0, 1.0, 0.0),
                    new Point3d(-1.0, 1.0, 0.0))));
        snapshot.BlockDefinitionBounds["Tree"] = new BoundingBox(new Point3d(-0.1, -0.1, 0.0), new Point3d(0.1, 0.1, 1.0));

        return new FinalOutputFixture(snapshot, scatter);
    }

    private static void AddPointGrid(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        var objects = new List<ResolvedSourceObject>();
        ulong fingerprint = 101;
        for (int x = -2; x <= 2; x++)
        {
            for (int y = -2; y <= 2; y++)
            {
                Guid id = Guid.NewGuid();
                sourceSet.ObjectIds.Add(id);
                var point = new Point(new Point3d(x, y, 0.05 * x));
                objects.Add(CreateSourceObject(id, point));
                fingerprint = unchecked((fingerprint * 397) ^ point.DataCRC(0));
            }
        }

        snapshot.SourceObjects[sourceSet] = objects;
        snapshot.SourceFingerprints[sourceSet] = fingerprint;
    }

    private static void AddObjects(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet, ulong fingerprint, params ResolvedSourceObject[] objects)
    {
        sourceSet.ObjectIds.AddRange(objects.Select(static obj => obj.ObjectId));
        snapshot.SourceObjects[sourceSet] = objects.ToList();
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

    private static PolylineCurve CreateClosedPolylineCurve(params Point3d[] points)
    {
        var closed = new Point3d[points.Length + 1];
        Array.Copy(points, closed, points.Length);
        closed[^1] = points[0];
        return new PolylineCurve(closed);
    }

    private sealed record FinalOutputFixture(TerrainBuildSnapshot Snapshot, ScatterObjectDefinition Scatter);
}
