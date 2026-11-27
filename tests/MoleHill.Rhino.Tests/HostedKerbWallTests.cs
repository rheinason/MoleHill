using System.Collections;
using System.Reflection;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Invoke inside an owned Rhino (see <c>docs/rhino-live-testing.md</c>). Drives the loaded RHP by reflection:
/// kerbs with a zero-height cut, a pointed end and a round nose, and walls made from two flat curves with a
/// Wall Height, each in breakline and grade mode, built, baked and checked in the document. Every check
/// prints PASS or throws.
/// </summary>
public static class HostedKerbWallTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const double KerbGap = 0.05;
    private const double KerbHeight = 0.15;
    private static Assembly _plugin = null!;
    private static object _controller = null!;

    private static double Ground(double x, double y) => 0.05 * x + 0.03 * y;

    public static void Run(RhinoDoc doc)
    {
        var plugIn = global::Rhino.PlugIns.PlugIn.Find(new Guid("0c0b9e83-4959-437e-a935-4addf0d3f886"));
        _plugin = plugIn!.GetType().Assembly;
        _controller = _plugin.GetType("MoleHill.Rhino.Services.TerrainController")!.GetProperty("Instance", Flags)!.GetValue(null)!;
        doc.ModelUnitSystem = UnitSystem.Meters;
        doc.ModelAbsoluteTolerance = 0.001;
        Console.WriteLine("RHP " + _plugin.ManifestModule.ModuleVersionId + " at " + _plugin.Location);

        foreach (string mode in new[] { "breaklines", "grade" })
        {
            KerbShapes(doc, mode);
            WallHeight(doc, mode);
        }
    }

    // Kerb cut (two solids), pointed end and round nose (one solid each), drawn on the ground.
    private static void KerbShapes(RhinoDoc doc, string mode)
    {
        var curves = new List<Curve>();
        double CutHeight(double x)
        {
            double d = Math.Abs(x - 10.0);
            return d <= 0.6 ? 0.0 : d >= 1.2 ? KerbHeight : KerbHeight * (d - 0.6) / 0.6;
        }

        var xs = Enumerable.Range(0, 101).Select(i => 5.0 + i * 0.1).ToArray();
        curves.Add(Polyline(xs.Select(x => new Point3d(x, 10, Ground(x, 10)))));
        curves.Add(Polyline(xs.Select(x => new Point3d(x, 10 + KerbGap, Ground(x, 10 + KerbGap) + CutHeight(x)))));

        curves.Add(Polyline(xs.Select(x => new Point3d(x, 20, Ground(x, 20)))));
        curves.Add(Polyline(xs.Select(x =>
        {
            double y = 20 + (x < 14.5 ? KerbGap : KerbGap * (15.0 - x) / 0.5);
            return new Point3d(x, y, Ground(x, y) + KerbHeight);
        })));

        const double r = 0.075;
        var toe = xs.Where(x => x <= 14.0).Select(x => new Point3d(x, 30, Ground(x, 30))).ToList();
        var top = xs.Where(x => x <= 14.0).Select(x => new Point3d(x, 30 + 2 * r, Ground(x, 30 + 2 * r) + KerbHeight)).ToList();
        for (int k = 1; k <= 8; k++)
        {
            double a = k / 8.0 * Math.PI / 2.0;
            var t = new Point3d(14.0 + r * Math.Sin(a), 30 + r - r * Math.Cos(a), 0);
            var u = new Point3d(14.0 + r * Math.Sin(a), 30 + r + r * Math.Cos(a), 0);
            toe.Add(new Point3d(t.X, t.Y, Ground(t.X, t.Y)));
            top.Add(new Point3d(u.X, u.Y, Ground(u.X, u.Y) + KerbHeight));
        }
        curves.Add(Polyline(toe));
        curves.Add(Polyline(top));

        var (terrain, doc2) = BuildWallTerrain(doc, $"Kerbs {mode}", curves, mode, maxWallWidth: 0.2, wallHeight: 0.0);
        List<Brep> solids = WallSolids(doc2, terrain);
        Require(solids.Count == 4, $"{mode}: expected 4 kerb solids (2 either side of the cut, pointed, round), got {solids.Count}. {BuildMessage(terrain)}");
        Require(solids.All(b => b.IsSolid && b.IsValid), $"{mode}: a kerb solid is open or invalid");
        Mesh mesh = TerrainMesh(doc2, terrain);
        AssertHoleFree(mesh, mode);
        AssertRailOnMesh(mesh, curves[0], 0.002, $"{mode} cut toe");
        AssertRailOnMesh(mesh, curves[1], 0.002, $"{mode} cut top");
        AssertRailOnMesh(mesh, curves[2], 0.002, $"{mode} pointed toe");
        AssertRailOnMesh(mesh, curves[4], 0.002, $"{mode} round toe");
        Console.WriteLine($"PASS kerbs/{mode}: {solids.Count} solids, terrain hole-free, rails on the terrain");
    }

    // Two straight curves sketched flat at z = 0 over ground rising across them: a 0.8 m garden wall.
    private static void WallHeight(RhinoDoc doc, string mode)
    {
        var curves = new List<Curve>
        {
            Polyline(new[] { new Point3d(5, 10, 0), new Point3d(15, 10, 0) }),
            Polyline(new[] { new Point3d(5, 10.2, 0), new Point3d(15, 10.2, 0) }),
        };
        var (terrain, doc2) = BuildWallTerrain(doc, $"Wall height {mode}", curves, mode, maxWallWidth: 0.4, wallHeight: 0.8);
        List<Brep> solids = WallSolids(doc2, terrain);
        Brep wall = Single(solids, $"{mode}: expected one wall solid from two flat curves. {BuildMessage(terrain)}");
        Require(wall.IsSolid, $"{mode}: height wall is not solid");

        Mesh mesh = TerrainMesh(doc2, terrain);
        AssertHoleFree(mesh, mode + " height");
        // The rail over the higher ground (y = 10.2, ground rises with y) is the top, 0.8 above it.
        int toeOnGround = 0, topLifted = 0;
        foreach (Point3d v in mesh.Vertices.Select(p => (Point3d)p))
        {
            if (v.X < 5.5 || v.X > 14.5)
                continue;
            if (Math.Abs(v.Y - 10.0) < 1e-3)
            {
                Require(Math.Abs(v.Z - Ground(v.X, v.Y)) < 0.003, $"{mode}: toe vertex at x={v.X:0.###} is at {v.Z:0.####}, ground {Ground(v.X, v.Y):0.####}");
                toeOnGround++;
            }
            else if (Math.Abs(v.Y - 10.2) < 1e-3 && v.Z > Ground(v.X, v.Y) + 0.4)
            {
                Require(Math.Abs(v.Z - (Ground(v.X, v.Y) + 0.8)) < 0.003, $"{mode}: top vertex at x={v.X:0.###} is at {v.Z:0.####}, expected {Ground(v.X, v.Y) + 0.8:0.####}");
                topLifted++;
            }
        }
        Require(toeOnGround >= 10 && topLifted >= 10, $"{mode}: too few rail vertices found (toe {toeOnGround}, top {topLifted})");
        BoundingBox box = wall.GetBoundingBox(true);
        Require(Math.Abs(box.Max.Z - (Ground(15, 10.2) + 0.8)) < 0.01, $"{mode}: solid top {box.Max.Z:0.###}, expected {Ground(15, 10.2) + 0.8:0.###}");
        Console.WriteLine($"PASS height/{mode}: one solid, toe on ground ({toeOnGround} vertices), top 0.8 above ({topLifted} vertices)");
    }

    private static (object Terrain, RhinoDoc Doc) BuildWallTerrain(
        RhinoDoc doc, string name, List<Curve> curves, string mode, double maxWallWidth, double wallHeight)
    {
        using var ground = new Mesh();
        int n = 41;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                ground.Vertices.Add(i, j, Ground(i, j));
        for (int j = 0; j + 1 < n; j++)
            for (int i = 0; i + 1 < n; i++)
            {
                int a = j * n + i;
                ground.Faces.AddFace(a, a + 1, a + n + 1);
                ground.Faces.AddFace(a, a + n + 1, a + n);
            }
        ground.Normals.ComputeNormals();
        Guid meshId = doc.Objects.AddMesh(ground);
        object terrain = Call(_controller, "CreateTerrainFromTinMeshId", doc, meshId, name)!;
        Set(terrain, "LiveUpdateEnabled", false);
        Guid terrainId = (Guid)Get(terrain, "TerrainId");
        Call(_controller, "AddModifier", doc, terrainId, "retaining-wall");
        object wall = ((IList)Get(terrain, "Modifiers")).Cast<object>().Last(m => m.GetType().Name == "RetainingWallModifierDefinition");
        Set(wall, "Mode", mode);
        Set(wall, "MaxWallWidth", maxWallWidth);
        Set(wall, "WallHeight", wallHeight);
        var ids = (IList)Get(Get(wall, "WallCurves"), "ObjectIds");
        foreach (Curve curve in curves)
            ids.Add(doc.Objects.AddCurve(curve));
        var buildMode = Enum.Parse(_plugin.GetType("MoleHill.Rhino.Services.TerrainBuildMode")!, "Final");
        Require((bool)Call(_controller, "BuildTerrainSynchronously", doc, Call(_controller, "GetState", doc)!, terrain, buildMode, null)!,
            name + ": build failed: " + BuildMessage(terrain));
        Call(_controller, "BakeTerrain", doc, terrainId);
        Require(((IList)Get(terrain, "BakedObjectIds")).Count > 0, name + ": bake created no outputs");
        return (terrain, doc);
    }

    private static IEnumerable<RhinoObject> Baked(RhinoDoc doc, object terrain) =>
        ((IList)Get(terrain, "BakedObjectIds")).Cast<Guid>().Select(id => doc.Objects.FindId(id)).Where(o => o != null)!;

    private static List<Brep> WallSolids(RhinoDoc doc, object terrain) =>
        Baked(doc, terrain)
            .Where(o => o.Attributes.Name?.Contains(":: Wall ", StringComparison.Ordinal) == true)
            .Select(o => o.Geometry)
            .Select(g => g is Extrusion e ? e.ToBrep() : g as Brep)
            .Where(b => b != null)
            .ToList()!;

    private static Mesh TerrainMesh(RhinoDoc doc, object terrain) =>
        Baked(doc, terrain).Select(o => o.Geometry).OfType<Mesh>().OrderByDescending(m => m.Faces.Count).First();

    private static void AssertHoleFree(Mesh mesh, string label)
    {
        int nonManifold = 0;
        for (int e = 0; e < mesh.TopologyEdges.Count; e++)
            if (mesh.TopologyEdges.GetConnectedFaces(e).Length > 2)
                nonManifold++;
        int loops = (mesh.GetNakedEdges() ?? Array.Empty<Polyline>()).Length;
        Require(nonManifold == 0 && loops == 1, $"{label}: terrain has {nonManifold} non-manifold edges and {loops} border loops");
    }

    private static void AssertRailOnMesh(Mesh mesh, Curve rail, double tolerance, string label)
    {
        rail.TryGetPolyline(out Polyline points);
        double worst = points.Max(p => p.DistanceTo(mesh.ClosestPoint(p)));
        Require(worst <= tolerance, $"{label}: a rail point is {worst * 1000:0.0} mm off the terrain");
    }

    private static string BuildMessage(object terrain) => Get(terrain, "LastBuildMessage")?.ToString() ?? string.Empty;

    private static PolylineCurve Polyline(IEnumerable<Point3d> points) => new(points.ToArray());

    private static T Single<T>(List<T> items, string message)
    {
        Require(items.Count == 1, message + $" (got {items.Count})");
        return items[0];
    }

    private static object Get(object value, string name) => value.GetType().GetProperty(name, Flags)!.GetValue(value)!;
    private static void Set(object value, string name, object data) => value.GetType().GetProperty(name, Flags)!.SetValue(value, data);
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(value, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
