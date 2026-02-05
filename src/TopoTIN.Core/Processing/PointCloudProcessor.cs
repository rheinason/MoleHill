namespace TopoTIN.Core.Processing;

/// <summary>
/// Merges spot points with breakline vertices, deduplicates by XY proximity,
/// and produces the final vertex array + remapped segments for the TIN engine.
/// Breakline vertices take priority over spot points when merging duplicates.
/// </summary>
public static class PointCloudProcessor
{
    private const double DefaultTolerance = 1e-6;

    /// <summary>
    /// Merged and deduplicated result ready for TinEngine.
    /// </summary>
    public readonly struct MergedData
    {
        /// <summary>Flat [x0,y0, x1,y1, …] XY coordinates.</summary>
        public readonly double[] XyCoords;

        /// <summary>Z values per vertex.</summary>
        public readonly double[] ZValues;

        /// <summary>Number of unique vertices.</summary>
        public readonly int VertexCount;

        /// <summary>Flat [a,b, …] segment pairs using merged indices.</summary>
        public readonly int[] Segments;

        /// <summary>Number of segments.</summary>
        public readonly int SegmentCount;

        /// <summary>Number of duplicate points that were removed.</summary>
        public readonly int DuplicatesRemoved;

        /// <summary>Number of invalid points (NaN/Inf) that were skipped.</summary>
        public readonly int InvalidsSkipped;

        public MergedData(double[] xyCoords, double[] zValues, int vertexCount,
                          int[] segments, int segmentCount,
                          int duplicatesRemoved, int invalidsSkipped)
        {
            XyCoords = xyCoords;
            ZValues = zValues;
            VertexCount = vertexCount;
            Segments = segments;
            SegmentCount = segmentCount;
            DuplicatesRemoved = duplicatesRemoved;
            InvalidsSkipped = invalidsSkipped;
        }
    }

    /// <summary>
    /// Merge spot points and breakline data into a single deduplicated set.
    /// </summary>
    /// <param name="spotXyz">Flat [x,y,z, …] spot point coordinates.</param>
    /// <param name="spotCount">Number of spot points.</param>
    /// <param name="breaklineData">Breakline discretization result.</param>
    /// <param name="tolerance">XY deduplication tolerance.</param>
    public static MergedData Merge(double[] spotXyz, int spotCount,
                                    BreaklineDiscretizer.BreaklineData breaklineData,
                                    double tolerance = DefaultTolerance)
    {
        double tol = Math.Max(tolerance, 1e-12);
        double invCell = 1.0 / tol;

        // Spatial hash grid for deduplication
        // Key: (cellX, cellY), Value: merged vertex index
        var grid = new Dictionary<(long, long), int>();
        var xyList = new List<double>();   // pairs of x,y
        var zList = new List<double>();
        int duplicates = 0;
        int invalids = 0;

        // Maps from original breakline vertex index → merged index
        var breaklineRemap = new int[breaklineData.VertexCount];

        // 1. Add breakline vertices first (they take priority)
        for (int i = 0; i < breaklineData.VertexCount; i++)
        {
            double x = breaklineData.Vertices[i * 3];
            double y = breaklineData.Vertices[i * 3 + 1];
            double z = breaklineData.Vertices[i * 3 + 2];

            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                invalids++;
                breaklineRemap[i] = -1;
                continue;
            }

            int merged = TryInsert(grid, xyList, zList, x, y, z, invCell, ref duplicates);
            breaklineRemap[i] = merged;
        }

        // 2. Add spot points (may deduplicate against breakline vertices)
        for (int i = 0; i < spotCount; i++)
        {
            double x = spotXyz[i * 3];
            double y = spotXyz[i * 3 + 1];
            double z = spotXyz[i * 3 + 2];

            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                invalids++;
                continue;
            }

            TryInsert(grid, xyList, zList, x, y, z, invCell, ref duplicates);
        }

        int vertexCount = xyList.Count / 2;

        // 3. Remap breakline segments
        var segList = new List<int>();
        for (int i = 0; i < breaklineData.SegmentCount; i++)
        {
            int a = breaklineData.Segments[i * 2];
            int b = breaklineData.Segments[i * 2 + 1];

            int ma = a < breaklineRemap.Length ? breaklineRemap[a] : -1;
            int mb = b < breaklineRemap.Length ? breaklineRemap[b] : -1;

            if (ma >= 0 && mb >= 0 && ma != mb)
            {
                segList.Add(ma);
                segList.Add(mb);
            }
        }

        return new MergedData(
            xyList.ToArray(),
            zList.ToArray(),
            vertexCount,
            segList.ToArray(),
            segList.Count / 2,
            duplicates,
            invalids);
    }

    private static int TryInsert(Dictionary<(long, long), int> grid,
                                  List<double> xyList, List<double> zList,
                                  double x, double y, double z,
                                  double invCell, ref int duplicates)
    {
        long cx = (long)Math.Floor(x * invCell);
        long cy = (long)Math.Floor(y * invCell);

        // Check the cell and its 8 neighbors for existing points
        for (long dx = -1; dx <= 1; dx++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                var key = (cx + dx, cy + dy);
                if (grid.TryGetValue(key, out int existingIdx))
                {
                    double ex = xyList[existingIdx * 2];
                    double ey = xyList[existingIdx * 2 + 1];
                    double dist = Math.Abs(x - ex) + Math.Abs(y - ey); // Manhattan for speed
                    if (dist < 1.0 / invCell * 2) // within tolerance
                    {
                        duplicates++;
                        return existingIdx;
                    }
                }
            }
        }

        // New unique point
        int newIdx = xyList.Count / 2;
        grid[(cx, cy)] = newIdx;
        xyList.Add(x);
        xyList.Add(y);
        zList.Add(double.IsNaN(z) || double.IsInfinity(z) ? 0.0 : z);
        return newIdx;
    }
}
