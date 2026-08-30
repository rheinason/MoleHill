namespace MoleHill.Core.Grading;

/// <summary>
/// Matches ordinary plan edge polylines to Grade Path centerlines and resolves them into aligned
/// left/right rails. Edge elevations are intentionally absent; PathGrader always uses centerline Z.
/// </summary>
public static class VariablePathWidthResolver
{
    private const double AmbiguityRatio = 1.5;

    public sealed record EdgeDefinition(double[] XyVertices, int VertexCount, bool IsClosed, int SourceIndex);

    public sealed class Options
    {
        /// <summary>Maximum plan distance from an edge to a centerline. Zero uses four times Width.</summary>
        public double MaxEdgeDistance { get; init; }

        public double Tolerance { get; init; } = 1e-6;
    }

    public sealed record Diagnostic(
        string Code,
        string Message,
        int? PathIndex = null,
        int? EdgeSourceIndex = null,
        double? X = null,
        double? Y = null);

    public sealed class Result
    {
        public required PathGrader.PathDefinition[] Paths { get; init; }

        public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

        public int MatchedEdgeCount { get; init; }

        public int PartialEdgeCount { get; init; }
    }

    private sealed class Candidate
    {
        public required int EdgeIndex { get; init; }
        public required int PathIndex { get; init; }
        public required int Side { get; init; }
        public required double Score { get; init; }
        public required double[] EdgeXy { get; init; }
        public required double[] Stations { get; init; }
        public required bool IsClosed { get; init; }
        public bool IsPartial => !IsClosed && (Stations[0] > 1e-6 || Stations[^1] < 1.0 - 1e-6);
    }

    public static Result Resolve(
        IReadOnlyList<PathGrader.PathDefinition> paths,
        IReadOnlyList<EdgeDefinition> edges,
        Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(edges);
        Options settings = options ?? new Options();
        double tolerance = Math.Max(Math.Abs(settings.Tolerance), 1e-9);
        var diagnostics = new List<Diagnostic>();

        if (paths.Count == 0 || edges.Count == 0)
        {
            return new Result
            {
                Paths = paths.ToArray(),
                Diagnostics = diagnostics,
                MatchedEdgeCount = 0,
                PartialEdgeCount = 0
            };
        }

        var candidatesByEdge = new List<Candidate>[edges.Count];
        for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
        {
            candidatesByEdge[edgeIndex] = new List<Candidate>();
            EdgeDefinition edge = edges[edgeIndex];
            if (!IsUsable(edge.XyVertices, edge.VertexCount))
            {
                diagnostics.Add(new Diagnostic(
                    "grade_path.variable_edge.invalid",
                    $"Width edge {edge.SourceIndex} has fewer than two valid plan points and was ignored.",
                    EdgeSourceIndex: edge.SourceIndex));
                continue;
            }

            for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
            {
                PathGrader.PathDefinition path = paths[pathIndex];
                bool pathClosed = IsClosed(path, tolerance);
                if (pathClosed != edge.IsClosed)
                    continue;

                double maxDistance = settings.MaxEdgeDistance > 0.0
                    ? settings.MaxEdgeDistance
                    : path.Width * 4.0;
                Candidate? candidate = pathClosed
                    ? EvaluateClosed(edgeIndex, pathIndex, path, edge, maxDistance, tolerance)
                    : EvaluateOpen(edgeIndex, pathIndex, path, edge, maxDistance, tolerance);
                if (candidate != null)
                    candidatesByEdge[edgeIndex].Add(candidate);
            }
        }

        // An authored edge can belong to only one path/side. Reject genuinely ambiguous ownership.
        var edgeWinners = new List<Candidate>();
        for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
        {
            List<Candidate> ranked = candidatesByEdge[edgeIndex].OrderBy(static candidate => candidate.Score).ToList();
            if (ranked.Count == 0)
            {
                GetRepresentativePoint(edges[edgeIndex], out double x, out double y);
                diagnostics.Add(new Diagnostic(
                    "grade_path.variable_edge.unmatched",
                    $"Width edge {edges[edgeIndex].SourceIndex} did not form a valid ordered side within the matching distance; constant Width is used.",
                    EdgeSourceIndex: edges[edgeIndex].SourceIndex,
                    X: x,
                    Y: y));
                continue;
            }

            if (ranked.Count > 1 && ranked[1].Score <= ranked[0].Score * AmbiguityRatio)
            {
                GetRepresentativePoint(edges[edgeIndex], out double x, out double y);
                diagnostics.Add(new Diagnostic(
                    "grade_path.variable_edge.ambiguous_path",
                    $"Width edge {edges[edgeIndex].SourceIndex} is ambiguous between nearby centerlines; constant Width is used.",
                    EdgeSourceIndex: edges[edgeIndex].SourceIndex,
                    X: x,
                    Y: y));
                continue;
            }

            edgeWinners.Add(ranked[0]);
        }

        // Each centerline has at most one authored edge per side.
        var accepted = new Dictionary<(int Path, int Side), Candidate>();
        foreach (IGrouping<(int Path, int Side), Candidate> group in edgeWinners.GroupBy(static candidate => (candidate.PathIndex, candidate.Side)))
        {
            List<Candidate> ranked = group.OrderBy(static candidate => candidate.Score).ToList();
            if (ranked.Count > 1 && ranked[1].Score <= ranked[0].Score * AmbiguityRatio)
            {
                foreach (Candidate candidate in ranked)
                {
                    GetRepresentativePoint(candidate.EdgeXy, candidate.EdgeXy.Length / 2, out double x, out double y);
                    diagnostics.Add(new Diagnostic(
                        "grade_path.variable_edge.ambiguous_side",
                        $"More than one width edge plausibly controls the same side of path {candidate.PathIndex}; that side uses constant Width.",
                        candidate.PathIndex,
                        edges[candidate.EdgeIndex].SourceIndex,
                        x,
                        y));
                }
                continue;
            }

            accepted[group.Key] = ranked[0];
        }

        var resolved = new PathGrader.PathDefinition[paths.Count];
        int partialCount = 0;
        for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
        {
            accepted.TryGetValue((pathIndex, 1), out Candidate? left);
            accepted.TryGetValue((pathIndex, -1), out Candidate? right);
            resolved[pathIndex] = ResolvePath(paths[pathIndex], left, right, tolerance);
            if (left?.IsPartial == true) partialCount++;
            if (right?.IsPartial == true) partialCount++;
        }

        foreach (Candidate candidate in accepted.Values)
        {
            GetRepresentativePoint(candidate.EdgeXy, candidate.EdgeXy.Length / 2, out double x, out double y);
            diagnostics.Add(new Diagnostic(
                candidate.IsPartial ? "grade_path.variable_edge.partial" : "grade_path.variable_edge.matched",
                candidate.IsPartial
                    ? $"Width edge {edges[candidate.EdgeIndex].SourceIndex} controls part of path {candidate.PathIndex}; its ends blend into constant Width."
                    : $"Width edge {edges[candidate.EdgeIndex].SourceIndex} controls the {(candidate.Side > 0 ? "left" : "right")} side of path {candidate.PathIndex}.",
                candidate.PathIndex,
                edges[candidate.EdgeIndex].SourceIndex,
                x,
                y));
        }

        return new Result
        {
            Paths = resolved,
            Diagnostics = diagnostics,
            MatchedEdgeCount = accepted.Count,
            PartialEdgeCount = partialCount
        };
    }

    private static Candidate? EvaluateOpen(
        int edgeIndex,
        int pathIndex,
        PathGrader.PathDefinition path,
        EdgeDefinition edge,
        double maxDistance,
        double tolerance)
    {
        Candidate? forward = EvaluateOpenDirection(edgeIndex, pathIndex, path, edge.XyVertices, edge.VertexCount, maxDistance, tolerance);
        double[] reversed = Reverse(edge.XyVertices, edge.VertexCount);
        Candidate? backward = EvaluateOpenDirection(edgeIndex, pathIndex, path, reversed, edge.VertexCount, maxDistance, tolerance);
        return forward is null || (backward != null && backward.Score < forward.Score) ? backward : forward;
    }

    private static Candidate? EvaluateOpenDirection(
        int edgeIndex,
        int pathIndex,
        PathGrader.PathDefinition path,
        double[] edgeXy,
        int edgeCount,
        double maxDistance,
        double tolerance)
    {
        double[] centerLengths = Cumulative(path.XyVertices, path.VertexCount);
        double centerLength = centerLengths[^1];
        if (centerLength <= tolerance)
            return null;

        var stations = new double[edgeCount];
        var distances = new double[edgeCount];
        var sides = new int[edgeCount];
        for (int i = 0; i < edgeCount; i++)
        {
            ProjectToPath(path, centerLengths, edgeXy[i * 2], edgeXy[(i * 2) + 1], out stations[i], out distances[i], out sides[i]);
        }

        int dominantSide = sides.Count(static side => side > 0) >= sides.Count(static side => side < 0) ? 1 : -1;
        int bestStart = -1;
        int bestEnd = -1;
        int runStart = -1;
        for (int i = 0; i < edgeCount; i++)
        {
            bool valid = distances[i] <= maxDistance && sides[i] == dominantSide;
            bool ordered = i == 0 || stations[i] + 0.04 >= stations[i - 1];
            if (valid && (runStart < 0 || ordered))
            {
                if (runStart < 0) runStart = i;
            }
            else
            {
                CommitRun(i - 1);
                runStart = valid ? i : -1;
            }
        }
        CommitRun(edgeCount - 1);

        if (bestStart < 0 || bestEnd <= bestStart)
            return null;

        int count = bestEnd - bestStart + 1;
        var acceptedXy = new double[count * 2];
        var acceptedStations = new double[count];
        var acceptedDistances = new double[count];
        Array.Copy(edgeXy, bestStart * 2, acceptedXy, 0, count * 2);
        Array.Copy(stations, bestStart, acceptedStations, 0, count);
        Array.Copy(distances, bestStart, acceptedDistances, 0, count);
        double coverage = Math.Max(0.0, acceptedStations[^1] - acceptedStations[0]);
        double score = Score(acceptedDistances) + ((1.0 - coverage) * path.Width);
        return new Candidate
        {
            EdgeIndex = edgeIndex,
            PathIndex = pathIndex,
            Side = dominantSide,
            Score = score,
            EdgeXy = acceptedXy,
            Stations = acceptedStations,
            IsClosed = false
        };

        void CommitRun(int runEnd)
        {
            if (runStart < 0 || runEnd <= runStart)
                return;
            if (bestStart < 0 || stations[runEnd] - stations[runStart] > stations[bestEnd] - stations[bestStart])
            {
                bestStart = runStart;
                bestEnd = runEnd;
            }
        }
    }

    private static Candidate? EvaluateClosed(
        int edgeIndex,
        int pathIndex,
        PathGrader.PathDefinition path,
        EdgeDefinition edge,
        double maxDistance,
        double tolerance)
    {
        int edgeCount = EffectiveClosedCount(edge.XyVertices, edge.VertexCount, tolerance);
        if (edgeCount < 3)
            return null;

        int seam = ClosestVertex(edge.XyVertices, edgeCount, path.XyVertices[0], path.XyVertices[1]);
        double[] forward = RotateClosed(edge.XyVertices, edgeCount, seam, reverse: false);
        double[] reverse = RotateClosed(edge.XyVertices, edgeCount, seam, reverse: true);
        Candidate? a = EvaluateClosedDirection(edgeIndex, pathIndex, path, forward, maxDistance);
        Candidate? b = EvaluateClosedDirection(edgeIndex, pathIndex, path, reverse, maxDistance);
        return a is null || (b != null && b.Score < a.Score) ? b : a;
    }

    private static Candidate? EvaluateClosedDirection(
        int edgeIndex,
        int pathIndex,
        PathGrader.PathDefinition path,
        double[] edgeXy,
        double maxDistance)
    {
        int count = edgeXy.Length / 2;
        double[] centerLengths = Cumulative(path.XyVertices, path.VertexCount);
        var distances = new double[count];
        var sides = new int[count];
        for (int i = 0; i < count; i++)
        {
            ProjectToPath(path, centerLengths, edgeXy[i * 2], edgeXy[(i * 2) + 1], out _, out distances[i], out sides[i]);
            if (distances[i] > maxDistance)
                return null;
        }

        int side = sides.Count(static value => value > 0) >= sides.Count(static value => value < 0) ? 1 : -1;
        if (sides.Count(value => value == side) < Math.Ceiling(count * 0.9))
            return null;

        var stations = new double[count];
        for (int i = 0; i < count; i++)
            stations[i] = (double)i / count;
        return new Candidate
        {
            EdgeIndex = edgeIndex,
            PathIndex = pathIndex,
            Side = side,
            Score = Score(distances),
            EdgeXy = edgeXy,
            Stations = stations,
            IsClosed = true
        };
    }

    private static PathGrader.PathDefinition ResolvePath(
        PathGrader.PathDefinition path,
        Candidate? left,
        Candidate? right,
        double tolerance)
    {
        if (left is null && right is null)
            return path;

        double[] sourceLengths = Cumulative(path.XyVertices, path.VertexCount);
        double totalLength = sourceLengths[^1];
        var stationSet = new SortedSet<double>();
        for (int i = 0; i < path.VertexCount; i++)
            stationSet.Add(totalLength <= tolerance ? 0.0 : sourceLengths[i] / totalLength);
        if (left is not null) foreach (double station in left.Stations) stationSet.Add(Math.Clamp(station, 0.0, 1.0));
        if (right is not null) foreach (double station in right.Stations) stationSet.Add(Math.Clamp(station, 0.0, 1.0));

        double[] stations = stationSet.ToArray();
        var centerXy = new double[stations.Length * 2];
        var z = new double[stations.Length];
        var leftXy = new double[stations.Length * 2];
        var rightXy = new double[stations.Length * 2];
        for (int i = 0; i < stations.Length; i++)
        {
            SamplePath(path, sourceLengths, stations[i], out double cx, out double cy, out double cz, out double tx, out double ty);
            centerXy[i * 2] = cx;
            centerXy[(i * 2) + 1] = cy;
            z[i] = cz;
            ResolveSide(path, left, stations[i], totalLength, cx, cy, -ty, tx, out leftXy[i * 2], out leftXy[(i * 2) + 1]);
            ResolveSide(path, right, stations[i], totalLength, cx, cy, ty, -tx, out rightXy[i * 2], out rightXy[(i * 2) + 1]);
        }

        return new PathGrader.PathDefinition(
            centerXy,
            z,
            stations.Length,
            path.Width,
            path.SlopeAngleDeg,
            path.MaxDistance,
            path.FillSlopeAngleDeg,
            leftXy,
            rightXy,
            path.IsClosed || IsClosed(path, tolerance));
    }

    private static void ResolveSide(
        PathGrader.PathDefinition path,
        Candidate? candidate,
        double station,
        double totalLength,
        double cx,
        double cy,
        double fallbackNx,
        double fallbackNy,
        out double x,
        out double y)
    {
        double fallbackX = cx + (fallbackNx * path.Width * 0.5);
        double fallbackY = cy + (fallbackNy * path.Width * 0.5);
        if (candidate is null)
        {
            x = fallbackX;
            y = fallbackY;
            return;
        }

        double first = candidate.Stations[0];
        double last = candidate.IsClosed ? 1.0 : candidate.Stations[^1];
        if (!candidate.IsClosed && (station < first || station > last))
        {
            x = fallbackX;
            y = fallbackY;
            return;
        }

        SampleCandidate(candidate, station, out double edgeX, out double edgeY);
        double influence = 1.0;
        if (!candidate.IsClosed)
        {
            double spanLength = Math.Max(0.0, (last - first) * totalLength);
            double blendLength = Math.Min(path.Width * 2.0, spanLength * 0.5);
            if (blendLength > 1e-9)
            {
                if (first > 1e-6)
                    influence *= SmoothStep(Math.Clamp(((station - first) * totalLength) / blendLength, 0.0, 1.0));
                if (last < 1.0 - 1e-6)
                    influence *= SmoothStep(Math.Clamp(((last - station) * totalLength) / blendLength, 0.0, 1.0));
            }
        }

        x = fallbackX + ((edgeX - fallbackX) * influence);
        y = fallbackY + ((edgeY - fallbackY) * influence);
    }

    private static void SampleCandidate(Candidate candidate, double station, out double x, out double y)
    {
        double query = candidate.IsClosed ? station - Math.Floor(station) : station;
        int index = 1;
        while (index < candidate.Stations.Length && candidate.Stations[index] < query)
            index++;
        if (index >= candidate.Stations.Length)
        {
            if (candidate.IsClosed)
            {
                double aStation = candidate.Stations[^1];
                double t = (query - aStation) / Math.Max(1.0 - aStation, 1e-12);
                x = candidate.EdgeXy[^2] + ((candidate.EdgeXy[0] - candidate.EdgeXy[^2]) * t);
                y = candidate.EdgeXy[^1] + ((candidate.EdgeXy[1] - candidate.EdgeXy[^1]) * t);
                return;
            }

            x = candidate.EdgeXy[^2];
            y = candidate.EdgeXy[^1];
            return;
        }

        if (index == 0)
        {
            x = candidate.EdgeXy[0];
            y = candidate.EdgeXy[1];
            return;
        }

        double a = candidate.Stations[index - 1];
        double b = candidate.Stations[index];
        double blend = b <= a + 1e-12 ? 0.0 : (query - a) / (b - a);
        x = candidate.EdgeXy[(index - 1) * 2] + ((candidate.EdgeXy[index * 2] - candidate.EdgeXy[(index - 1) * 2]) * blend);
        y = candidate.EdgeXy[((index - 1) * 2) + 1] + ((candidate.EdgeXy[(index * 2) + 1] - candidate.EdgeXy[((index - 1) * 2) + 1]) * blend);
    }

    private static void ProjectToPath(
        PathGrader.PathDefinition path,
        double[] cumulative,
        double x,
        double y,
        out double station,
        out double distance,
        out int side)
    {
        double bestSquared = double.MaxValue;
        double bestAlong = 0.0;
        double bestCross = 0.0;
        for (int i = 1; i < path.VertexCount; i++)
        {
            double ax = path.XyVertices[(i - 1) * 2];
            double ay = path.XyVertices[((i - 1) * 2) + 1];
            double bx = path.XyVertices[i * 2];
            double by = path.XyVertices[(i * 2) + 1];
            double dx = bx - ax;
            double dy = by - ay;
            double lengthSquared = (dx * dx) + (dy * dy);
            double t = lengthSquared <= 1e-18 ? 0.0 : Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared, 0.0, 1.0);
            double px = ax + (dx * t);
            double py = ay + (dy * t);
            double ex = x - px;
            double ey = y - py;
            double squared = (ex * ex) + (ey * ey);
            if (squared >= bestSquared)
                continue;
            bestSquared = squared;
            bestAlong = cumulative[i - 1] + ((cumulative[i] - cumulative[i - 1]) * t);
            bestCross = (dx * (y - ay)) - (dy * (x - ax));
        }

        station = cumulative[^1] <= 1e-12 ? 0.0 : Math.Clamp(bestAlong / cumulative[^1], 0.0, 1.0);
        distance = Math.Sqrt(bestSquared);
        side = bestCross >= 0.0 ? 1 : -1;
    }

    private static void SamplePath(
        PathGrader.PathDefinition path,
        double[] cumulative,
        double station,
        out double x,
        out double y,
        out double z,
        out double tangentX,
        out double tangentY)
    {
        double target = station * cumulative[^1];
        int index = 1;
        while (index < cumulative.Length && cumulative[index] < target)
            index++;
        index = Math.Min(index, cumulative.Length - 1);
        double aDistance = cumulative[index - 1];
        double bDistance = cumulative[index];
        double t = bDistance <= aDistance + 1e-12 ? 0.0 : (target - aDistance) / (bDistance - aDistance);
        double ax = path.XyVertices[(index - 1) * 2];
        double ay = path.XyVertices[((index - 1) * 2) + 1];
        double bx = path.XyVertices[index * 2];
        double by = path.XyVertices[(index * 2) + 1];
        x = ax + ((bx - ax) * t);
        y = ay + ((by - ay) * t);
        z = path.ZValues[index - 1] + ((path.ZValues[index] - path.ZValues[index - 1]) * t);
        double dx = bx - ax;
        double dy = by - ay;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        tangentX = length <= 1e-12 ? 1.0 : dx / length;
        tangentY = length <= 1e-12 ? 0.0 : dy / length;
    }

    private static double Score(double[] distances)
    {
        double[] sorted = (double[])distances.Clone();
        Array.Sort(sorted);
        double mean = distances.Average();
        double q1 = sorted[(int)Math.Floor((sorted.Length - 1) * 0.25)];
        double q3 = sorted[(int)Math.Floor((sorted.Length - 1) * 0.75)];
        return mean + (0.5 * (q3 - q1)) + (0.25 * sorted[^1]);
    }

    private static double[] Cumulative(double[] xy, int count)
    {
        var result = new double[count];
        for (int i = 1; i < count; i++)
        {
            double dx = xy[i * 2] - xy[(i - 1) * 2];
            double dy = xy[(i * 2) + 1] - xy[((i - 1) * 2) + 1];
            result[i] = result[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
        }
        return result;
    }

    private static bool IsClosed(PathGrader.PathDefinition path, double tolerance)
    {
        if (path.IsClosed || path.VertexCount < 3)
            return path.IsClosed;
        double dx = path.XyVertices[0] - path.XyVertices[(path.VertexCount - 1) * 2];
        double dy = path.XyVertices[1] - path.XyVertices[((path.VertexCount - 1) * 2) + 1];
        return (dx * dx) + (dy * dy) <= tolerance * tolerance;
    }

    private static bool IsUsable(double[] xy, int count) => xy is not null && count >= 2 && xy.Length >= count * 2;

    private static void GetRepresentativePoint(EdgeDefinition edge, out double x, out double y) =>
        GetRepresentativePoint(edge.XyVertices, edge.VertexCount, out x, out y);

    private static void GetRepresentativePoint(double[] xy, int count, out double x, out double y)
    {
        if (!IsUsable(xy, count))
        {
            x = double.NaN;
            y = double.NaN;
            return;
        }

        int index = Math.Clamp(count / 2, 0, count - 1);
        x = xy[index * 2];
        y = xy[(index * 2) + 1];
    }

    private static double[] Reverse(double[] xy, int count)
    {
        var result = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            result[i * 2] = xy[(count - 1 - i) * 2];
            result[(i * 2) + 1] = xy[((count - 1 - i) * 2) + 1];
        }
        return result;
    }

    private static int EffectiveClosedCount(double[] xy, int count, double tolerance)
    {
        if (count < 2) return count;
        double dx = xy[0] - xy[(count - 1) * 2];
        double dy = xy[1] - xy[((count - 1) * 2) + 1];
        return (dx * dx) + (dy * dy) <= tolerance * tolerance ? count - 1 : count;
    }

    private static int ClosestVertex(double[] xy, int count, double x, double y)
    {
        int best = 0;
        double bestSquared = double.MaxValue;
        for (int i = 0; i < count; i++)
        {
            double dx = xy[i * 2] - x;
            double dy = xy[(i * 2) + 1] - y;
            double squared = (dx * dx) + (dy * dy);
            if (squared < bestSquared) { bestSquared = squared; best = i; }
        }
        return best;
    }

    private static double[] RotateClosed(double[] xy, int count, int seam, bool reverse)
    {
        var result = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            int source = reverse ? (seam - i + count) % count : (seam + i) % count;
            result[i * 2] = xy[source * 2];
            result[(i * 2) + 1] = xy[(source * 2) + 1];
        }
        return result;
    }

    private static double SmoothStep(double value) => value * value * (3.0 - (2.0 * value));
}
