using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static class TerrainCaseBundleExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static TerrainCaseBundleExportResult Export(
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainBuildSnapshot snapshot,
        TerrainDisplayState? displayState)
    {
        string caseRoot = Path.Combine(Path.GetTempPath(), "MoleHillCases");
        Directory.CreateDirectory(caseRoot);

        string caseDirectory = CreateUniqueCaseDirectory(caseRoot, terrain);
        Directory.CreateDirectory(caseDirectory);

        string terrainJsonPath = Path.Combine(caseDirectory, "terrain.json");
        File.WriteAllText(terrainJsonPath, TerrainSerializer.Serialize(new[] { snapshot.Terrain }));

        string buildLogPath = Path.Combine(caseDirectory, "build-log.txt");
        File.WriteAllText(buildLogPath, terrain.LastBuildMessage ?? string.Empty);

        List<ResolvedSourceObject> sourceObjects = CollectSourceObjects(snapshot);
        string? sourceModelFileName = null;
        if (sourceObjects.Count > 0)
        {
            sourceModelFileName = "sources.3dm";
            WriteSourceModel(
                Path.Combine(caseDirectory, sourceModelFileName),
                snapshot.ModelAbsoluteTolerance,
                sourceObjects);
        }

        var outputMeshes = new List<CaseOutputMeshManifest>();
        AddOutputMesh(caseDirectory, outputMeshes, "terrain", displayState?.TerrainMesh);
        AddOutputMesh(caseDirectory, outputMeshes, "base", displayState?.BaseTerrainMesh);
        AddOutputMesh(caseDirectory, outputMeshes, "preview", displayState?.PreviewTerrainMesh);

        TerrainCoreCaseTestExport? coreTestExport = null;
        if (TerrainCoreCaseTestExporter.TryCreate(snapshot, out var generatedCoreTest) &&
            generatedCoreTest != null)
        {
            coreTestExport = generatedCoreTest;
            File.WriteAllText(
                Path.Combine(caseDirectory, coreTestExport.FileName),
                coreTestExport.SourceCode,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        IReadOnlyList<RetainingWallPlannerCaseTestExport> retainingWallPlannerExports =
            RetainingWallPlannerCaseTestExporter.Create(snapshot);
        foreach (RetainingWallPlannerCaseTestExport retainingWallPlannerExport in retainingWallPlannerExports)
        {
            File.WriteAllText(
                Path.Combine(caseDirectory, retainingWallPlannerExport.FileName),
                retainingWallPlannerExport.SourceCode,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var manifest = new TerrainCaseBundleManifest
        {
            FormatVersion = 1,
            ExportedUtc = DateTimeOffset.UtcNow,
            PluginVersion = typeof(TerrainCaseBundleExporter).Assembly.GetName().Version?.ToString(),
            RhinoDocumentName = doc.Name,
            RhinoDocumentPath = doc.Path,
            TerrainId = terrain.TerrainId,
            TerrainName = terrain.Name,
            LastBuildUtc = terrain.LastBuildUtc,
            ModelAbsoluteTolerance = snapshot.ModelAbsoluteTolerance,
            BuildLogFile = Path.GetFileName(buildLogPath),
            TerrainDefinitionFile = Path.GetFileName(terrainJsonPath),
            CoreTestFile = coreTestExport?.FileName,
            RetainingWallPlannerTestFiles = retainingWallPlannerExports.Select(static export => export.FileName).ToList(),
            SourceModelFile = sourceModelFileName,
            SourceObjects = sourceObjects.Select(ToManifest).ToList(),
            SourceSets = snapshot.SourceObjects
                .Select((entry, index) => ToManifest(snapshot, entry.Key, entry.Value, index))
                .ToList(),
            OutputMeshes = outputMeshes
        };

        string manifestPath = Path.Combine(caseDirectory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        File.WriteAllText(Path.Combine(caseDirectory, "README.txt"), BuildReadme(manifest));

        string archivePath = caseDirectory + ".zip";
        if (File.Exists(archivePath))
            File.Delete(archivePath);

        ZipFile.CreateFromDirectory(caseDirectory, archivePath, CompressionLevel.Fastest, includeBaseDirectory: true);
        string? copiedSource = retainingWallPlannerExports.FirstOrDefault()?.SourceCode ?? coreTestExport?.SourceCode;
        return new TerrainCaseBundleExportResult(archivePath, copiedSource);
    }

    private static string CreateUniqueCaseDirectory(string rootDirectory, TerrainDefinition terrain)
    {
        string terrainSlug = SanitizeFileName(terrain.Name);
        string prefix = string.IsNullOrWhiteSpace(terrainSlug) ? "terrain" : terrainSlug;
        string suffix = terrain.TerrainId.ToString("N")[..8];
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string basePath = Path.Combine(rootDirectory, $"{prefix}-{timestamp}-{suffix}");
        string candidate = basePath;
        int counter = 2;
        while (Directory.Exists(candidate) || File.Exists(candidate + ".zip"))
        {
            candidate = basePath + "-" + counter.ToString(CultureInfo.InvariantCulture);
            counter++;
        }

        return candidate;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (char ch in value.Trim())
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0)
                continue;

            builder.Append(char.IsWhiteSpace(ch) ? '-' : ch);
        }

        return builder.ToString().Trim('-');
    }

    private static List<ResolvedSourceObject> CollectSourceObjects(TerrainBuildSnapshot snapshot)
    {
        return snapshot.SourceObjects.Values
            .SelectMany(static objects => objects)
            .GroupBy(static obj => obj.ObjectId)
            .Select(static group => group.First())
            .OrderBy(static obj => obj.LayerPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static obj => obj.ObjectId)
            .ToList();
    }

    private static void WriteSourceModel(
        string path,
        double modelAbsoluteTolerance,
        IReadOnlyList<ResolvedSourceObject> sourceObjects)
    {
        var file = new File3dm();
        file.Settings.ModelAbsoluteTolerance = modelAbsoluteTolerance;

        file.AllLayers.Add(new Layer { Name = "Inputs" });
        int layerIndex = file.AllLayers.Count - 1;
        foreach (ResolvedSourceObject sourceObject in sourceObjects)
        {
            GeometryBase geometry = sourceObject.Geometry.Duplicate();
            if (sourceObject.HasSourceTransform)
                geometry.Transform(sourceObject.SourceTransform);

            var attributes = new ObjectAttributes
            {
                LayerIndex = layerIndex,
                Name = $"{geometry.ObjectType} {sourceObject.ObjectId}"
            };

            attributes.SetUserString("MoleHill.SourceObjectId", sourceObject.ObjectId.ToString());
            attributes.SetUserString("MoleHill.GeometryType", geometry.ObjectType.ToString());
            attributes.SetUserString("MoleHill.GeometryDataCrc", sourceObject.GeometryDataCrc.ToString(CultureInfo.InvariantCulture));
            attributes.SetUserString("MoleHill.HasSourceTransform", sourceObject.HasSourceTransform ? "true" : "false");
            if (!string.IsNullOrWhiteSpace(sourceObject.LayerPath))
                attributes.SetUserString("MoleHill.SourceLayerPath", sourceObject.LayerPath);

            file.Objects.Add(geometry, attributes);
        }

        if (!file.Write(path, 8))
            throw new InvalidOperationException("Could not write the source geometry repro model.");
    }

    private static void AddOutputMesh(
        string caseDirectory,
        ICollection<CaseOutputMeshManifest> manifests,
        string meshKind,
        RhinoMesh? mesh)
    {
        if (mesh == null || mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
            return;

        string fileName = $"output-{meshKind}.obj";
        string filePath = Path.Combine(caseDirectory, fileName);
        WriteMeshObj(filePath, mesh, meshKind);
        manifests.Add(new CaseOutputMeshManifest
        {
            Kind = meshKind,
            File = fileName,
            VertexCount = mesh.Vertices.Count,
            FaceCount = mesh.Faces.Count,
            BoundingBox = ToBoundingBoxArray(mesh.GetBoundingBox(true))
        });
    }

    private static void WriteMeshObj(string path, RhinoMesh mesh, string objectName)
    {
        RhinoMesh exportMesh = mesh.DuplicateMesh();
        exportMesh.Faces.ConvertQuadsToTriangles();

        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine($"o {SanitizeObjName(objectName)}");

        for (int i = 0; i < exportMesh.Vertices.Count; i++)
        {
            Point3f vertex = exportMesh.Vertices[i];
            writer.WriteLine(
                FormattableString.Invariant($"v {vertex.X:R} {vertex.Y:R} {vertex.Z:R}"));
        }

        for (int i = 0; i < exportMesh.Faces.Count; i++)
        {
            MeshFace face = exportMesh.Faces[i];
            writer.WriteLine(
                FormattableString.Invariant($"f {face.A + 1} {face.B + 1} {face.C + 1}"));
        }
    }

    private static string SanitizeObjName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "mesh";

        return string.Concat(value.Trim().Select(static ch => char.IsWhiteSpace(ch) ? '_' : ch));
    }

    private static CaseSourceObjectManifest ToManifest(ResolvedSourceObject sourceObject)
    {
        return new CaseSourceObjectManifest
        {
            ObjectId = sourceObject.ObjectId,
            LayerPath = sourceObject.LayerPath,
            GeometryType = sourceObject.Geometry.ObjectType.ToString(),
            GeometryDataCrc = sourceObject.GeometryDataCrc,
            HasSourceTransform = sourceObject.HasSourceTransform,
            SourceTransform = sourceObject.HasSourceTransform ? ToTransformArray(sourceObject.SourceTransform) : null,
            LocalBoundingBox = ToBoundingBoxArray(sourceObject.LocalBoundingBox),
            WorldBoundingBox = ToBoundingBoxArray(sourceObject.WorldBoundingBox)
        };
    }

    private static CaseSourceSetManifest ToManifest(
        TerrainBuildSnapshot snapshot,
        SourceReferenceSet sourceSet,
        IReadOnlyCollection<ResolvedSourceObject> resolvedObjects,
        int index)
    {
        snapshot.SourceFingerprints.TryGetValue(sourceSet, out ulong fingerprint);
        return new CaseSourceSetManifest
        {
            Index = index,
            Fingerprint = fingerprint.ToString(CultureInfo.InvariantCulture),
            ObjectIds = sourceSet.ObjectIds
                .Where(static id => id != Guid.Empty)
                .Distinct()
                .ToArray(),
            LayerPaths = sourceSet.LayerPaths
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ResolvedObjectCount = resolvedObjects.Count
        };
    }

    private static double[]? ToBoundingBoxArray(BoundingBox boundingBox)
    {
        if (!boundingBox.IsValid)
            return null;

        return new[]
        {
            boundingBox.Min.X,
            boundingBox.Min.Y,
            boundingBox.Min.Z,
            boundingBox.Max.X,
            boundingBox.Max.Y,
            boundingBox.Max.Z
        };
    }

    private static double[] ToTransformArray(Transform transform)
    {
        return new[]
        {
            transform.M00, transform.M01, transform.M02, transform.M03,
            transform.M10, transform.M11, transform.M12, transform.M13,
            transform.M20, transform.M21, transform.M22, transform.M23,
            transform.M30, transform.M31, transform.M32, transform.M33
        };
    }

    private static string BuildReadme(TerrainCaseBundleManifest manifest)
    {
        var lines = new List<string>
        {
            "MoleHill Case Bundle",
            string.Empty,
            "Files:",
            $"- {manifest.TerrainDefinitionFile}: terrain definition snapshot",
            $"- {manifest.BuildLogFile}: current build/status log"
        };

        if (!string.IsNullOrWhiteSpace(manifest.SourceModelFile))
            lines.Add($"- {manifest.SourceModelFile}: resolved input geometry in world space");

        if (!string.IsNullOrWhiteSpace(manifest.CoreTestFile))
            lines.Add($"- {manifest.CoreTestFile}: xUnit core regression test source copied by Copy Case");

        foreach (string retainingWallPlannerTestFile in manifest.RetainingWallPlannerTestFiles)
            lines.Add($"- {retainingWallPlannerTestFile}: xUnit retaining-wall planner regression test source copied by Copy Case");

        foreach (CaseOutputMeshManifest outputMesh in manifest.OutputMeshes)
            lines.Add($"- {outputMesh.File}: exported {outputMesh.Kind} mesh");

        lines.Add("- manifest.json: case metadata and source summaries");
        return string.Join(System.Environment.NewLine, lines);
    }

    private sealed class TerrainCaseBundleManifest
    {
        public int FormatVersion { get; init; }

        public DateTimeOffset ExportedUtc { get; init; }

        public string? PluginVersion { get; init; }

        public string? RhinoDocumentName { get; init; }

        public string? RhinoDocumentPath { get; init; }

        public Guid TerrainId { get; init; }

        public string TerrainName { get; init; } = string.Empty;

        public DateTimeOffset? LastBuildUtc { get; init; }

        public double ModelAbsoluteTolerance { get; init; }

        public string TerrainDefinitionFile { get; init; } = string.Empty;

        public string BuildLogFile { get; init; } = string.Empty;

        public string? CoreTestFile { get; init; }

        public List<string> RetainingWallPlannerTestFiles { get; init; } = new();

        public string? SourceModelFile { get; init; }

        public List<CaseSourceSetManifest> SourceSets { get; init; } = new();

        public List<CaseSourceObjectManifest> SourceObjects { get; init; } = new();

        public List<CaseOutputMeshManifest> OutputMeshes { get; init; } = new();
    }
}

internal sealed record TerrainCaseBundleExportResult(string ArchivePath, string? CoreTestCode);

internal sealed class CaseSourceSetManifest
{
    public int Index { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    public Guid[] ObjectIds { get; init; } = Array.Empty<Guid>();

    public string[] LayerPaths { get; init; } = Array.Empty<string>();

    public int ResolvedObjectCount { get; init; }
}

internal sealed class CaseSourceObjectManifest
{
    public Guid ObjectId { get; init; }

    public string? LayerPath { get; init; }

    public string GeometryType { get; init; } = string.Empty;

    public uint GeometryDataCrc { get; init; }

    public bool HasSourceTransform { get; init; }

    public double[]? SourceTransform { get; init; }

    public double[]? LocalBoundingBox { get; init; }

    public double[]? WorldBoundingBox { get; init; }
}

internal sealed class CaseOutputMeshManifest
{
    public string Kind { get; init; } = string.Empty;

    public string File { get; init; } = string.Empty;

    public int VertexCount { get; init; }

    public int FaceCount { get; init; }

    public double[]? BoundingBox { get; init; }
}
