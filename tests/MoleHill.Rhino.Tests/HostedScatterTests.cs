using System.Collections;
using System.Reflection;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace MoleHill.Rhino.Tests;

/// <summary>Run inside a disposable Rhino against the loaded plugin, not source-linked types.</summary>
public static class HostedScatterTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(RhinoDoc doc, string archivePath)
    {
        Assembly plugin = global::Rhino.PlugIns.PlugIn.Find(new Guid("0c0b9e83-4959-437e-a935-4addf0d3f886")).GetType().Assembly;
        object controller = plugin.GetType("MoleHill.Rhino.Services.TerrainController")!.GetProperty("Instance", Flags)!.GetValue(null)!;
        doc.ModelUnitSystem = UnitSystem.Meters;
        // Use a single 2.5D surface for terrain sampling.
        using var tin = new Mesh();
        tin.Vertices.Add(-20, -20, 0); tin.Vertices.Add(20, -20, 0);
        tin.Vertices.Add(20, 20, 0); tin.Vertices.Add(-20, 20, 0);
        tin.Faces.AddFace(0, 1, 2); tin.Faces.AddFace(0, 2, 3);
        object terrain = Call(controller, "CreateTerrainFromTinMeshId", doc, doc.Objects.AddMesh(tin), "Scatter regression")!;
        Set(terrain, "LiveUpdateEnabled", false);
        Guid terrainId = (Guid)Get(terrain, "TerrainId");
        Call(controller, "AddObjectDefinition", doc, terrainId, "scatter");
        object scatter = ((IList)Get(terrain, "Objects"))[0]!;
        Set(scatter, "Count", 1000d);
        Set(scatter, "RandomRotationMinDegrees", 45d);
        Set(scatter, "RandomRotationMaxDegrees", 45d);
        Set(scatter, "RandomScaleMin", 1.5d);
        Set(scatter, "RandomScaleMax", 1.5d);
        Set(scatter, "PreviewCap", 23);
        Set(scatter, "PreviewMode", Enum.Parse(plugin.GetType("MoleHill.Rhino.Model.ScatterPreviewMode")!, "RenderInstances"));
        using var shape = Mesh.CreateFromBox(new BoundingBox(new Point3d(-2, -1, 0), new Point3d(2, 1, 3)), 1, 1, 1);
        int blockIndex = doc.InstanceDefinitions.Add("Scatter regression block", "", Point3d.Origin, new GeometryBase[] { shape }, new[] { new ObjectAttributes() });
        var sourceAttributes = new ObjectAttributes();
        sourceAttributes.SetUserString("Enscape.Test", "retained");
        doc.Objects.AddInstanceObject(blockIndex, Transform.Translation(40, 0, 0), sourceAttributes);
        object entry = Activator.CreateInstance(plugin.GetType("MoleHill.Rhino.Model.ScatterBlockEntry")!)!;
        Set(entry, "BlockDefinitionName", "Scatter regression block");
        ((IList)Get(scatter, "Blocks")).Add(entry);
        using var boundary = new PolylineCurve(new[] { new Point3d(-10, -10, 0), new Point3d(10, -10, 0), new Point3d(10, 10, 0), new Point3d(-10, 10, 0), new Point3d(-10, -10, 0) });
        ((IList)Get(Get(scatter, "Boundaries"), "ObjectIds")).Add(doc.Objects.AddCurve(boundary));
        object mode = Enum.Parse(plugin.GetType("MoleHill.Rhino.Services.TerrainBuildMode")!, "Final");
        void Build() => Require((bool)Call(controller, "BuildTerrainSynchronously", doc, Call(controller, "GetState", doc), terrain, mode, null)!, "Build failed");
        InstanceObject[] References() => doc.Objects.GetObjectList(new ObjectEnumeratorSettings { ReferenceObjects = true, NormalObjects = true, LockedObjects = true, HiddenObjects = true })
            .OfType<InstanceObject>().Where(item => item.Attributes.GetUserString("MoleHill.ScatterPreview") != null).ToArray();
        Build();
        Require(References().Length == 23, "Native preview cap");
        Guid[] ids = References().Select(item => item.Id).ToArray();
        Call(controller, "RefreshTerrainDisplay", doc, terrainId);
        Require(ids.SequenceEqual(References().Select(item => item.Id)), "Display refresh recreated native instances");
        Set(scatter, "KeepWholeBlocksInside", true);
        Build();
        object cache = Call(controller, "GetRuntimeCache", doc.RuntimeSerialNumber, terrainId)!;
        object display = Get(cache, "DisplayState");
        var placements = ((IList)Get(display, "ScatterObjects")).Cast<object>().ToArray();
        Require(placements.Length > 0 && placements.Length < 1000, "Whole-block filter did not reduce placements");
        foreach (object placement in placements)
        {
            Transform pose = (Transform)Get(placement, "InstanceTransform");
            foreach (Point3d corner in shape.GetBoundingBox(true).GetCorners())
            {
                Point3d p = pose * corner;
                Require(p.X >= -10 && p.X <= 10 && p.Y >= -10 && p.Y <= 10, "Block overhang after rotation and scale");
            }
        }
        Require(References().Length == 23 && References().All(item => item.Attributes.GetUserString("Enscape.Test") == "retained"), "Preview or metadata lost");
        // Transient instance objects can report IsReference == false; their tag must exclude them
        // from watched-layer and explicit source resolution as well.
        object sourceSet = Activator.CreateInstance(plugin.GetType("MoleHill.Rhino.Model.SourceReferenceSet")!)!;
        ((IList)Get(sourceSet, "ObjectIds")).Add(References()[0].Id);
        Type resolver = plugin.GetType("MoleHill.Rhino.Services.RhinoSourceResolver")!;
        object resolved = resolver.GetMethods(Flags).Single(method => method.Name == "ResolveObjects" && method.GetParameters().Length == 2).Invoke(null, new[] { doc, sourceSet })!;
        Require(((IList)resolved).Count == 0, "Preview fed back into source resolution");
        Set(scatter, "IsEnabled", false);
        Call(controller, "RefreshTerrainDisplay", doc, terrainId);
        Require(References().Length == 0, "Disable left previews");
        Set(scatter, "IsEnabled", true);
        Set(scatter, "PreviewMode", Enum.Parse(plugin.GetType("MoleHill.Rhino.Model.ScatterPreviewMode")!, "Points"));
        Call(controller, "RefreshTerrainDisplay", doc, terrainId);
        Require(References().Length == 0, "Point mode left previews");
        Set(scatter, "PreviewMode", Enum.Parse(plugin.GetType("MoleHill.Rhino.Model.ScatterPreviewMode")!, "RenderInstances"));
        Call(controller, "RefreshTerrainDisplay", doc, terrainId);
        Require(References().Length == 23, "Native mode failed to restore previews");
        Require(doc.WriteFile(archivePath, new FileWriteOptions()), "Save failed");
        using (File3dm saved = File3dm.Read(archivePath))
            Require(saved.Objects.Count(item => item.Geometry is InstanceReferenceGeometry) == 1, "Temporary previews persisted in 3dm");
        Call(controller, "SetTerrainVisible", doc, terrainId, false);
        Require(References().Length == 0, "Hide left preview instances");
        Call(controller, "SetTerrainVisible", doc, terrainId, true);
        Require(References().Length == 23, "Show lost previews");
        Call(controller, "BakeTerrain", doc, terrainId);
        var baked = ((IList)Get(terrain, "BakedObjectIds")).Cast<Guid>().Select(id => doc.Objects.FindId(id)).OfType<InstanceObject>().ToArray();
        Require(baked.Length == placements.Length, "Bake count differs from generated scatter");
        Require(baked.All(item => !item.IsReference && item.Attributes.GetUserString("Enscape.Test") == "retained"), "Bake metadata or persistence");
        Call(controller, "DeleteTerrain", doc, terrainId);
        Require(References().Length == 0, "Delete left temporary previews");
        Console.WriteLine($"PASS scatter: rotated/scaled containment; cap; stable refresh; metadata; save exclusion; hide/show; {baked.Length} baked native instances; delete cleanup. RHP {plugin.ManifestModule.ModuleVersionId}");
    }

    private static object Get(object value, string name) => value.GetType().GetProperty(name, Flags)!.GetValue(value)!;
    private static void Set(object value, string name, object data) => value.GetType().GetProperty(name, Flags)!.SetValue(value, data);
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(value, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
