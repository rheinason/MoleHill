using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class ObjectPlacementTests
{
    [RhinoNativeFact]
    public void Plant_TiltedCar_PreservesRotationAndOnlyTranslatesVertically()
    {
        using var car = Mesh.CreateFromBox(new BoundingBox(new Point3d(-2, -1, 0), new Point3d(2, 1, 0.8)), 1, 1, 1);
        car.Transform(Transform.Rotation(70 * Math.PI / 180, Vector3d.YAxis, Point3d.Origin));
        using var terrain = Terrain(0.7, 0.2);
        Assert.True(TerrainBuildService.TryBuildObjectPlacement(Snapshot(), new LowestPointObjectDefinition(), Source(Guid.NewGuid(), car),
            new Dictionary<Guid, TerrainObjectPlacementState>(), terrain, out var placement, out var message), message);
        Transform t = placement.AppliedTransform;
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++) Assert.Equal(row == column ? 1 : 0, t[row, column], 12);
        Assert.Equal(0, t.M03, 12);
        Assert.Equal(0, t.M13, 12);
        var lowest = car.Vertices.ToPoint3dArray().OrderBy(p => p.Z).First();
        lowest.Transform(t);
        Assert.InRange(Math.Abs(lowest.Z - (0.7 * lowest.X + 0.2 * lowest.Y)), 0, 1e-5);
    }

    [RhinoNativeFact]
    public void Orient_MoveBetweenSteepSlopesRepeatedly_MatchesFreshPlacement()
    {
        using var car = Mesh.CreateFromBox(new BoundingBox(new Point3d(-2, -1, 0), new Point3d(2, 1, 0.8)), 1, 1, 1);
        car.Transform(Transform.Rotation(70 * Math.PI / 180, Vector3d.YAxis, Point3d.Origin));
        var definition = new SurfaceOrientedObjectDefinition { HeadingDegrees = 17, PlacementScale = 1.1,
            RandomRotationMinDegrees = 12, RandomRotationMaxDegrees = 12, RandomScaleMin = 0.9, RandomScaleMax = 0.9 };
        var state = new TerrainObjectPlacementState { ObjectId = Guid.NewGuid() };
        var states = new Dictionary<Guid, TerrainObjectPlacementState> { [state.ObjectId] = state };
        ObjectPlacementReference? reference = null;
        for (int round = 0; round < 40; round++)
        {
            using var terrain = Terrain(round % 2 == 0 ? 1.4 : -0.7, round % 2 == 0 ? 0.4 : -0.3);
            using var snapshotGeometry = car.DuplicateMesh();
            var source = Source(state.ObjectId, snapshotGeometry, reference);
            Assert.True(TerrainBuildService.TryBuildObjectPlacement(Snapshot(), definition, source, states, terrain,
                out var placement, out var message), message);
            Assert.True(state.GetLastAppliedTransform().TryGetInverse(out var inverse));
            if (reference == null)
            {
                reference = placement.BaselineReference!.CopyReference();
            }
            var delta = placement.AppliedTransform * inverse;
            Assert.True(car.Transform(delta));
            reference.TransformReference(delta);
            Assert.True(reference.TryGetPlane(out var basePlane));
            var expectedNormal = new Vector3d(round % 2 == 0 ? -1.4 : 0.7, round % 2 == 0 ? -0.4 : 0.3, 1);
            expectedNormal.Unitize();
            Assert.True(Vector3d.Multiply(basePlane.Normal, expectedNormal) > 1 - 1e-7);
            state.SetLastAppliedTransform(placement.AppliedTransform);
            var first = car.Vertices.ToPoint3dArray();
            using var repeatedGeometry = car.DuplicateMesh();
            Assert.True(TerrainBuildService.TryBuildObjectPlacement(Snapshot(), definition, Source(state.ObjectId, repeatedGeometry, reference), states,
                terrain, out var repeated, out message), message);
            Assert.True(state.GetLastAppliedTransform().TryGetInverse(out inverse));
            using var repeatedCar = car.DuplicateMesh();
            repeatedCar.Transform(repeated.AppliedTransform * inverse);
            for (int i = 0; i < first.Length; i++) Assert.InRange(first[i].DistanceTo(repeatedCar.Vertices[i]), 0, 2e-5);
            // This is the same conjugation used when Rhino's Move changes the neutral pose.
            var move = Transform.Translation(round % 2 == 0 ? 4 : -4, 0, 0);
            car.Transform(move);
            reference.TransformReference(move);
            move.TryGetInverse(out var inverseMove);
            state.SetLastAppliedTransform(move * state.GetLastAppliedTransform() * inverseMove);
        }
    }

    [RhinoNativeFact]
    public void Orient_FootprintOutsideTerrainOrInvalidHistory_LeavesSourceUntouched()
    {
        using var car = Mesh.CreateFromBox(new BoundingBox(new Point3d(-2, -1, 0), new Point3d(2, 1, 1)), 1, 1, 1);
        uint before = car.DataCRC(0);
        using var terrain = Terrain(0, 0, 1);
        var id = Guid.NewGuid();
        Assert.False(TerrainBuildService.TryBuildObjectPlacement(Snapshot(), new SurfaceOrientedObjectDefinition(), Source(id, car),
            new Dictionary<Guid, TerrainObjectPlacementState>(), terrain, out _, out var diagnostic));
        Assert.Contains("footprint", diagnostic);
        Assert.Equal(before, car.DataCRC(0));
        var invalid = new TerrainObjectPlacementState { ObjectId = id, LastAppliedTransform = new double[2] };
        Assert.False(TerrainBuildService.TryBuildObjectPlacement(Snapshot(), new SurfaceOrientedObjectDefinition(), Source(id, car),
            new Dictionary<Guid, TerrainObjectPlacementState> { [id] = invalid }, terrain, out _, out _));
    }

    [RhinoNativeFact]
    public void Reference_TransformedReferenceCopy_PreservesBaseAndDoesNotChangeOriginal()
    {
        using var car = Mesh.CreateFromBox(new BoundingBox(new Point3d(-2, -1, 0), new Point3d(2, 1, 1)), 1, 1, 1);
        Assert.True(ObjectPlacementGeometry.TryGetReference(Source(Guid.NewGuid(), car), car, Transform.Identity, out var reference));
        var original = reference.BaseCorners.ToArray();
        var copy = reference.CopyReference();
        var move = Transform.Translation(10, 20, 30) * Transform.Scale(Plane.WorldXY, -2, 3, 1);
        copy.TransformReference(move);
        var saved = copy;
        Assert.True(saved.IsValid);
        Assert.Equal(original, reference.BaseCorners);
        for (int i = 0; i < 4; i++)
        {
            var expected = original[i]; expected.Transform(move);
            Assert.InRange(expected.DistanceTo(saved.BaseCorners[i]), 0, 1e-10);
        }
    }

    [Fact]
    public void Serialize_ReleasedObjectsAndControls_RoundTripAndKeepCorruptHistory()
    {
        var id = Guid.NewGuid();
        var definition = new SurfaceOrientedObjectDefinition { HeadingDegrees = 35, PlacementScale = 1.2,
            ReleasedObjectIds = new() { id }, PlacementStates = new() { new() { ObjectId = id, LastAppliedTransform = new double[2] } } };
        string json = TerrainSerializer.Serialize(new[] { new TerrainDefinition { Objects = new() { definition } } });
        var restored = TerrainSerializer.Deserialize(json).Single().Objects.Single();
        Assert.Equal(35, restored.HeadingDegrees);
        Assert.Equal(1.2, restored.PlacementScale);
        Assert.Contains(id, restored.ReleasedObjectIds);
        Assert.Equal(2, restored.PlacementStates.Single().LastAppliedTransform.Length);
    }

    [Fact]
    public void ScaleObject_FixedControlsAndReleasedIds_OnlyOffsetChangesUnits()
    {
        Guid id = Guid.NewGuid();
        var definition = new LowestPointObjectDefinition { HeadingDegrees = 45, PlacementScale = 1.5,
            ZOffset = 0.2, ReleasedObjectIds = new() { id } };
        TerrainUnitScaler.Scale(definition, 1000);
        Assert.Equal(45, definition.HeadingDegrees);
        Assert.Equal(1.5, definition.PlacementScale);
        Assert.Equal(200, definition.ZOffset);
        Assert.Contains(id, definition.ReleasedObjectIds);
    }

    [Fact]
    public void Remap_CopiedTerrain_RemapsReleasedObjectExclusions()
    {
        Guid original = Guid.NewGuid(), copy = Guid.NewGuid();
        var definition = new LowestPointObjectDefinition { ReleasedObjectIds = new() { original } };
        var terrain = new TerrainDefinition { Name = "Copy", Objects = new() { definition } };
        TerrainOwnership.Remap(terrain, "Original", LayerRoleTable.Build(new LayerTemplateDefinition(), "Original"),
            new Dictionary<Guid, Guid> { [original] = copy });
        Assert.Equal(new[] { copy }, definition.ReleasedObjectIds);
    }

    private static TerrainBuildSnapshot Snapshot() => new() { Terrain = new TerrainDefinition(), ModelAbsoluteTolerance = 0.001, ModelUnitSystem = UnitSystem.Meters };
    private static ResolvedSourceObject Source(Guid id, GeometryBase geometry, ObjectPlacementReference? reference = null) => new() { ObjectId = id, Geometry = geometry,
        GeometryDataCrc = geometry.DataCRC(0), WorldBoundingBox = geometry.GetBoundingBox(true), PlacementReference = reference };
    private static Mesh Terrain(double x, double y, double size = 100)
    {
        var mesh = new Mesh();
        foreach (var p in new[] { new Point3d(-size, -size, 0), new Point3d(size, -size, 0), new Point3d(size, size, 0), new Point3d(-size, size, 0) })
            mesh.Vertices.Add(p.X, p.Y, x * p.X + y * p.Y);
        mesh.Faces.AddFace(0, 1, 2); mesh.Faces.AddFace(0, 2, 3);
        mesh.Normals.ComputeNormals();
        return mesh;
    }
}
