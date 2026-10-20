namespace MoleHill.Core.Geometry;

/// <summary>
/// Which of a set of plan polylines cross themselves or each other. Segments are bucketed on a uniform grid,
/// so a few thousand drawn curves are checked in linear time; only strict crossings count, never a shared
/// end, a touch or a collinear overlap (outline loops meet at their junctions). A chain folding straight back
/// on itself at a vertex counts too: sampled finely, that is a cusp whose curve crosses within one chord.
/// </summary>
internal static class PolylineCrossings
{
    /// <summary>Indices of the chains (XYZ, stride 3) involved in at least one strict plan crossing.</summary>
    public static HashSet<int> Find(IReadOnlyList<(double[] Points, bool Closed)> chains)
    {
        var segments = new List<(int Chain, int Index, double X0, double Y0, double X1, double Y1)>();
        var involved = new HashSet<int>();
        double length = 0;
        for (int c = 0; c < chains.Count; c++)
        {
            double[] p = chains[c].Points;
            int count = p.Length / 3, last = chains[c].Closed && count > 2 ? count : count - 1;
            for (int i = 0; i < last; i++)
            {
                int j = (i + 1) % count;
                if ((i + 1 < last || chains[c].Closed) && Folds(p, i, j, (j + 1) % count)) involved.Add(c);
                segments.Add((c, i, p[i * 3], p[i * 3 + 1], p[j * 3], p[j * 3 + 1]));
                length += Math.Abs(p[j * 3] - p[i * 3]) + Math.Abs(p[j * 3 + 1] - p[i * 3 + 1]);
            }
        }
        if (segments.Count < 2) return involved;
        double cell = Math.Max(length / segments.Count * 4, 1e-9);
        var buckets = new Dictionary<(long, long), List<int>>();
        for (int s = 0; s < segments.Count; s++)
        {
            var e = segments[s];
            long x0 = Cell(Math.Min(e.X0, e.X1)), x1 = Cell(Math.Max(e.X0, e.X1));
            long y0 = Cell(Math.Min(e.Y0, e.Y1)), y1 = Cell(Math.Max(e.Y0, e.Y1));
            if ((x1 - x0 + 1) * (y1 - y0 + 1) <= 64)
            {
                for (long x = x0; x <= x1; x++)
                    for (long y = y0; y <= y1; y++)
                        Add(x, y, s);
                continue;
            }
            // A long diagonal segment: walk it in half cells, taking each step's cell and its neighbours.
            var seen = new HashSet<(long, long)>();
            int steps = (int)Math.Ceiling(Math.Sqrt((e.X1 - e.X0) * (e.X1 - e.X0) + (e.Y1 - e.Y0) * (e.Y1 - e.Y0)) / cell * 2);
            for (int k = 0; k <= steps; k++)
            {
                long cx = Cell(e.X0 + (e.X1 - e.X0) * k / steps), cy = Cell(e.Y0 + (e.Y1 - e.Y0) * k / steps);
                for (long x = cx - 1; x <= cx + 1; x++)
                    for (long y = cy - 1; y <= cy + 1; y++)
                        if (seen.Add((x, y))) Add(x, y, s);
            }
        }
        foreach (var list in buckets.Values)
            for (int a = 0; a < list.Count; a++)
                for (int b = a + 1; b < list.Count; b++)
                {
                    var p = segments[list[a]]; var q = segments[list[b]];
                    if (involved.Contains(p.Chain) && involved.Contains(q.Chain)) continue;
                    if (p.Chain == q.Chain && Adjacent(p.Index, q.Index, chains[p.Chain])) continue;
                    if (Geometry2D.SegmentsCrossStrictly(p.X0, p.Y0, p.X1, p.Y1, q.X0, q.Y0, q.X1, q.Y1))
                    { involved.Add(p.Chain); involved.Add(q.Chain); }
                }
        return involved;

        long Cell(double value) => (long)Math.Floor(value / cell);
        void Add(long x, long y, int segment)
        {
            if (!buckets.TryGetValue((x, y), out var list)) buckets[(x, y)] = list = new List<int>();
            list.Add(segment);
        }
    }

    private static bool Folds(double[] p, int a, int b, int c)
    {
        double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], vx = p[c * 3] - p[b * 3], vy = p[c * 3 + 1] - p[b * 3 + 1];
        double lengths = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        return lengths > 0 && (ux * vx + uy * vy) < -lengths * FoldCosine;
    }

    // A turn beyond 178 degrees.
    private const double FoldCosine = 0.99939;

    private static bool Adjacent(int i, int j, (double[] Points, bool Closed) chain)
    {
        int count = chain.Points.Length / 3;
        return Math.Abs(i - j) <= 1 || chain.Closed && Math.Abs(i - j) == count - 1;
    }
}
