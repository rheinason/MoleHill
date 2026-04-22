using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal sealed class ConstraintCoincidenceSnapper
{
    private readonly double[] _vertices;
    private readonly VertexRef[] _vertexRefs;
    private readonly SpatialHashGrid2D _vertexIndex;
    private readonly EdgeRef[] _edges;
    private readonly SpatialHashGrid2D _edgeIndex;
    private readonly double _tolerance;
    private readonly double _toleranceSquared;
    private readonly SpatialHashGrid2D.QueryScratch _vertexScratch;
    private readonly SpatialHashGrid2D.QueryScratch _edgeScratch;
    private readonly List<int> _vertexCandidates = new(8);
    private readonly List<int> _edgeCandidates = new(16);

    private readonly record struct VertexRef(double X, double Y);
    private readonly record struct EdgeRef(double Ax, double Ay, double Bx, double By);

    internal ConstraintCoincidenceSnapper(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double tolerance)
    {
        _vertices = vertices;
        _tolerance = tolerance;
        _toleranceSquared = tolerance * tolerance;

        _vertexRefs = new VertexRef[vertexCount];
        var vertexBounds = new Bounds2D[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            _vertexRefs[i] = new VertexRef(x, y);
            vertexBounds[i] = new Bounds2D(x - tolerance, x + tolerance, y - tolerance, y + tolerance);
        }

        _vertexIndex = SpatialHashGrid2D.Build(vertexBounds);
        _vertexScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(vertexCount, 1));

        var edgeKeys = new HashSet<long>(faceCount * 3, IndexedMeshTools.EdgeKeyComparer.Instance);
        var edges = new List<EdgeRef>(faceCount * 2);
        var edgeBounds = new List<Bounds2D>(faceCount * 2);
        for (int f = 0; f < faceCount; f++)
        {
            AddEdge(faces[f * 3], faces[f * 3 + 1]);
            AddEdge(faces[f * 3 + 1], faces[f * 3 + 2]);
            AddEdge(faces[f * 3 + 2], faces[f * 3]);
        }

        _edges = edges.ToArray();
        _edgeIndex = SpatialHashGrid2D.Build(edgeBounds.ToArray());
        _edgeScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(_edges.Length, 1));

        void AddEdge(int a, int b)
        {
            long key = IndexedMeshTools.GetEdgeKey(a, b);
            if (!edgeKeys.Add(key))
                return;

            double ax = vertices[a * 3];
            double ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3];
            double by = vertices[b * 3 + 1];
            edges.Add(new EdgeRef(ax, ay, bx, by));
            edgeBounds.Add(new Bounds2D(
                Math.Min(ax, bx) - tolerance,
                Math.Max(ax, bx) + tolerance,
                Math.Min(ay, by) - tolerance,
                Math.Max(ay, by) + tolerance));
        }
    }

    internal void SnapPoint(double x, double y, out double snappedX, out double snappedY)
    {
        var queryBounds = new Bounds2D(x - _tolerance, x + _tolerance, y - _tolerance, y + _tolerance);

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
