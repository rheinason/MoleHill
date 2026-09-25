using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Processing;

/// <summary>
/// Blends a 2.5D mesh vertically toward another 2.5D mesh. Optional closed XY loops use the even-odd
/// rule, so nested loops naturally describe islands and holes; feathering is contained inside the
/// selected region. With no loops the region is the target's footprint, and the feather fades in from
/// its edge. Given the input faces, vertices of steep wall faces are left where they are.
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
        Func<bool>? shouldCancel = null,
        int[]? faces = null,
        int faceCount = 0,
        double wallFaceMinSlopeDeg = 0.0)
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
        bool[] frozen = BuildWallVertexMask(vertices, usableVertexCount, faces, faceCount, wallFaceMinSlopeDeg);
        var projector = new MeshHeightProjector(
            targetVertices,
            Math.Clamp(targetVertexCount, 0, targetVertices.Length / 3),
            targetFaces,
            Math.Clamp(targetFaceCount, 0, targetFaces.Length / 3));
        var cancellation = Engine.CancellationProbe.For(shouldCancel);
        cancellation.ThrowIfCancelled();

        // Pass 1: where the target is, and how strongly the region asks for it. NaN marks a vertex that
        // takes no part (non-finite, outside the region, or a wall vertex).
        var targetZs = new double[usableVertexCount];
        var weights = new double[usableVertexCount];
        var outsideFootprint = new List<int>();
        for (int vertex = 0; vertex < usableVertexCount; vertex++)
        {
            cancellation.ThrowIfCancelledOften();
            targetZs[vertex] = double.NaN;
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
                outsideFootprint.Add(vertex);
                continue;
            }

            // Walls are never buried: a steep face's top and toe sit at the same XY, so projecting both
            // to one target Z would collapse the wall. Its vertices keep their elevation.
            if (frozen[vertex])
                continue;

            targetZs[vertex] = targetZ;
            weights[vertex] = regionWeight;
        }

        // With no boundary, the region is the target's own footprint, and its edge would otherwise be a
        // hard step between projected and untouched ground. Fade the projection in over the feather
        // distance from the nearest vertex the target does not cover.
        SegmentProximityIndex? footprintEdge = loops.Count == 0 && feather > 0.0 && outsideFootprint.Count > 0
            ? CreatePointIndex(vertices, outsideFootprint)
            : null;
        var footprintQuery = footprintEdge == null ? null : new SegmentProximityIndex.QueryState(footprintEdge.SegmentCount);

        // Pass 2: blend.
        for (int vertex = 0; vertex < usableVertexCount; vertex++)
        {
            cancellation.ThrowIfCancelledOften();
            double targetZ = targetZs[vertex];
            if (double.IsNaN(targetZ))
                continue;

            int offset = vertex * 3;
            double weight = clampedStrength * weights[vertex];
            if (footprintEdge != null)
            {
                double distance = footprintEdge.NearestDistance(vertices[offset], vertices[offset + 1], footprintQuery!);
                if (distance < feather)
                    weight *= SmoothStep(distance / feather);
            }

            double z = vertices[offset + 2];
            result[offset + 2] = z + ((targetZ - z) * weight);
        }

        return result;
    }

    /// <summary>
    /// Vertices of the input mesh's wall faces — the same steep-face test the remesher freezes walls by.
    /// All false when no faces are given or the threshold is off. Faces with out-of-range indices are
    /// ignored.
    /// </summary>
    private static bool[] BuildWallVertexMask(double[] vertices, int vertexCount, int[]? faces, int faceCount, double wallFaceMinSlopeDeg)
    {
        var frozen = new bool[vertexCount];
        if (faces == null || !(wallFaceMinSlopeDeg > 0.0))
            return frozen;

        int usableFaceCount = Math.Clamp(faceCount, 0, faces.Length / 3);
        var validFaces = new List<int>(usableFaceCount * 3);
        for (int face = 0; face < usableFaceCount; face++)
        {
            int a = faces[face * 3], b = faces[(face * 3) + 1], c = faces[(face * 3) + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                continue;
            validFaces.Add(a);
            validFaces.Add(b);
            validFaces.Add(c);
        }

        int[] faceArray = validFaces.ToArray();
        bool[] frozenFaces = Engine.FeaturePolylineGraph.BuildFrozenFaceMask(
            vertices, faceArray, faceArray.Length / 3, wallFaceMinSlopeDeg);
        for (int face = 0; face < frozenFaces.Length; face++)
        {
            if (!frozenFaces[face])
                continue;
            frozen[faceArray[face * 3]] = true;
            frozen[faceArray[(face * 3) + 1]] = true;
            frozen[faceArray[(face * 3) + 2]] = true;
        }

        return frozen;
    }

    /// <summary>Indexes points as zero-length segments, for exact nearest-point distance queries.</summary>
    private static SegmentProximityIndex? CreatePointIndex(double[] vertices, List<int> pointVertices)
    {
        var xy = new double[pointVertices.Count * 2];
        var segments = new int[pointVertices.Count * 2];
        for (int i = 0; i < pointVertices.Count; i++)
        {
            xy[i * 2] = vertices[pointVertices[i] * 3];
            xy[(i * 2) + 1] = vertices[(pointVertices[i] * 3) + 1];
            segments[i * 2] = i;
            segments[(i * 2) + 1] = i;
        }

        return SegmentProximityIndex.TryCreateForSegments(xy, segments, pointVertices.Count);
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
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

        // One distance per loop: a loop further than the feather cannot lower the minimum below it, so
        // there is no need to test "within" first and then measure again.
        double nearest = double.PositiveInfinity;
        for (int i = 0; i < loops.Count; i++)
            nearest = Math.Min(nearest, loops[i].Polygon.DistanceIfNear(x, y, feather));

        if (!double.IsFinite(nearest) || nearest >= feather)
            return 1.0;

        return SmoothStep(nearest / feather);
    }

    private sealed record PreparedLoop(double[] Xy, int VertexCount, PreparedPolygon Polygon);
}
