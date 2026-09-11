using MoleHill.Core.Engine;

namespace MoleHill.Core.Analysis;

/// <summary>Traces a single deterministic downhill water path through a 2.5D triangle mesh.</summary>
public static class WaterflowTracer
{
    public sealed class Options
    {
        /// <summary>Maximum plan length. Zero or less continues until the mesh edge or a sink.</summary>
        public double MaxLength { get; init; }

        /// <summary>Safety cap for triangle transitions.</summary>
        public int MaxSteps { get; init; } = 10_000;

        /// <summary>Minimum downhill slope ratio treated as a local flat/sink.</summary>
        public double MinimumSlopeRatio { get; init; } = 1e-9;

        /// <summary>Numerical tolerance used for point-in-triangle and edge crossing tests.</summary>
        public double Tolerance { get; init; } = 1e-10;

        /// <summary>Optional cooperative cancellation check used during lookup and tracing.</summary>
        public Func<bool>? CancellationRequested { get; init; }
    }

    public sealed class Result
    {
        public IReadOnlyList<Path> Paths { get; init; } = Array.Empty<Path>();

        public int RejectedStartCount { get; init; }
    }

    public sealed class Path
    {
        /// <summary>Terrain-conforming XYZ path points, including the projected start point.</summary>
        public required double[] PointsXyz { get; init; }

        public int PointCount => PointsXyz.Length / 3;

        public double PlanLength { get; init; }

        public bool ReachedBoundary { get; init; }

        public bool TerminatedAtSink { get; init; }
    }

    /// <summary>
    /// Trace downhill from each XY start point. A path follows the constant gradient of each
    /// triangle, then continues through the adjacent triangle. At a local flat/sink it stops; at
    /// a naked mesh edge it reaches the boundary. Start-point Z values are ignored.
    /// </summary>
    public static Result Trace(
        IReadOnlyList<double> vertices,
        int vertexCount,
        IReadOnlyList<int> faces,
        int faceCount,
        IReadOnlyList<double> startXy,
        int startPointCount,
        Options? options = null)
    {
        if (vertices == null)
            throw new ArgumentNullException(nameof(vertices));
        if (faces == null)
            throw new ArgumentNullException(nameof(faces));
        if (startXy == null)
            throw new ArgumentNullException(nameof(startXy));
        if (vertexCount < 0 || vertexCount * 3 > vertices.Count)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (faceCount < 0 || faceCount * 3 > faces.Count)
            throw new ArgumentOutOfRangeException(nameof(faceCount));
        if (startPointCount < 0 || startPointCount * 2 > startXy.Count)
            throw new ArgumentOutOfRangeException(nameof(startPointCount));

        Options settings = options ?? new Options();
        int maxSteps = Math.Max(1, settings.MaxSteps);
        double tolerance = Math.Max(Math.Abs(settings.Tolerance), 1e-12);
        int[] neighbors = BuildNeighbors(faces, faceCount, vertexCount, settings);
        FaceSpatialIndex faceIndexLookup = FaceSpatialIndex.Build(vertices, vertexCount, faces, faceCount, settings);
        // Setup (neighbours + face index) is once per call; the traces themselves are independent
        // functions of read-only state, so they can run together. Results are written by start index
        // and compacted in order afterwards, so output order and the rejected count are exactly what
        // the serial loop produced.
        var traced = new Path?[startPointCount];
        if (ShouldTraceInParallel(startPointCount, faceCount))
        {
            TraceStartsInParallel(
                vertices, vertexCount, faces, faceCount, startXy, startPointCount,
                neighbors, faceIndexLookup, settings, maxSteps, tolerance, traced);
        }
        else
        {
            var queryState = new FaceSpatialIndex.QueryState(faceIndexLookup.ItemCount);
            for (int startIndex = 0; startIndex < startPointCount; startIndex++)
            {
                ThrowIfCancellationRequested(settings);
                traced[startIndex] = TraceStart(
                    vertices, vertexCount, faces, startXy, startIndex,
                    neighbors, faceIndexLookup, queryState, settings, maxSteps, tolerance);
            }
        }

        var paths = new List<Path>(startPointCount);
        int rejected = 0;
        for (int startIndex = 0; startIndex < startPointCount; startIndex++)
        {
            Path? path = traced[startIndex];
            if (path == null)
                rejected++;
            else
                paths.Add(path);
        }

        return new Result { Paths = paths, RejectedStartCount = rejected };
    }

    /// <summary>
    /// Parallel tracing only pays off once there are enough independent starts to cover the partition
    /// overhead. Per-worker query scratch is sparse so a large mesh does not multiply face-sized
    /// stamp arrays by the worker count.
    /// </summary>
    private static bool ShouldTraceInParallel(int startPointCount, int faceCount)
    {
        const int minimumStarts = 8;
        const int minimumWork = 50_000;
        return startPointCount >= minimumStarts &&
               Environment.ProcessorCount > 1 &&
               (long)startPointCount * faceCount >= minimumWork;
    }

    private static void TraceStartsInParallel(
        IReadOnlyList<double> vertices,
        int vertexCount,
        IReadOnlyList<int> faces,
        int faceCount,
        IReadOnlyList<double> startXy,
        int startPointCount,
        int[] neighbors,
        FaceSpatialIndex faceIndexLookup,
        Options settings,
        int maxSteps,
        double tolerance,
        Path?[] traced)
    {
        try
        {
            Parallel.For(
                0,
                startPointCount,
                () => new FaceSpatialIndex.QueryState(faceIndexLookup.ItemCount),
                (startIndex, _, queryState) =>
                {
                    ThrowIfCancellationRequested(settings);
                    traced[startIndex] = TraceStart(
                        vertices, vertexCount, faces, startXy, startIndex,
                        neighbors, faceIndexLookup, queryState, settings, maxSteps, tolerance);
                    return queryState;
                },
                static _ => { });
        }
        catch (AggregateException aggregate)
        {
            // Parallel.For wraps worker exceptions. Cancellation must keep reaching callers as the
            // OperationCanceledException the serial path threw.
            foreach (Exception inner in aggregate.Flatten().InnerExceptions)
            {
                if (inner is OperationCanceledException canceled)
                    throw canceled;
            }

            throw;
        }
    }

    private static Path? TraceStart(
        IReadOnlyList<double> vertices,
        int vertexCount,
        IReadOnlyList<int> faces,
        IReadOnlyList<double> startXy,
        int startIndex,
        IReadOnlyList<int> neighbors,
        FaceSpatialIndex faceIndexLookup,
        FaceSpatialIndex.QueryState queryState,
        Options settings,
        int maxSteps,
        double tolerance)
    {
        double x = startXy[startIndex * 2];
        double y = startXy[(startIndex * 2) + 1];
        int faceIndex = FindContainingFace(
            vertices, vertexCount, faces, faceIndexLookup, queryState, x, y, tolerance, settings);
        if (faceIndex < 0)
            return null;

        return TracePath(vertices, faces, neighbors, x, y, faceIndex, settings, maxSteps, tolerance);
    }

    private static Path TracePath(
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        IReadOnlyList<int> neighbors,
        double x,
        double y,
        int faceIndex,
        Options settings,
        int maxSteps,
        double tolerance)
    {
        var points = new List<double>(Math.Min(maxSteps + 1, 256)) { x, y, 0.0 };
        double planLength = 0.0;
        bool reachedBoundary = false;
        bool terminatedAtSink = false;
        double currentX = x;
        double currentY = y;
        var visitedFaces = new HashSet<int> { faceIndex };
        double maxLength = settings.MaxLength > 0.0 && double.IsFinite(settings.MaxLength)
            ? settings.MaxLength
            : double.PositiveInfinity;

        for (int step = 0; step < maxSteps; step++)
        {
            ThrowIfCancellationRequested(settings);
            bool hasPlane = TryGetPlane(vertices, faces, faceIndex, out Plane plane, out double slopeRatio);
            if (hasPlane && points.Count == 3)
                points[2] = plane.Evaluate(currentX, currentY);

            if (!hasPlane || slopeRatio <= Math.Max(0.0, settings.MinimumSlopeRatio))
            {
                terminatedAtSink = true;
                break;
            }

            double dx = plane.GradientX == 0.0 && plane.GradientY == 0.0
                ? 0.0
                : -plane.GradientX;
            double dy = -plane.GradientY;
            double directionLength = Math.Sqrt((dx * dx) + (dy * dy));
            if (directionLength <= tolerance)
            {
                terminatedAtSink = true;
                break;
            }

            if (!TryFindExit(
                    vertices,
                    faces,
                    faceIndex,
                    currentX,
                    currentY,
                    dx,
                    dy,
                    tolerance,
                    out double distance,
                    out int exitEdge,
                    out double exitX,
                    out double exitY))
            {
                terminatedAtSink = true;
                break;
            }

            double remaining = maxLength - planLength;
            if (remaining <= tolerance)
                break;

            bool limitedByLength = distance > remaining;
            double segmentLength = limitedByLength ? remaining : distance;
            double segmentX = currentX + (dx / directionLength * segmentLength);
            double segmentY = currentY + (dy / directionLength * segmentLength);
            points.Add(segmentX);
            points.Add(segmentY);
            points.Add(plane.Evaluate(segmentX, segmentY));
            planLength += segmentLength;

            if (limitedByLength)
                break;

            int nextFace = neighbors[(faceIndex * 3) + exitEdge];
            if (nextFace < 0)
            {
                reachedBoundary = true;
                break;
            }

            // A downhill ray should not revisit a face on a scalar terrain. Revisit means the
            // discrete face gradients have formed a local cycle, so stop rather than oscillate.
            if (!visitedFaces.Add(nextFace))
            {
                terminatedAtSink = true;
                break;
            }

            faceIndex = nextFace;

            double nudge = Math.Max(tolerance * 8.0, 1e-12);
            currentX = exitX + (dx / directionLength * nudge);
            currentY = exitY + (dy / directionLength * nudge);
        }

        return new Path
        {
            PointsXyz = points.ToArray(),
            PlanLength = planLength,
            ReachedBoundary = reachedBoundary,
            TerminatedAtSink = terminatedAtSink
        };
    }

    private static int[] BuildNeighbors(
        IReadOnlyList<int> faces,
        int faceCount,
        int vertexCount,
        Options settings)
    {
        var neighbors = new int[faceCount * 3];
        Array.Fill(neighbors, -1);

        // Presized: a closed triangle mesh has about 1.5 edges per face, so an unsized dictionary
        // rehashes its way up from 0 to that on every trace call.
        var edges = new Dictionary<EdgeKey, (int Face, int Edge)>(Math.Max(16, faceCount * 2));

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if ((faceIndex & 4095) == 0)
                ThrowIfCancellationRequested(settings);
            for (int edge = 0; edge < 3; edge++)
            {
                int a = faces[(faceIndex * 3) + edge];
                int b = faces[(faceIndex * 3) + ((edge + 1) % 3)];
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || a == b)
                    continue;

                var key = new EdgeKey(a, b);
                if (!edges.TryGetValue(key, out var previous))
                {
                    edges.Add(key, (faceIndex, edge));
                    continue;
                }

                // Non-manifold edges are treated as boundaries rather than choosing an arbitrary
                // third face. The first two faces still form a deterministic pair.
                if (neighbors[(previous.Face * 3) + previous.Edge] < 0)
                {
                    neighbors[(previous.Face * 3) + previous.Edge] = faceIndex;
                    neighbors[(faceIndex * 3) + edge] = previous.Face;
                }
            }
        }

        return neighbors;
    }

    private static int FindContainingFace(
        IReadOnlyList<double> vertices,
        int vertexCount,
        IReadOnlyList<int> faces,
        FaceSpatialIndex faceIndexLookup,
        FaceSpatialIndex.QueryState queryState,
        double x,
        double y,
        double tolerance,
        Options settings)
    {
        List<int> candidates = queryState.Candidates;
        faceIndexLookup.Gather(x, y, tolerance, queryState, candidates);
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            if ((candidateIndex & 255) == 0)
                ThrowIfCancellationRequested(settings);
            int faceIndex = candidates[candidateIndex];
            int a = faces[faceIndex * 3];
            int b = faces[(faceIndex * 3) + 1];
            int c = faces[(faceIndex * 3) + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                continue;

            double ax = vertices[a * 3];
            double ay = vertices[(a * 3) + 1];
            double bx = vertices[b * 3];
            double by = vertices[(b * 3) + 1];
            double cx = vertices[c * 3];
            double cy = vertices[(c * 3) + 1];
            double area = Cross(bx - ax, by - ay, cx - ax, cy - ay);
            if (Math.Abs(area) <= tolerance)
                continue;

            double ab = Cross(bx - ax, by - ay, x - ax, y - ay);
            double bc = Cross(cx - bx, cy - by, x - bx, y - by);
            double ca = Cross(ax - cx, ay - cy, x - cx, y - cy);
            if ((ab >= -tolerance && bc >= -tolerance && ca >= -tolerance) ||
                (ab <= tolerance && bc <= tolerance && ca <= tolerance))
                return faceIndex;
        }

        return -1;
    }

    private static void ThrowIfCancellationRequested(Options settings)
    {
        if (settings.CancellationRequested?.Invoke() == true)
            throw new OperationCanceledException();
    }

    private static bool TryGetPlane(
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        int faceIndex,
        out Plane plane,
        out double slopeRatio)
    {
        int a = faces[faceIndex * 3];
        int b = faces[(faceIndex * 3) + 1];
        int c = faces[(faceIndex * 3) + 2];
        double ax = vertices[a * 3];
        double ay = vertices[(a * 3) + 1];
        double az = vertices[(a * 3) + 2];
        double ux = vertices[b * 3] - ax;
        double uy = vertices[(b * 3) + 1] - ay;
        double uz = vertices[(b * 3) + 2] - az;
        double vx = vertices[c * 3] - ax;
        double vy = vertices[(c * 3) + 1] - ay;
        double vz = vertices[(c * 3) + 2] - az;
        double nx = (uy * vz) - (uz * vy);
        double ny = (uz * vx) - (ux * vz);
        double nz = (ux * vy) - (uy * vx);
        if (Math.Abs(nz) <= 1e-14)
        {
            plane = default;
            slopeRatio = 0.0;
            return false;
        }

        double gradientX = -nx / nz;
        double gradientY = -ny / nz;
        plane = new Plane(ax, ay, az, gradientX, gradientY);
        slopeRatio = Math.Sqrt((gradientX * gradientX) + (gradientY * gradientY));
        return double.IsFinite(slopeRatio);
    }

    private static bool TryFindExit(
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        int faceIndex,
        double x,
        double y,
        double dx,
        double dy,
        double tolerance,
        out double distance,
        out int exitEdge,
        out double exitX,
        out double exitY)
    {
        distance = double.PositiveInfinity;
        exitEdge = -1;
        exitX = x;
        exitY = y;
        double bestT = double.PositiveInfinity;

        for (int edge = 0; edge < 3; edge++)
        {
            int a = faces[(faceIndex * 3) + edge];
            int b = faces[(faceIndex * 3) + ((edge + 1) % 3)];
            double ax = vertices[a * 3];
            double ay = vertices[(a * 3) + 1];
            double bx = vertices[b * 3];
            double by = vertices[(b * 3) + 1];
            double edgeX = bx - ax;
            double edgeY = by - ay;
            double denominator = Cross(dx, dy, edgeX, edgeY);
            if (Math.Abs(denominator) <= tolerance)
                continue;

            double toEdgeX = ax - x;
            double toEdgeY = ay - y;
            double t = Cross(toEdgeX, toEdgeY, edgeX, edgeY) / denominator;
            double u = Cross(toEdgeX, toEdgeY, dx, dy) / denominator;
            if (t <= tolerance || u < -tolerance || u > 1.0 + tolerance || t >= bestT)
                continue;

            bestT = t;
            exitEdge = edge;
            exitX = x + (dx * t);
            exitY = y + (dy * t);
        }

        if (exitEdge < 0)
            return false;

        distance = bestT * Math.Sqrt((dx * dx) + (dy * dy));
        return double.IsFinite(distance);
    }

    private static double Cross(double ax, double ay, double bx, double by) => (ax * by) - (ay * bx);

    private readonly record struct EdgeKey
    {
        public EdgeKey(int a, int b)
        {
            A = Math.Min(a, b);
            B = Math.Max(a, b);
        }

        public int A { get; }

        public int B { get; }
    }

    private readonly record struct Plane(double OriginX, double OriginY, double OriginZ, double GradientX, double GradientY)
    {
        public double Evaluate(double x, double y) => OriginZ + (GradientX * (x - OriginX)) + (GradientY * (y - OriginY));
    }

    /// <summary>
    /// Face lookup index. Immutable once built: the mutable query buffers live in
    /// <see cref="QueryState"/>, which each caller (and each parallel worker) owns. That is what makes
    /// one index safe to share across concurrent traces instead of rebuilding it per start.
    /// </summary>
    private sealed class FaceSpatialIndex
    {
        private readonly SpatialHashGrid2D _grid;
        private readonly int[] _faceIndexes;

        private FaceSpatialIndex(SpatialHashGrid2D grid, int[] faceIndexes)
        {
            _grid = grid;
            _faceIndexes = faceIndexes;
        }

        public int ItemCount => _faceIndexes.Length;

        /// <summary>Per-caller query buffers. Never share one between threads.</summary>
        public sealed class QueryState
        {
            public QueryState(int itemCount)
            {
                Scratch = new SpatialHashGrid2D.QueryScratch(itemCount, sparse: true);
            }

            public SpatialHashGrid2D.QueryScratch Scratch { get; }

            public List<int> LocalCandidates { get; } = new(16);

            public List<int> Candidates { get; } = new(16);
        }

        public static FaceSpatialIndex Build(
            IReadOnlyList<double> vertices,
            int vertexCount,
            IReadOnlyList<int> faces,
            int faceCount,
            Options settings)
        {
            var bounds = new List<Bounds2D>(faceCount);
            var indexes = new List<int>(faceCount);
            for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
            {
                if ((faceIndex & 4095) == 0)
                    ThrowIfCancellationRequested(settings);
                int a = faces[faceIndex * 3];
                int b = faces[(faceIndex * 3) + 1];
                int c = faces[(faceIndex * 3) + 2];
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                    continue;

                double ax = vertices[a * 3], ay = vertices[(a * 3) + 1];
                double bx = vertices[b * 3], by = vertices[(b * 3) + 1];
                double cx = vertices[c * 3], cy = vertices[(c * 3) + 1];
                bounds.Add(new Bounds2D(
                    Math.Min(ax, Math.Min(bx, cx)),
                    Math.Max(ax, Math.Max(bx, cx)),
                    Math.Min(ay, Math.Min(by, cy)),
                    Math.Max(ay, Math.Max(by, cy))));
                indexes.Add(faceIndex);
            }

            return new FaceSpatialIndex(SpatialHashGrid2D.Build(bounds.ToArray()), indexes.ToArray());
        }

        public void Gather(double x, double y, double tolerance, QueryState state, List<int> result)
        {
            result.Clear();
            _grid.GatherCandidates(Bounds2D.FromPoint(x, y, tolerance), state.LocalCandidates, state.Scratch);
            foreach (int localIndex in state.LocalCandidates)
                result.Add(_faceIndexes[localIndex]);
        }
    }
}
