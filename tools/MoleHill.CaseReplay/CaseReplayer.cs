using System.Collections;
using System.Reflection;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace MoleHill.CaseReplay;

/// <summary>
/// Builds a case bundle's terrain in a headless document. The plugin's services are internal, so they are
/// reached by reflection; every call goes through the same snapshot builder and build service the panel uses.
/// </summary>
internal static class CaseReplayer
{
    private const BindingFlags AnyMember = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(Assembly plugin, string caseDirectory, string? unitsOverride, bool stages)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(caseDirectory, "manifest.json")));
        JsonElement root = manifest.RootElement;
        UnitSystem units = ResolveUnits(unitsOverride, root);
        double tolerance = root.TryGetProperty("modelAbsoluteTolerance", out JsonElement t) ? t.GetDouble() : 0.001;
        Guid terrainId = root.TryGetProperty("terrainId", out JsonElement id) ? id.GetGuid() : Guid.Empty;

        RhinoDoc doc = RhinoDoc.CreateHeadless(null);
        try
        {
            doc.ModelUnitSystem = units;
            doc.ModelAbsoluteTolerance = tolerance;
            int added = LoadSources(doc, Path.Combine(caseDirectory, "sources.3dm"));
            Console.WriteLine($"Replay document: {units}, tolerance {tolerance:G6}, {added:N0} source objects");

            string terrainJson = File.ReadAllText(Path.Combine(caseDirectory, "terrain.json"));
            if (stages)
            {
                int modifierCount = ModifierCount(DeserializeTerrain(plugin, terrainJson, units, terrainId));
                for (int upto = 0; upto < modifierCount; upto++)
                    BuildAndReport(plugin, doc, terrainJson, units, terrainId, upto, printDiagnostics: false);
            }

            BuildAndReport(plugin, doc, terrainJson, units, terrainId, int.MaxValue, printDiagnostics: true);
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static UnitSystem ResolveUnits(string? unitsOverride, JsonElement root)
    {
        string? name = unitsOverride ??
            (root.TryGetProperty("modelUnitSystem", out JsonElement recorded) ? recorded.GetString() : null);
        if (name == null)
        {
            Console.WriteLine("Model units not recorded in this bundle; assuming Meters (pass --units to override).");
            return UnitSystem.Meters;
        }

        return Enum.Parse<UnitSystem>(name, ignoreCase: true);
    }

    /// <summary>
    /// Adds the bundle's sources to <paramref name="doc"/> as stored: coordinates are taken as they are, never
    /// rescaled by the file's unit tag (bundles before 2026-10-06 tagged metre models as millimetres), each
    /// object goes on the layer it was resolved from, and block references get their copied definitions.
    /// </summary>
    private static int LoadSources(RhinoDoc doc, string path)
    {
        File3dm file = File3dm.Read(path) ?? throw new InvalidOperationException($"Could not read {path}");
        var definitionIndex = new Dictionary<Guid, int>();
        foreach (InstanceDefinitionGeometry definition in file.AllInstanceDefinitions)
            AddDefinition(doc, file, definition, definitionIndex);

        int added = 0;
        foreach (File3dmObject fileObject in file.Objects)
        {
            GeometryBase? geometry = fileObject.Geometry?.Duplicate();
            if (geometry == null)
                continue;

            ObjectAttributes attributes = fileObject.Attributes.Duplicate();
            attributes.LayerIndex = EnsureLayer(doc, attributes.GetUserString("MoleHill.SourceLayerPath") ?? "Inputs");
            if (attributes.GetUserString("MoleHill.SourceObjectId") is { } sourceId && Guid.TryParse(sourceId, out Guid objectId))
                attributes.ObjectId = objectId;

            Guid addedId = geometry is InstanceReferenceGeometry reference && definitionIndex.TryGetValue(reference.ParentIdefId, out int index)
                ? doc.Objects.AddInstanceObject(index, reference.Xform, attributes)
                : geometry is InstanceReferenceGeometry ? Guid.Empty : doc.Objects.Add(geometry, attributes);
            if (addedId != Guid.Empty)
                added++;
        }

        return added;
    }

    private static int AddDefinition(RhinoDoc doc, File3dm file, InstanceDefinitionGeometry definition, Dictionary<Guid, int> definitionIndex)
    {
        if (definitionIndex.TryGetValue(definition.Id, out int existing))
            return existing;

        var geometry = new List<GeometryBase>();
        var attributes = new List<ObjectAttributes>();
        foreach (Guid memberId in definition.GetObjectIds())
        {
            File3dmObject? member = file.Objects.FindId(memberId);
            GeometryBase? memberGeometry = member?.Geometry?.Duplicate();
            if (memberGeometry == null)
                continue;
            geometry.Add(memberGeometry);
            attributes.Add(member!.Attributes.Duplicate());
        }

        int index = doc.InstanceDefinitions.Add(definition.Name, definition.Description, Point3d.Origin, geometry, attributes);
        definitionIndex[definition.Id] = index;
        return index;
    }

    private static int EnsureLayer(RhinoDoc doc, string fullPath)
    {
        int index = doc.Layers.FindByFullPath(fullPath, -1);
        if (index >= 0)
            return index;

        int parent = -1;
        string? path = null;
        foreach (string part in fullPath.Split("::"))
        {
            path = path == null ? part : path + "::" + part;
            int found = doc.Layers.FindByFullPath(path, -1);
            if (found < 0)
            {
                var layer = new Layer { Name = part };
                if (parent >= 0)
                    layer.ParentLayerId = doc.Layers[parent].Id;
                found = doc.Layers.Add(layer);
            }

            parent = found;
        }

        return parent;
    }

    private static object DeserializeTerrain(Assembly plugin, string json, UnitSystem units, Guid terrainId)
    {
        MethodInfo deserialize = Type(plugin, "MoleHill.Rhino.Services.TerrainSerializer")
            .GetMethod("Deserialize", AnyMember, null, new[] { typeof(string), typeof(UnitSystem) }, null)
            ?? throw new MissingMethodException("TerrainSerializer.Deserialize(string, UnitSystem)");
        foreach (object terrain in (IEnumerable)deserialize.Invoke(null, new object[] { json, units })!)
        {
            if (terrainId == Guid.Empty || (Guid)Property(terrain, "TerrainId")! == terrainId)
                return terrain;
        }

        throw new InvalidOperationException($"terrain.json has no terrain {terrainId}.");
    }

    private static int ModifierCount(object terrain) => ((ICollection)Property(terrain, "Modifiers")!).Count;

    private static void BuildAndReport(
        Assembly plugin, RhinoDoc doc, string terrainJson, UnitSystem units, Guid terrainId, int upto, bool printDiagnostics)
    {
        object terrain = DeserializeTerrain(plugin, terrainJson, units, terrainId);
        var modifiers = ((IEnumerable)Property(terrain, "Modifiers")!).Cast<object>().ToList();
        string label = "full build";
        if (upto < modifiers.Count)
        {
            for (int i = upto + 1; i < modifiers.Count; i++)
                modifiers[i].GetType().GetProperty("IsEnabled")!.SetValue(modifiers[i], false);
            label = $"through {Property(modifiers[upto], "Label")}";
        }

        MethodInfo create = Type(plugin, "MoleHill.Rhino.Services.TerrainBuildSnapshotBuilder").GetMethods(AnyMember)
            .First(m => m.Name == "Create" && m.GetParameters().Length == 3);
        object snapshot = create.Invoke(null, new object?[] { doc, terrain, null })!;

        Type service = Type(plugin, "MoleHill.Rhino.Services.TerrainBuildService");
        MethodInfo build = service.GetMethods(AnyMember)
            .First(m => m.Name == "Build" && m.GetParameters()[0].ParameterType.Name == "TerrainBuildSnapshot");
        object?[] arguments = build.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        arguments[0] = snapshot;
        arguments[1] = Activator.CreateInstance(Type(plugin, "MoleHill.Rhino.Services.TerrainRuntimeCache"), nonPublic: true);
        arguments[2] = Enum.Parse(Type(plugin, "MoleHill.Rhino.Services.TerrainBuildMode"), "Final");

        var timer = System.Diagnostics.Stopwatch.StartNew();
        object result = build.Invoke(Activator.CreateInstance(service, nonPublic: true), arguments)!;
        timer.Stop();

        var mesh = Property(result, "PrimaryMesh") as Mesh;
        Console.WriteLine($"{label} ({timer.ElapsedMilliseconds:N0} ms): {Describe(mesh)}");
        if (!printDiagnostics)
            return;

        foreach (object diagnostic in (IEnumerable)Property(result, "Diagnostics")!)
        {
            string text = diagnostic.ToString() ?? string.Empty;
            if (!text.StartsWith("timing.", StringComparison.Ordinal))
                Console.WriteLine($"  {text}");
        }
    }

    private static string Describe(Mesh? mesh)
    {
        if (mesh == null)
            return "no mesh";

        Polyline[]? borders = mesh.GetNakedEdges();
        int nonManifold = 0;
        for (int e = 0; e < mesh.TopologyEdges.Count; e++)
        {
            if (mesh.TopologyEdges.GetConnectedFaces(e).Length > 2)
                nonManifold++;
        }

        return $"{mesh.Vertices.Count:N0} verts / {mesh.Faces.Count:N0} faces, {borders?.Length ?? 0} border loop(s), " +
               $"{nonManifold} non-manifold edge(s), valid {mesh.IsValid}";
    }

    private static Type Type(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true)!;

    private static object? Property(object instance, string name) =>
        instance.GetType().GetProperty(name, AnyMember)?.GetValue(instance);
}
