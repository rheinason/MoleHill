using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Bounded quality-first wall insertion with adaptive patch expansion and verified fallback.
/// </summary>
internal static partial class MeshConstraintTopologyInserter
{
    internal static bool TryInsertQualityWallPatch(double[] vertices, int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints, double tolerance,
        out double[] outputVertices, out int[] outputFaces, out string? message)
    {
        outputVertices = vertices;
        outputFaces = faces;
        message = "Quality patch exceeds the bounded small-terrain budget.";
        if (faces.Length == 0 || faces.Length / 3 > 4096 || constraints.Count == 0 || constraints.Sum(c => c.PointCount) > 512)
            return false;
        var boundary = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faces.Length / 3);
        if (!boundary.HasSingleClosedBoundaryLoop)
        {
            message = "Quality patch requires one manifold terrain perimeter.";
            return false;
        }
        double area = PatchPlanArea(vertices, faces);
        if (!double.IsFinite(area) || area <= 0) return false;
        var originalEdges = PatchEdgeUses(faces);
        var perimeter = originalEdges.Where(e => e.Value == 1).Select(e => e.Key).ToArray();
        try
        {
            // A ring count is a search budget, not an acceptance criterion. Require achieved quality
            // across the stitched output, including its untouched neighbours, before publishing it.
            for (int rings = 0; rings <= 6; rings++)
            {
                if (!TryBuildWallPatchCandidate(vertices, faces, constraints, tolerance, 0,
                        out var candidateVertices, out var candidateFaces, out message, rings)) return false;
                if (candidateFaces.Length / 3 > 25000) break;
                double worst = PatchMinimumAngle(candidateVertices, candidateFaces);
                if (worst < 5) continue;
                if (candidateVertices.Any(v => !double.IsFinite(v))) return false;
                var usedVertices = candidateFaces.ToHashSet();
                if (faces.Any(i => !usedVertices.Contains(i)))
                {
                    message = "Quality patch would discard an existing terrain vertex.";
                    return false;
                }
                var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(candidateFaces, candidateFaces.Length / 3);
                double candidateArea = PatchPlanArea(candidateVertices, candidateFaces);
                if (!topology.HasSingleClosedBoundaryLoop ||
                    !double.IsFinite(candidateArea) || Math.Abs(candidateArea - area) > area * 1e-8)
                {
                    message = "Quality patch failed area or manifold validation.";
                    return false;
                }
                var edges = PatchEdgeUses(candidateFaces);
                double numericalTolerance = Math.Max(1e-8, tolerance * 1e-5);
                foreach (var edge in edges.Where(e => e.Value == 1))
                {
                    var a = PatchPoint(candidateVertices, (int)(edge.Key >> 32));
                    var b = PatchPoint(candidateVertices, (int)edge.Key);
                    if (!perimeter.Any(e => PointOnSegment(a, PatchPoint(vertices, (int)(e >> 32)), PatchPoint(vertices, (int)e), numericalTolerance) &&
                                            PointOnSegment(b, PatchPoint(vertices, (int)(e >> 32)), PatchPoint(vertices, (int)e), numericalTolerance)))
                    {
                        message = "Quality patch introduced an interior boundary.";
                        return false;
                    }
                }
                outputVertices = candidateVertices;
                outputFaces = candidateFaces;
                message = $"Retaining Wall quality patch: {rings} neighbour rings; minimum plan angle {worst:F2} degrees.";
                return true;
            }
            message = "Quality patch did not meet the 5-degree floor within its expansion budget.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            message = $"Quality patch declined: {ex.Message}";
        }
        return false;
    }

    private static Point2D PatchPoint(double[] vertices, int i) => new(vertices[i * 3], vertices[i * 3 + 1]);

    private static Dictionary<long, int> PatchEdgeUses(int[] faces)
    {
        var edges = new Dictionary<long, int>(IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int i = 0; i < faces.Length; i += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = faces[i + e], b = faces[i + (e + 1) % 3];
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        return edges;
    }

    private static double PatchPlanArea(double[] vertices, int[] faces)
    {
        double area = 0;
        for (int i = 0; i < faces.Length; i += 3)
        {
            var a = PatchPoint(vertices, faces[i]); var b = PatchPoint(vertices, faces[i + 1]); var c = PatchPoint(vertices, faces[i + 2]);
            double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (cross <= 0) return double.NaN;
            area += cross / 2;
        }
        return area;
    }

    private static double PatchMinimumAngle(double[] vertices, int[] faces)
    {
        double worst = 180;
        for (int i = 0; i < faces.Length; i += 3)
            for (int e = 0; e < 3; e++)
            {
                var a = PatchPoint(vertices, faces[i + e]);
                var b = PatchPoint(vertices, faces[i + (e + 1) % 3]);
                var c = PatchPoint(vertices, faces[i + (e + 2) % 3]);
                double ux = b.X - a.X, uy = b.Y - a.Y, vx = c.X - a.X, vy = c.Y - a.Y;
                double angle = Math.Atan2(Math.Abs(ux * vy - uy * vx), ux * vx + uy * vy) * 180 / Math.PI;
                if (!double.IsFinite(angle)) return 0;
                worst = Math.Min(worst, angle);
            }
        return worst;
    }
}
