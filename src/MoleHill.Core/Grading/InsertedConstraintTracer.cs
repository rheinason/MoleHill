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
    public static ConstraintPolyline? Trace(
        ConstraintPolyline constraint,
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

        // Start at the vertex nearest the line's first point. On a closed line that vertex may sit just
        // before the seam, where its arc parameter reads as nearly the whole length rather than zero; it is
        // the start all the same. (A graded ring wall whose conform had snapped its first point a few
        // millimetres back along the ring failed to trace for exactly that reason.)
        double p0x = p[0], p0y = p[1];
        int start = -1;
        double startS = 0.0;
        double bestStart = double.PositiveInfinity;
        foreach ((int v, (double s, double _)) in along)
        {
            bool atStart = s <= tolerance || (constraint.IsClosed && s >= total - tolerance);
            if (!atStart)
                continue;
            double d = Sq(vertices[v * 3] - p0x) + Sq(vertices[v * 3 + 1] - p0y);
            if (d < bestStart)
            {
                start = v;
                bestStart = d;
                startS = s <= tolerance ? s : s - total;
            }
        }

        if (start < 0)
            return null;

        // Walk forward along the line: from each vertex, to an adjacent on-line vertex further along it,
        // nearest first. Where several walls' batters are conformed side by side, a vertex within tolerance
        // of the line can belong to a neighbouring chain and lead nowhere, so a walk that dead-ends short of
        // the line's end backs up and tries the next candidate. A vertex that dead-ended once always will
        // (what lies ahead of it does not depend on how it was reached), so it is not tried again.
        var chain = new List<int> { start };
        var chainS = new List<double> { startS };
        var onChain = new HashSet<int> { start };
        var deadEnds = new HashSet<int>();
        var pending = new Stack<List<(int Vertex, double S)>>();
        pending.Push(Advances(start, startS));
        int budget = Math.Max(64, along.Count * 8);
        while (pending.Count > 0 && budget-- > 0)
        {
            int current = chain[^1];
            double currentS = chainS[^1];
            List<(int Vertex, double S)> options = pending.Peek();
            if (options.Count == 0)
            {
                if (IsComplete(current, currentS))
                    return ChainPolyline(chain);

                pending.Pop();
                deadEnds.Add(current);
                onChain.Remove(current);
                chain.RemoveAt(chain.Count - 1);
                chainS.RemoveAt(chainS.Count - 1);
                continue;
            }

            (int next, double nextS) = options[0];
            options.RemoveAt(0);
            if (onChain.Contains(next) || deadEnds.Contains(next))
                continue;

            chain.Add(next);
            chainS.Add(nextS);
            onChain.Add(next);
            pending.Push(Advances(next, nextS));
        }

        return null;

        List<(int Vertex, double S)> Advances(int from, double fromS)
        {
            var result = new List<(int Vertex, double S)>();
            foreach (int n in adjacency.NeighborsOf(from))
            {
                if (n != start && along.TryGetValue(n, out var info) && info.S > fromS)
                    result.Add((n, info.S));
            }

            result.Sort(static (a, b) => a.S.CompareTo(b.S));
            return result;
        }

        bool IsComplete(int last, double lastS)
        {
            if (constraint.IsClosed)
            {
                // The walk ends on the last vertex before the seam; it must close back onto the start.
                return chain.Count >= 3 && adjacency.NeighborsContain(last, start) && total - lastS <= Math.Max(tolerance, total * 0.5);
            }

            return chain.Count >= 2 && total - lastS <= tolerance;
        }

        ConstraintPolyline ChainPolyline(List<int> vertexChain)
        {
            var points = new double[vertexChain.Count * 3];
            for (int i = 0; i < vertexChain.Count; i++)
            {
                points[i * 3] = vertices[vertexChain[i] * 3];
                points[i * 3 + 1] = vertices[vertexChain[i] * 3 + 1];
                points[i * 3 + 2] = vertices[vertexChain[i] * 3 + 2];
            }

            return new ConstraintPolyline(points, vertexChain.Count, constraint.IsClosed, constraint.PreserveInputElevation);
        }
    }

    /// <summary>Traces each constraint, keeping the drawn line where the trace does not complete.</summary>
    public static List<ConstraintPolyline> TraceAll(
        IReadOnlyList<ConstraintPolyline> constraints,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double tolerance,
        out int traced)
    {
        traced = 0;
        var result = new List<ConstraintPolyline>(constraints.Count);
        if (constraints.Count == 0)
            return result;

        var bounds = new Bounds2D[vertexCount];
        for (int v = 0; v < vertexCount; v++)
            bounds[v] = Bounds2D.FromPoint(vertices[v * 3], vertices[v * 3 + 1]);
        SpatialHashGrid2D grid = SpatialHashGrid2D.Build(bounds);
        MeshVertexAdjacency adjacency = MeshVertexAdjacency.Build(new List<int>(faces.AsSpan(0, faceCount * 3).ToArray()), faceCount, vertexCount);

        MeshAreaSplitter.AreaBoundary? outline = null;
        bool outlineBuilt = false;
        foreach (ConstraintPolyline constraint in constraints)
        {
            ConstraintPolyline? tracedLine = Trace(constraint, vertices, vertexCount, grid, adjacency, tolerance);
            if (tracedLine is { } line)
            {
                result.Add(line);
                traced++;
                continue;
            }

            // A line that runs off the terrain was only inserted where it lies over it, so the whole line
            // cannot trace. Trace the pieces inside the terrain's outline instead: persisting the drawn line,
            // outside part included, is what leaves zero-area caps in a later constrained rebuild.
            if (!outlineBuilt)
            {
                outline = BuildOuterOutline(vertices, faces, faceCount);
                outlineBuilt = true;
            }

            List<ConstraintPolyline>? pieces = outline == null
                ? null
                : TraceInsidePieces(constraint, outline, vertices, vertexCount, grid, adjacency, tolerance);
            if (pieces != null)
            {
                result.AddRange(pieces);
                traced++;
            }
            else
            {
                result.Add(constraint);
            }
        }

        return result;
    }

    private static List<ConstraintPolyline>? TraceInsidePieces(
        ConstraintPolyline constraint,
        MeshAreaSplitter.AreaBoundary outline,
        double[] vertices,
        int vertexCount,
        SpatialHashGrid2D grid,
        MeshVertexAdjacency adjacency,
        double tolerance)
    {
        List<Processing.RegionInputClipper.InputPolyline> clipped = Processing.RegionInputClipper.ClipPolylines(
            [new Processing.RegionInputClipper.InputPolyline(constraint.Points, constraint.PointCount, constraint.IsClosed)],
            [outline],
            tolerance);
        if (clipped.Count == 0)
            return null;

        var pieces = new List<ConstraintPolyline>(clipped.Count);
        foreach (Processing.RegionInputClipper.InputPolyline piece in clipped)
        {
            var candidate = new ConstraintPolyline(piece.Points, piece.PointCount, piece.IsClosed, constraint.PreserveInputElevation);
            if (Trace(candidate, vertices, vertexCount, grid, adjacency, tolerance) is not { } tracedPiece)
                return null;
            pieces.Add(tracedPiece);
        }

        return pieces;
    }

    /// <summary>The terrain's outer border (its largest boundary loop) as a plan polygon.</summary>
    private static MeshAreaSplitter.AreaBoundary? BuildOuterOutline(double[] vertices, int[] faces, int faceCount)
    {
        if (!MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(faces, faceCount, out List<int[]> loops) || loops.Count == 0)
            return null;

        int[]? largest = null;
        double largestArea = 0.0;
        foreach (int[] loop in loops)
        {
            double area = 0.0;
            for (int i = 0; i < loop.Length; i++)
            {
                int a = loop[i], b = loop[(i + 1) % loop.Length];
                area += (vertices[a * 3] * vertices[(b * 3) + 1]) - (vertices[b * 3] * vertices[(a * 3) + 1]);
            }

            if (Math.Abs(area) > largestArea)
            {
                largestArea = Math.Abs(area);
                largest = loop;
            }
        }

        if (largest == null || largest.Length < 3)
            return null;

        var xy = new double[largest.Length * 2];
        for (int i = 0; i < largest.Length; i++)
        {
            xy[i * 2] = vertices[largest[i] * 3];
            xy[(i * 2) + 1] = vertices[(largest[i] * 3) + 1];
        }

        return new MeshAreaSplitter.AreaBoundary(xy, largest.Length);
    }

    private static double Sq(double value) => value * value;
}
