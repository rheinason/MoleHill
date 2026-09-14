using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Processing;

/// <summary>
/// Blends a 2.5D mesh vertically toward another 2.5D mesh. Optional closed XY loops use the even-odd
/// rule, so nested loops naturally describe islands and holes; feathering is contained inside the
/// selected region.
/// </summary>
public static class SurfaceConformer
{
    public static double[] Conform(
        double[] vertices,
        int vertexCount,
        double[] targetVertices,
        int targetVertexCount,
        int[] targetFaces,
        int targetFaceCount,
        IReadOnlyList<double[]> boundaryLoops,
        double strength,
        double featherDistance,
        double tolerance,
        Func<bool>? shouldCancel = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(targetVertices);
        ArgumentNullException.ThrowIfNull(targetFaces);
        ArgumentNullException.ThrowIfNull(boundaryLoops);

        int usableVertexCount = Math.Clamp(vertexCount, 0, vertices.Length / 3);
        var result = (double[])vertices.Clone();
        if (usableVertexCount == 0 || targetVertexCount <= 0 || targetFaceCount <= 0)
            return result;

        double clampedStrength = Math.Clamp(strength, 0.0, 1.0);
        if (!(clampedStrength > 0.0))
            return result;

        double feather = double.IsFinite(featherDistance) ? Math.Max(0.0, featherDistance) : 0.0;
        List<PreparedLoop> loops = PrepareLoops(boundaryLoops);
        var projector = new MeshHeightProjector(
            targetVertices,
            Math.Clamp(targetVertexCount, 0, targetVertices.Length / 3),
            targetFaces,
            Math.Clamp(targetFaceCount, 0, targetFaces.Length / 3));
        var cancellation = Engine.CancellationProbe.For(shouldCancel);
        cancellation.ThrowIfCancelled();

        for (int vertex = 0; vertex < usableVertexCount; vertex++)
        {
            cancellation.ThrowIfCancelledOften();
            int offset = vertex * 3;
            double x = vertices[offset];
            double y = vertices[offset + 1];
            double z = vertices[offset + 2];
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                continue;

            double regionWeight = RegionWeight(x, y, loops, feather);
            if (!(regionWeight > 0.0))
                continue;

            if (!projector.TryProjectZ(x, y, z, tolerance, out double targetZ, out _) ||
                !double.IsFinite(targetZ))
            {
                continue;
            }

            double weight = clampedStrength * regionWeight;
            result[offset + 2] = z + ((targetZ - z) * weight);
        }

        return result;
    }

    private static List<PreparedLoop> PrepareLoops(IReadOnlyList<double[]> boundaryLoops)
    {
        var loops = new List<PreparedLoop>(boundaryLoops.Count);
        foreach (double[] xy in boundaryLoops)
        {
            if (xy == null)
                continue;

            int count = xy.Length / 2;
            PreparedPolygon? polygon = PreparedPolygon.TryCreate(xy, count);
            if (polygon != null)
                loops.Add(new PreparedLoop(xy, count, polygon));
        }

        return loops;
    }

    private static double RegionWeight(double x, double y, IReadOnlyList<PreparedLoop> loops, double feather)
    {
        if (loops.Count == 0)
            return 1.0;

        bool inside = false;
        for (int i = 0; i < loops.Count; i++)
        {
            if (loops[i].Polygon.Contains(x, y))
                inside = !inside;
        }

        if (!inside)
            return 0.0;
        if (!(feather > 0.0))
            return 1.0;

        double nearest = double.PositiveInfinity;
        for (int i = 0; i < loops.Count; i++)
        {
            PreparedLoop loop = loops[i];
            if (!loop.Polygon.IsWithin(x, y, feather))
                continue;

            nearest = Math.Min(nearest, GradingGeometry2D.DistanceToPolygon(x, y, loop.Xy, loop.VertexCount));
        }

        if (!double.IsFinite(nearest) || nearest >= feather)
            return 1.0;

        double t = Math.Clamp(nearest / feather, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    private sealed record PreparedLoop(double[] Xy, int VertexCount, PreparedPolygon Polygon);
}
