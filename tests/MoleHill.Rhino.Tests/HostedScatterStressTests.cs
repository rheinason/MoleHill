using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Tests;

/// <summary>Interactive stress cases for an owned Rhino; timings are observations, not baselines.</summary>
public static class HostedScatterStressTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Assembly _plugin = null!;
    private static object _controller = null!, _terrain = null!, _scatter = null!;
    private static Guid _terrainId, _scatterId;
    private static string _directory = null!;
    private static BoundingBox _complexBounds;
    private static int _leafFaces;
    private const string ComplexName = "Stress nested canopy";
    private const string SmallName = "Stress bollard";

    public static void SetUp(RhinoDoc doc, string directory)
    {
        _directory = directory; Directory.CreateDirectory(directory);
        _plugin = global::Rhino.PlugIns.PlugIn.Find(new Guid("0c0b9e83-4959-437e-a935-4addf0d3f886")).GetType().Assembly;
        _controller = _plugin.GetType("MoleHill.Rhino.Services.TerrainController")!.GetProperty("Instance", Flags)!.GetValue(null)!;
        doc.ModelUnitSystem = UnitSystem.Meters;
        using var mesh = new Mesh();
        const int n = 80;
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
            {
                double px = -400 + 10 * x, py = -400 + 10 * y;
                mesh.Vertices.Add(px, py, 0.03 * px + 0.015 * py + 0.3 * Math.Sin(px / 30));
            }
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * (n + 1) + x;
                mesh.Faces.AddFace(i, i + 1, i + n + 2); mesh.Faces.AddFace(i, i + n + 2, i + n + 1);
            }
        Guid ground = doc.Objects.AddMesh(mesh);
        _terrain = Call(_controller, "CreateTerrainFromTinMeshId", doc, ground, "Scatter stress")!;
        Set(_terrain, "LiveUpdateEnabled", false); _terrainId = (Guid)Get(_terrain, "TerrainId");
        Call(_controller, "AddObjectDefinition", doc, _terrainId, "scatter");
        _scatter = ((IList)Get(_terrain, "Objects"))[0]!; _scatterId = (Guid)Get(_scatter, "Id");
        Set(_scatter, "Name", "Stress planting"); Set(_scatter, "PreviewCap", 1000);
        SetEnum(_scatter, "PreviewMode", "ShapePoints"); Set(_scatter, "RandomSeed", 37);
        Set(_scatter, "RandomRotationMinDegrees", 0d); Set(_scatter, "RandomRotationMaxDegrees", 360d);
        Set(_scatter, "RandomScaleMin", 0.75d); Set(_scatter, "RandomScaleMax", 1.25d);
        using var leaf = Mesh.CreateFromSphere(new Sphere(Point3d.Origin, 0.32), 32, 16);
        _leafFaces = leaf.Faces.Count;
        int green = doc.Materials.Add(new Material { DiffuseColor = System.Drawing.Color.ForestGreen });
        int leafIndex = doc.InstanceDefinitions.Add("Stress leaf", "", Point3d.Origin, new GeometryBase[] { leaf },
            new[] { new ObjectAttributes { MaterialSource = ObjectMaterialSource.MaterialFromObject, MaterialIndex = green } });
        var branchGeometry = new List<GeometryBase>();
        for (int i = 0; i < 10; i++)
            branchGeometry.Add(new InstanceReferenceGeometry(doc.InstanceDefinitions[leafIndex].Id,
                Transform.Translation(0.2 * i, 0.25 * Math.Sin(i), 0.12 * i)));
        int branchIndex = doc.InstanceDefinitions.Add("Stress branch", "", Point3d.Origin, branchGeometry,
            branchGeometry.Select(_ => new ObjectAttributes()).ToArray());
        var crown = new List<GeometryBase>();
        for (int i = 0; i < 8; i++)
            crown.Add(new InstanceReferenceGeometry(doc.InstanceDefinitions[branchIndex].Id,
                Transform.Translation(0.7, -0.3, 2.4 + 0.1 * (i % 2)) * Transform.Rotation(i * Math.PI / 4, Vector3d.ZAxis, Point3d.Origin)));
        int crownIndex = doc.InstanceDefinitions.Add("Stress crown", "", Point3d.Origin, crown, crown.Select(_ => new ObjectAttributes()).ToArray());
        using var trunk = new Cylinder(new Circle(new Plane(new Point3d(0.7, -0.3, 0), Vector3d.ZAxis), 0.16), 2.8).ToBrep(true, true);
        int complexIndex = doc.InstanceDefinitions.Add(ComplexName, "Nested mesh canopy + Brep trunk", Point3d.Origin,
            new GeometryBase[] { trunk, new InstanceReferenceGeometry(doc.InstanceDefinitions[crownIndex].Id, Transform.Identity) },
            new[] { new ObjectAttributes(), new ObjectAttributes() });
        _complexBounds = DefinitionBounds(doc.InstanceDefinitions[complexIndex], Transform.Identity);
        using var small = Mesh.CreateFromBox(new BoundingBox(new Point3d(-0.2, -0.2, 0), new Point3d(0.2, 0.2, 1)), 1, 1, 1);
        doc.InstanceDefinitions.Add(SmallName, "", Point3d.Origin, new GeometryBase[] { small }, new[] { new ObjectAttributes() });
        AddBlock(ComplexName); AddBlock(SmallName);
        SetBoundary(doc, -150, -150, 150, 150);
        Build(doc);
        Record("setup", new { sourceLeafFaces = _leafFaces, nestedLeafInstances = 80, expandedCanopyFaces = 80 * _leafFaces,
            complexBounds = _complexBounds.ToString(), terrainFaces = mesh.Faces.Count, rhp = _plugin.ManifestModule.ModuleVersionId });
    }

    public static void Large(RhinoDoc doc, int count, bool bake)
    {
        Set(_scatter, "Count", (double)count); Set(_scatter, "KeepWholeBlocksInside", false);
        var timer = Stopwatch.StartNew(); Build(doc); timer.Stop();
        object[] outputs = Outputs(); Require(outputs.Length == count, "Large count mismatch");
        double buildMs = timer.Elapsed.TotalMilliseconds;
        Transform[] poses = outputs.Take(64).Select(item => (Transform)Get(item, "InstanceTransform")).ToArray();
        timer.Restart(); Build(doc); timer.Stop();
        Require(poses.SequenceEqual(Outputs().Take(64).Select(item => (Transform)Get(item, "InstanceTransform"))), "Cached poses changed");
        double cachedMs = timer.Elapsed.TotalMilliseconds;
        double? bakeMs = null;
        if (bake)
        {
            bool redraw = doc.Views.RedrawEnabled; doc.Views.RedrawEnabled = false;
            try
            {
                timer.Restart(); Call(_controller, "BakeScatter", doc, _terrainId, _scatterId); timer.Stop();
                bakeMs = timer.Elapsed.TotalMilliseconds;
                Guid[] ids = ((IList)Get(_terrain, "BakedObjectIds")).Cast<Guid>().ToArray();
                Require(ids.Length == count, "Baked count or replacement mismatch");
                foreach (Guid id in ids)
                {
                    Require(doc.Objects.FindId(id) is InstanceObject obj && obj.InstanceDefinition.Name is ComplexName or SmallName,
                        "Bake exploded or lost original definitions");
                    doc.Objects.Hide(id, true);
                }
            }
            finally { doc.Views.RedrawEnabled = redraw; }
        }
        Record("large", new { requested = count, placed = outputs.Length, buildMs, cachedMs, bakeMs,
            privateMemoryMb = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0 });
    }

    public static void Narrow(RhinoDoc doc)
    {
        Set(_scatter, "Count", 5000d); SetBoundary(doc, -50, -0.15, 50, 0.15);
        Set(_scatter, "KeepWholeBlocksInside", false); Build(doc); Require(Outputs().Length == 5000, "Insertion-point strip fixture");
        Set(_scatter, "KeepWholeBlocksInside", true); Build(doc); Require(Outputs().Length == 0, "Oversize blocks escaped a 0.3 m strip");
        object summary = ((IList)Get(Display(), "ScatterSummaries"))[0]!;
        Require((int)Get(summary, "FootprintRejected") == 5000, "Zero-placement explanation missing");
        SetBoundary(doc, -50, -1, 50, 1); Build(doc);
        object[] fitting = Outputs(); Require(fitting.Length > 0 && fitting.Length < 5000, "2 m strip did not filter");
        Require(fitting.All(item => (string)Get(item, "InstanceDefinitionName") == SmallName), "Nested canopy fit inside an undersized strip");
        foreach (object item in fitting)
        {
            Transform pose = (Transform)Get(item, "InstanceTransform");
            foreach (Point3d p in new BoundingBox(new Point3d(-0.2, -0.2, 0), new Point3d(0.2, 0.2, 1)).GetCorners())
            { Point3d q = pose * p; Require(q.Y >= -1 && q.Y <= 1 && q.X >= -50 && q.X <= 50, "Strip overhang"); }
        }
        int stripPlaced = fitting.Length;
        Set(_scatter, "KeepWholeBlocksInside", false); Set(_scatter, "BoundarySetback", 1.01d); Build(doc);
        Require(Outputs().Length == 0 && (int)Get(((IList)Get(Display(), "ScatterSummaries"))[0]!, "Setback") == 5000,
            "Overlapping setbacks did not produce an explained zero result");
        Set(_scatter, "BoundarySetback", 0d); SetBoundary(doc, -50, -50, 50, 50);
        Set(_scatter, "Count", 3000d); Set(_scatter, "KeepWholeBlocksInside", true); Build(doc);
        foreach (object item in Outputs())
        {
            BoundingBox bounds = (string)Get(item, "InstanceDefinitionName") == ComplexName ? _complexBounds :
                new BoundingBox(new Point3d(-0.2, -0.2, 0), new Point3d(0.2, 0.2, 1));
            foreach (Point3d p in bounds.GetCorners())
            { Point3d q = (Transform)Get(item, "InstanceTransform") * p; Require(q.X >= -50 && q.X <= 50 && q.Y >= -50 && q.Y <= 50, "Complex nested bounds overhang"); }
        }
        Record("narrow", new { stripWidth = 0.3, oversizeRejected = 5000, widerStripWidth = 2, widerStripPlaced = stripPlaced,
            overlappingSetbacksRejected = 5000, complexContained = Outputs().Length });
    }

    public static void PreviewProbes(RhinoDoc doc)
    {
        InstanceDefinition block = doc.InstanceDefinitions.Find(ComplexName);
        object preview = _plugin.GetType("MoleHill.Rhino.Services.ScatterBlockPreview")!.GetMethod("Get", Flags)!.Invoke(null, new object[] { doc, block })!;
        int displayFaces = ((IEnumerable)Get(preview, "Meshes")).Cast<object>().Sum(item => ((Mesh)item.GetType().GetField("Item1")!.GetValue(item)!).Faces.Count);
        Require(displayFaces >= 80 * _leafFaces, "Nested canopy missing from conduit preview");
        Type provider = _plugin.GetType("MoleHill.Rhino.Services.TerrainRenderMeshProvider")!;
        Type part = provider.GetNestedType("DefinitionMeshPart", Flags)!;
        object cache = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), part.MakeArrayType()))!;
        var parts = (IEnumerable)provider.GetMethod("GetDefinitionMeshes", Flags)!.Invoke(null, new[] { doc, ComplexName, cache })!;
        int renderFaces = parts.Cast<object>().Sum(item => ((Mesh)Get(item, "Mesh")).Faces.Count);
        Record("preview-meshes", new { displayFaces, renderFaces, expectedCanopyFaces = 80 * _leafFaces,
            rendererHasNestedCanopy = renderFaces >= 80 * _leafFaces });
        Require(renderFaces >= 80 * _leafFaces, "Nested canopy missing from RDK render output");
        Set(_scatter, "PreviewCap", 16); SetEnum(_scatter, "PreviewMode", "Instances");
        Call(_controller, "RefreshTerrainDisplay", doc, _terrainId);
        doc.Views.Redraw();
    }

    public static void NativePreview(RhinoDoc doc)
    {
        Set(_scatter, "PreviewCap", 16); SetEnum(_scatter, "PreviewMode", "RenderInstances");
        Call(_controller, "RefreshTerrainDisplay", doc, _terrainId);
        var objects = doc.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, ReferenceObjects = true })
            .OfType<InstanceObject>().Where(item => item.Attributes.GetUserString("MoleHill.ScatterPreview") != null).ToArray();
        Require(objects.Length == 16, "Native preview cap mismatch");
        Require(objects.All(item => item.InstanceDefinition.Name is ComplexName or SmallName), "Native preview lost definitions");
        Guid[] previewIds = objects.Select(item => item.Id).ToArray();
        SetEnum(_scatter, "PreviewMode", "ShapePoints"); Call(_controller, "RefreshTerrainDisplay", doc, _terrainId);
        Require(previewIds.All(id => doc.Objects.FindId(id) == null), "Native preview cleanup failed");
        Record("native-preview", new { cap = 16, instances = objects.Length, removedOnModeChange = true });
    }

    public static void PrepareUi(RhinoDoc doc)
    {
        SetBoundary(doc, -20, -20, 20, 20); Set(_scatter, "Count", 3000d); Set(_scatter, "KeepWholeBlocksInside", false);
        Set(_scatter, "BoundarySetback", 1d); Set(_scatter, "BoundaryFeather", 2d); Set(_scatter, "PreviewCap", 200);
        SetEnum(_scatter, "PreviewMode", "ShapePoints");
        Set(((IList)Get(_scatter, "Blocks"))[0]!, "IsEnabled", false);
        Build(doc);
        Call(_controller, "Save", doc, Call(_controller, "GetState", doc), true);
        Record("ui-ready", new { terrainId = _terrainId, scatterId = _scatterId, placed = Outputs().Length });
    }

    public static void AssertUi(RhinoDoc doc, string label)
    {
        _terrain = Call(_controller, "GetSelectedTerrain", doc)!;
        _scatter = ((IList)Get(_terrain, "Objects")).Cast<object>().Single(item => (Guid)Get(item, "Id") == _scatterId);
        Record("ui-state", new { label, count = Get(_scatter, "Count"), unit = Get(_scatter, "DensityUnit").ToString(),
            density = Get(_scatter, "PerAreaDensity"), blocks = ((IList)Get(_scatter, "Blocks")).Cast<object>().Select(item => new
            { name = Get(item, "BlockDefinitionName"), enabled = Get(item, "IsEnabled"), weight = Get(item, "Weight") }).ToArray(),
            currentSummaryCount = ((IList)Call(_controller, "GetScatterSummaries", doc, _terrainId)!).Count });
    }

    private static void AddBlock(string name)
    { object entry = Activator.CreateInstance(_plugin.GetType("MoleHill.Rhino.Model.ScatterBlockEntry")!)!; Set(entry, "BlockDefinitionName", name); ((IList)Get(_scatter, "Blocks")).Add(entry); }
    private static void SetBoundary(RhinoDoc doc, double x0, double y0, double x1, double y1)
    {
        var ids = (IList)Get(Get(_scatter, "Boundaries"), "ObjectIds");
        foreach (Guid id in ids.Cast<Guid>().ToArray()) doc.Objects.Delete(id, true);
        ids.Clear(); using var curve = new PolylineCurve(new[] { new Point3d(x0,y0,0),new Point3d(x1,y0,0),new Point3d(x1,y1,0),new Point3d(x0,y1,0),new Point3d(x0,y0,0) });
        ids.Add(doc.Objects.AddCurve(curve));
    }
    private static BoundingBox DefinitionBounds(InstanceDefinition definition, Transform transform)
    {
        BoundingBox result = BoundingBox.Empty;
        foreach (RhinoObject member in definition.GetObjects())
            if (member is InstanceObject nested) result.Union(DefinitionBounds(nested.InstanceDefinition, transform * nested.InstanceXform));
            else foreach (Point3d point in member.Geometry.GetBoundingBox(true).GetCorners()) result.Union(transform * point);
        return result;
    }
    private static object Display() => Get(Call(_controller, "GetRuntimeCache", _docSerial, _terrainId)!, "DisplayState");
    private static uint _docSerial;
    private static object[] Outputs() => ((IList)Get(Display(), "ScatterObjects")).Cast<object>().ToArray();
    private static void Build(RhinoDoc doc)
    {
        _docSerial = doc.RuntimeSerialNumber;
        object mode = Enum.Parse(_plugin.GetType("MoleHill.Rhino.Services.TerrainBuildMode")!, "Final");
        Call(_controller, "RemovePendingBuild", doc.RuntimeSerialNumber, _terrainId, mode);
        Call(_controller, "RemovePendingBuild", doc.RuntimeSerialNumber, _terrainId,
            Enum.Parse(_plugin.GetType("MoleHill.Rhino.Services.TerrainBuildMode")!, "Preview"));
        Require((bool)Call(_controller, "BuildTerrainSynchronously", doc, Call(_controller, "GetState", doc), _terrain, mode, null)!, "Build failed");
    }
    private static void Record(string test, object data)
    { string line = JsonSerializer.Serialize(new { test, utc = DateTime.UtcNow, data }); File.AppendAllText(Path.Combine(_directory, "results.jsonl"), line + System.Environment.NewLine); Console.WriteLine(line); }
    private static void SetEnum(object value, string name, string text) => Set(value, name, Enum.Parse(value.GetType().GetProperty(name)!.PropertyType, text));
    private static object Get(object value, string name) => value.GetType().GetProperty(name, Flags)!.GetValue(value)!;
    private static void Set(object value, string name, object data) => value.GetType().GetProperty(name, Flags)!.SetValue(value, data);
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(value, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
