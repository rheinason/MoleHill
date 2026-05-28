using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    internal static bool TryBuildBoundaryLoop(double[] vertices, int[] faces, int faceCount, out double[] boundaryXy, out int boundaryVertexCount)
    {
        return MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(vertices, faces, faceCount, out boundaryXy, out boundaryVertexCount);
    }

    private static void AddBoundaryNeighbor(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out var list))
        {
            list = new List<int>(2);
            adjacency[from] = list;
        }

        list.Add(to);
    }

    internal static bool AllPointsInsideOrOnBoundary(double[] xy, int vertexCount, double[] boundaryLoop, int boundaryVertexCount, double tolerance)
    {
        return GradingGeometry2D.AllPointsInsideOrOnBoundary(xy, vertexCount, boundaryLoop, boundaryVertexCount, tolerance);
    }

    private static bool TryIntersectLines(
        double ax, double ay, double adx, double ady,
        double bx, double by, double bdx, double bdy,
        out double ix, out double iy)
    {
        double denom = adx * bdy - ady * bdx;
        if (Math.Abs(denom) < 1e-12)
        {
            ix = 0;
            iy = 0;
            return false;
        }

        double t = ((bx - ax) * bdy - (by - ay) * bdx) / denom;
        ix = ax + t * adx;
        iy = ay + t * ady;
        return true;
    }

    internal static double DistToBoundaryWithZ(
        double px,
        double py,
        double[] boundaryVertices,
        int boundaryVertexCount,
        out double boundaryZ,
        out double closestBx,
        out double closestBy)
    {
        boundaryZ = 0;
        closestBx = px;
        closestBy = py;
        double minDist = double.MaxValue;

        for (int i = 0; i < boundaryVertexCount; i++)
        {
            int next = (i + 1) % boundaryVertexCount;
            double ax = boundaryVertices[i * 3];
            double ay = boundaryVertices[i * 3 + 1];
            double az = boundaryVertices[i * 3 + 2];
            double bx = boundaryVertices[next * 3];
            double by = boundaryVertices[next * 3 + 1];
            double bz = boundaryVertices[next * 3 + 2];

            double dx = bx - ax;
            double dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            double t = 0;
            double cx = ax;
            double cy = ay;
            if (lenSq > 1e-20)
            {
                t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
                cx = ax + t * dx;
                cy = ay + t * dy;
            }

            double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            if (dist >= minDist)
                continue;

            minDist = dist;
            boundaryZ = az + (bz - az) * t;
            closestBx = cx;
            closestBy = cy;
        }

        return minDist;
    }

    private static GradingResult BuildResult(
        double[] originalVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] gradedVertices,
        IReadOnlyList<OutputPolyline>? outputPolylines = null,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<GradingPatch>? patchSummaries = null)
    {
        return GradingResultBuilder.BuildFromXyz(
            originalVertices,
            gradedVertices,
            vertexCount,
            faces,
            faceCount,
            outputPolylines,
            diagnostics,
            patchSummaries,
            BuildStructuredPadDiagnostics(diagnostics));
    }

    private static IReadOnlyList<GradingDiagnostic>? BuildStructuredPadDiagnostics(IReadOnlyList<string>? diagnostics)
    {
        if (diagnostics == null || diagnostics.Count == 0)
            return null;

        var structured = new GradingDiagnostic[diagnostics.Count];
        for (int i = 0; i < diagnostics.Count; i++)
        {
            string message = diagnostics[i];
            structured[i] = new GradingDiagnostic(
                ClassifyPadDiagnosticSeverity(message),
                ClassifyPadDiagnosticCode(message),
                message,
                Operation: "grade_pad",
                TargetIndex: TryExtractPadDiagnosticIndex(message, out int padIndex) ? padIndex : null);
        }

        return structured;
    }

    private static GradingDiagnosticSeverity ClassifyPadDiagnosticSeverity(string message)
    {
        if (message.Contains("topology summary", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Contains("topology healthy=False", StringComparison.OrdinalIgnoreCase))
                return GradingDiagnosticSeverity.Warning;
            if (message.Contains("topology healthy=True", StringComparison.OrdinalIgnoreCase))
                return GradingDiagnosticSeverity.Information;
        }

        return message.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("skipped", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("collapsed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("clipped", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("no measurable", StringComparison.OrdinalIgnoreCase)
            ? GradingDiagnosticSeverity.Warning
            : GradingDiagnosticSeverity.Information;
    }

    private static string ClassifyPadDiagnosticCode(string message)
    {
        if (message.Contains("batter slope warning", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.slope.deviation";
        if (message.Contains("batter slope check", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.slope.check";
        if (message.Contains("topology summary", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.topology.summary";
        if (message.Contains("seam vertices", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.seam_vertices";
        if (message.Contains("seam deviation", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.seam_deviation";
        if (message.Contains("topology band width", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.band_width";
        if (message.Contains("patch boundary edges near seam", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.patch_boundary_edges";
        if (message.Contains("outside-mesh naked edges near seam", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.outside_boundary_edges";
        if (message.Contains("seam segment matches", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.segment_matches";
        if (message.Contains("seam-near boundary segments", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.near_boundary_segments";
        if (message.Contains("protected stitch apron", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.apron";
        if (message.Contains("terrain-side stitch loop", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.terrain_side_loop";
        if (message.Contains("merged-mesh naked edges near seam", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.merged_boundary_edges";
        if (message.Contains("daylight seam reached", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.daylight.clipped_to_terrain";
        if (message.Contains("split local patch", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.patch.split_local";
        if (message.Contains("corner constraints", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.patch.corner_constraints";
        if (message.Contains("coupled protected patch", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.coupled_patch";
        if (message.Contains("stitch", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("seam", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch";

        return "grade_pad.diagnostic";
    }

    private static bool TryExtractPadDiagnosticIndex(string message, out int padIndex)
    {
        padIndex = 0;
        const string prefix = "Grade Pad[";
        int start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return false;

        start += prefix.Length;
        int end = message.IndexOf(']', start);
        return end > start &&
               int.TryParse(message.AsSpan(start, end - start), out padIndex);
    }
}
