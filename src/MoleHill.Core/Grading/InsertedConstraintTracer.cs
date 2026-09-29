using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Recovers a constraint polyline as it was actually inserted into a mesh: the chain of mesh vertices,
/// joined by mesh edges, that runs along it. Local insertion splits a line wherever it crosses a mesh edge
/// and snaps it through nearby vertices, so the inserted line has vertices the drawn one does not.
/// </summary>
/// <remarks>
/// A constraint persisted for a later stage must be the inserted form. A later constrained rebuild seeds
/// every mesh vertex; given the drawn line, it inserts each segment within rounding of the split vertices
/// and leaves a zero-area cap at each one — the same failure that persisting Triangulate's raw lines
/// produced (~11,000 caps on one terrain, which stair cleanup then deleted into holes).
/// </remarks>
internal static class InsertedConstraintTracer
{
    /// <summary>
    /// Traces <paramref name="constraint"/> through the mesh. Returns null when the vertices along it do
    /// not form one edge-connected chain from its start to its end, so the caller can keep the drawn line.
    /// </summary>
    public static SurfaceRemesher.ConstraintPolyline? Trace(
        SurfaceRemesher.ConstraintPolyline constraint,
        double[] vertices,
        int vertexCount,
        SpatialHashGrid2D vertexGrid,
        MeshVertexAdjacency adjacency,
        double tolerance)
    {
        int pointCount = constraint.PointCount;
        if (pointCount < 2)
            return null;

        double[] p = constraint.Points;
        int segmentCount = constraint.IsClosed ? pointCount : pointCount - 1;
        var cumulative = new double[segmentCount + 1];
        for (int i = 0; i < segmentCount; i++)
        {
            int j = (i + 1) % pointCount;
            cumulative[i + 1] = cumulative[i] + Math.Sqrt(Sq(p[j * 3] - p[i * 3]) + Sq(p[j * 3 + 1] - p[i * 3 + 1]));
        }

        double total = cumulative[segmentCount];
        if (total <= tolerance)
            return null;

        // Arc parameter of every vertex within tolerance of the line (closest segment wins).
        var along = new Dictionary<int, (double S, double Distance)>();
        var candidates = new List<int>();
        double toleranceSquared = tolerance * tolerance;
        for (int i = 0; i < segmentCount; i++)
        {
            int j = (i + 1) % pointCount;
            double ax = p[i * 3], ay = p[i * 3 + 1], bx = p[j * 3], by = p[j * 3 + 1];
            double dx = bx - ax, dy = by - ay;
            double lengthSquared = (dx * dx) + (dy * dy);
            vertexGrid.GatherCandidates(
                new Bounds2D(Math.Min(ax, bx) - tolerance, Math.Max(ax, bx) + tolerance, Math.Min(ay, by) - tolerance, Math.Max(ay, by) + tolerance),
                candidates);
            foreach (int v in candidates)
            {
                if ((uint)v >= (uint)vertexCount)
                    continue;
                double px = vertices[v * 3] - ax, py = vertices[v * 3 + 1] - ay;
                double t = lengthSquared > 0 ? Math.Clamp(((px * dx) + (py * dy)) / lengthSquared, 0.0, 1.0) : 0.0;
                double distanceSquared = Sq(px - (t * dx)) + Sq(py - (t * dy));
                if (distanceSquared > toleranceSquared)
                    continue;
                double s = cumulative[i] + (t * Math.Sqrt(lengthSquared));
                if (!along.TryGetValue(v, out var existing) || distanceSquared < existing.Distance)
                    along[v] = (s, distanceSquared);
            }
        }

        // Start at the vertex on the line's first point; walk to the adjacent on-line vertex that advances
        // least, which is the next vertex of the inserted chain.
        int start = -1;
        double bestStart = double.PositiveInfinity;
        foreach ((int v, (double s, double d)) in along)
        {
            if (s <= tolerance && d < bestStart)
            {
                start = v;
                bestStart = d;
            }
        }

        if (start < 0)
            return null;

        var chain = new List<int> { start };
        var visited = new HashSet<int> { start };
        double currentS = 0.0;
        int current = start;
        while (true)
        {
            int next = -1;
            double nextS = double.PositiveInfinity;
            foreach (int n in adjacency.NeighborsOf(current))
            {
                if (visited.Contains(n) || !along.TryGetValue(n, out var info))
                    continue;
                if (info.S > currentS && info.S < nextS)
                {
                    next = n;
                    nextS = info.S;
                }
            }

            if (next < 0)
                break;
            chain.Add(next);
            visited.Add(next);
            current = next;
            currentS = nextS;
        }

        if (constraint.IsClosed)
        {
            // The walk ends on the last vertex before the seam; it must close back onto the start.
            if (chain.Count < 3 || !adjacency.NeighborsContain(current, start) || total - currentS > Math.Max(tolerance, total * 0.5))
                return null;
        }
        else if (total - currentS > tolerance || chain.Count < 2)
        {
            return null;
        }

        var points = new double[chain.Count * 3];
        for (int i = 0; i < chain.Count; i++)
        {
            points[i * 3] = vertices[chain[i] * 3];
            points[i * 3 + 1] = vertices[chain[i] * 3 + 1];
            points[i * 3 + 2] = vertices[chain[i] * 3 + 2];
        }

        return new SurfaceRemesher.ConstraintPolyline(points, chain.Count, constraint.IsClosed, constraint.PreserveInputElevation);
    }

    /// <summary>Traces each constraint, keeping the drawn line where the trace does not complete.</summary>
    public static List<SurfaceRemesher.ConstraintPolyline> TraceAll(
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double tolerance,
        out int traced)
    {
        traced = 0;
        var result = new List<SurfaceRemesher.ConstraintPolyline>(constraints.Count);
        if (constraints.Count == 0)
            return result;

        var bounds = new Bounds2D[vertexCount];
        for (int v = 0; v < vertexCount; v++)
            bounds[v] = Bounds2D.FromPoint(vertices[v * 3], vertices[v * 3 + 1]);
        SpatialHashGrid2D grid = SpatialHashGrid2D.Build(bounds);
        MeshVertexAdjacency adjacency = MeshVertexAdjacency.Build(new List<int>(faces.AsSpan(0, faceCount * 3).ToArray()), faceCount, vertexCount);

        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            SurfaceRemesher.ConstraintPolyline? tracedLine = Trace(constraint, vertices, vertexCount, grid, adjacency, tolerance);
            if (tracedLine is { } line)
            {
                result.Add(line);
                traced++;
            }
            else
            {
                result.Add(constraint);
            }
        }

        return result;
    }

    private static double Sq(double value) => value * value;
}
