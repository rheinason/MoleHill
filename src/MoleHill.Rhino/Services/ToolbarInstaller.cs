using System.Reflection;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.Services;

internal static class ToolbarInstaller
{
    private const string ToolbarFileName = "MoleHill.Toolbar.rui";

    public static void EnsureInstalled()
    {
        string? sourcePath = GetPackagedToolbarPath();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        string localPath = GetLocalToolbarPath();
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        if (!File.Exists(localPath) || File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(localPath))
            File.Copy(sourcePath, localPath, overwrite: true);

        foreach (var toolbarFile in RhinoApp.ToolbarFiles)
        {
            if (string.Equals(toolbarFile.Path, localPath, StringComparison.OrdinalIgnoreCase))
                return;
        }

        RhinoApp.ToolbarFiles.Open(localPath);
    }

    private static string GetLocalToolbarPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "McNeel", "Rhinoceros", "8.0", "UI", "Plug-ins", "MoleHill", ToolbarFileName);
    }

    private static string? GetPackagedToolbarPath()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(assemblyPath))
            return null;

        string? assemblyDirectory = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            return null;

        return Path.Combine(assemblyDirectory, "Toolbars", ToolbarFileName);
    }
}
