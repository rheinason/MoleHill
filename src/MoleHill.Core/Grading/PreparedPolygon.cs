namespace MoleHill.Core.Grading;

/// <summary>
/// A closed XY loop preprocessed for repeated containment and proximity queries.
/// </summary>
/// <remarks>
/// <para>
/// The answers are identical to <see cref="GradingGeometry2D.PointInPolygon"/> and
/// <see cref="GradingGeometry2D.DistanceToPolygon"/>; only the work to reach them changes. Both of
/// those walk every edge for every query, so filtering 1M input points against a detailed boundary, or
/// classifying every face centroid against a clipped loop, costs points x edges.
/// </para>
/// <para>
/// Two accelerations, both exact:
/// </para>
/// <list type="bullet">
/// <item>Loop bounds. A point outside them is outside the loop, and a point further than
/// <c>margin</c> from them is further than <c>margin</c> from every edge.</item>
/// <item>For loops above <see cref="IndexThreshold"/> vertices, edges are bucketed by the Y range they
/// span. The crossing test only toggles for edges straddling the query's Y, and parity is independent
/// of the order the toggling edges are visited in, so restricting the walk to the query's bucket
/// returns the same answer. Below the threshold the linear walk wins and no index is built.</item>
/// </list>
/// </remarks>
public sealed class PreparedPolygon
{
    /// <summary>Loops smaller than this are cheaper to walk than to index.</summary>
    public const int IndexThreshold = 32;

    private readonly double[] _xy;
    private readonly int _vertexCount;

    // Y-bucketed edge index, or null for a small loop. _bucketEdges holds edge indices (an edge is the
    // segment from vertex i to vertex i-1, matching the crossing walk) grouped by bucket.
    private readonly int[]? _bucketStart;
    private readonly int[]? _bucketEdges;
    private readonly double _invBucketHeight;
    private readonly int _bucketCount;

    // A loop carrying a non-finite coordinate has no usable bounds: rejecting against them could
    // discard a point the linear crossing walk would have accepted. Such a loop keeps the linear
    // path, so the answer is whatever it always was.
    private readonly bool _boundsUsable;

    private PreparedPolygon(double[] xy, int vertexCount, double minX, double maxX, double minY, double maxY, bool boundsUsable)
    {
        _xy = xy;
        _vertexCount = vertexCount;
        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
        _boundsUsable = boundsUsable;

        if (!boundsUsable || vertexCount < IndexThreshold || maxY <= minY)
            return;

        _bucketCount = Math.Clamp(vertexCount / 4, 8, 4096);
        _invBucketHeight = _bucketCount / (maxY - minY);

        var counts = new int[_bucketCount + 1];
        for (int i = 0, j = vertexCount - 1; i < vertexCount; j = i++)
        {
            GetEdgeBuckets(i, j, out int first, out int last);
            for (int bucket = first; bucket <= last; bucket++)
                counts[bucket + 1]++;
        }

        for (int bucket = 1; bucket < counts.Length; bucket++)
            counts[bucket] += counts[bucket - 1];

        _bucketStart = counts;
        _bucketEdges = new int[counts[_bucketCount]];
        var cursor = new int[_bucketCount];
        Array.Copy(counts, cursor, _bucketCount);
        for (int i = 0, j = vertexCount - 1; i < vertexCount; j = i++)
        {
            GetEdgeBuckets(i, j, out int first, out int last);
            for (int bucket = first; bucket <= last; bucket++)
                _bucketEdges[cursor[bucket]++] = i;
        }
    }

    public double MinX { get; }

    public double MaxX { get; }

    public double MinY { get; }

    public double MaxY { get; }

    public int VertexCount => _vertexCount;

    /// <summary>
    /// Prepares a flat XY loop. Returns null when it has fewer than three vertices. A loop containing a
    /// non-finite coordinate is still prepared, but keeps the linear walk rather than rejecting against
    /// bounds it cannot trust.
    /// </summary>
    public static PreparedPolygon? TryCreate(double[] xy, int vertexCount)
    {
        if (xy == null || vertexCount < 3 || xy.Length < vertexCount * 2)
            return null;

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        bool boundsUsable = true;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = xy[i * 2];
            double y = xy[(i * 2) + 1];
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                boundsUsable = false;
                continue;
            }

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        if (minX > maxX || minY > maxY)
            boundsUsable = false;

        return new PreparedPolygon(xy, vertexCount, minX, maxX, minY, maxY, boundsUsable);
    }

    /// <summary>Prepares each loop with at least three vertices; shorter ones are dropped.</summary>
    public static List<PreparedPolygon> CreateAll(IReadOnlyList<double[]> loopsXy)
    {
        var prepared = new List<PreparedPolygon>(loopsXy.Count);
        foreach (double[] loop in loopsXy)
        {
            PreparedPolygon? polygon = TryCreate(loop, loop.Length / 2);
            if (polygon != null)
                prepared.Add(polygon);
        }

        return prepared;
    }

    /// <summary>Same result as <see cref="GradingGeometry2D.PointInPolygon"/>.</summary>
    public bool Contains(double x, double y)
    {
        if (!_boundsUsable)
            return GradingGeometry2D.PointInPolygon(x, y, _xy, _vertexCount);

        if (x < MinX || x > MaxX || y < MinY || y > MaxY)
            return false;

        if (_bucketStart == null || _bucketEdges == null)
            return GradingGeometry2D.PointInPolygon(x, y, _xy, _vertexCount);

        int bucket = BucketOf(y);
        bool inside = false;
        for (int slot = _bucketStart[bucket]; slot < _bucketStart[bucket + 1]; slot++)
        {
            int i = _bucketEdges[slot];
            int j = i == 0 ? _vertexCount - 1 : i - 1;
            double xi = _xy[i * 2];
            double yi = _xy[(i * 2) + 1];
            double xj = _xy[j * 2];
            double yj = _xy[(j * 2) + 1];
            if ((yi > y) != (yj > y) && x < (((xj - xi) * (y - yi) / (yj - yi)) + xi))
                inside = !inside;
        }

        return inside;
    }

    /// <summary>
    /// True when the point is within <paramref name="margin"/> of the loop's boundary. Equivalent to
    /// <c>GradingGeometry2D.DistanceToPolygon(...) &lt;= margin</c>, without measuring edges that the
    /// loop bounds already place further away.
    /// </summary>
    public bool IsWithin(double x, double y, double margin)
    {
        if (margin < 0.0)
            return false;

        if (_boundsUsable &&
            (x < MinX - margin || x > MaxX + margin || y < MinY - margin || y > MaxY + margin))
        {
            return false;
        }

        return GradingGeometry2D.DistanceToPolygon(x, y, _xy, _vertexCount) <= margin;
    }

    private int BucketOf(double y)
    {
        return Math.Clamp((int)((y - MinY) * _invBucketHeight), 0, _bucketCount - 1);
    }

    private void GetEdgeBuckets(int i, int j, out int first, out int last)
    {
        double yi = _xy[(i * 2) + 1];
        double yj = _xy[(j * 2) + 1];
        first = BucketOf(Math.Min(yi, yj));
        last = BucketOf(Math.Max(yi, yj));
    }
}
