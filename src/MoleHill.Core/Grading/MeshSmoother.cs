using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Laplacian mesh smoothing within specified boundary regions.
/// Smooths Z values of vertices inside boundaries while keeping
/// boundary and exterior vertices fixed. Per-boundary strength.
/// </summary>
public static class MeshSmoother
{
    private readonly record struct IndexedBreaklineSegment(double Ax, double Ay, double Bx, double By);

    public readonly record struct BreaklinePolyline(double[] XyPts, int PointCount, bool IsClosed);

    public sealed class PreparedSmoothingData
    {
        public required int VertexCount { get; init; }

        public required int[] NeighborOffsets { get; init; }

        public required int[] NeighborIndices { get; init; }

        public required bool[] IsMeshBoundary { get; init; }

        public required bool[] InsideBoundaries { get; init; }

        public required bool[] IsOnBreakline { get; init; }
    }

    /// <summary>
    /// Smooth a triangle mesh, optionally within closed boundary regions.
    /// If no boundaries are provided, globalStrength is applied to all interior vertices.
    /// Breakline vertices can be held rigid via breaklineFixity.
    /// </summary>
    /// <param name="vertices">Flat XYZ: [x0,y0,z0, ...]</param>
    /// <param name="vertexCount">Number of vertices.</param>
    /// <param name="faces">Triangle indices: [i0,i1,i2, ...]</param>
    /// <param name="faceCount">Number of faces.</param>
    /// <param name="boundaries">Closed polygon boundaries with per-boundary strength. Empty = smooth whole interior.</param>
    /// <param name="globalStrength">Smoothing strength used when no boundaries are provided (0-1).</param>
    /// <param name="breaklines">XY flat arrays of breakline polylines whose vertices should resist smoothing.</param>
    /// <param name="breaklineFixity">How fixed breakline vertices are (0 = free, 1 = fully fixed).</param>
    /// <param name="snapTolerance">Distance tolerance for "is vertex on breakline segment" check.</param>
    /// <param name="iterations">Number of smoothing passes.</param>
    /// <returns>New vertex array with smoothed Z values.</returns>
    public static double[] Smooth(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        (double[] xyVerts, int vertCount, double strength)[] boundaries,
        double globalStrength,
        (double[] xyPts, int ptCount)[] breaklines,
        double breaklineFixity,
        double snapTolerance,
        int iterations)
    {
        return Smooth(
            vertices,
            vertexCount,
            faces,
            faceCount,
            boundaries,
            globalStrength,
            ToBreaklinePolylines(breaklines),
            breaklineFixity,
            snapTolerance,
            iterations);
    }

    public static double[] Smooth(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        (double[] xyVerts, int vertCount, double strength)[] boundaries,
        double globalStrength,
        BreaklinePolyline[] breaklines,
        double breaklineFixity,
        double snapTolerance,
        int iterations)
    {
        if (iterations <= 0)
            return (double[])vertices.Clone();

        BuildNeighborGraph(vertexCount, faces, faceCount, out var neighborOffsets, out var neighborIndices, out var isMeshBoundary);

        var vertexStrength = BuildVertexStrengths(vertices, vertexCount, boundaries, globalStrength);

        if (breaklines.Length > 0 && breaklineFixity > 0)
        {
            var isOnBreakline = BuildBreaklineMask(vertices, vertexCount, breaklines, snapTolerance, vertexStrength: vertexStrength);
            double breaklineScale = 1.0 - Math.Clamp(breaklineFixity, 0.0, 1.0);
            for (int i = 0; i < vertexCount; i++)
            {
                if (isOnBreakline[i])
                    vertexStrength[i] *= breaklineScale;
            }
        }

        var result = (double[])vertices.Clone();

        for (int iter = 0; iter < iterations; iter++)
        {
            var newZ = new double[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                newZ[i] = result[i * 3 + 2];

            System.Threading.Tasks.Parallel.For(0, vertexCount, i =>
            {
                double s = vertexStrength[i];
                if (s <= 0 || isMeshBoundary[i])
                    return;

                int start = neighborOffsets[i];
                int end = neighborOffsets[i + 1];
                if (end <= start)
                    return;

                if (!TryEstimatePlaneZ(vertices, result, i, neighborIndices, start, end, out double targetZ) &&
                    !TryGetNeighborAverageZ(result, neighborIndices, start, end, out targetZ))
                {
                    return;
                }

                newZ[i] = result[i * 3 + 2] + s * (targetZ - result[i * 3 + 2]);
            });

            for (int i = 0; i < vertexCount; i++)
                result[i * 3 + 2] = newZ[i];
        }

        return result;
    }

    public static PreparedSmoothingData Prepare(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        (double[] xyVerts, int vertCount)[] boundaries,
        (double[] xyPts, int ptCount)[] breaklines,
        double snapTolerance)
    {
        return Prepare(
            vertices,
            vertexCount,
            faces,
            faceCount,
            boundaries,
            ToBreaklinePolylines(breaklines),
            snapTolerance);
    }

    public static PreparedSmoothingData Prepare(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        (double[] xyVerts, int vertCount)[] boundaries,
        BreaklinePolyline[] breaklines,
        double snapTolerance)
    {
        BuildNeighborGraph(vertexCount, faces, faceCount, out var neighborOffsets, out var neighborIndices, out var isMeshBoundary);

        var insideBoundaries = BuildInsideBoundaryMask(vertices, vertexCount, boundaries);
        var isOnBreakline = breaklines.Length > 0
            ? BuildBreaklineMask(vertices, vertexCount, breaklines, snapTolerance, insideBoundaries: insideBoundaries)
            : new bool[vertexCount];

        return new PreparedSmoothingData
        {
            VertexCount = vertexCount,
            NeighborOffsets = neighborOffsets,
            NeighborIndices = neighborIndices,
            IsMeshBoundary = isMeshBoundary,
            InsideBoundaries = insideBoundaries,
            IsOnBreakline = isOnBreakline
        };
    }

    public static double[] SmoothPrepared(
        double[] vertices,
        PreparedSmoothingData prepared,
        double globalStrength,
        double breaklineFixity,
        int iterations)
    {
        if (iterations <= 0)
            return (double[])vertices.Clone();

        if (vertices.Length < prepared.VertexCount * 3)
            throw new ArgumentException("Prepared smoothing data does not match the vertex array.", nameof(vertices));

        double strength = Math.Clamp(globalStrength, 0.0, 1.0);
        double breaklineScale = 1.0 - Math.Clamp(breaklineFixity, 0.0, 1.0);
        var result = (double[])vertices.Clone();

        for (int iter = 0; iter < iterations; iter++)
        {
            var newZ = new double[prepared.VertexCount];
            for (int i = 0; i < prepared.VertexCount; i++)
                newZ[i] = result[i * 3 + 2];

            System.Threading.Tasks.Parallel.For(0, prepared.VertexCount, i =>
            {
                if (!prepared.InsideBoundaries[i] || prepared.IsMeshBoundary[i])
                    return;

                double effectiveStrength = prepared.IsOnBreakline[i]
                    ? strength * breaklineScale
                    : strength;
                if (effectiveStrength <= 0)
                    return;

                int start = prepared.NeighborOffsets[i];
                int end = prepared.NeighborOffsets[i + 1];
                if (end <= start)
                    return;

                if (!TryEstimatePlaneZ(vertices, result, i, prepared.NeighborIndices, start, end, out double targetZ) &&
                    !TryGetNeighborAverageZ(result, prepared.NeighborIndices, start, end, out targetZ))
                {
                    return;
                }

                newZ[i] = result[i * 3 + 2] + effectiveStrength * (targetZ - result[i * 3 + 2]);
            });

            for (int i = 0; i < prepared.VertexCount; i++)
                result[i * 3 + 2] = newZ[i];
        }

        return result;
    }

    private static double[] BuildVertexStrengths(
        double[] vertices,
        int vertexCount,
        (double[] xyVerts, int vertCount, double strength)[] boundaries,
        double globalStrength)
    {
        var vertexStrength = new double[vertexCount];
        if (boundaries.Length == 0)
        {
            Array.Fill(vertexStrength, Math.Clamp(globalStrength, 0.0, 1.0));
            return vertexStrength;
        }

        var boundaryBounds = BuildBoundaryBounds(boundaries);
        var boundaryIndex = SpatialHashGrid2D.Build(boundaryBounds);
        var boundaryScratch = new SpatialHashGrid2D.QueryScratch(boundaries.Length);
        var boundaryCandidates = new List<int>(8);

        for (int i = 0; i < vertexCount; i++)
        {
            double px = vertices[i * 3];
            double py = vertices[i * 3 + 1];
            int boundaryIndexHit = FindLastContainingBoundary(px, py, boundaries, boundaryBounds, boundaryIndex, boundaryCandidates, boundaryScratch);
            if (boundaryIndexHit >= 0)
                vertexStrength[i] = Math.Clamp(boundaries[boundaryIndexHit].strength, 0.0, 1.0);
        }

        return vertexStrength;
    }

    private static bool[] BuildInsideBoundaryMask(
        double[] vertices,
        int vertexCount,
        (double[] xyVerts, int vertCount)[] boundaries)
    {
        var insideBoundaries = new bool[vertexCount];
        if (boundaries.Length == 0)
        {
            Array.Fill(insideBoundaries, true);
            return insideBoundaries;
        }

        var boundaryBounds = BuildBoundaryBounds(boundaries);
        var boundaryIndex = SpatialHashGrid2D.Build(boundaryBounds);
        var boundaryScratch = new SpatialHashGrid2D.QueryScratch(boundaries.Length);
        var boundaryCandidates = new List<int>(8);

        for (int i = 0; i < vertexCount; i++)
        {
            double px = vertices[i * 3];
            double py = vertices[i * 3 + 1];
            if (IsPointInsideAnyBoundary(px, py, boundaries, boundaryBounds, boundaryIndex, boundaryCandidates, boundaryScratch))
                insideBoundaries[i] = true;
        }

        return insideBoundaries;
    }

    private static int FindLastContainingBoundary(
        double px,
        double py,
        (double[] xyVerts, int vertCount, double strength)[] boundaries,
        Bounds2D[] boundaryBounds,
        SpatialHashGrid2D boundaryIndex,
        List<int> boundaryCandidates,
        SpatialHashGrid2D.QueryScratch boundaryScratch)
    {
        boundaryIndex.GatherCandidates(Bounds2D.FromPoint(px, py), boundaryCandidates, boundaryScratch);

        int bestMatch = -1;
        var pointBounds = Bounds2D.FromPoint(px, py);
        for (int i = 0; i < boundaryCandidates.Count; i++)
        {
            int boundaryIndexHit = boundaryCandidates[i];
            if (boundaryIndexHit <= bestMatch || !boundaryBounds[boundaryIndexHit].Intersects(pointBounds))
                continue;

            var boundary = boundaries[boundaryIndexHit];
            if (GradingGeometry2D.PointInPolygon(px, py, boundary.xyVerts, boundary.vertCount))
                bestMatch = boundaryIndexHit;
        }

        return bestMatch;
    }

    private static bool IsPointInsideAnyBoundary(
        double px,
        double py,
        (double[] xyVerts, int vertCount)[] boundaries,
        Bounds2D[] boundaryBounds,
        SpatialHashGrid2D boundaryIndex,
        List<int> boundaryCandidates,
        SpatialHashGrid2D.QueryScratch boundaryScratch)
    {
        boundaryIndex.GatherCandidates(Bounds2D.FromPoint(px, py), boundaryCandidates, boundaryScratch);

        var pointBounds = Bounds2D.FromPoint(px, py);
        for (int i = 0; i < boundaryCandidates.Count; i++)
        {
            int boundaryIndexHit = boundaryCandidates[i];
            if (!boundaryBounds[boundaryIndexHit].Intersects(pointBounds))
                continue;

            var boundary = boundaries[boundaryIndexHit];
            if (GradingGeometry2D.PointInPolygon(px, py, boundary.xyVerts, boundary.vertCount))
                return true;
        }

        return false;
    }

    private static Bounds2D[] BuildBoundaryBounds((double[] xyVerts, int vertCount, double strength)[] boundaries)
    {
        var result = new Bounds2D[boundaries.Length];
        for (int i = 0; i < boundaries.Length; i++)
            result[i] = ComputeBounds(boundaries[i].xyVerts, boundaries[i].vertCount);

        return result;
    }

    private static Bounds2D[] BuildBoundaryBounds((double[] xyVerts, int vertCount)[] boundaries)
    {
        var result = new Bounds2D[boundaries.Length];
        for (int i = 0; i < boundaries.Length; i++)
            result[i] = ComputeBounds(boundaries[i].xyVerts, boundaries[i].vertCount);

        return result;
    }

    private static bool[] BuildBreaklineMask(
        double[] vertices,
        int vertexCount,
        BreaklinePolyline[] breaklines,
        double snapTolerance,
        bool[]? insideBoundaries = null,
        double[]? vertexStrength = null)
    {
        var result = new bool[vertexCount];
        if (breaklines.Length == 0)
            return result;

        var breaklineSegments = BuildBreaklineSegments(breaklines, out var breaklineIndex);
        if (breaklineSegments.Length == 0)
            return result;

        var breaklineScratch = new SpatialHashGrid2D.QueryScratch(breaklineSegments.Length);
        var breaklineCandidates = new List<int>(8);
        double tolerance = Math.Max(snapTolerance, 0.0);

        for (int i = 0; i < vertexCount; i++)
        {
            if (insideBoundaries != null && !insideBoundaries[i])
                continue;
            if (vertexStrength != null && vertexStrength[i] <= 0)
                continue;

            double px = vertices[i * 3];
            double py = vertices[i * 3 + 1];
            breaklineIndex.GatherCandidates(Bounds2D.FromPoint(px, py, tolerance), breaklineCandidates, breaklineScratch);
            result[i] = IsOnAnyBreakline(px, py, breaklineSegments, breaklineCandidates, tolerance);
        }

        return result;
    }

    private static IndexedBreaklineSegment[] BuildBreaklineSegments(
        BreaklinePolyline[] breaklines,
        out SpatialHashGrid2D breaklineIndex)
    {
        var segments = new List<IndexedBreaklineSegment>();
        var bounds = new List<Bounds2D>();

        foreach (var breakline in breaklines)
        {
            double[] pts = breakline.XyPts;
            int pointCount = Math.Min(breakline.PointCount, pts.Length / 2);
            for (int i = 0; i < pointCount - 1; i++)
                AddBreaklineSegment(pts[i * 2], pts[i * 2 + 1], pts[i * 2 + 2], pts[i * 2 + 3]);

            if (breakline.IsClosed && pointCount > 2)
            {
                double ax = pts[(pointCount - 1) * 2];
                double ay = pts[(pointCount - 1) * 2 + 1];
                double bx = pts[0];
                double by = pts[1];
                if (ax != bx || ay != by)
                    AddBreaklineSegment(ax, ay, bx, by);
            }
        }

        breaklineIndex = SpatialHashGrid2D.Build(bounds.ToArray());
        return segments.ToArray();

        void AddBreaklineSegment(double ax, double ay, double bx, double by)
        {
            segments.Add(new IndexedBreaklineSegment(ax, ay, bx, by));
            bounds.Add(new Bounds2D(
                Math.Min(ax, bx),
                Math.Max(ax, bx),
                Math.Min(ay, by),
                Math.Max(ay, by)));
        }
    }

    private static BreaklinePolyline[] ToBreaklinePolylines((double[] xyPts, int ptCount)[] breaklines)
    {
        var result = new BreaklinePolyline[breaklines.Length];
        for (int i = 0; i < breaklines.Length; i++)
            result[i] = new BreaklinePolyline(breaklines[i].xyPts, breaklines[i].ptCount, IsClosed: false);

        return result;
    }

    private static Bounds2D ComputeBounds(double[] xyVerts, int vertCount)
    {
        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;

        for (int i = 0; i < vertCount; i++)
        {
            double x = xyVerts[i * 2];
            double y = xyVerts[i * 2 + 1];
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        return new Bounds2D(minX, maxX, minY, maxY);
    }

    private static bool IsOnAnyBreakline(
        double px,
        double py,
        IndexedBreaklineSegment[] breaklineSegments,
        List<int> breaklineCandidates,
        double tol)
    {
        double tolSq = tol * tol;
        for (int i = 0; i < breaklineCandidates.Count; i++)
        {
            var segment = breaklineSegments[breaklineCandidates[i]];
            double dx = segment.Bx - segment.Ax;
            double dy = segment.By - segment.Ay;
            double lenSq = dx * dx + dy * dy;
            double t;
            if (lenSq < tolSq)
                t = 0.0;
            else
                t = Math.Max(0.0, Math.Min(1.0, ((px - segment.Ax) * dx + (py - segment.Ay) * dy) / lenSq));

            double ex = segment.Ax + t * dx - px;
            double ey = segment.Ay + t * dy - py;
            if ((ex * ex) + (ey * ey) < tolSq)
                return true;
        }

        return false;
    }

    private static bool TryEstimatePlaneZ(
        double[] vertices,
        double[] current,
        int vertexIndex,
        int[] neighborIndices,
        int start,
        int end,
        out double targetZ)
    {
        int sampleCount = (end - start) + 1;
        if (sampleCount < 3)
        {
            targetZ = current[vertexIndex * 3 + 2];
            return false;
        }

        double meanX = vertices[vertexIndex * 3];
        double meanY = vertices[vertexIndex * 3 + 1];
        double meanZ = current[vertexIndex * 3 + 2];

        for (int index = start; index < end; index++)
        {
            int neighborIndex = neighborIndices[index];
            meanX += vertices[neighborIndex * 3];
            meanY += vertices[neighborIndex * 3 + 1];
            meanZ += current[neighborIndex * 3 + 2];
        }

        meanX /= sampleCount;
        meanY /= sampleCount;
        meanZ /= sampleCount;

        double sxx = 0;
        double sxy = 0;
        double syy = 0;
        double sxz = 0;
        double syz = 0;

        AccumulatePlaneFit(vertexIndex);
        for (int index = start; index < end; index++)
            AccumulatePlaneFit(neighborIndices[index]);

        double determinant = sxx * syy - sxy * sxy;
        double scale = Math.Max(1.0, sxx + syy);
        if (Math.Abs(determinant) <= 1e-12 * scale * scale)
        {
            targetZ = current[vertexIndex * 3 + 2];
            return false;
        }

        double ax = (sxz * syy - syz * sxy) / determinant;
        double ay = (syz * sxx - sxz * sxy) / determinant;

        double px = vertices[vertexIndex * 3];
        double py = vertices[vertexIndex * 3 + 1];
        targetZ = meanZ + ax * (px - meanX) + ay * (py - meanY);
        return true;

        void AccumulatePlaneFit(int sampleIndex)
        {
            double dx = vertices[sampleIndex * 3] - meanX;
            double dy = vertices[sampleIndex * 3 + 1] - meanY;
            double dz = current[sampleIndex * 3 + 2] - meanZ;

            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
            sxz += dx * dz;
            syz += dy * dz;
        }
    }

    private static void BuildNeighborGraph(
        int vertexCount,
        int[] faces,
        int faceCount,
        out int[] neighborOffsets,
        out int[] neighborIndices,
        out bool[] isMeshBoundary)
    {
        var edgeCount = new Dictionary<long, int>(faceCount * 3, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[f * 3 + 1];
            int c = faces[f * 3 + 2];
            CountEdge(edgeCount, a, b);
            CountEdge(edgeCount, b, c);
            CountEdge(edgeCount, c, a);
        }

        var neighborCounts = new int[vertexCount];
        isMeshBoundary = new bool[vertexCount];
        foreach (var kvp in edgeCount)
        {
            int a = (int)(kvp.Key >> 32);
            int b = (int)(kvp.Key & 0xFFFFFFFFL);
            neighborCounts[a]++;
            neighborCounts[b]++;

            if (kvp.Value == 1)
            {
                isMeshBoundary[a] = true;
                isMeshBoundary[b] = true;
            }
        }

        neighborOffsets = new int[vertexCount + 1];
        for (int i = 0; i < vertexCount; i++)
            neighborOffsets[i + 1] = neighborOffsets[i] + neighborCounts[i];

        neighborIndices = new int[neighborOffsets[vertexCount]];
        var cursors = new int[vertexCount];
        Array.Copy(neighborOffsets, cursors, vertexCount);

        foreach (var kvp in edgeCount)
        {
            int a = (int)(kvp.Key >> 32);
            int b = (int)(kvp.Key & 0xFFFFFFFFL);
            neighborIndices[cursors[a]++] = b;
            neighborIndices[cursors[b]++] = a;
        }
    }

    private static void CountEdge(Dictionary<long, int> edgeCount, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        edgeCount[key] = edgeCount.GetValueOrDefault(key, 0) + 1;
    }

    private static bool TryGetNeighborAverageZ(double[] current, int[] neighborIndices, int start, int end, out double targetZ)
    {
        int count = end - start;
        if (count <= 0)
        {
            targetZ = 0.0;
            return false;
        }

        double sum = 0.0;
        for (int index = start; index < end; index++)
            sum += current[neighborIndices[index] * 3 + 2];

        targetZ = sum / count;
        return true;
    }
}
