using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Processing;

/// <summary>Inserts measured breaklines into the contouring reference before recovering its contours.</summary>
public static partial class TerrainCurveRecovery
{
    /// <summary>
    /// Re-triangulates the reference's samples and original border with the measured rails as fixed
    /// edges. Rail heights take precedence over a coincident coarse sample; this retains the actual
    /// step instead of asking recovery to match both a smoothed step and its measured sharp edge.
    /// Returns null on a failed triangulation, leaving the original reference untouched.
    /// </summary>
    public static IndexedTriMesh? ConstrainReference(
        IndexedTriMesh terrain, IReadOnlyList<double[]> breaklines, double planTolerance, out string? error)
    {
        ArgumentNullException.ThrowIfNull(breaklines);
        ValidateBreaklines(breaklines);
        if (!double.IsFinite(planTolerance) || planTolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(planTolerance));
        if (breaklines.Count == 0)
        {
            error = null;
            return terrain;
        }
        if (!MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(terrain.Faces, terrain.FaceCount, out List<int[]> loops))
        {
            error = "The reference mesh must have simple border loops before inserting breaklines.";
            return null;
        }
        var borders = new List<double[]>();
        foreach (int[] loop in loops)
        {
            var points = new double[loop.Length * 3];
            for (int i = 0; i < loop.Length; i++)
                Array.Copy(terrain.Vertices, loop[i] * 3, points, i * 3, 3);
            borders.Add(points);
        }
        var railXy = new List<double>();
        var railSegments = new List<int>();
        foreach (double[] rail in breaklines)
        {
            int start = railXy.Count / 2;
            for (int i = 0; i < rail.Length; i += 3)
            {
                railXy.Add(rail[i]);
                railXy.Add(rail[i + 1]);
                if (i > 0)
                {
                    railSegments.Add(start + i / 3 - 1);
                    railSegments.Add(start + i / 3);
                }
            }
        }
        SegmentProximityIndex? railIndex = SegmentProximityIndex.TryCreateForSegments(
            railXy.ToArray(), railSegments.ToArray(), railSegments.Count / 2);
        var query = new SegmentProximityIndex.QueryState(railSegments.Count / 2);
        double magnitude = terrain.Vertices.Take(terrain.VertexCount * 3).Select(Math.Abs).DefaultIfEmpty(1).Max();
        double railClearance = Math.Max(planTolerance * 0.01, magnitude * 1e-9);
        var samples = new List<double>(terrain.VertexCount * 3);
        for (int v = 0; v < terrain.VertexCount; v++)
        {
            double x = terrain.Vertices[v * 3], y = terrain.Vertices[v * 3 + 1];
            // A coarse average sitting anywhere ON a measured segment is not a second height for
            // that segment. The triangulator would split the rail through that sample and make a
            // sawtooth wall, even though the supplied rail vertices themselves were correct.
            if (railIndex != null && railIndex.NearestDistance(x, y, query) <= railClearance)
                continue;
            samples.Add(x);
            samples.Add(y);
            samples.Add(terrain.Vertices[v * 3 + 2]);
        }
        TinResult? result = Rebuild(borders, new List<double[]>(), new List<RecoveredContour>(),
            samples, planTolerance, out error, breaklines);
        return result == null ? null : new IndexedTriMesh(result.Vertices, result.VertexCount, result.Faces, result.FaceCount);
    }

    private static void ValidateBreaklines(IReadOnlyList<double[]> breaklines)
    {
        ArgumentNullException.ThrowIfNull(breaklines);
        foreach (double[] line in breaklines)
            if (line == null || line.Length < 6 || line.Length % 3 != 0 || line.Any(value => !double.IsFinite(value)))
                throw new ArgumentException("Each fixed breakline needs at least two finite XYZ points.", nameof(breaklines));
    }
}
