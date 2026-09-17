using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class SimplifyModifierIntegrationTests
{
    [RhinoNativeFact]
    public void Build_ExactTinThenSimplify_ReducesAndRestoresDiagnosticsOnCacheHit()
    {
        var simplify = new SimplifyModifierDefinition { MaximumDeviation = 0.0 };
        CreateFixture(simplify, out TerrainBuildSnapshot snapshot, out TerrainRuntimeCache cache, out TerrainBuildService service);

        TerrainBuildResult first = service.Build(snapshot, cache, TerrainBuildMode.Final);
        TerrainBuildResult second = service.Build(snapshot, cache, TerrainBuildMode.Final);

        Assert.NotNull(first.BaseMesh);
        Assert.NotNull(first.PrimaryMesh);
        Assert.True(first.PrimaryMesh!.Vertices.Count < first.BaseMesh!.Vertices.Count);
        Assert.Contains(first.Diagnostics, line => line.Contains("termination ToleranceSatisfied", StringComparison.Ordinal));
        Assert.Contains(second.Diagnostics, line => line.Contains("termination ToleranceSatisfied", StringComparison.Ordinal));
        Assert.Contains(second.Timings, timing =>
            timing.Stage == "Simplify" &&
            timing.Detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true);
    }

    [RhinoNativeFact]
    public void Build_TargetCountMode_StaysAtOrBelowCapAndReportsAchievedError()
    {
        var simplify = new SimplifyModifierDefinition
        {
            Mode = SimplifyModifierDefinition.TargetVertexCountMode,
            TargetVertexCount = 80
        };
        CreateFixture(simplify, out TerrainBuildSnapshot snapshot, out TerrainRuntimeCache cache, out TerrainBuildService service);

        TerrainBuildResult result = service.Build(snapshot, cache, TerrainBuildMode.Final);

        Assert.NotNull(result.PrimaryMesh);
        Assert.InRange(result.PrimaryMesh!.Vertices.Count, 3, 80);
        Assert.Contains(result.Diagnostics, line =>
            line.Contains("termination TargetCountSatisfied", StringComparison.Ordinal) &&
            line.Contains("measured maximum deviation", StringComparison.Ordinal));
    }

    [RhinoNativeFact]
    public void Build_RetainPercentageMode_UsesFloorRoundedIncomingVertexCap()
    {
        var simplify = new SimplifyModifierDefinition
        {
            Mode = SimplifyModifierDefinition.RetainPercentageMode,
            RetainPercentage = 50.0
        };
        CreateFixture(simplify, out TerrainBuildSnapshot snapshot, out TerrainRuntimeCache cache, out TerrainBuildService service);

        TerrainBuildResult result = service.Build(snapshot, cache, TerrainBuildMode.Final);

        Assert.NotNull(result.BaseMesh);
        Assert.NotNull(result.PrimaryMesh);
        int expectedCap = (int)Math.Floor(result.BaseMesh!.Vertices.Count * 0.5);
        Assert.InRange(result.PrimaryMesh!.Vertices.Count, 3, expectedCap);
        Assert.Contains(result.Diagnostics, line => line.Contains("termination TargetCountSatisfied", StringComparison.Ordinal));
    }

    [RhinoNativeFact]
    public void Build_GradePadThenSimplify_DoesNotPersistTemporaryPadTopologyAsElevationConstraints()
    {
        var triangulate = new TriangulateModifierDefinition { PeelBoundaryTriangles = false };
        var gradePad = new GradePadModifierDefinition
        {
            SlopeAngle = 33.0,
            MaxDistance = 20.0
        };
        var simplify = new SimplifyModifierDefinition { MaximumDeviation = 0.25 };
        var terrain = new TerrainDefinition
        {
            GlobalTolerance = 0.001,
            Modifiers = [triangulate, gradePad, simplify]
        };
        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };

        Mesh sourceMesh = RollingGrid(40, 5.0);
        Guid meshId = Guid.NewGuid();
        triangulate.TinMesh.ObjectIds.Add(meshId);
        AddSource(snapshot, triangulate.TinMesh, meshId, sourceMesh, 12345);

        var padCurve = new PolylineCurve(
        [
            new Point3d(60, 60, 0),
            new Point3d(120, 60, 0),
            new Point3d(120, 120, 0),
            new Point3d(60, 120, 0),
            new Point3d(60, 60, 0)
        ]);
        Guid padId = Guid.NewGuid();
        gradePad.Boundaries.ObjectIds.Add(padId);
        AddSource(snapshot, gradePad.Boundaries, padId, padCurve, 67890);

        TerrainBuildResult result = new TerrainBuildService().Build(
            snapshot,
            new TerrainRuntimeCache(),
            TerrainBuildMode.Final);

        Assert.NotNull(result.PrimaryMesh);
        Assert.Empty(result.PersistentElevationConstraints);
        Assert.NotEmpty(result.PersistentHardConstraints);
        Assert.DoesNotContain(result.Diagnostics, line =>
            line.Contains("persistent elevation constraint conflicts", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not represented by connected edges", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, line =>
            line.Contains("Simplify:", StringComparison.Ordinal) &&
            line.Contains("termination ToleranceSatisfied", StringComparison.Ordinal));
    }

    private static void CreateFixture(
        SimplifyModifierDefinition simplify,
        out TerrainBuildSnapshot snapshot,
        out TerrainRuntimeCache cache,
        out TerrainBuildService service)
    {
        var triangulate = new TriangulateModifierDefinition { PeelBoundaryTriangles = false };
        var terrain = new TerrainDefinition
        {
            GlobalTolerance = 0.001,
            Modifiers = [triangulate, simplify]
        };
        snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };
        var sourceMesh = PlanarGrid(12);
        Guid sourceId = Guid.NewGuid();
        triangulate.TinMesh.ObjectIds.Add(sourceId);
        BoundingBox bounds = sourceMesh.GetBoundingBox(true);
        snapshot.SourceObjects[triangulate.TinMesh] =
        [
            new ResolvedSourceObject
            {
                ObjectId = sourceId,
                Geometry = sourceMesh,
                LocalBoundingBox = bounds,
                WorldBoundingBox = bounds,
                GeometryDataCrc = sourceMesh.DataCRC(0)
            }
        ];
        snapshot.SourceFingerprints[triangulate.TinMesh] = 12345;
        cache = new TerrainRuntimeCache();
        service = new TerrainBuildService();
    }

    private static Mesh PlanarGrid(int size)
    {
        var mesh = new Mesh();
        int row = size + 1;
        for (int y = 0; y <= size; y++)
        for (int x = 0; x <= size; x++)
            mesh.Vertices.Add(x, y, 2.0 + (x * 0.1) + (y * 0.2));
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int a = y * row + x, b = a + 1, d = a + row, c = d + 1;
            mesh.Faces.AddFace(a, b, c);
            mesh.Faces.AddFace(a, c, d);
        }
        return mesh;
    }

    private static Mesh RollingGrid(int size, double spacing)
    {
        var mesh = new Mesh();
        int row = size;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double px = x * spacing;
            double py = y * spacing;
            double z = (Math.Sin(px * 0.05) + Math.Cos(py * 0.05)) * 3.0;
            mesh.Vertices.Add(px, py, z);
        }
        for (int y = 0; y < size - 1; y++)
        for (int x = 0; x < size - 1; x++)
        {
            int a = y * row + x, b = a + 1, d = a + row, c = d + 1;
            mesh.Faces.AddFace(a, b, c);
            mesh.Faces.AddFace(a, c, d);
        }
        return mesh;
    }

    private static void AddSource(
        TerrainBuildSnapshot snapshot,
        SourceReferenceSet sourceSet,
        Guid objectId,
        GeometryBase geometry,
        ulong fingerprint)
    {
        BoundingBox bounds = geometry.GetBoundingBox(true);
        snapshot.SourceObjects[sourceSet] =
        [
            new ResolvedSourceObject
            {
                ObjectId = objectId,
                Geometry = geometry,
                LocalBoundingBox = bounds,
                WorldBoundingBox = bounds,
                GeometryDataCrc = geometry.DataCRC(0)
            }
        ];
        snapshot.SourceFingerprints[sourceSet] = fingerprint;
    }
}
