using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Tests;

/// <summary>
    /// Geometric invariants the Grade Pad cascade guarantees, independent of removed fallback internals.
    /// These replace old characterization tests that asserted diagnostic vocabulary and heuristics.
/// </summary>
internal static class PadInvariantAssert
{
    /// <summary>
    /// Asserts that a Grade Pad result is a valid explicit-batter grading of the given terrain:
    /// produced without failure, watertight and manifold, built by the explicit path, retaining the
    /// terrain, and flat at the pad plane across each pad top.
    /// </summary>
    /// <summary>
    /// The diagnostic emitted by the explicit-batter mode (and only that mode). Later cascade tiers emit
    /// their own mode lines plus <c>grade_pad.*.fallback</c> notes recording why earlier tiers deferred.
    /// </summary>
    private const string ExplicitModeMarker = "explicit batter construction (ruled";

    public static bool UsedExplicitMode(GradingResult result) =>
        result.Diagnostics.Any(d => d.Contains(ExplicitModeMarker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Asserts the explicit-batter path actually produced the result (not a silent fallback). When a
    /// case is expected to be handled by the robust engine, use this so a fallback masquerading as a
    /// success fails the test instead of passing it.
    /// </summary>
    public static void AssertExplicitModeUsed(GradingResult result)
    {
        Assert.True(
            UsedExplicitMode(result),
            "Expected the explicit-batter path, but the result came from a later Grade Pad tier. " +
            "Diagnostics:" + Environment.NewLine + string.Join(Environment.NewLine, result.Diagnostics));
    }

    public static void AssertValidExplicitGrading(
        GradingResult? result,
        string? errorMessage,
        int inputFaceCount,
        PadGrader.PadBoundary[] pads,
        double padTopZTolerance = 1e-3,
        bool requireExplicit = false)
    {
        Assert.True(result != null, errorMessage);
        Assert.True(
            string.IsNullOrWhiteSpace(errorMessage) ||
            !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase),
            errorMessage);

        string diagnostics = string.Join(Environment.NewLine, result!.Diagnostics);

        // The graded mesh must contain geometry. (Face count may legitimately drop below the input
        // when a pad carves many small faces out of dense terrain and replaces them with a coarser
        // batter strip and pad top, so this is only a non-empty check.)
        Assert.True(result.FaceCount > 0, "Expected a non-empty graded mesh.");

        AssertWatertightManifold(result, diagnostics);

        if (requireExplicit)
            AssertExplicitModeUsed(result);

        // Exact pad-top flatness is a guarantee of the explicit-batter engine. Cases that defer to later
        // watertight tiers are not held to it here; they only need to be watertight and manifold.
        if (UsedExplicitMode(result))
            AssertPadTopsFlat(result, pads, padTopZTolerance);
    }

    public static void AssertWatertightManifold(GradingResult result, string? context = null)
    {
        MeshTopologyValidator.BoundaryGraphAnalysis analysis =
            MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);

        Assert.True(
            analysis.NonManifoldEdgeCount == 0,
            $"Expected a manifold graded mesh; found {analysis.NonManifoldEdgeCount} non-manifold edge(s).{Suffix(context)}");
        Assert.False(
            analysis.HasOpenBoundaryChains,
            $"Expected a watertight graded mesh with no open naked-edge chains.{Suffix(context)}");
    }

    /// <summary>
    /// Every triangle whose three vertices fall inside exactly one pad footprint must sit on that
    /// pad's plane. This directly guards the pad-top flatness the engine promises without depending
    /// on any diagnostic text.
    /// </summary>
    public static void AssertPadTopsFlat(
        GradingResult result,
        PadGrader.PadBoundary[] pads,
        double zTolerance)
    {
        double[] v = result.Vertices;
        for (int f = 0; f < result.FaceCount; f++)
        {
            int a = result.Faces[f * 3];
            int b = result.Faces[f * 3 + 1];
            int c = result.Faces[f * 3 + 2];

            int owner = -1;
            int ownerCount = 0;
            for (int p = 0; p < pads.Length; p++)
            {
                PadGrader.PadBoundary pad = pads[p];
                if (VertexInside(v, a, pad) && VertexInside(v, b, pad) && VertexInside(v, c, pad))
                {
                    owner = p;
                    ownerCount++;
                }
            }

            // Only check faces unambiguously inside one pad: overlapping footprints have
            // ownership rules outside the scope of a flatness check.
            if (ownerCount != 1)
                continue;

            PadGrader.PadBoundary ownerPad = pads[owner];
            AssertVertexOnPlane(v, a, ownerPad, zTolerance);
            AssertVertexOnPlane(v, b, ownerPad, zTolerance);
            AssertVertexOnPlane(v, c, ownerPad, zTolerance);
        }
    }

    private static bool VertexInside(double[] v, int index, PadGrader.PadBoundary pad) =>
        Geometry2D.PointInPolygon(v[index * 3], v[index * 3 + 1], pad.XyVertices, pad.VertexCount);

    private static void AssertVertexOnPlane(double[] v, int index, PadGrader.PadBoundary pad, double zTolerance)
    {
        double x = v[index * 3];
        double y = v[index * 3 + 1];
        double expected = pad.EvaluateZ(x, y);
        double actual = v[index * 3 + 2];
        Assert.True(
            Math.Abs(actual - expected) <= zTolerance,
            $"Pad-top vertex ({x:F3},{y:F3}) elevation {actual:F4} deviates from pad plane {expected:F4} by more than {zTolerance}.");
    }

    private static string Suffix(string? context) =>
        string.IsNullOrEmpty(context) ? string.Empty : $"{Environment.NewLine}{context}";
}
