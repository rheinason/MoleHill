using System.Reflection;
using System.Security.Cryptography;
using Rhino;

namespace MoleHill.Rhino.Services;

internal static class ToolbarInstaller
{
    private const string ToolbarBaseName = "MoleHill.Toolbar";

    public static void EnsureInstalled()
    {
        string? sourcePath = GetPackagedToolbarPath();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        string localDir = GetLocalToolbarDirectory();
        Directory.CreateDirectory(localDir);

        // Stamp the local file name with a hash of the packaged toolbar's content. Rhino never
        // reloads an already-open .rui just because its bytes changed, and it rewrites its cached
        // copy back to disk on shutdown — so a fixed file name means toolbar updates never take
        // effect. Changing the file name whenever the content changes forces Rhino to load it fresh.
        string stamp = ComputeContentHash(sourcePath);
        string localPath = Path.Combine(localDir, $"{ToolbarBaseName}-{stamp}.rui");

        bool alreadyCurrent = false;
        foreach (var toolbarFile in RhinoApp.ToolbarFiles.ToList())
        {
            if (!IsManagedToolbar(toolbarFile.Path, localDir))
                continue;

            if (string.Equals(toolbarFile.Path, localPath, StringComparison.OrdinalIgnoreCase))
                alreadyCurrent = true;
            else
                toolbarFile.Close(prompt: false); // drop stale MoleHill toolbar versions so old buttons disappear
        }

        if (!alreadyCurrent)
        {
            if (!File.Exists(localPath))
                File.Copy(sourcePath, localPath, overwrite: true);

            RhinoApp.ToolbarFiles.Open(localPath);
        }

        CleanUpOldToolbarFiles(localDir, localPath);
    }

    private static bool IsManagedToolbar(string? path, string localDir)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string? directory = Path.GetDirectoryName(path);
        return string.Equals(directory, localDir, StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(path).StartsWith(ToolbarBaseName, StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanUpOldToolbarFiles(string localDir, string keepPath)
    {
        foreach (string file in Directory.EnumerateFiles(localDir, $"{ToolbarBaseName}*.rui"))
        {
            if (string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(file);
            }
            catch
            {
                // A previous version's file may still be locked by Rhino; leave it for next launch.
            }
        }
    }

    private static string ComputeContentHash(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string GetLocalToolbarDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "McNeel", "Rhinoceros", "8.0", "UI", "Plug-ins", "MoleHill");
    }

    private static string? GetPackagedToolbarPath()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return null;

        string? assemblyDirectory = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            return null;

        return Path.Combine(assemblyDirectory, "Toolbars", $"{ToolbarBaseName}.rui");
    }
}
