using System.Text.RegularExpressions;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

/// <summary>
/// Every Grasshopper component names an embedded icon that exists. Five components shipped with
/// <c>Icon =&gt; null</c> and showed Grasshopper's blank placeholder; a scan of the sources is the only
/// check that needs no Grasshopper runtime.
/// </summary>
public class ComponentIconGuardTests
{
    private static readonly Regex IconProperty = new(@"override\s+(?:System\.Drawing\.)?Bitmap\?\s+Icon\s*=>\s*(?<body>[^;]+);", RegexOptions.Singleline);
    private static readonly Regex ResourceName = new("\"(?<name>[^\"]+\\.png)\"");

    [Theory]
    [InlineData("src/MoleHill.Grasshopper/Components", "src/MoleHill.Grasshopper/Resources", "MoleHill.Grasshopper.Resources.")]
    public void EveryComponentIcon_NamesAnExistingResource(string componentDirectory, string resourceDirectory, string resourcePrefix)
    {
        string root = FindRepositoryRoot();
        var failures = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(root, componentDirectory), "*.cs"))
        {
            string source = File.ReadAllText(file);
            if (!source.Contains("ComponentGuid", StringComparison.Ordinal))
                continue;

            Match icon = IconProperty.Match(source);
            Match resource = ResourceName.Match(icon.Success ? icon.Groups["body"].Value : string.Empty);
            if (!resource.Success)
            {
                failures.Add($"{Path.GetFileName(file)}: no embedded icon.");
                continue;
            }

            string name = resource.Groups["name"].Value;
            string fileName = name.StartsWith(resourcePrefix, StringComparison.Ordinal) ? name[resourcePrefix.Length..] : name;
            if (!File.Exists(Path.Combine(root, resourceDirectory, fileName)))
                failures.Add($"{Path.GetFileName(file)}: icon '{name}' is not in {resourceDirectory}.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MoleHill.sln")))
            directory = directory.Parent;
        Assert.True(directory != null, $"MoleHill.sln not found above {AppContext.BaseDirectory}.");
        return directory!.FullName;
    }
}
