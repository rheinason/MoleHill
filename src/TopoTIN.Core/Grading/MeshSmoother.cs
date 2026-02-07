namespace TopoTIN.Core.Grading;

/// <summary>
/// Laplacian mesh smoothing within specified boundary regions.
/// Smooths Z values of vertices inside boundaries while keeping
/// boundary and exterior vertices fixed. Per-boundary strength.
/// </summary>
public static class MeshSmoother
{
    /// <summary>
    /// Smooth a triangle mesh within specified closed boundary regions.
    /// Each boundary carries its own strength value.
    /// </summary>
    /// <param name="vertices">Flat XYZ: [x0,y0,z0, ...]</param>
    /// <param name="vertexCount">Number of vertices.</param>
    /// <param name="faces">Triangle indices: [i0,i1,i2, ...]</param>
    /// <param name="faceCount">Number of faces.</param>
    /// <param name="boundaries">Closed polygon boundaries with per-boundary strength.</param>
    /// <param name="iterations">Number of smoothing passes.</param>
    /// <returns>New vertex array with smoothed Z values.</returns>
    public static double[] Smooth(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        (double[] xyVerts, int vertCount, double strength)[] boundaries,
        int iterations)
    {
        if (iterations <= 0)
            return (double[])vertices.Clone();

        // Build adjacency: for each vertex, list of connected vertex indices
        var neighbors = new List<int>[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            neighbors[i] = new List<int>();

        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            AddEdge(neighbors, a, b);
            AddEdge(neighbors, b, c);
            AddEdge(neighbors, c, a);
        }

        // Determine per-vertex strength (last boundary wins for overlapping)
        var vertexStrength = new double[vertexCount];
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

        // Find naked (boundary) edges — vertices on mesh boundary should not move
        var edgeCount = new Dictionary<long, int>();
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            CountEdge(edgeCount, a, b);
            CountEdge(edgeCount, b, c);
            CountEdge(edgeCount, c, a);
        }

        var isMeshBoundary = new bool[vertexCount];
        foreach (var kvp in edgeCount)
        {
            if (kvp.Value == 1) // naked edge
            {
                int a = (int)(kvp.Key >> 32);
                int b = (int)(kvp.Key & 0xFFFFFFFFL);
                isMeshBoundary[a] = true;
                isMeshBoundary[b] = true;
            }
        }

        // Iterative Laplacian smoothing (Z only)
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
                if (neighbors[i].Count == 0) continue;

                double avgZ = 0;
                foreach (int n in neighbors[i])
                    avgZ += result[n * 3 + 2];
                avgZ /= neighbors[i].Count;

                newZ[i] = result[i * 3 + 2] + s * (avgZ - result[i * 3 + 2]);
            }

            for (int i = 0; i < vertexCount; i++)
                result[i * 3 + 2] = newZ[i];
        }

        return result;
    }

    private static void AddEdge(List<int>[] neighbors, int a, int b)
    {
        if (!neighbors[a].Contains(b)) neighbors[a].Add(b);
        if (!neighbors[b].Contains(a)) neighbors[b].Add(a);
    }

    private static void CountEdge(Dictionary<long, int> edgeCount, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        edgeCount[key] = edgeCount.GetValueOrDefault(key, 0) + 1;
    }
}
