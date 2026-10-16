// Recovers a small set of editable curves and spot points that rebuild a terrain within a tolerance.
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Processing;

/// <summary>
/// Turns a dense terrain (a DEM or point-cloud mesh) back into the kind of input a person would draw: a
/// border, spot points on peaks and pits, and contours — as few as rebuild it within a stated vertical
/// tolerance. Simplify (B6) reduces the mesh; this inverts it, so the output is input that stays editable.
///
/// <para>The loop is greedy. Rebuild a TIN from what has been chosen so far, find the terrain vertex it
/// misses by most, and add the contour through that vertex (simplified in plan within
/// <see cref="Options.PlanTolerance"/>). A worst vertex the contour does not catch — a peak's contour
/// shrinks to nothing — becomes a spot point instead. Stop once every terrain vertex is within
/// <see cref="Options.VerticalTolerance"/>, or the curve budget is spent.</para>
///
/// <para>The deviation is measured at the terrain's vertices. Both surfaces are linear between their own
/// vertices, so a miss between them is possible where the rebuild's edges cross the terrain's; the result
/// says it is vertex-sampled. Landform lines (ridges and valleys from the Curvature analysis) are not
/// seeded yet: contours stand in for them, which costs curves where a ridge line would have done.</para>
/// </summary>
public static class TerrainCurveRecovery
{
    public sealed class Options
    {
        /// <summary>The largest vertical miss allowed at any terrain vertex.</summary>
        public required double VerticalTolerance { get; init; }

        /// <summary>How far a simplified contour or border may move in plan. Defaults to the vertical tolerance.</summary>
        public double PlanTolerance { get; init; }

        /// <summary>Stop after adding this many contours, met tolerance or not.</summary>
        public int MaximumCurves { get; init; } = 200;

        /// <summary>A peak or pit must be the highest (lowest) vertex this many rings out to seed a spot point.</summary>
        public int ExtremumRings { get; init; } = 3;

        public Func<bool>? ShouldCancel { get; init; }

        /// <summary>Called after each round with (round, worst deviation).</summary>
        public Action<int, double>? ReportProgress { get; init; }
    }

    public enum Termination
    {
        ToleranceSatisfied,
        CurveBudgetSpent,
        NoProgress,
        TriangulationFailed,
        Cancelled,
        UnsupportedInput
    }

    public sealed class RecoveredContour
    {
        public required double Level { get; init; }

        /// <summary>Flat XYZ points, every Z the level. A closed contour repeats no point; see <see cref="IsClosed"/>.</summary>
        public required double[] PointsXyz { get; init; }

        public required bool IsClosed { get; init; }
    }

    public sealed class Result
    {
        /// <summary>The terrain's border loops, simplified, as flat XYZ (closed, first point not repeated).</summary>
        public required IReadOnlyList<double[]> Borders { get; init; }

        public required IReadOnlyList<RecoveredContour> Contours { get; init; }

        /// <summary>Spot points as flat XYZ.</summary>
        public required double[] SpotPoints { get; init; }

        /// <summary>The largest vertical miss at a terrain vertex by the final rebuild.</summary>
        public required double MaximumDeviation { get; init; }

        public required int Rounds { get; init; }

        public required Termination Termination { get; init; }

        public required string Diagnostic { get; init; }

        public int SpotPointCount => SpotPoints.Length / 3;
    }

    public static Result Recover(IndexedTriMesh terrain, Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = terrain;
        double verticalTolerance = options.VerticalTolerance;
        if (!double.IsFinite(verticalTolerance) || verticalTolerance <= 0.0 || vertexCount < 3 || faceCount < 1)
            return Unsupported("A positive vertical tolerance and a terrain of at least one face are required.");

        double planTolerance = options.PlanTolerance > 0.0 ? options.PlanTolerance : verticalTolerance;
        if (!MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(faces, faceCount, out List<int[]> loops) || loops.Count == 0)
            return Unsupported("The terrain's border is not a set of simple loops.");

        // Borders first: they bound the rebuild, so a contour can only refine inside them.
        var borders = new List<double[]>(loops.Count);
        foreach (int[] loop in loops)
        {
            double[] loopXyz = new double[loop.Length * 3];
            for (int i = 0; i < loop.Length; i++)
                Array.Copy(vertices, loop[i] * 3, loopXyz, i * 3, 3);
            double[] simplified = SimplifyClosed(loopXyz, planTolerance, verticalTolerance);
            if (simplified.Length >= 9)
                borders.Add(simplified);
        }

        if (borders.Count == 0)
            return Unsupported("The terrain's border collapsed when simplified.");

        var spots = new List<double>();
        var spotted = new HashSet<int>();
        foreach (int extremum in FindExtrema(vertices, vertexCount, faces, faceCount, Math.Max(1, options.ExtremumRings), loops))
        {
            spotted.Add(extremum);
            spots.Add(vertices[extremum * 3]);
            spots.Add(vertices[extremum * 3 + 1]);
            spots.Add(vertices[extremum * 3 + 2]);
        }

        var contours = new List<RecoveredContour>();
        double worstDeviation = double.PositiveInfinity;
        int round = 0;
        int lastWorstVertex = -1;
        Termination termination;
        string diagnostic;
        while (true)
        {
            if (options.ShouldCancel?.Invoke() == true)
            {
                termination = Termination.Cancelled;
                diagnostic = "Cancelled.";
                break;
            }

            TinResult? rebuild = Rebuild(borders, contours, spots, planTolerance, out string? error);
            if (rebuild == null)
            {
                termination = Termination.TriangulationFailed;
                diagnostic = error ?? "The chosen curves could not be triangulated.";
                break;
            }

            (worstDeviation, int worstVertex) = WorstVertex(vertices, vertexCount, rebuild, verticalTolerance);
            options.ReportProgress?.Invoke(round, worstDeviation);
            if (worstVertex < 0 || worstDeviation <= verticalTolerance)
            {
                termination = Termination.ToleranceSatisfied;
                diagnostic = FormattableString.Invariant($"Within {verticalTolerance:G4} at every terrain vertex.");
                break;
            }

            if (contours.Count >= options.MaximumCurves)
            {
                termination = Termination.CurveBudgetSpent;
                diagnostic = FormattableString.Invariant($"Stopped at {contours.Count} contours with a {worstDeviation:G4} miss left.");
                break;
            }

            round++;
            double level = vertices[worstVertex * 3 + 2];
            RecoveredContour? contour = worstVertex == lastWorstVertex
                ? null
                : TraceContourThrough(terrain, worstVertex, level, planTolerance);
            if (contour != null && !contours.Any(existing => Math.Abs(existing.Level - level) < 1e-12 &&
                                                             SameStart(existing, contour)))
            {
                contours.Add(contour);
            }
            else if (spotted.Add(worstVertex))
            {
                spots.Add(vertices[worstVertex * 3]);
                spots.Add(vertices[worstVertex * 3 + 1]);
                spots.Add(level);
            }
            else
            {
                termination = Termination.NoProgress;
                diagnostic = FormattableString.Invariant($"The worst vertex could not be captured; a {worstDeviation:G4} miss is left.");
                break;
            }

            lastWorstVertex = worstVertex;
        }

        return new Result
        {
            Borders = borders,
            Contours = contours,
            SpotPoints = spots.ToArray(),
            MaximumDeviation = double.IsFinite(worstDeviation) ? worstDeviation : double.NaN,
            Rounds = round,
            Termination = termination,
            Diagnostic = diagnostic
        };
    }

    private static bool SameStart(RecoveredContour a, RecoveredContour b) =>
        Math.Abs(a.PointsXyz[0] - b.PointsXyz[0]) < 1e-9 && Math.Abs(a.PointsXyz[1] - b.PointsXyz[1]) < 1e-9;

    /// <summary>
    /// The traced contour at <paramref name="level"/> passing nearest the vertex, simplified in plan. Null when
    /// it is too small to keep (a peak's or pit's contour shrinks to a point), so the vertex becomes a spot.
    /// </summary>
    private static RecoveredContour? TraceContourThrough(IndexedTriMesh terrain, int vertex, double level, double planTolerance)
    {
        double[] vertices = terrain.Vertices;
        double x = vertices[vertex * 3], y = vertices[vertex * 3 + 1];

        // A level exactly at a vertex makes every face meeting it a tie; nudge it off.
        double nudge = Math.Max(Math.Abs(level), 1.0) * 1e-9;
        List<ContourLevel> traced = ContourGenerator.Generate(terrain, new[] { level - nudge }, planTolerance * 1e-3);
        ContourPolyline? nearest = null;
        double nearestDistance = double.PositiveInfinity;
        foreach (ContourLevel contourLevel in traced)
        {
            foreach (ContourPolyline polyline in contourLevel.Polylines)
            {
                double distance = PlanDistanceToPolyline(polyline.PointsXyz, polyline.IsClosed, x, y);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = polyline;
                }
            }
        }

        if (nearest == null || nearestDistance > planTolerance * 4.0)
            return null;

        double[] points = nearest.PointsXyz;
        if (nearest.IsClosed && points.Length >= 6 &&
            Math.Abs(points[0] - points[^3]) < 1e-12 && Math.Abs(points[1] - points[^2]) < 1e-12)
            points = points[..^3];

        double[] simplified = nearest.IsClosed
            ? SimplifyClosed(points, planTolerance, double.PositiveInfinity)
            : SimplifyOpen(points, planTolerance, double.PositiveInfinity);
        int minimum = nearest.IsClosed ? 3 : 2;
        if (simplified.Length / 3 < minimum)
            return null;
        for (int i = 2; i < simplified.Length; i += 3)
            simplified[i] = level;
        return new RecoveredContour { Level = level, PointsXyz = simplified, IsClosed = nearest.IsClosed };
    }

    private static TinResult? Rebuild(
        List<double[]> borders,
        List<RecoveredContour> contours,
        List<double> spots,
        double planTolerance,
        out string? error)
    {
        var xy = new List<double>();
        var z = new List<double>();
        var segments = new List<int>();
        var index = new Dictionary<CellKey, int>();
        double cell = Math.Max(planTolerance * 1e-3, 1e-9);

        int Add(double px, double py, double pz)
        {
            var key = new CellKey((long)Math.Round(px / cell), (long)Math.Round(py / cell));
            if (index.TryGetValue(key, out int existing))
                return existing;
            int added = z.Count;
            index[key] = added;
            xy.Add(px);
            xy.Add(py);
            z.Add(pz);
            return added;
        }

        void AddChain(double[] points, bool closed)
        {
            int count = points.Length / 3;
            int first = -1, previous = -1;
            for (int i = 0; i < count; i++)
            {
                int current = Add(points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
                if (i == 0)
                    first = current;
                else if (current != previous)
                {
                    segments.Add(previous);
                    segments.Add(current);
                }

                previous = current;
            }

            if (closed && count > 2 && previous != first)
            {
                segments.Add(previous);
                segments.Add(first);
            }
        }

        foreach (double[] border in borders)
            AddChain(border, closed: true);
        foreach (RecoveredContour contour in contours)
            AddChain(contour.PointsXyz, contour.IsClosed);
        for (int i = 0; i < spots.Count; i += 3)
            Add(spots[i], spots[i + 1], spots[i + 2]);

        return new TinEngine().Build(
            xy.ToArray(), z.ToArray(), segments.ToArray(), QualitySettings.None, out error,
            useConvexHull: false, includeEdgeTopology: false);
    }

    private static (double Deviation, int Vertex) WorstVertex(double[] vertices, int vertexCount, TinResult rebuild, double tolerance)
    {
        var projector = new MeshHeightProjector(rebuild.Vertices, rebuild.VertexCount, rebuild.Faces, rebuild.FaceCount);
        double worst = 0.0;
        int worstVertex = -1;
        double lookup = tolerance * 1e-3;
        for (int v = 0; v < vertexCount; v++)
        {
            double x = vertices[v * 3], y = vertices[v * 3 + 1], zRef = vertices[v * 3 + 2];
            // A vertex the simplified border left outside is the border's plan tolerance at work, not a miss.
            if (!projector.TryGetHeightExtremes(x, y, lookup, out double low, out double high))
                continue;
            double deviation = Math.Max(Math.Abs(zRef - low), Math.Abs(zRef - high));
            if (deviation > worst)
            {
                worst = deviation;
                worstVertex = v;
            }
        }

        return (worst, worstVertex);
    }

    /// <summary>Interior vertices higher (or lower) than every vertex within <paramref name="rings"/> rings.</summary>
    private static List<int> FindExtrema(double[] vertices, int vertexCount, int[] faces, int faceCount, int rings, List<int[]> borderLoops)
    {
        var onBorder = new bool[vertexCount];
        foreach (int[] loop in borderLoops)
            foreach (int v in loop)
                onBorder[v] = true;

        MeshVertexAdjacency adjacency = MeshVertexAdjacency.Build(new List<int>(faces[..(faceCount * 3)]), faceCount, vertexCount);
        var extrema = new List<int>();
        var visited = new Dictionary<int, int>();
        var frontier = new List<int>();
        var next = new List<int>();
        for (int v = 0; v < vertexCount; v++)
        {
            if (onBorder[v] || !adjacency.HasNeighbors(v))
                continue;

            double z = vertices[v * 3 + 2];
            bool isMax = true, isMin = true;
            foreach (int n in adjacency.NeighborsOf(v))
            {
                double nz = vertices[n * 3 + 2];
                isMax &= z > nz;
                isMin &= z < nz;
            }

            if (!isMax && !isMin)
                continue;

            visited.Clear();
            visited[v] = 0;
            frontier.Clear();
            frontier.Add(v);
            for (int ring = 1; ring <= rings && (isMax || isMin); ring++)
            {
                next.Clear();
                foreach (int u in frontier)
                {
                    foreach (int n in adjacency.NeighborsOf(u))
                    {
                        if (visited.ContainsKey(n))
                            continue;
                        visited[n] = ring;
                        next.Add(n);
                        double nz = vertices[n * 3 + 2];
                        isMax &= z > nz;
                        isMin &= z < nz;
                    }
                }

                (frontier, next) = (next, frontier);
            }

            if (isMax || isMin)
                extrema.Add(v);
        }

        return extrema;
    }

    private static double PlanDistanceToPolyline(double[] points, bool closed, double x, double y)
    {
        int count = points.Length / 3;
        double best = double.PositiveInfinity;
        int segments = closed ? count : count - 1;
        for (int i = 0; i < segments; i++)
        {
            int j = (i + 1) % count;
            best = Math.Min(best, PlanDistanceToSegment(
                x, y, points[i * 3], points[i * 3 + 1], points[j * 3], points[j * 3 + 1], out _));
        }

        return count == 1 ? Math.Sqrt(Sq(points[0] - x) + Sq(points[1] - y)) : best;
    }

    private static double PlanDistanceToSegment(double px, double py, double ax, double ay, double bx, double by, out double t)
    {
        double dx = bx - ax, dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        t = lengthSquared > 0.0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSquared, 0.0, 1.0) : 0.0;
        return Math.Sqrt(Sq(ax + dx * t - px) + Sq(ay + dy * t - py));
    }

    private static double Sq(double value) => value * value;

    /// <summary>
    /// Douglas–Peucker on a closed loop (first point not repeated): split at the point farthest from the
    /// first, simplify both halves, rejoin. A point is kept when it lies further than
    /// <paramref name="planTolerance"/> from the chord in plan or further than
    /// <paramref name="verticalTolerance"/> from the chord's height at that point.
    /// </summary>
    internal static double[] SimplifyClosed(double[] loop, double planTolerance, double verticalTolerance)
    {
        int count = loop.Length / 3;
        if (count <= 3)
            return (double[])loop.Clone();
        int far = 0;
        double farthest = -1.0;
        for (int i = 1; i < count; i++)
        {
            double d = Sq(loop[i * 3] - loop[0]) + Sq(loop[i * 3 + 1] - loop[1]);
            if (d > farthest)
            {
                farthest = d;
                far = i;
            }
        }

        var keep = new bool[count + 1];
        keep[0] = keep[far] = keep[count] = true;
        double[] wrapped = new double[(count + 1) * 3];
        Array.Copy(loop, wrapped, loop.Length);
        Array.Copy(loop, 0, wrapped, count * 3, 3);
        Mark(wrapped, 0, far, planTolerance, verticalTolerance, keep);
        Mark(wrapped, far, count, planTolerance, verticalTolerance, keep);
        var output = new List<double>();
        for (int i = 0; i < count; i++)
        {
            if (!keep[i])
                continue;
            output.Add(loop[i * 3]);
            output.Add(loop[i * 3 + 1]);
            output.Add(loop[i * 3 + 2]);
        }

        return output.ToArray();
    }

    internal static double[] SimplifyOpen(double[] points, double planTolerance, double verticalTolerance)
    {
        int count = points.Length / 3;
        if (count <= 2)
            return (double[])points.Clone();
        var keep = new bool[count];
        keep[0] = keep[count - 1] = true;
        Mark(points, 0, count - 1, planTolerance, verticalTolerance, keep);
        var output = new List<double>();
        for (int i = 0; i < count; i++)
        {
            if (!keep[i])
                continue;
            output.Add(points[i * 3]);
            output.Add(points[i * 3 + 1]);
            output.Add(points[i * 3 + 2]);
        }

        return output.ToArray();
    }

    private static void Mark(double[] points, int first, int last, double planTolerance, double verticalTolerance, bool[] keep)
    {
        var stack = new Stack<(int First, int Last)>();
        stack.Push((first, last));
        while (stack.Count > 0)
        {
            (int a, int b) = stack.Pop();
            if (b - a < 2)
                continue;
            double ax = points[a * 3], ay = points[a * 3 + 1], az = points[a * 3 + 2];
            double bx = points[b * 3], by = points[b * 3 + 1], bz = points[b * 3 + 2];
            int worst = -1;
            double worstScore = 1.0;
            for (int i = a + 1; i < b; i++)
            {
                double plan = PlanDistanceToSegment(points[i * 3], points[i * 3 + 1], ax, ay, bx, by, out double t);
                double vertical = Math.Abs(points[i * 3 + 2] - (az + (bz - az) * t));
                double score = Math.Max(plan / planTolerance,
                    double.IsPositiveInfinity(verticalTolerance) ? 0.0 : vertical / verticalTolerance);
                if (score > worstScore)
                {
                    worstScore = score;
                    worst = i;
                }
            }

            if (worst < 0)
                continue;
            keep[worst] = true;
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
    }

    private static Result Unsupported(string reason) => new()
    {
        Borders = Array.Empty<double[]>(),
        Contours = Array.Empty<RecoveredContour>(),
        SpotPoints = Array.Empty<double>(),
        MaximumDeviation = double.NaN,
        Rounds = 0,
        Termination = Termination.UnsupportedInput,
        Diagnostic = reason
    };

    private readonly record struct CellKey(long X, long Y);
}
