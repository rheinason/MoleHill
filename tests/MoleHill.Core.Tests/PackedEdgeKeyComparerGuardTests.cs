using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// R02 guard. A <c>Dictionary</c>/<c>HashSet</c> keyed by a packed edge key (<c>(min &lt;&lt; 32) | max</c>)
/// MUST be constructed with <c>IndexedMeshTools.EdgeKeyComparer.Instance</c> (or the <c>ulong</c>
/// counterpart <c>PackedKeyComparer</c>). The default <c>long</c>/<c>ulong</c> hash is <c>lo ^ hi</c>,
/// which for adjacent mesh indices collapses nearly every edge into a handful of buckets and turns an
/// O(n) pass into a quadratic scan — it once cost 7 s of a 10 s remesh on a 180k-face terrain.
///
/// The convention is invisible at the call site, so this scans the shipped sources instead of trusting
/// review. Spatial-cell keys use a different encoding (already-mixed or coordinate-derived) and are
/// deliberately exempt; each exemption is named below with its reason, so adding a long-keyed
/// collection is a decision rather than an omission.
/// </summary>
public class PackedEdgeKeyComparerGuardTests
{
    /// <summary>
    /// file (repo-relative, forward slashes) → identifiers whose long/ulong key is a spatial-cell key
    /// or another already-mixed hash, not a packed edge key. "(inline)" covers a construction passed
    /// directly as an argument, with no variable to name.
    /// </summary>
    private static readonly Dictionary<string, string[]> CellKeyedExemptions = new()
    {
        ["src/MoleHill.Core/Analysis/MeshHeightProjector.cs"] = new[] { "_cellSlots" },
        ["src/MoleHill.Core/Engine/LocalMeshRefiner.cs"] = new[] { "_cells" },
        ["src/MoleHill.Core/Engine/SpatialHashGrid2D.cs"] = new[] { "cellSlots", "(inline)" },
        ["src/MoleHill.Core/Engine/SurfaceRemesher.cs"] = new[] { "_cells", "cells", "grid" },
        ["src/MoleHill.Core/Engine/TinBoundaryPreparer.cs"] = new[] { "_cells" },
        ["src/MoleHill.Core/Grading/MeshAreaTopologySplitter.cs"] = new[] { "_cells" },
        ["src/MoleHill.Core/Grading/MeshConstraintTopologyInserter.cs"] = new[] { "_cells" },
        ["src/MoleHill.Core/Grading/SpatialVertexHash.cs"] = new[] { "_grid" },
        ["src/MoleHill.Core/Grading/TerrainFaceGrid.cs"] = new[] { "_cellSlots" },
        ["src/MoleHill.Core/Retopo/CrossFieldSolver.cs"] = new[] { "_cells" },
        ["src/MoleHill.Shared/RetainingWallPlannerCore.cs"] = new[] { "cells" },
        // Cell key is pre-mixed by the caller ((cx * 73856093) ^ (cy * 19349663)), so the default hash
        // is not the collapsing one.
        ["src/MoleHill.Rhino/Services/TerrainBuildService.Tin.cs"] = new[] { "used" },
    };

    private static readonly Regex Construction = new(
        @"new\s+(?:HashSet|Dictionary)\s*<\s*u?long\s*[,>]",
        RegexOptions.Compiled);

    private static readonly Regex TargetTypedConstruction = new(
        @"(?:HashSet|Dictionary)\s*<\s*u?long\s*[,>][^;=()]*?=\s*new\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void LongKeyedCollections_UseTheEdgeKeyComparerOrAreDeclaredCellKeyed()
    {
        string root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (string file in EnumerateSourceFiles(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string text = File.ReadAllText(file);

            foreach (Match match in Construction.Matches(text))
                Inspect(relative, text, match.Index, offenders);

            foreach (Match match in TargetTypedConstruction.Matches(text))
                Inspect(relative, text, match.Index + match.Value.LastIndexOf("new", StringComparison.Ordinal), offenders);
        }

        Assert.True(
            offenders.Count == 0,
            "Packed-key collections constructed with the default comparer. Either pass " +
            "IndexedMeshTools.EdgeKeyComparer.Instance / PackedKeyComparer.Instance (or use " +
            "IndexedMeshTools.CreateEdgeKeySet/CreateEdgeKeyMap), or — if the key is a spatial-cell " +
            "key — add it to CellKeyedExemptions with its reason:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Guard_SeesTheSourcesItClaimsToScan()
    {
        // A scan that silently matches nothing would pass forever. Anchor it on a site that must exist.
        string root = FindRepositoryRoot();
        List<string> files = EnumerateSourceFiles(root).ToList();

        Assert.Contains(files, file => file.EndsWith("IndexedMeshTools.cs", StringComparison.Ordinal));
        Assert.True(files.Count > 100, $"Expected the whole src tree, scanned only {files.Count} files.");
        Assert.DoesNotContain(files, file => file.Replace('\\', '/').Contains("/TriangleNet/", StringComparison.Ordinal));
    }

    private static void Inspect(string relative, string text, int newKeyword, List<string> offenders)
    {
        int open = FindConstructorParen(text, newKeyword);
        if (open < 0)
            return;

        string arguments = ReadBalanced(text, open);
        if (arguments.Contains("Comparer.Instance", StringComparison.Ordinal))
            return;

        string identifier = ReadAssignedIdentifier(text, newKeyword);
        if (CellKeyedExemptions.TryGetValue(relative, out string[]? exempt) &&
            exempt.Contains(identifier, StringComparer.Ordinal))
        {
            return;
        }

        int line = text.Take(newKeyword).Count(c => c == '\n') + 1;
        offenders.Add($"  {relative}:{line} — '{identifier}'");
    }

    /// <summary>
    /// The '(' opening the constructor's argument list, skipping the type argument list: the value type
    /// of an edge map is routinely a tuple, whose parens would otherwise be read as the arguments.
    /// </summary>
    private static int FindConstructorParen(string text, int newKeyword)
    {
        int angleDepth = 0;
        for (int i = newKeyword + 3; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<')
                angleDepth++;
            else if (c == '>')
                angleDepth--;
            else if (c == '(' && angleDepth == 0)
                return i;
            else if (c == ';' || c == '{')
                return -1;
        }

        return -1;
    }

    /// <summary>Text between <paramref name="open"/> and its matching close paren.</summary>
    private static string ReadBalanced(string text, int open)
    {
        int depth = 0;
        var builder = new StringBuilder();
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(')
                depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                    break;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The identifier being assigned, scanning left from the construction: the name after the last
    /// '=' on the statement. Returns "(inline)" when the construction is an argument, not an assignment.
    /// </summary>
    private static string ReadAssignedIdentifier(string text, int start)
    {
        int i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
            i--;

        if (i < 0 || text[i] != '=')
            return "(inline)";

        i--;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
            i--;

        int end = i;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i--;

        return end <= i ? "(inline)" : text.Substring(i + 1, end - i);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MoleHill.sln")))
            directory = directory.Parent;

        Assert.True(directory != null, $"MoleHill.sln not found above {AppContext.BaseDirectory}.");
        return directory!.FullName;
    }
}
