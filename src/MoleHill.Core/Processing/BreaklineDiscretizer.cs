namespace MoleHill.Core.Processing;

/// <summary>
/// Converts polyline breaklines (already tessellated from curves at the GH level)
/// into vertex arrays and segment index chains.
/// Input: list of polylines, each as a flat array [x0,y0,z0, x1,y1,z1, …].
/// </summary>
public static class BreaklineDiscretizer
{
    /// <summary>
    /// Result of breakline discretization.
    /// </summary>
    public readonly struct BreaklineData
    {
        /// <summary>Flat [x,y,z, …] of all breakline vertices.</summary>
        public readonly double[] Vertices;

        /// <summary>Number of breakline vertices.</summary>
        public readonly int VertexCount;

        /// <summary>Flat [a,b, …] segment pairs referencing local vertex indices.</summary>
        public readonly int[] Segments;

        /// <summary>Number of segments.</summary>
        public readonly int SegmentCount;

        public BreaklineData(double[] vertices, int vertexCount, int[] segments, int segmentCount)
        {
            Vertices = vertices;
            VertexCount = vertexCount;
            Segments = segments;
            SegmentCount = segmentCount;
        }
    }

    /// <summary>
    /// Process a list of polylines into breakline data.
    /// Each polyline is a flat XYZ array: [x0,y0,z0, x1,y1,z1, …].
    /// </summary>
    public static BreaklineData Process(IReadOnlyList<double[]> polylines)
    {
        if (polylines.Count == 0)
            return new BreaklineData(Array.Empty<double>(), 0, Array.Empty<int>(), 0);

        // Count totals
        int totalVerts = 0;
        int totalSegs = 0;
        foreach (var pl in polylines)
        {
            int n = pl.Length / 3;
            if (n < 2) continue;
            totalVerts += n;
            totalSegs += n - 1;
        }

        var verts = new double[totalVerts * 3];
        var segs = new int[totalSegs * 2];
        int vi = 0; // vertex write index
        int si = 0; // segment write index
        int baseIdx = 0; // base vertex index for current polyline

        foreach (var pl in polylines)
        {
            int n = pl.Length / 3;
            if (n < 2) continue;

            Array.Copy(pl, 0, verts, vi * 3, n * 3);

            for (int i = 0; i < n - 1; i++)
            {
                segs[si++] = baseIdx + i;
                segs[si++] = baseIdx + i + 1;
            }

            vi += n;
            baseIdx += n;
        }

        return new BreaklineData(verts, totalVerts, segs, totalSegs);
    }
}
