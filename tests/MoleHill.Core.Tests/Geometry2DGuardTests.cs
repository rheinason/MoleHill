using System.Text.RegularExpressions;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The 2D primitives live once, in <c>MoleHill.Core.Geometry.Geometry2D</c>. Before 2026-10 they were
/// redefined privately in over a dozen files (eleven copies of <c>Cross</c> alone), and three inclusive
/// segment tests and one strict one shared the name <c>SegmentsIntersect</c>. This fails on a new private
/// copy in Core or the Rhino host - the assemblies that can see Core's internals - unless it only forwards
/// to <c>Geometry2D</c> or is listed below with the reason its meaning differs.
/// </summary>
public class Geometry2DGuardTests
{
    private static readonly Regex Declaration = new(
        @"static\s+(?:double|bool|Point2D)\s+(Cross|Orient|Orientation|Lerp|SignedArea|SegmentsIntersect|OnSegment|DistanceSquared|DistanceSquaredXY|PointInPolygon|PointInTriangle)\s*\(",
        RegexOptions.Compiled);

    /// <summary>file (repo-relative) → primitive names it may define, each with why it is not the shared one.</summary>
    private static readonly Dictionary<string, string[]> Exemptions = new()
    {
        // The home.
        ["src/MoleHill.Core/Geometry/Geometry2D.cs"] = new[] { "*" },

        // Point2D overloads of the face-cut kernel's own value type.
        ["src/MoleHill.Core/Grading/FaceCut/FaceCutGeometry.cs"] = new[] { "Lerp", "DistanceSquared" },

        // Barycentric, and false for a degenerate triangle; Geometry2D.PointInTriangleInclusive is
        // orientation-based with a 1e-12 tolerance and accepts the boundary of a degenerate one.
        ["src/MoleHill.Core/Engine/SurfaceRemesher.cs"] = new[] { "PointInTriangle" },

        // Zero-tolerance sign test; switching it to the 1e-12 tolerance would change which faces a
        // near-boundary point lands in during topology repair.
        ["src/MoleHill.Core/Grading/MeshTopologyOperations.cs"] = new[] { "PointInTriangle" },

        // RhinoCommon Point3d, 3D.
        ["src/MoleHill.Rhino/Services/Commands/GeometryCommandAlgorithms.cs"] = new[] { "DistanceSquared" },
    };

    [Fact]
    public void PlanGeometryPrimitives_AreNotRedefinedOutsideGeometry2D()
    {
        string root = RepositoryPaths.FindRoot();
        var offenders = new List<string>();

        foreach (string file in RepositoryPaths.EnumerateShippedSources(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!relative.StartsWith("src/MoleHill.Core/", StringComparison.Ordinal) &&
                !relative.StartsWith("src/MoleHill.Rhino/", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (Match match in Declaration.Matches(text))
            {
                string name = match.Groups[1].Value;
                if (Exemptions.TryGetValue(relative, out string[]? allowed) &&
                    (allowed.Contains("*") || allowed.Contains(name)))
                {
                    continue;
                }

                if (IsForwarder(text, match.Index + match.Length))
                    continue;

                int line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{relative}:{line} {name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Private copies of Geometry2D primitives. Call Geometry2D, or exempt with a reason:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>True when the declaration's body is a single call into Geometry2D.</summary>
    private static bool IsForwarder(string text, int afterParameterListStart)
    {
        int end = text.IndexOfAny(new[] { ';', '{' }, afterParameterListStart);
        if (end < 0)
            return false;

        int close = text.IndexOf(';', end);
        string body = text.Substring(afterParameterListStart, (close < 0 ? text.Length : close) - afterParameterListStart);
        return body.Contains("Geometry2D.", StringComparison.Ordinal) && body.Count(c => c == ';') <= 1;
    }
}
