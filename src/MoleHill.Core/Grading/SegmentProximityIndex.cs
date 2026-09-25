using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Exact nearest-segment distance queries against a fixed set of 2D segments.
/// </summary>
/// <remarks>
/// <para>
/// Checking every source vertex against every target segment is O(source x target), which turns
/// quadratic when both loops are detailed. This returns the same distance a full scan returns — not an
/// approximation, and not a fixed-radius query, which cannot report the true distance for a point that
/// misses everything by a long way.
/// </para>
/// <para>
/// The search grows a query box until the best distance found inside it is no larger than the box's
/// half-width. At that point every unexamined segment lies outside the box and is therefore further
/// away, so the best found is the true minimum. If the box grows past the whole index extent without
/// finding anything — a distant miss — it falls back to scanning every segment, which keeps distant
/// misses exact rather than clamped.
/// </para>
/// </remarks>
internal sealed class SegmentProximityIndex
{
    /// <summary>Below this many segments a linear scan beats building and querying an index.</summary>
    public const int IndexThreshold = 64;

    private readonly double[] _xy;
    private readonly int[] _segmentStarts;
    private readonly int[] _segmentEnds;
    private readonly SpatialHashGrid2D _grid;
    private readonly double _step;
    private readonly double _extentDiagonal;
    private readonly Bounds2D _extent;

    private SegmentProximityIndex(
        double[] xy,
        int[] segmentStarts,
        int[] segmentEnds,
        SpatialHashGrid2D grid,
        Bounds2D extent)
    {
        _xy = xy;
        _segmentStarts = segmentStarts;
        _segmentEnds = segmentEnds;
        _grid = grid;
        _extent = extent;

        double width = extent.MaxX - extent.MinX;
        double height = extent.MaxY - extent.MinY;
        _extentDiagonal = Math.Sqrt((width * width) + (height * height));

        // First query box: roughly one grid cell, so a point sitting on the geometry usually resolves
        // in a single pass.
        double span = Math.Max(width, height);
        _step = span > 0.0
            ? Math.Max(span / Math.Max(8.0, Math.Sqrt(segmentStarts.Length)), 1e-12)
            : 1e-12;
    }

    public int SegmentCount => _segmentStarts.Length;

    /// <summary>
    /// Indexes the closed loop's segments. Returns null when the loop is too small to be worth
    /// indexing, in which case the caller keeps its linear scan.
    /// </summary>
    public static SegmentProximityIndex? TryCreateForClosedLoop(double[] loopXy, int vertexCount)
    {
        if (loopXy == null || vertexCount < IndexThreshold || loopXy.Length < vertexCount * 2)
            return null;

        var starts = new int[vertexCount];
        var ends = new int[vertexCount];
        var bounds = new Bounds2D[vertexCount];
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;

        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            starts[i] = i;
            ends[i] = next;

            double ax = loopXy[i * 2], ay = loopXy[(i * 2) + 1];
            double bx = loopXy[next * 2], by = loopXy[(next * 2) + 1];
            if (!double.IsFinite(ax) || !double.IsFinite(ay) || !double.IsFinite(bx) || !double.IsFinite(by))
                return null;

            bounds[i] = new Bounds2D(Math.Min(ax, bx), Math.Max(ax, bx), Math.Min(ay, by), Math.Max(ay, by));
            minX = Math.Min(minX, bounds[i].MinX);
            maxX = Math.Max(maxX, bounds[i].MaxX);
            minY = Math.Min(minY, bounds[i].MinY);
            maxY = Math.Max(maxY, bounds[i].MaxY);
        }

        return new SegmentProximityIndex(
            loopXy,
            starts,
            ends,
            SpatialHashGrid2D.Build(bounds),
            new Bounds2D(minX, maxX, minY, maxY));
    }

    /// <summary>
    /// Indexes arbitrary segments given as vertex-index pairs <c>[a0, b0, a1, b1, …]</c> into a flat XY
    /// array. Always builds, however few segments there are; returns null only when there are none or a
    /// coordinate is non-finite.
    /// </summary>
    public static SegmentProximityIndex? TryCreateForSegments(double[] xy, int[] segments, int segmentCount)
    {
        if (xy == null || segments == null || segmentCount <= 0 || segments.Length < segmentCount * 2)
            return null;

        var starts = new int[segmentCount];
        var ends = new int[segmentCount];
        var bounds = new Bounds2D[segmentCount];
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;

        for (int i = 0; i < segmentCount; i++)
        {
            int a = segments[i * 2];
            int b = segments[(i * 2) + 1];
            if ((uint)a >= (uint)(xy.Length / 2) || (uint)b >= (uint)(xy.Length / 2))
                return null;

            starts[i] = a;
            ends[i] = b;
            double ax = xy[a * 2], ay = xy[(a * 2) + 1];
            double bx = xy[b * 2], by = xy[(b * 2) + 1];
            if (!double.IsFinite(ax) || !double.IsFinite(ay) || !double.IsFinite(bx) || !double.IsFinite(by))
                return null;

            bounds[i] = new Bounds2D(Math.Min(ax, bx), Math.Max(ax, bx), Math.Min(ay, by), Math.Max(ay, by));
            minX = Math.Min(minX, bounds[i].MinX);
            maxX = Math.Max(maxX, bounds[i].MaxX);
            minY = Math.Min(minY, bounds[i].MinY);
            maxY = Math.Max(maxY, bounds[i].MaxY);
        }

        return new SegmentProximityIndex(
            xy,
            starts,
            ends,
            SpatialHashGrid2D.Build(bounds),
            new Bounds2D(minX, maxX, minY, maxY));
    }

    /// <summary>Per-caller query buffers. Never share one between threads.</summary>
    public sealed class QueryState
    {
        public QueryState(int segmentCount)
        {
            Scratch = new SpatialHashGrid2D.QueryScratch(segmentCount);
        }

        public SpatialHashGrid2D.QueryScratch Scratch { get; }

        public List<int> Candidates { get; } = new(32);
    }

    /// <summary>The exact distance from the point to the nearest indexed segment.</summary>
    public double NearestDistance(double x, double y, QueryState state)
    {
        // Never start smaller than the point's own distance to the indexed extent: a query box that
        // does not reach the geometry can only waste a round.
        double distanceToExtent = DistanceToExtent(x, y);
        double radius = Math.Max(_step, distanceToExtent + _step);
        double limit = distanceToExtent + _extentDiagonal + _step;

        while (radius <= limit)
        {
            double best = BestWithin(x, y, radius, state);

            // Everything outside the box is further than `radius`, so a best at or inside the box is
            // already the global minimum.
            if (best <= radius)
                return best;

            radius *= 2.0;
        }

        return ScanAll(x, y);
    }

    private double BestWithin(double x, double y, double radius, QueryState state)
    {
        _grid.GatherCandidates(new Bounds2D(x - radius, x + radius, y - radius, y + radius), state.Candidates, state.Scratch);

        double best = double.MaxValue;
        foreach (int segment in state.Candidates)
        {
            double distance = DistanceToSegment(x, y, segment);
            if (distance < best)
                best = distance;
        }

        return best;
    }

    private double ScanAll(double x, double y)
    {
        double best = double.MaxValue;
        for (int segment = 0; segment < _segmentStarts.Length; segment++)
        {
            double distance = DistanceToSegment(x, y, segment);
            if (distance < best)
                best = distance;
        }

        return best;
    }

    private double DistanceToSegment(double px, double py, int segment)
    {
        int a = _segmentStarts[segment];
        int b = _segmentEnds[segment];
        // Deliberately the caller's own formula, so an indexed answer is bit-identical to its scan.
        return SeamValidator.DistancePointToSegment(
            px,
            py,
            _xy[a * 2],
            _xy[(a * 2) + 1],
            _xy[b * 2],
            _xy[(b * 2) + 1]);
    }

    private double DistanceToExtent(double x, double y)
    {
        double dx = Math.Max(Math.Max(_extent.MinX - x, x - _extent.MaxX), 0.0);
        double dy = Math.Max(Math.Max(_extent.MinY - y, y - _extent.MaxY), 0.0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
