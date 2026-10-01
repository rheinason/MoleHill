namespace MoleHill.Core.Engine;

/// <summary>
/// Splits each constraint segment at every input vertex lying on it. A breakline drawn to end on another one
/// (snapped in CAD) leaves its end vertex on the other's segment, but floating point puts it a few ulps to one
/// side. Triangle's exact predicates see a vertex off the line, try to cut the segment where the second line
/// "crosses" it, land on that same vertex, and fail the whole triangulation with "Topological inconsistency
/// after splitting a segment". One such T-junction dropped every breakline and contour of a real terrain to
/// plain Delaunay. Splitting the segment at the vertex states the junction exactly.
/// </summary>
internal static class BreaklineTJunctions
{
    /// <summary>
    /// Returns <paramref name="segments"/> (flat index pairs) with each segment split at every vertex within
    /// <paramref name="tolerance"/> of its interior, in order along it. Returns the input array itself when no
    /// vertex lies on any segment.
    /// </summary>
    public static int[] SplitAtVertices(double[] xyCoords, int[] segments, double tolerance, out int splitCount)
    {
        splitCount = 0;
        int vertexCount = xyCoords.Length / 2;
        int segmentCount = segments.Length / 2;
        if (segmentCount == 0 || vertexCount < 3)
            return segments;

        var bounds = new Bounds2D[segmentCount];
        var valid = new bool[segmentCount];
        for (int s = 0; s < segmentCount; s++)
        {
            int a = segments[s * 2], b = segments[s * 2 + 1];
            if (a < 0 || b < 0 || a >= vertexCount || b >= vertexCount || a == b)
                continue;

            valid[s] = true;
            bounds[s] = new Bounds2D(
                Math.Min(xyCoords[a * 2], xyCoords[b * 2]) - tolerance,
                Math.Max(xyCoords[a * 2], xyCoords[b * 2]) + tolerance,
                Math.Min(xyCoords[a * 2 + 1], xyCoords[b * 2 + 1]) - tolerance,
                Math.Max(xyCoords[a * 2 + 1], xyCoords[b * 2 + 1]) + tolerance);
        }

        SpatialHashGrid2D grid = SpatialHashGrid2D.Build(bounds, valid);
        var scratch = new SpatialHashGrid2D.QueryScratch(segmentCount);
        var candidates = new List<int>(16);
        List<(double T, int Vertex)>?[] splits = new List<(double, int)>?[segmentCount];
        double toleranceSquared = tolerance * tolerance;

        for (int v = 0; v < vertexCount; v++)
        {
            double x = xyCoords[v * 2], y = xyCoords[v * 2 + 1];
            grid.GatherCandidates(Bounds2D.FromPoint(x, y), candidates, scratch);
            foreach (int s in candidates)
            {
                int a = segments[s * 2], b = segments[s * 2 + 1];
                if (!valid[s] || v == a || v == b)
                    continue;

                double ax = xyCoords[a * 2], ay = xyCoords[a * 2 + 1];
                double dx = xyCoords[b * 2] - ax, dy = xyCoords[b * 2 + 1] - ay;
                double lengthSquared = (dx * dx) + (dy * dy);
                if (lengthSquared <= toleranceSquared)
                    continue;

                double t = (((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared;
                double px = ax + (dx * t) - x, py = ay + (dy * t) - y;
                if ((px * px) + (py * py) > toleranceSquared)
                    continue;

                // Interior only: a vertex within tolerance of an end is that end's coincident twin, which
                // vertex merging owns, not a junction.
                double alongFromEnds = Math.Min(t, 1.0 - t) * Math.Sqrt(lengthSquared);
                if (alongFromEnds <= tolerance)
                    continue;

                (splits[s] ??= new List<(double, int)>(1)).Add((t, v));
            }
        }

        var result = new List<int>(segments.Length + 8);
        for (int s = 0; s < segmentCount; s++)
        {
            int a = segments[s * 2], b = segments[s * 2 + 1];
            List<(double T, int Vertex)>? onSegment = splits[s];
            if (onSegment == null)
            {
                result.Add(a);
                result.Add(b);
                continue;
            }

            onSegment.Sort(static (left, right) => left.T.CompareTo(right.T));
            int previous = a;
            foreach ((_, int vertex) in onSegment)
            {
                if (vertex == previous)
                    continue;
                result.Add(previous);
                result.Add(vertex);
                previous = vertex;
                splitCount++;
            }

            result.Add(previous);
            result.Add(b);
        }

        return splitCount == 0 ? segments : result.ToArray();
    }
}
