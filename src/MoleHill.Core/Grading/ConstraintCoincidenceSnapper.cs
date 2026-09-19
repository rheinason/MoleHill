using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Snaps constraint points onto nearby terrain vertices and edges.
///
/// The index may be clipped to a caller-declared region of interest. Indexing a whole terrain to snap
/// a handful of grading constraints was measured at 0.91 s of a 1.00 s <c>PadGrader.CreateConstraints</c>
/// on a 186,501-edge terrain, and a pad influences a few percent of it. Clipping is exact rather than
/// approximate: a query at a point only ever reaches members whose tolerance-expanded bounds meet that
/// point's own tolerance box, so every member that could win a query lying wholly inside the region is
/// inside the region too. A query that escapes the declared region abandons it and rebuilds over the
/// whole mesh, so a badly chosen region costs speed and never geometry.
/// </summary>
internal sealed class ConstraintCoincidenceSnapper
{
    private readonly double[] _vertices;
    private readonly int _vertexCount;
    private readonly int[] _faces;
    private readonly int _faceCount;
    private VertexRef[] _vertexRefs = Array.Empty<VertexRef>();
    private SpatialHashGrid2D _vertexIndex;
    private EdgeRef[] _edges = Array.Empty<EdgeRef>();
    private SpatialHashGrid2D _edgeIndex;
    private readonly double _tolerance;
    private readonly double _toleranceSquared;
    private SpatialHashGrid2D.QueryScratch _vertexScratch;
    private SpatialHashGrid2D.QueryScratch _edgeScratch;
    private readonly List<int> _vertexCandidates = new(8);
    private readonly List<int> _edgeCandidates = new(16);
    private Bounds2D? _region;

    private readonly record struct VertexRef(double X, double Y);
    private readonly record struct EdgeRef(double Ax, double Ay, double Bx, double By);

    /// <summary>Indexed vertex and deduplicated-edge counts, for tests and timing diagnostics.</summary>
    internal (int Vertices, int Edges) IndexedCounts => (_vertexRefs.Length, _edges.Length);

    /// <summary>True once a query outside the declared region forced the full-mesh rebuild.</summary>
    internal bool RegionWasAbandoned { get; private set; }

    /// <summary>
    /// Builds a snapper indexed only where <paramref name="constraints"/> can actually reach. Construct
    /// it after the constraints exist, not before: the region is then exact by construction rather than
    /// an estimate of a pad or path influence envelope.
    /// </summary>
    internal static ConstraintCoincidenceSnapper ForConstraints(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double tolerance,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        return new ConstraintCoincidenceSnapper(
            vertices,
            vertexCount,
            faces,
            faceCount,
            tolerance,
            RegionCovering(constraints, tolerance));
    }

    /// <summary>
    /// The tolerance-expanded XY bounds of every constraint point, or null when there is nothing to
    /// snap — null means "index everything", which is the safe reading of an unknown region.
    /// </summary>
    internal static Bounds2D? RegionCovering(
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        bool any = false;
        for (int c = 0; c < constraints.Count; c++)
        {
            SurfaceRemesher.ConstraintPolyline constraint = constraints[c];
            for (int i = 0; i < constraint.PointCount; i++)
            {
                double x = constraint.Points[i * 3];
                double y = constraint.Points[i * 3 + 1];
                if (double.IsNaN(x) || double.IsNaN(y))
                    return null;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                any = true;
            }
        }

        if (!any)
            return null;

        // One tolerance for the query box around each point, one more so a member whose own expanded
        // bounds only just reach that box is still inside the region.
        double pad = Math.Abs(tolerance) * 2.0;
        return new Bounds2D(minX - pad, maxX + pad, minY - pad, maxY + pad);
    }

    internal ConstraintCoincidenceSnapper(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double tolerance,
        Bounds2D? region = null)
    {
        _vertices = vertices;
        _vertexCount = vertexCount;
        _faces = faces;
        _faceCount = faceCount;
        _tolerance = tolerance;
        _toleranceSquared = tolerance * tolerance;
        _region = region;

        BuildIndex(region);
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_vertexIndex), nameof(_edgeIndex), nameof(_vertexScratch), nameof(_edgeScratch))]
    private void BuildIndex(Bounds2D? region)
    {
        double tolerance = _tolerance;
        double[] vertices = _vertices;
        int seedCapacity = region == null ? _vertexCount : Math.Min(_vertexCount, 1024);

        var vertexRefs = new List<VertexRef>(Math.Max(seedCapacity, 1));
        var vertexBounds = new List<Bounds2D>(Math.Max(seedCapacity, 1));
        for (int i = 0; i < _vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            var bounds = new Bounds2D(x - tolerance, x + tolerance, y - tolerance, y + tolerance);
            if (region is { } vertexRegion && !bounds.Intersects(vertexRegion))
                continue;

            vertexRefs.Add(new VertexRef(x, y));
            vertexBounds.Add(bounds);
        }

        _vertexRefs = vertexRefs.ToArray();
        _vertexIndex = SpatialHashGrid2D.Build(vertexBounds.ToArray());
        _vertexScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(_vertexRefs.Length, 1));

        int edgeCapacity = region == null ? Math.Max(_faceCount * 2, 1) : Math.Min(Math.Max(_faceCount * 2, 1), 1024);
        var edgeKeys = new HashSet<long>(edgeCapacity, IndexedMeshTools.EdgeKeyComparer.Instance);
        var edges = new List<EdgeRef>(edgeCapacity);
        var edgeBounds = new List<Bounds2D>(edgeCapacity);
        for (int f = 0; f < _faceCount; f++)
        {
            AddEdge(_faces[f * 3], _faces[f * 3 + 1]);
            AddEdge(_faces[f * 3 + 1], _faces[f * 3 + 2]);
            AddEdge(_faces[f * 3 + 2], _faces[f * 3]);
        }

        _edges = edges.ToArray();
        _edgeIndex = SpatialHashGrid2D.Build(edgeBounds.ToArray());
        _edgeScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(_edges.Length, 1));

        void AddEdge(int a, int b)
        {
            double ax = vertices[a * 3];
            double ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3];
            double by = vertices[b * 3 + 1];
            var bounds = new Bounds2D(
                Math.Min(ax, bx) - tolerance,
                Math.Max(ax, bx) + tolerance,
                Math.Min(ay, by) - tolerance,
                Math.Max(ay, by) + tolerance);

            // Region test before the hash: the point of clipping is to not pay per terrain edge.
            if (region is { } edgeRegion && !bounds.Intersects(edgeRegion))
                return;

            if (!edgeKeys.Add(IndexedMeshTools.GetEdgeKey(a, b)))
                return;

            edges.Add(new EdgeRef(ax, ay, bx, by));
            edgeBounds.Add(bounds);
        }
    }

    internal void SnapPoint(double x, double y, out double snappedX, out double snappedY)
    {
        var queryBounds = new Bounds2D(x - _tolerance, x + _tolerance, y - _tolerance, y + _tolerance);

        // A query reaching outside the declared region could miss a member that was never indexed.
        // Abandon the region and index the whole mesh rather than return a silently different answer.
        if (_region is { } region && !Contains(region, queryBounds))
        {
            _region = null;
            RegionWasAbandoned = true;
            BuildIndex(null);
        }

        _vertexIndex.GatherCandidates(queryBounds, _vertexCandidates, _vertexScratch);
        double bestDistanceSquared = _toleranceSquared;
        snappedX = x;
        snappedY = y;
        bool foundVertex = false;
        foreach (int candidate in _vertexCandidates)
        {
            VertexRef vertex = _vertexRefs[candidate];
            double dx = vertex.X - x;
            double dy = vertex.Y - y;
            double distanceSquared = (dx * dx) + (dy * dy);
            if (distanceSquared > bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            snappedX = vertex.X;
            snappedY = vertex.Y;
            foundVertex = true;
        }

        if (foundVertex)
            return;

        _edgeIndex.GatherCandidates(queryBounds, _edgeCandidates, _edgeScratch);
        foreach (int candidate in _edgeCandidates)
        {
            EdgeRef edge = _edges[candidate];
            double t = ParameterOnSegment(edge.Ax, edge.Ay, edge.Bx, edge.By, x, y);
            double projectedX = edge.Ax + ((edge.Bx - edge.Ax) * t);
            double projectedY = edge.Ay + ((edge.By - edge.Ay) * t);
            double dx = projectedX - x;
            double dy = projectedY - y;
            double distanceSquared = (dx * dx) + (dy * dy);
            if (distanceSquared > bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            snappedX = projectedX;
            snappedY = projectedY;
        }
    }

    internal SurfaceRemesher.ConstraintPolyline SnapConstraintPolyline(SurfaceRemesher.ConstraintPolyline constraint)
    {
        if (constraint.PointCount < 2)
            return constraint;

        var points = new List<double>(constraint.PointCount * 3);
        for (int i = 0; i < constraint.PointCount; i++)
        {
            SnapPoint(
                constraint.Points[i * 3],
                constraint.Points[i * 3 + 1],
                out double snappedX,
                out double snappedY);

            double z = constraint.Points[i * 3 + 2];
            if (points.Count >= 3)
            {
                double dx = points[^3] - snappedX;
                double dy = points[^2] - snappedY;
                if ((dx * dx) + (dy * dy) <= _toleranceSquared)
                {
                    points[^3] = snappedX;
                    points[^2] = snappedY;
                    points[^1] = z;
                    continue;
                }
            }

            points.Add(snappedX);
            points.Add(snappedY);
            points.Add(z);
        }

        int pointCount = points.Count / 3;
        if (constraint.IsClosed && pointCount >= 3)
        {
            double dx = points[0] - points[^3];
            double dy = points[1] - points[^2];
            if ((dx * dx) + (dy * dy) <= _toleranceSquared)
            {
                points[^3] = points[0];
                points[^2] = points[1];
            }
        }

        return pointCount >= 2
            ? new SurfaceRemesher.ConstraintPolyline(points.ToArray(), pointCount, constraint.IsClosed, constraint.PreserveInputElevation)
            : new SurfaceRemesher.ConstraintPolyline(Array.Empty<double>(), 0, constraint.IsClosed, constraint.PreserveInputElevation);
    }

    private static bool Contains(in Bounds2D outer, in Bounds2D inner)
    {
        return inner.MinX >= outer.MinX && inner.MaxX <= outer.MaxX &&
               inner.MinY >= outer.MinY && inner.MaxY <= outer.MaxY;
    }

    private static double ParameterOnSegment(double ax, double ay, double bx, double by, double px, double py)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0.0, 1.0);
    }
}
