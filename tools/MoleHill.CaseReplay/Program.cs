using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Rhino.FileIO;
using Rhino.Geometry;

namespace MoleHill.CaseReplay;

internal static class Program
{
    private const string DefaultPluginDirectory = @".artifacts\build-verify\MoleHill.Rhino";
    private const string RhinoSystemDirectory = @"C:\Program Files\Rhino 8\System";
    private const string RhinoNetCoreDirectory = @"C:\Program Files\Rhino 8\System\netcore";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/MoleHill.CaseReplay/MoleHill.CaseReplay.csproj -- <case-zip-or-dir> [plugin-output-dir]");
            return 1;
        }

        string casePath = Path.GetFullPath(args[0]);
        string pluginDirectory = Path.GetFullPath(args.Length > 1 ? args[1] : DefaultPluginDirectory);
        string pluginAssemblyPath = Path.Combine(pluginDirectory, "MoleHill.Rhino.rhp");
        if (!File.Exists(pluginAssemblyPath))
        {
            Console.Error.WriteLine($"Could not find plugin assembly at {pluginAssemblyPath}");
            return 1;
        }

        PrependToPath(RhinoSystemDirectory);
        PrependToPath(RhinoNetCoreDirectory);
        RegisterAssemblyResolver(pluginDirectory);

        string caseDirectory;
        bool deleteExtractedDirectory = false;
        if (File.Exists(casePath) && string.Equals(Path.GetExtension(casePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            caseDirectory = ExtractCaseArchive(casePath);
            deleteExtractedDirectory = true;
        }
        else if (Directory.Exists(casePath))
        {
            caseDirectory = casePath;
        }
        else
        {
            Console.Error.WriteLine($"Could not find case bundle at {casePath}");
            return 1;
        }

        try
        {
            Rhino.Runtime.HostUtils.InitializeRhinoCommon();
            var pluginAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginAssemblyPath);
            BuildCase(pluginAssembly, caseDirectory);
            return 0;
        }
        finally
        {
            if (deleteExtractedDirectory && Directory.Exists(caseDirectory))
                Directory.Delete(caseDirectory, recursive: true);
        }
    }

    private static void BuildCase(Assembly pluginAssembly, string caseDirectory)
    {
        string terrainJsonPath = Path.Combine(caseDirectory, "terrain.json");
        string manifestJsonPath = Path.Combine(caseDirectory, "manifest.json");
        string sourcesPath = Path.Combine(caseDirectory, "sources.3dm");

        if (!File.Exists(terrainJsonPath))
            throw new FileNotFoundException("Case bundle is missing terrain.json", terrainJsonPath);
        if (!File.Exists(manifestJsonPath))
            throw new FileNotFoundException("Case bundle is missing manifest.json", manifestJsonPath);
        if (!File.Exists(sourcesPath))
            throw new FileNotFoundException("Case bundle is missing sources.3dm", sourcesPath);

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestJsonPath));
        double modelAbsoluteTolerance = manifest.RootElement.GetProperty("modelAbsoluteTolerance").GetDouble();
        Guid terrainId = manifest.RootElement.TryGetProperty("terrainId", out JsonElement terrainIdElement)
            ? terrainIdElement.GetGuid()
            : Guid.Empty;

        Type terrainSerializerType = GetRequiredType(pluginAssembly, "MoleHill.Rhino.Services.TerrainSerializer");
        MethodInfo deserializeMethod = terrainSerializerType.GetMethod("Deserialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(terrainSerializerType.FullName, "Deserialize");
        object terrains = deserializeMethod.Invoke(null, new object?[] { File.ReadAllText(terrainJsonPath) })
            ?? throw new InvalidOperationException("TerrainSerializer.Deserialize returned null.");

        object terrain = GetTerrainById(terrains, terrainId)
            ?? throw new InvalidOperationException($"Could not find terrain {terrainId} in terrain.json.");

        Type snapshotType = GetRequiredType(pluginAssembly, "MoleHill.Rhino.Services.TerrainBuildSnapshot");
        object snapshot = Activator.CreateInstance(snapshotType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create TerrainBuildSnapshot.");
        snapshotType.GetProperty("Terrain")!.SetValue(snapshot, terrain);
        snapshotType.GetProperty("ModelAbsoluteTolerance")!.SetValue(snapshot, modelAbsoluteTolerance);

        var resolvedSourceObjects = LoadResolvedSourceObjects(pluginAssembly, sourcesPath);
        PopulateSnapshotSourceSets(pluginAssembly, snapshot, terrain, resolvedSourceObjects);

        Type runtimeCacheType = GetRequiredType(pluginAssembly, "MoleHill.Rhino.Services.TerrainRuntimeCache");
        object runtimeCache = Activator.CreateInstance(runtimeCacheType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create TerrainRuntimeCache.");

        Type buildServiceType = GetRequiredType(pluginAssembly, "MoleHill.Rhino.Services.TerrainBuildService");
        object buildService = Activator.CreateInstance(buildServiceType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create TerrainBuildService.");

        Type buildModeType = GetRequiredType(pluginAssembly, "MoleHill.Rhino.Services.TerrainBuildMode");
        object finalMode = Enum.Parse(buildModeType, "Final");

        MethodInfo buildMethod = buildServiceType.GetMethod(
            "Build",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { snapshotType, runtimeCacheType, buildModeType, typeof(Func<bool>) },
            modifiers: null)
            ?? throw new MissingMethodException(buildServiceType.FullName, "Build(snapshot, runtimeCache, mode, shouldCancel)");

        object buildResult = buildMethod.Invoke(buildService, new object?[] { snapshot, runtimeCache, finalMode, null })
            ?? throw new InvalidOperationException("TerrainBuildService.Build returned null.");

        Console.WriteLine($"Case: {caseDirectory}");
        Console.WriteLine($"Terrain: {GetPropertyValue<string>(terrain, "Name")}");

        IEnumerable<object> diagnostics = EnumerateObjects(GetPropertyValue<object>(buildResult, "Diagnostics"));
        foreach (object diagnostic in diagnostics)
            Console.WriteLine(diagnostic);

        object? primaryMesh = GetPropertyValue<object?>(buildResult, "PrimaryMesh");
        if (primaryMesh is Mesh mesh)
            Console.WriteLine($"Replay primary mesh: {mesh.Vertices.Count} verts / {mesh.Faces.Count} faces");
    }

    private static object? GetTerrainById(object terrains, Guid terrainId)
    {
        foreach (object terrain in EnumerateObjects(terrains))
        {
            if (terrainId == Guid.Empty || GetPropertyValue<Guid>(terrain, "TerrainId") == terrainId)
                return terrain;
        }

        return null;
    }

    private static List<object> LoadResolvedSourceObjects(Assembly pluginAssembly, string sourcesPath)
    {
        var file = File3dm.Read(sourcesPath)
            ?? throw new InvalidOperationException($"Could not read source model {sourcesPath}");

        Type resolvedSourceObjectType = GetRequiredType(pluginAssembly, "MoleHill.Rhino.Services.ResolvedSourceObject");
        var sourceObjects = new List<object>(file.Objects.Count);
        foreach (File3dmObject fileObject in file.Objects)
        {
            GeometryBase? geometry = fileObject.Geometry?.Duplicate();
            if (geometry == null)
                continue;

            var resolvedSourceObject = Activator.CreateInstance(resolvedSourceObjectType, nonPublic: true)
                ?? throw new InvalidOperationException("Could not create ResolvedSourceObject.");

            string? sourceObjectIdText = fileObject.Attributes.GetUserString("MoleHill.SourceObjectId");
            Guid sourceObjectId = Guid.TryParse(sourceObjectIdText, out Guid parsedSourceObjectId)
                ? parsedSourceObjectId
                : Guid.Empty;
            string? sourceLayerPath = fileObject.Attributes.GetUserString("MoleHill.SourceLayerPath");
            uint geometryDataCrc = geometry.DataCRC(0u);

            resolvedSourceObjectType.GetProperty("ObjectId")!.SetValue(resolvedSourceObject, sourceObjectId);
            resolvedSourceObjectType.GetProperty("LayerPath")!.SetValue(resolvedSourceObject, sourceLayerPath);
            resolvedSourceObjectType.GetProperty("Geometry")!.SetValue(resolvedSourceObject, geometry);
            resolvedSourceObjectType.GetProperty("LocalBoundingBox")!.SetValue(resolvedSourceObject, geometry.GetBoundingBox(true));
            resolvedSourceObjectType.GetProperty("WorldBoundingBox")!.SetValue(resolvedSourceObject, geometry.GetBoundingBox(true));
            resolvedSourceObjectType.GetProperty("SourceTransform")!.SetValue(resolvedSourceObject, Transform.Identity);
            resolvedSourceObjectType.GetProperty("HasSourceTransform")!.SetValue(resolvedSourceObject, false);
            resolvedSourceObjectType.GetProperty("GeometryDataCrc")!.SetValue(resolvedSourceObject, geometryDataCrc);

            sourceObjects.Add(resolvedSourceObject);
        }

        return sourceObjects;
    }

    private static void PopulateSnapshotSourceSets(Assembly pluginAssembly, object snapshot, object terrain, IReadOnlyList<object> resolvedSourceObjects)
    {
        object sourceObjectsDictionary = GetPropertyValue<object>(snapshot, "SourceObjects");
        object sourceFingerprintsDictionary = GetPropertyValue<object>(snapshot, "SourceFingerprints");
        MethodInfo addSourceObjectsMethod = sourceObjectsDictionary.GetType().GetMethod("Add")
            ?? throw new MissingMethodException(sourceObjectsDictionary.GetType().FullName, "Add");
        MethodInfo addSourceFingerprintsMethod = sourceFingerprintsDictionary.GetType().GetMethod("Add")
            ?? throw new MissingMethodException(sourceFingerprintsDictionary.GetType().FullName, "Add");

        Type resolvedSourceObjectType = GetRequiredType(pluginAssembly, "MoleHill.Rhino.Services.ResolvedSourceObject");
        Type resolvedSourceObjectListType = typeof(List<>).MakeGenericType(resolvedSourceObjectType);

        MethodInfo enumerateSourceSetsMethod = terrain.GetType().GetMethod("EnumerateSourceSets")
            ?? throw new MissingMethodException(terrain.GetType().FullName, "EnumerateSourceSets");
        var seenSourceSets = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (object sourceSet in EnumerateObjects(enumerateSourceSetsMethod.Invoke(terrain, null)!))
        {
            if (!seenSourceSets.Add(sourceSet))
                continue;

            HashSet<Guid> objectIds = EnumerateObjects(GetPropertyValue<object>(sourceSet, "ObjectIds"))
                .Select(static item => (Guid)item)
                .Where(static id => id != Guid.Empty)
                .ToHashSet();
            HashSet<string> layerPaths = EnumerateObjects(GetPropertyValue<object>(sourceSet, "LayerPaths"))
                .Select(static item => (string)item)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            object resolvedObjectsForSet = Activator.CreateInstance(resolvedSourceObjectListType)
                ?? throw new InvalidOperationException($"Could not create {resolvedSourceObjectListType.FullName}.");
            MethodInfo addResolvedObjectMethod = resolvedSourceObjectListType.GetMethod("Add")
                ?? throw new MissingMethodException(resolvedSourceObjectListType.FullName, "Add");

            foreach (object resolvedSourceObject in resolvedSourceObjects)
            {
                Guid objectId = GetPropertyValue<Guid>(resolvedSourceObject, "ObjectId");
                string? layerPath = GetPropertyValue<string?>(resolvedSourceObject, "LayerPath");
                bool matchesObject = objectIds.Count > 0 && objectIds.Contains(objectId);
                bool matchesLayer = layerPaths.Count > 0 && layerPath != null && layerPaths.Contains(layerPath);
                if (!matchesObject && !matchesLayer)
                    continue;

                addResolvedObjectMethod.Invoke(resolvedObjectsForSet, new[] { resolvedSourceObject });
            }

            addSourceObjectsMethod.Invoke(sourceObjectsDictionary, new[] { sourceSet, resolvedObjectsForSet });
            addSourceFingerprintsMethod.Invoke(sourceFingerprintsDictionary, new object[] { sourceSet, 0UL });
        }
    }

    private static IEnumerable<object> EnumerateObjects(object enumerable)
    {
        foreach (object? item in (System.Collections.IEnumerable)enumerable)
        {
            if (item != null)
                yield return item;
        }
    }

    private static T GetPropertyValue<T>(object instance, string propertyName)
    {
        PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(instance.GetType().FullName, propertyName);
        object? value = property.GetValue(instance);
        return value is T typedValue
            ? typedValue
            : throw new InvalidOperationException($"Property {instance.GetType().FullName}.{propertyName} did not contain a {typeof(T).FullName}.");
    }

    private static Type GetRequiredType(Assembly assembly, string fullName)
    {
        return assembly.GetType(fullName, throwOnError: true, ignoreCase: false)
            ?? throw new InvalidOperationException($"Could not load {fullName}.");
    }

    private static string ExtractCaseArchive(string zipPath)
    {
        string extractRoot = Path.Combine(Path.GetTempPath(), "MoleHillCaseReplay");
        Directory.CreateDirectory(extractRoot);
        string extractDirectory = Path.Combine(
            extractRoot,
            Path.GetFileNameWithoutExtension(zipPath) + "-" + Guid.NewGuid().ToString("N")[..8]);
        ZipFile.ExtractToDirectory(zipPath, extractDirectory);

        string[] childDirectories = Directory.GetDirectories(extractDirectory);
        return childDirectories.Length == 1 ? childDirectories[0] : extractDirectory;
    }

    private static void RegisterAssemblyResolver(string pluginDirectory)
    {
        var searchDirectories = new[]
        {
            pluginDirectory,
            RhinoNetCoreDirectory,
            RhinoSystemDirectory
        };

        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
        {
            foreach (string searchDirectory in searchDirectories)
            {
                string dllPath = Path.Combine(searchDirectory, assemblyName.Name + ".dll");
                if (File.Exists(dllPath))
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);

                string rhpPath = Path.Combine(searchDirectory, assemblyName.Name + ".rhp");
                if (File.Exists(rhpPath))
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(rhpPath);
            }

            return null;
        };
    }

    private static void PrependToPath(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        string current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (current.Split(Path.PathSeparator).Contains(directory, StringComparer.OrdinalIgnoreCase))
            return;

        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + current);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
