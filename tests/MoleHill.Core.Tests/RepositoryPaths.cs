using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Locates the checkout from a running test, so no test names a developer's absolute path, and lists
/// the shipped sources the convention guards scan.
/// </summary>
internal static class RepositoryPaths
{
    /// <summary>The directory holding <c>MoleHill.sln</c>, found by walking up from the test binaries.</summary>
    public static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MoleHill.sln")))
            directory = directory.Parent;

        Assert.True(directory != null, $"MoleHill.sln not found above {AppContext.BaseDirectory}.");
        return directory!.FullName;
    }

    /// <summary>
    /// Every MoleHill <c>*.cs</c> under <c>src/</c>: the vendored TriangleNet and build output are skipped.
    /// </summary>
    public static IEnumerable<string> EnumerateShippedSources(string root)
    {
        string src = Path.Combine(root, "src");
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/TriangleNet/", StringComparison.Ordinal) ||
                normalized.Contains("/bin/", StringComparison.Ordinal) ||
                normalized.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }
}
