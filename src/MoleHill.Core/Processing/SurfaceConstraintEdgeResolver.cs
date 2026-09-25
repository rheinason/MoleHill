using MoleHill.Core.Engine;

namespace MoleHill.Core.Processing;

/// <summary>Maps persistent 3D constraint polylines to coincident edges of an existing 2.5D mesh.</summary>
public static class SurfaceConstraintEdgeResolver
{
    private readonly record struct IndexedSegment(
        double Ax, double Ay, double Az,
        double Bx, double By, double Bz,
        bool CheckElevation);

    /// <summary>
    /// Resolves each constraint on its own, so one that no longer lies on the mesh does not sink the rest.
    /// <paramref name="resolved"/> says which constraints were represented; only their edges are returned.
    /// </summary>
    public static int[] ResolveEach(
        double[] vertices,
        int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance,
        out bool[] resolved)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        resolved = new bool[constraints.Count];
        var required = new List<int>();
        for (int i = 0; i < constraints.Count; i++)
        {
            if (!TryResolve(vertices, faces, [constraints[i]], tolerance, out int[] segments, out _))
                continue;

            resolved[i] = true;
            required.AddRange(segments);
        }

        return required.ToArray();
    }

    public static bool TryResolve(
        double[] vertices,
        int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance,
        out int[] requiredSegments,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(constraints);
        requiredSegments = Array.Empty<int>();
        failure = null;
        if (vertices.Length % 3 != 0 || faces.Length % 3 != 0)
        {
            failure = "Mesh arrays must contain XYZ vertices and triangle indices.";
            return false;
        }
        if (!double.IsFinite(tolerance))
        {
            failure = "Constraint matching tolerance must be finite.";
            return false;
        }
        if (constraints.Count == 0)
            return true;

        double effectiveTolerance = Math.Max(Math.Abs(tolerance), 1e-10);
        var segments = new List<IndexedSegment>();
        var bounds = new List<Bounds2D>();
        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            int pointCount = Math.Min(constraint.PointCount, constraint.Points.Length / 3);
            for (int point = 1; point < pointCount; point++)
                AddSegment(constraint, point - 1, point);
            if (constraint.IsClosed && pointCount > 2 &&
                (constraint.Points[0] != constraint.Points[(pointCount - 1) * 3] ||
                 constraint.Points[1] != constraint.Points[(pointCount - 1) * 3 + 1]))
                AddSegment(constraint, pointCount - 1, 0);
        }

        if (segments.Count == 0)
            return true;

        SpatialHashGrid2D index = SpatialHashGrid2D.Build(bounds.ToArray());
        var segmentMatched = new bool[segments.Count];
        var scratch = new SpatialHashGrid2D.QueryScratch(segments.Count);
        var candidates = new List<int>();
        var meshEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        var required = new List<int>();
        string? localFailure = null;
        for (int face = 0; face < faces.Length / 3; face++)
        {
            int offset = face * 3;
            AddMeshEdge(faces[offset], faces[offset + 1]);
            AddMeshEdge(faces[offset + 1], faces[offset + 2]);
            AddMeshEdge(faces[offset + 2], faces[offset]);
            if (localFailure != null)
            {
                failure = localFailure;
                return false;
            }
        }

        if (segmentMatched.Any(matched => !matched))
        {
            failure = "A persistent constraint is not represented by connected edges on the incoming mesh.";
            return false;
        }

        requiredSegments = required.ToArray();
        return true;

        void AddSegment(SurfaceRemesher.ConstraintPolyline constraint, int a, int b)
        {
            double ax = constraint.Points[a * 3], ay = constraint.Points[a * 3 + 1], az = constraint.Points[a * 3 + 2];
            double bx = constraint.Points[b * 3], by = constraint.Points[b * 3 + 1], bz = constraint.Points[b * 3 + 2];
            double dx = bx - ax, dy = by - ay;
            if ((dx * dx) + (dy * dy) <= effectiveTolerance * effectiveTolerance)
                return;
            segments.Add(new IndexedSegment(ax, ay, az, bx, by, bz, constraint.PreserveInputElevation));
            bounds.Add(new Bounds2D(
                Math.Min(ax, bx) - effectiveTolerance, Math.Max(ax, bx) + effectiveTolerance,
                Math.Min(ay, by) - effectiveTolerance, Math.Max(ay, by) + effectiveTolerance));
        }

        void AddMeshEdge(int a, int b)
        {
            int vertexCount = vertices.Length / 3;
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount)
            {
                localFailure = "The incoming mesh contains an out-of-range face index.";
                return;
            }
            long key = IndexedMeshTools.GetEdgeKey(a, b);
            if (!meshEdges.Add(key))
                return;
            double ax = vertices[a * 3], ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3], by = vertices[b * 3 + 1];
            var edgeBounds = new Bounds2D(
                Math.Min(ax, bx) - effectiveTolerance, Math.Max(ax, bx) + effectiveTolerance,
                Math.Min(ay, by) - effectiveTolerance, Math.Max(ay, by) + effectiveTolerance);
            index.GatherCandidates(edgeBounds, candidates, scratch);
            foreach (int candidate in candidates)
            {
                IndexedSegment segment = segments[candidate];
                if (!PointOnSegment(ax, ay, segment, effectiveTolerance, out double ta) ||
                    !PointOnSegment(bx, by, segment, effectiveTolerance, out double tb))
                    continue;

                if (segment.CheckElevation)
                {
                    double expectedA = segment.Az + ((segment.Bz - segment.Az) * ta);
                    double expectedB = segment.Az + ((segment.Bz - segment.Az) * tb);
                    if (Math.Abs(vertices[a * 3 + 2] - expectedA) > effectiveTolerance * 4.0 ||
                        Math.Abs(vertices[b * 3 + 2] - expectedB) > effectiveTolerance * 4.0)
                    {
                        localFailure = "A persistent elevation constraint conflicts with the incoming surface.";
                        return;
                    }
                }

                required.Add(a);
                required.Add(b);
                segmentMatched[candidate] = true;
                return;
            }
        }
    }

    private static bool PointOnSegment(
        double x,
        double y,
        IndexedSegment segment,
        double tolerance,
        out double parameter)
    {
        double dx = segment.Bx - segment.Ax, dy = segment.By - segment.Ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        parameter = (((x - segment.Ax) * dx) + ((y - segment.Ay) * dy)) / lengthSquared;
        double parameterTolerance = tolerance / Math.Sqrt(lengthSquared);
        if (parameter < -parameterTolerance || parameter > 1.0 + parameterTolerance)
            return false;
        parameter = Math.Clamp(parameter, 0.0, 1.0);
        double px = segment.Ax + (dx * parameter), py = segment.Ay + (dy * parameter);
        double ox = x - px, oy = y - py;
        return (ox * ox) + (oy * oy) <= tolerance * tolerance;
    }
}
