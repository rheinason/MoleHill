namespace MoleHill.Core.Processing;

/// <summary>
/// Merges spot points with breakline vertices, deduplicates by XY proximity,
/// and produces the final vertex array + remapped segments for the TIN engine.
/// Breakline vertices take priority over spot points when merging duplicates.
/// Z-aware dedup: breakline-to-breakline merging also requires Z proximity
/// (preserves parallel breaklines at different elevations, e.g. retaining walls).
/// </summary>
public static class PointCloudProcessor
{
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
                                    double tolerance)
    {
        double tol = Math.Max(tolerance, 1e-12);
        double tolSq = tol * tol;
        double cellSize = tol * 2;
        double invCell = 1.0 / cellSize;

        // Spatial hash grid for deduplication
        var grid = new Dictionary<(long, long), List<int>>();
        var xyList = new List<double>();
        var zList = new List<double>();
        int duplicates = 0;
        int invalids = 0;

        var breaklineRemap = new int[breaklineData.VertexCount];

        // 1. Add breakline vertices first (they take priority)
        //    Z-aware: only merge breakline-to-breakline if BOTH XY and Z are close
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

            int merged = TryInsert(grid, xyList, zList, x, y, z, invCell, tolSq, true, tolSq, ref duplicates);
            breaklineRemap[i] = merged;
        }

        // 2. Add spot points (XY-only dedup against existing points)
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

            TryInsert(grid, xyList, zList, x, y, z, invCell, tolSq, false, 0, ref duplicates);
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

    /// <param name="checkZ">If true, also require Z proximity for merging (breakline mode).</param>
    /// <param name="zTolSq">Squared Z tolerance when checkZ is true.</param>
    private static int TryInsert(Dictionary<(long, long), List<int>> grid,
                                  List<double> xyList, List<double> zList,
                                  double x, double y, double z,
                                  double invCell, double tolSq,
                                  bool checkZ, double zTolSq,
                                  ref int duplicates)
    {
        long cx = (long)Math.Floor(x * invCell);
        long cy = (long)Math.Floor(y * invCell);

        for (long dx = -1; dx <= 1; dx++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                var key = (cx + dx, cy + dy);
                if (grid.TryGetValue(key, out var indices))
                {
                    foreach (int existingIdx in indices)
                    {
                        double ex = xyList[existingIdx * 2];
                        double ey = xyList[existingIdx * 2 + 1];
                        double dx2 = x - ex;
                        double dy2 = y - ey;
                        double distSq = dx2 * dx2 + dy2 * dy2;
                        if (distSq < tolSq)
                        {
                            // Z-aware check: don't merge breakline points with different Z
                            if (checkZ)
                            {
                                double dz = z - zList[existingIdx];
                                if (dz * dz >= zTolSq)
                                    continue; // different elevation → keep both
                            }

                            duplicates++;
                            return existingIdx;
                        }
                    }
                }
            }
        }

        // New unique point
        int newIdx = xyList.Count / 2;
        var cellKey = (cx, cy);
        if (!grid.TryGetValue(cellKey, out var list))
        {
            list = new List<int>();
            grid[cellKey] = list;
        }
        list.Add(newIdx);

        xyList.Add(x);
        xyList.Add(y);
        zList.Add(double.IsNaN(z) || double.IsInfinity(z) ? 0.0 : z);
        return newIdx;
    }
}
