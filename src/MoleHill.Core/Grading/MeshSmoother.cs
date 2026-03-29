namespace MoleHill.Core.Grading;

/// <summary>
/// Laplacian mesh smoothing within specified boundary regions.
/// Smooths Z values of vertices inside boundaries while keeping
/// boundary and exterior vertices fixed. Per-boundary strength.
/// </summary>
public static class MeshSmoother
{
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
        if (iterations <= 0)
            return (double[])vertices.Clone();

        BuildNeighborGraph(vertexCount, faces, faceCount, out var neighborOffsets, out var neighborIndices, out var isMeshBoundary);

        // Step 1 — Determine per-vertex strength
        var vertexStrength = new double[vertexCount];
        if (boundaries.Length == 0)
        {
            double gs = Math.Max(0, Math.Min(1, globalStrength));
            for (int i = 0; i < vertexCount; i++)
                vertexStrength[i] = gs;
        }
        else
        {
            for (int i = 0; i < vertexCount; i++)
            {
                double px = vertices[i * 3];
                double py = vertices[i * 3 + 1];

                foreach (var (xyVerts, vertCount, strength) in boundaries)
                {
                    if (strength > 0 && PadGrader.PointInPolygon(px, py, xyVerts, vertCount))
                        vertexStrength[i] = Math.Max(0, Math.Min(1, strength));
                }
            }
        }

        // Step 2 — Breakline fixity: scale down strength for vertices on breaklines
        if (breaklines.Length > 0 && breaklineFixity > 0)
        {
            for (int i = 0; i < vertexCount; i++)
            {
                if (vertexStrength[i] <= 0) continue;
                double px = vertices[i * 3], py = vertices[i * 3 + 1];
                if (IsOnAnyBreakline(px, py, breaklines, snapTolerance))
                    vertexStrength[i] *= (1.0 - breaklineFixity);
            }
        }

        // Find naked (boundary) edges — vertices on mesh boundary should not move
        // Iterative smoothing: move Z toward a local best-fit plane.
        // This preserves planar slopes on irregular triangulations, unlike
        // a plain neighbor-average on Z which can create ripples.
        var result = (double[])vertices.Clone();

        for (int iter = 0; iter < iterations; iter++)
        {
            var newZ = new double[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                newZ[i] = result[i * 3 + 2];

            for (int i = 0; i < vertexCount; i++)
            {
                double s = vertexStrength[i];
                if (s <= 0 || isMeshBoundary[i]) continue;
                int start = neighborOffsets[i];
                int end = neighborOffsets[i + 1];
                if (end <= start) continue;

                if (!TryEstimatePlaneZ(vertices, result, i, neighborIndices, start, end, out double targetZ) &&
                    !TryGetNeighborAverageZ(result, neighborIndices, start, end, out targetZ))
                {
                    continue;
                }

                newZ[i] = result[i * 3 + 2] + s * (targetZ - result[i * 3 + 2]);
            }

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
        BuildNeighborGraph(vertexCount, faces, faceCount, out var neighborOffsets, out var neighborIndices, out var isMeshBoundary);

        var insideBoundaries = new bool[vertexCount];
        if (boundaries.Length == 0)
        {
            Array.Fill(insideBoundaries, true);
        }
        else
        {
            for (int i = 0; i < vertexCount; i++)
            {
                double px = vertices[i * 3];
                double py = vertices[i * 3 + 1];
                foreach (var (xyVerts, vertCount) in boundaries)
                {
                    if (PadGrader.PointInPolygon(px, py, xyVerts, vertCount))
                    {
                        insideBoundaries[i] = true;
                        break;
                    }
                }
            }
        }

        var isOnBreakline = new bool[vertexCount];
        if (breaklines.Length > 0)
        {
            for (int i = 0; i < vertexCount; i++)
            {
                if (!insideBoundaries[i])
                    continue;

                double px = vertices[i * 3];
                double py = vertices[i * 3 + 1];
                isOnBreakline[i] = IsOnAnyBreakline(px, py, breaklines, snapTolerance);
            }
        }

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

    private static bool IsOnAnyBreakline(double px, double py,
        (double[] xyPts, int ptCount)[] breaklines, double tol)
    {
        double tolSq = tol * tol;
        foreach (var (pts, n) in breaklines)
        {
            for (int j = 0; j < n - 1; j++)
            {
                double ax = pts[j * 2], ay = pts[j * 2 + 1];
                double bx = pts[j * 2 + 2], by = pts[j * 2 + 3];
                double dx = bx - ax, dy = by - ay;
                double lenSq = dx * dx + dy * dy;
                double t;
                if (lenSq < tolSq) // degenerate segment → point check
                    t = 0;
                else
                    t = Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
                double ex = ax + t * dx - px, ey = ay + t * dy - py;
                if (ex * ex + ey * ey < tolSq) return true;
            }
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
        var edgeCount = new Dictionary<long, int>(faceCount * 3);
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
