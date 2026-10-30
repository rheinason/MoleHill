using System.Collections;
using System.Reflection;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace MoleHill.Rhino.Tests;

/// <summary>Invoke inside an owned Rhino. Reflection deliberately exercises the loaded RHP,
/// whose custom user data has a real plug-in owner, rather than the source-linked test assembly.</summary>
public static class HostedObjectPlacementTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Assembly _plugin = null!;
    private static object _controller = null!;
    private static object _terrain = null!;
    private static object _definition = null!;
    private static Guid _terrainId;

    public static void SetUp(RhinoDoc doc)
    {
        var plugIn = global::Rhino.PlugIns.PlugIn.Find(new Guid("0c0b9e83-4959-437e-a935-4addf0d3f886"));
        _plugin = plugIn.GetType().Assembly;
        _controller = _plugin.GetType("MoleHill.Rhino.Services.TerrainController")!.GetProperty("Instance", Flags)!.GetValue(null)!;
        doc.ModelUnitSystem = UnitSystem.Meters;
        doc.ModelAbsoluteTolerance = 0.0001;
        using var ground = new Mesh();
        for (int row = 0; row < 2; row++)
            foreach (double x in new[] { -50.0, 0, 50 })
            {
                double y = row == 0 ? -50 : 50;
                ground.Vertices.Add(x, y, GroundZ(x, y));
            }
        ground.Faces.AddFace(0, 1, 4); ground.Faces.AddFace(0, 4, 3);
        ground.Faces.AddFace(1, 2, 5); ground.Faces.AddFace(1, 5, 4);
        ground.Normals.ComputeNormals();
        Guid meshId = doc.Objects.AddMesh(ground);
        _terrain = Call(_controller, "CreateTerrainFromTinMeshId", doc, meshId, "Placement regression")!;
        Set(_terrain, "LiveUpdateEnabled", false);
        _terrainId = (Guid)Get(_terrain, "TerrainId");
        Call(_controller, "AddObjectDefinition", doc, _terrainId, "surface-oriented");
        _definition = ((IList)Get(_terrain, "Objects"))[0]!;
        int layer = doc.Layers.Add("Placement regression cars", System.Drawing.Color.Coral);
        var sources = (IList)Get(Get(_definition, "Sources"), "ObjectIds");
        ((IList)Get(Get(_definition, "Sources"), "LayerPaths")).Add("Placement regression cars");
        using var car = Mesh.CreateFromBox(new BoundingBox(new Point3d(-2, -1, 0), new Point3d(2, 1, 0.8)), 1, 1, 1);
        car.Vertices.UseDoublePrecisionVertices = true;
        int block = doc.InstanceDefinitions.Add("Placement regression car", "", Point3d.Origin,
            new GeometryBase[] { car }, new[] { new ObjectAttributes() });
        Transform tilt = Transform.Rotation(70 * Math.PI / 180, Vector3d.YAxis, Point3d.Origin);
        sources.Add(doc.Objects.AddInstanceObject(block, Transform.Translation(-20, 4, 5) * tilt,
            new ObjectAttributes { Name = "Placement car block", LayerIndex = layer }));
        car.Transform(Transform.Translation(-20, -4, 5) * tilt);
        sources.Add(doc.Objects.AddMesh(car, new ObjectAttributes { Name = "Placement car mesh", LayerIndex = layer }));
        ForceBuild(doc);
        AssertCars(doc, 2);
        Call(_controller, "BakeTerrain", doc, _terrainId);
        Require(((IList)Get(_terrain, "BakedObjectIds")).Count > 0, "Bake created no outputs");
        SelectCars(doc);
        Console.WriteLine("PASS live setup: 70-degree block and loose mesh oriented and baked; exact RHP " + _plugin.ManifestModule.ModuleVersionId);
    }

    public static void AfterMove(RhinoDoc doc)
    {
        ForceBuild(doc);
        AssertCars(doc, 2);
        Console.WriteLine("PASS native Move then same-card orientation");
    }

    /// <summary>Read-only assertion after a native command and its live rebuild have completed.
    /// Do not force a build here: that would conceal asynchronous placement/undo failures.</summary>
    public static void AssertAfterNativeCommand(RhinoDoc doc, int count = 2)
    {
        // Undo replaces the controller's definitions; never assert against the pre-undo instances.
        _terrain = Call(_controller, "GetSelectedTerrain", doc)!;
        _definition = ((IList)Get(_terrain, "Objects"))[0]!;
        AssertCars(doc, count);
        Console.WriteLine("PASS live native command: geometry count, frame, scale and ownership");
    }

    public static void ApiCycles(RhinoDoc doc)
    {
        for (int i = 0; i < 30; i++)
        {
            double direction = Cars(doc).First().Geometry.GetBoundingBox(true).Center.X < 0 ? 40 : -40;
            foreach (var car in Cars(doc).ToArray())
                Require(doc.Objects.Transform(car.Id, Transform.Translation(direction, 0, 0), true) != Guid.Empty, "API move failed");
            ForceBuild(doc);
            AssertCars(doc, 2);
        }
        // A missed corner must not snap an object back to its original pose.
        foreach (var car in Cars(doc).ToArray()) doc.Objects.Transform(car.Id, Transform.Translation(200, 0, 0), true);
        var bounds = Cars(doc).Select(car => car.Geometry.GetBoundingBox(true)).ToArray();
        ForceBuild(doc);
        Require((int)Get(_definition, "LastSkippedObjectCount") == 2, "Footprint misses were not reported");
        int index = 0;
        foreach (var car in Cars(doc)) Require(car.Geometry.GetBoundingBox(true).Min.DistanceTo(bounds[index++].Min) < 1e-8, "Failed placement moved the car");
        foreach (var car in Cars(doc).ToArray()) doc.Objects.Transform(car.Id, Transform.Translation(-200, 0, 0), true);
        ForceBuild(doc);
        AssertCars(doc, 2);
        Console.WriteLine("PASS 30 API move/slope cycles and footprint-miss recovery");
    }

    public static void SaveReopen(RhinoDoc doc, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "placement-roundtrip.3dm");
        Require(doc.Write3dmFile(path, new FileWriteOptions()), "Save failed");
        using var reopened = RhinoDoc.OpenHeadless(path);
        var terrains = (IList)Call(_controller, "GetTerrains", reopened)!;
        var originalTerrain = _terrain;
        var originalDefinition = _definition;
        try
        {
            _terrain = terrains[0]!;
            _definition = ((IList)Get(_terrain, "Objects"))[0]!;
            ForceBuild(reopened);
            AssertCars(reopened, 2);
        }
        finally { _terrain = originalTerrain; _definition = originalDefinition; }
        Console.WriteLine("PASS native 3dm save/reopen: base and live history retained");
    }

    public static void ReleaseCopyConflict(RhinoDoc doc)
    {
        var before = Cars(doc).Select(car => car.Geometry.GetBoundingBox(true)).ToArray();
        Call(_controller, "ReleaseObjects", doc, _terrainId, Get(_definition, "Id"), false);
        ForceBuild(doc);
        Require(((IList)Get(_definition, "PlacementStates")).Count == 0, "Release retained live states");
        Require((int)Get(_definition, "LastPlacedObjectCount") == 0, "Watched layer recaptured released objects");
        int i = 0;
        foreach (var car in Cars(doc)) Require(car.Geometry.GetBoundingBox(true).Min.DistanceTo(before[i++].Min) < 1e-8, "Release moved a car");
        ((IList)Get(_definition, "ReleasedObjectIds")).Clear();
        foreach (var car in Cars(doc).ToArray())
        {
            Guid copy = doc.Objects.Transform(car.Id, Transform.Translation(0, 8, 0), false);
            Require(copy != Guid.Empty, "Copy failed");
        }
        ForceBuild(doc);
        AssertCars(doc, 4);
        ForceBuild(doc);
        AssertCars(doc, 4);
        Call(_controller, "AddObjectDefinition", doc, _terrainId, "surface-oriented");
        var definitions = (IList)Get(_terrain, "Objects");
        var conflict = definitions[0]!;
        ((IList)Get(Get(conflict, "Sources"), "LayerPaths")).Add("Placement regression cars");
        before = Cars(doc).Select(car => car.Geometry.GetBoundingBox(true)).ToArray();
        ForceBuild(doc);
        Require((int)Get(_definition, "LastSkippedObjectCount") == 4, "Conflicting ownership was not rejected");
        i = 0;
        foreach (var car in Cars(doc)) Require(car.Geometry.GetBoundingBox(true).Min.DistanceTo(before[i++].Min) < 1e-8, "Conflict transformed a car");
        definitions.Remove(conflict);
        ForceBuild(doc);
        AssertCars(doc, 4);
        Console.WriteLine("PASS release on watched layer, reassignment, copied block/mesh, and conflicting ownership");
    }

    private static void ForceBuild(RhinoDoc doc)
    {
        var mode = Enum.Parse(_plugin.GetType("MoleHill.Rhino.Services.TerrainBuildMode")!, "Final");
        Require((bool)Call(_controller, "BuildTerrainSynchronously", doc, Call(_controller, "GetState", doc)!, _terrain, mode, null)!,
            "Build failed: " + Get(_terrain, "LastBuildMessage"));
    }

    private static void AssertCars(RhinoDoc doc, int count)
    {
        Require(Cars(doc).Count() == count, "Duplicate or missing car geometry");
        Require((int)Get(_definition, "LastPlacedObjectCount") == count, "Placement count: " + Get(_terrain, "LastBuildMessage"));
        foreach (var car in Cars(doc))
        {
            var reference = car.Geometry.UserData.Find(_plugin.GetType("MoleHill.Rhino.Services.ObjectPlacementReference")!);
            Require(reference != null, "Object lost its placement reference");
            var args = new object?[] { null };
            Require((bool)reference!.GetType().GetMethod("TryGetPlane", Flags)!.Invoke(reference, args)!, "Invalid remembered base");
            var plane = (Plane)args[0]!;
            var normal = new Vector3d(plane.Origin.X < 0 ? -1.4 : 0.7, -0.2, 1); normal.Unitize();
            Require(Vector3d.Multiply(plane.Normal, normal) > 1 - 1e-7, "Car has wrong or accumulated tilt");
            Require(Math.Abs(plane.Origin.Z - GroundZ(plane.Origin.X, plane.Origin.Y)) < 0.001, "Base missed the terrain");
            var corners = (Point3d[])Get(reference, "BaseCorners");
            Require(Math.Abs(corners[0].DistanceTo(corners[1]) - 4) < 0.001 &&
                Math.Abs(corners[0].DistanceTo(corners[3]) - 2) < 0.001, "Scale accumulated");
        }
    }

    private static IEnumerable<RhinoObject> Cars(RhinoDoc doc) => doc.Objects.Where(obj => !obj.IsInstanceDefinitionGeometry && obj.Attributes.Name?.StartsWith("Placement car", StringComparison.Ordinal) == true);
    private static void SelectCars(RhinoDoc doc) { doc.Objects.UnselectAll(); foreach (var car in Cars(doc)) doc.Objects.Select(car.Id); }
    private static double GroundZ(double x, double y) => (x < 0 ? 1.4 : -0.7) * x + 0.2 * y;
    private static object Get(object value, string name) => value.GetType().GetProperty(name, Flags)!.GetValue(value)!;
    private static void Set(object value, string name, object data) => value.GetType().GetProperty(name, Flags)!.SetValue(value, data);
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(value, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
