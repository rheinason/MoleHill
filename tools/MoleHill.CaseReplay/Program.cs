using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace MoleHill.CaseReplay;

/// <summary>
/// Replays a MoleHill case bundle (the "Copy Case" zip) outside an interactive Rhino: Rhino runs in-process
/// and headless (Rhino.Inside), the bundle's sources go into a headless document on their original layers,
/// and the terrain is built through the plugin's own snapshot builder and build service, as the panel would.
/// </summary>
internal static class Program
{
    private const string DefaultPluginDirectory = @"src\MoleHill.Rhino\bin\Release\net7.0";
    private const string RhinoSystemDirectory = @"C:\Program Files\Rhino 8\System";
    private const string RhinoNetCoreDirectory = @"C:\Program Files\Rhino 8\System\netcore";

    private sealed record Options(string CasePath, string PluginDirectory, string? Units, bool Stages);

    [STAThread]
    private static int Main(string[] args)
    {
        Options? options = ParseArguments(args);
        if (options == null)
        {
            Console.Error.WriteLine(
                "Usage: MoleHill.CaseReplay <case-zip-or-dir> [--plugin <dir>] [--units Meters|Millimeters|...] [--stages]\n" +
                "  --units   the source model's unit system; bundles exported before 2026-10-06 do not record it\n" +
                "            and default to Meters\n" +
                "  --stages  also build after each modifier and report the mesh's border loops and non-manifold edges");
            return 1;
        }

        string casePath = Path.GetFullPath(options.CasePath);
        string pluginAssemblyPath = Path.Combine(Path.GetFullPath(options.PluginDirectory), "MoleHill.Rhino.rhp");

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
            PrintCaseInventory(caseDirectory);
            if (!File.Exists(pluginAssemblyPath))
            {
                Console.Error.WriteLine($"Could not find plugin assembly at {pluginAssemblyPath}");
                return 1;
            }

            // Rhino.Inside finds the installed Rhino and loads RhinoCommon and the native core from it.
            RhinoInside.Resolver.Initialize();
            PrependToPath(RhinoSystemDirectory);
            RegisterAssemblyResolver(Path.GetDirectoryName(pluginAssemblyPath)!);
            try
            {
                return RunInsideRhino(pluginAssemblyPath, caseDirectory, options);
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
                Console.Error.WriteLine("Replay failed after the bundle inventory was read.");
                Console.Error.WriteLine(inner.ToString());
                return 2;
            }
        }
        finally
        {
            if (deleteExtractedDirectory && Directory.Exists(caseDirectory))
                Directory.Delete(caseDirectory, recursive: true);
        }
    }

    private static Options? ParseArguments(string[] args)
    {
        string? casePath = null, plugin = null, units = null;
        bool stages = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--plugin" when i + 1 < args.Length:
                    plugin = args[++i];
                    break;
                case "--units" when i + 1 < args.Length:
                    units = args[++i];
                    break;
                case "--stages":
                    stages = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || casePath != null)
                        return null;
                    casePath = args[i];
                    break;
            }
        }

        return casePath == null ? null : new Options(casePath, plugin ?? DefaultPluginDirectory, units, stages);
    }

    // Kept out of Main so RhinoCommon is only loaded after the resolver and PATH are in place.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunInsideRhino(string pluginAssemblyPath, string caseDirectory, Options options)
    {
        using var core = new Rhino.Runtime.InProcess.RhinoCore(
            new[] { "/netcore", "/nosplash" },
            Rhino.Runtime.InProcess.WindowStyle.NoWindow);
        Assembly plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginAssemblyPath);
        CaseReplayer.Run(plugin, caseDirectory, options.Units, options.Stages);
        return 0;
    }

    private static void PrintCaseInventory(string caseDirectory)
    {
        Console.WriteLine($"Case bundle: {caseDirectory}");

        string manifestJsonPath = Path.Combine(caseDirectory, "manifest.json");
        if (!File.Exists(manifestJsonPath))
        {
            Console.WriteLine("Manifest: missing");
            return;
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestJsonPath));
        JsonElement root = manifest.RootElement;
        Console.WriteLine($"Terrain: {ReadString(root, "terrainName") ?? "(unknown)"} ({ReadString(root, "terrainId") ?? "no id"})");
        Console.WriteLine($"Exported UTC: {ReadString(root, "exportedUtc") ?? "(unknown)"}");
        Console.WriteLine($"Plugin version: {ReadString(root, "pluginVersion") ?? "(unknown)"}");
        Console.WriteLine($"Rhino document: {ReadString(root, "rhinoDocumentName") ?? "(unknown)"}");
        Console.WriteLine($"Model units: {ReadString(root, "modelUnitSystem") ?? "(not recorded)"}");
        if (root.TryGetProperty("modelAbsoluteTolerance", out JsonElement toleranceElement) &&
            toleranceElement.TryGetDouble(out double tolerance))
        {
            Console.WriteLine($"Model absolute tolerance: {tolerance:G17}");
        }

        WriteManifestFileStatus(caseDirectory, root, "terrainDefinitionFile", "Terrain definition");
        WriteManifestFileStatus(caseDirectory, root, "buildLogFile", "Build log");
        WriteManifestFileStatus(caseDirectory, root, "sourceModelFile", "Source model");
        WriteManifestFileStatus(caseDirectory, root, "coreTestFile", "Core copied case");

        if (root.TryGetProperty("retainingWallPlannerTestFiles", out JsonElement plannerFiles) &&
            plannerFiles.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement fileElement in plannerFiles.EnumerateArray())
            {
                string? fileName = fileElement.GetString();
                if (!string.IsNullOrWhiteSpace(fileName))
                    WriteFileStatus(caseDirectory, fileName, "Retaining-wall copied case");
            }
        }

        foreach (string objPath in Directory.GetFiles(caseDirectory, "output-*.obj").OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            CountObjMesh(objPath, out int vertexCount, out int faceCount);
            Console.WriteLine($"{Path.GetFileName(objPath)}: {vertexCount:N0} verts / {faceCount:N0} faces");
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void WriteManifestFileStatus(string caseDirectory, JsonElement root, string propertyName, string label)
    {
        string? fileName = ReadString(root, propertyName);
        if (!string.IsNullOrWhiteSpace(fileName))
            WriteFileStatus(caseDirectory, fileName, label);
    }

    private static void WriteFileStatus(string caseDirectory, string fileName, string label)
    {
        string path = Path.Combine(caseDirectory, fileName);
        Console.WriteLine(File.Exists(path)
            ? $"{label}: {fileName} ({new FileInfo(path).Length:N0} bytes)"
            : $"{label}: {fileName} (missing)");
    }

    private static void CountObjMesh(string objPath, out int vertexCount, out int faceCount)
    {
        vertexCount = 0;
        faceCount = 0;
        foreach (string line in File.ReadLines(objPath))
        {
            if (line.StartsWith("v ", StringComparison.Ordinal))
                vertexCount++;
            else if (line.StartsWith("f ", StringComparison.Ordinal))
                faceCount++;
        }
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
        var searchDirectories = new[] { pluginDirectory, RhinoNetCoreDirectory, RhinoSystemDirectory };
        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
        {
            foreach (string searchDirectory in searchDirectories)
            {
                foreach (string extension in new[] { ".dll", ".rhp" })
                {
                    string path = Path.Combine(searchDirectory, assemblyName.Name + extension);
                    if (File.Exists(path))
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                }
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
}
