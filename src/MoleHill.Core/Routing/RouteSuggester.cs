using MoleHill.Core.Geometry;

namespace MoleHill.Core.Routing;

/// <summary>
/// Suggests up to three distinct routes between picked points over a sampled terrain, each returned as a
/// simplified 3D polyline whose profile never exceeds the grade limit.
/// </summary>
/// <remarks>
/// <para><b>Search.</b> A* over the nodes of a <see cref="RouteHeightGrid"/>, 16-connected (the eight
/// neighbours plus the eight knight moves, so a route can hold a bearing within ~13 degrees of any
/// direction instead of zig-zagging between 45-degree ones). A move of plan length L costs</para>
/// <code>
/// L * (wDistance + wSteepness * min(r, 1)^2 + wEarthworks * E(r))     r = moveGrade / maxGrade
/// E(r) = 0 for r &lt;= 1, else 2x + 4x^2 with x = r - 1
/// </code>
/// <para>Steepness is quadratic in the share of the limit used, so a route spends grade where it has to.
/// A move steeper than the limit is not forbidden: the profile cannot follow that ground, so it costs
/// earthworks — little for a short bump, a great deal for a hillside — and the Earthworks weight decides
/// between cutting through and going round. A move's grade is the steeper of its two halves, measured
/// against the ground at its midpoint (the mean of the nodes it passes between), so a diagonal or knight
/// move cannot step over a ridge or a ditch that its end nodes straddle. A move between or past
/// off-terrain nodes is impossible, which is what keeps a route out of holes.</para>
///
/// <para><b>Profile.</b> The plan path is straightened (string pulling: a chord replaces a stretch of grid
/// path when it costs no more by the same pricing, so a grid route becomes the straight line it
/// approximates, and a chord over a hole is never taken), sampled along its length, and given the grade-limited profile of
/// <see cref="GradeLimitedProfile"/>: the ground wherever the ground is within the limit, an even split
/// of cut and fill where it is not, held to the ground at the picked points. The grade limit is
/// therefore hard on the output — the centreline Grade Path will take as its design — and the
/// earthworks readout is that profile minus the ground over the corridor width. A leg whose ends are too
/// far apart in height for its length fails with the grade it would need.</para>
///
/// <para><b>Alternatives.</b> After each route the nodes within the corridor band around it cost 50% more,
/// and the search runs again. A candidate whose drawn line (after straightening — two grid paths can
/// straighten into one line) still runs inside an earlier route's band for 70% or more of its length is
/// a near-duplicate: it is dropped, its band is penalised too, and the search tries
/// again, a bounded number of times. Routes come back ordered by unpenalised cost.</para>
///
/// <para>Everything is single-threaded and free of hashing or randomness, so a result is reproducible
/// bit for bit.</para>
/// </remarks>
public static class RouteSuggester
{
    public const int MaxAlternatives = 3;

    /// <summary>Extra cost factor on a node per earlier route whose band it lies in.</summary>
    private const float DiversityPenaltyStep = 0.5f;

    /// <summary>A candidate sharing at least this much of its length with an earlier route's band is dropped.</summary>
    internal const double NearDuplicateShare = 0.7;

    private const int AttemptsPerAlternative = 4;

    /// <summary>
    /// The plan rectangle worth searching: the points' bounding box grown by half its diagonal, so a
    /// route can swing wide of a hill or zig-zag up a slope without the domain clipping it.
    /// </summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) SearchDomain(
        IReadOnlyList<(double X, double Y)> points,
        double minimumMargin = 0.0)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach ((double x, double y) in points)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        double margin = Math.Max(0.5 * diagonal, minimumMargin);
        return (minX - margin, minY - margin, maxX + margin, maxY + margin);
    }

    /// <summary>Total plan length of the polyline through the points — what the automatic cell size scales with.</summary>
    public static double DirectSpan(IReadOnlyList<(double X, double Y)> points)
    {
        double span = 0.0;
        for (int i = 1; i < points.Count; i++)
            span += Math.Sqrt(Geometry2D.DistanceSquared(points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y));
        return span;
    }

    public static RouteSuggestionResult Suggest(
        RouteHeightGrid grid,
        RouteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Points.Count < 2)
            return RouteSuggestionResult.Fail(RouteFailure.TooFewPoints);

        if (!IsValid(request))
            return RouteSuggestionResult.Fail(RouteFailure.InvalidSettings);

        int pointCount = request.Points.Count;
        var nodes = new int[pointCount];
        var pins = new double[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            (double x, double y) = request.Points[i];
            nodes[i] = grid.NearestGroundNode(x, y, 2);
            if (nodes[i] < 0)
                return RouteSuggestionResult.Fail(RouteFailure.PointOffTerrain, point: i);

            pins[i] = grid.TrySample(x, y, out double z) ? z : grid.HeightAt(nodes[i]);
        }

        var search = new GridSearch(grid, request);
        double cell = grid.CellSize;
        int bandCells = Math.Max(2, (int)Math.Ceiling(request.CorridorWidth / cell));
        int wanted = Math.Clamp(request.AlternativeCount, 1, MaxAlternatives);
        double profileTolerance = request.ProfileTolerance > 0.0 ? request.ProfileTolerance : 0.01 * cell;

        var accepted = new List<RouteAlternative>();
        var bands = new byte[grid.NodeCount];
        int dropped = 0;
        int attempts = 1 + ((wanted - 1) * AttemptsPerAlternative);

        for (int attempt = 0; attempt < attempts && accepted.Count < wanted; attempt++)
        {
            var legs = new List<List<int>>(pointCount - 1);
            bool connected = true;
            for (int leg = 0; leg < pointCount - 1; leg++)
            {
                List<int>? path = search.FindPath(nodes[leg], nodes[leg + 1], cancellationToken);
                if (path == null)
                {
                    if (accepted.Count == 0)
                        return RouteSuggestionResult.Fail(RouteFailure.NotConnected, leg: leg);
                    connected = false;
                    break;
                }

                legs.Add(path);
            }

            // Penalised costs only grow, so a leg that cannot be joined now never will be.
            if (!connected)
                break;

            List<int> raw = Concatenate(legs);
            if (!TryBuildRoute(grid, request, legs, pins, profileTolerance, out double[] points, out int failedLeg, out double required))
            {
                if (accepted.Count == 0)
                    return RouteSuggestionResult.Fail(RouteFailure.ExceedsMaxGrade, leg: failedLeg, requiredGrade: required);

                dropped++;
                search.Penalise(raw, bandCells, DiversityPenaltyStep);
                continue;
            }

            // Compared as drawn, not as searched: two grid paths a few cells apart can straighten into
            // the same line, and the user only ever sees the line.
            List<int> drawn = search.Rasterise(points);
            if (accepted.Count > 0 && IsNearDuplicate(search, drawn, bands, accepted.Count))
            {
                dropped++;
                search.Penalise(raw, bandCells, DiversityPenaltyStep);
                search.Penalise(drawn, bandCells, DiversityPenaltyStep);
                continue;
            }

            RouteMetrics metrics = RouteMetrics.Measure(
                points,
                (x, y) => grid.TrySample(x, y, out double z) ? z : null,
                request.CorridorWidth,
                0.5 * cell);
            accepted.Add(new RouteAlternative(points, metrics, RouteLegBuilder.PolylineCost(grid, new RouteCost(request), points)));
            search.Stamp(drawn, bandCells, bands, (byte)(1 << (accepted.Count - 1)));
            search.Penalise(drawn, bandCells, DiversityPenaltyStep);
        }

        List<RouteAlternative> ordered = accepted.OrderBy(route => route.SearchCost).ToList();
        return new RouteSuggestionResult(RouteFailure.None, ordered, -1, -1, null, dropped);
    }

    private static bool IsValid(RouteRequest request)
    {
        static bool NonNegative(double value) => value >= 0.0 && double.IsFinite(value);
        return request.MaxGrade > 0.0 && double.IsFinite(request.MaxGrade) &&
            NonNegative(request.DistanceWeight) &&
            NonNegative(request.SteepnessWeight) &&
            NonNegative(request.EarthworksWeight) &&
            request.DistanceWeight + request.SteepnessWeight + request.EarthworksWeight > 0.0 &&
            request.CorridorWidth > 0.0 && double.IsFinite(request.CorridorWidth);
    }

    private static List<int> Concatenate(List<List<int>> legs)
    {
        var raw = new List<int>(legs.Sum(leg => leg.Count));
        foreach (List<int> leg in legs)
        {
            int skip = raw.Count > 0 && leg.Count > 0 && raw[^1] == leg[0] ? 1 : 0;
            for (int i = skip; i < leg.Count; i++)
                raw.Add(leg[i]);
        }

        return raw;
    }

    /// <summary>True when the candidate runs inside any one earlier route's band for most of its length.</summary>
    private static bool IsNearDuplicate(GridSearch search, List<int> raw, byte[] bands, int routeCount)
    {
        double total = 0.0;
        var shared = new double[routeCount];
        for (int i = 1; i < raw.Count; i++)
        {
            double length = search.PlanDistance(raw[i - 1], raw[i]);
            total += length;
            byte common = (byte)(bands[raw[i - 1]] & bands[raw[i]]);
            for (int route = 0; route < routeCount; route++)
            {
                if ((common & (1 << route)) != 0)
                    shared[route] += length;
            }
        }

        if (total <= 0.0)
            return true;

        for (int route = 0; route < routeCount; route++)
        {
            if (shared[route] >= NearDuplicateShare * total)
                return true;
        }

        return false;
    }

    private static bool TryBuildRoute(
        RouteHeightGrid grid,
        RouteRequest request,
        List<List<int>> legs,
        double[] pins,
        double profileTolerance,
        out double[] points,
        out int failedLeg,
        out double requiredGrade)
    {
        var output = new List<double>();
        failedLeg = -1;
        requiredGrade = 0.0;
        for (int leg = 0; leg < legs.Count; leg++)
        {
            // Straightening shortens a leg; if that is what tips it over the limit, the raw grid path —
            // the longest honest version of the same route — is tried before giving up.
            if (!RouteLegBuilder.TryBuild(grid, request, legs[leg], request.Points[leg], request.Points[leg + 1],
                    pins[leg], pins[leg + 1], straighten: true, profileTolerance, out List<double> legPoints, out double required) &&
                !RouteLegBuilder.TryBuild(grid, request, legs[leg], request.Points[leg], request.Points[leg + 1],
                    pins[leg], pins[leg + 1], straighten: false, profileTolerance, out legPoints, out required))
            {
                points = Array.Empty<double>();
                failedLeg = leg;
                requiredGrade = required;
                return false;
            }

            int skip = output.Count > 0 ? 3 : 0;
            for (int i = skip; i < legPoints.Count; i++)
                output.Add(legPoints[i]);
        }

        points = output.ToArray();
        return true;
    }

    /// <summary>The grid A* and the per-node diversity penalty it reads.</summary>
    private sealed class GridSearch
    {
        private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1, 2, 2, -2, -2, 1, 1, -1, -1 };
        private static readonly int[] Dy = { 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 2, -2, 2, -2 };
        private static readonly double[] UnitLength = BuildUnitLengths();

        private readonly RouteHeightGrid _grid;
        private readonly int _columns;
        private readonly int _rows;
        private readonly double _cell;
        private readonly RouteCost _price;
        private readonly double[] _cost;
        private readonly int[] _parent;
        private readonly int[] _seen;
        private readonly int[] _closed;
        private readonly int[] _mark;
        private readonly float[] _penalty;
        private readonly PriorityQueue<int, double> _open = new();
        private int _generation;
        private int _markGeneration;

        public GridSearch(RouteHeightGrid grid, RouteRequest request)
        {
            _grid = grid;
            _columns = grid.Columns;
            _rows = grid.Rows;
            _cell = grid.CellSize;
            _price = new RouteCost(request);
            int count = grid.NodeCount;
            _cost = new double[count];
            _parent = new int[count];
            _seen = new int[count];
            _closed = new int[count];
            _mark = new int[count];
            _penalty = new float[count];
        }

        public double PlanDistance(int a, int b)
        {
            int dc = (a % _columns) - (b % _columns);
            int dr = (a / _columns) - (b / _columns);
            return _cell * Math.Sqrt((dc * dc) + (dr * dr));
        }

        public List<int>? FindPath(int start, int goal, CancellationToken cancellationToken)
        {
            _generation++;
            _open.Clear();
            int goalColumn = goal % _columns;
            int goalRow = goal / _columns;
            _cost[start] = 0.0;
            _seen[start] = _generation;
            _parent[start] = -1;
            _open.Enqueue(start, Heuristic(start % _columns, start / _columns, goalColumn, goalRow));
            int pops = 0;

            while (_open.TryDequeue(out int node, out _))
            {
                if (_closed[node] == _generation)
                    continue;
                _closed[node] = _generation;
                if (node == goal)
                    return Reconstruct(goal);

                if ((++pops & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int column = node % _columns;
                int row = node / _columns;
                double reached = _cost[node];
                for (int move = 0; move < Dx.Length; move++)
                {
                    int toColumn = column + Dx[move];
                    int toRow = row + Dy[move];
                    if (toColumn < 0 || toColumn >= _columns || toRow < 0 || toRow >= _rows)
                        continue;

                    int to = (toRow * _columns) + toColumn;
                    if (_closed[to] == _generation || !TryMoveCost(column, row, move, out double moveCost))
                        continue;

                    double cost = reached + (moveCost * (1.0 + _penalty[to]));
                    if (_seen[to] == _generation && cost >= _cost[to])
                        continue;

                    _seen[to] = _generation;
                    _cost[to] = cost;
                    _parent[to] = node;
                    _open.Enqueue(to, cost + Heuristic(toColumn, toRow, goalColumn, goalRow));
                }
            }

            return null;
        }

        /// <summary>The grid nodes nearest a polyline, sampled every half cell, without consecutive repeats.</summary>
        public List<int> Rasterise(double[] pointsXyz)
        {
            var nodes = new List<int>();
            for (int i = 1; i < pointsXyz.Length / 3; i++)
            {
                double ax = pointsXyz[(i - 1) * 3], ay = pointsXyz[((i - 1) * 3) + 1];
                double bx = pointsXyz[i * 3], by = pointsXyz[(i * 3) + 1];
                double length = Math.Sqrt(Geometry2D.DistanceSquared(ax, ay, bx, by));
                int pieces = Math.Max(1, (int)Math.Ceiling(length / (0.5 * _cell)));
                for (int k = i == 1 ? 0 : 1; k <= pieces; k++)
                {
                    double t = (double)k / pieces;
                    int column = Math.Clamp((int)Math.Round((Geometry2D.Lerp(ax, bx, t) - _grid.OriginX) / _cell), 0, _columns - 1);
                    int row = Math.Clamp((int)Math.Round((Geometry2D.Lerp(ay, by, t) - _grid.OriginY) / _cell), 0, _rows - 1);
                    int node = (row * _columns) + column;
                    if (nodes.Count == 0 || nodes[^1] != node)
                        nodes.Add(node);
                }
            }

            return nodes;
        }

        public void Penalise(List<int> path, int radiusCells, float step)
        {
            foreach (int node in NodesNear(path, radiusCells))
                _penalty[node] += step;
        }

        public void Stamp(List<int> path, int radiusCells, byte[] bands, byte bit)
        {
            foreach (int node in NodesNear(path, radiusCells))
                bands[node] |= bit;
        }

        /// <summary>Each node within the radius of any path node, once.</summary>
        private IEnumerable<int> NodesNear(List<int> path, int radiusCells)
        {
            _markGeneration++;
            int radiusSquared = radiusCells * radiusCells;
            foreach (int node in path)
            {
                int column = node % _columns;
                int row = node / _columns;
                for (int dr = -radiusCells; dr <= radiusCells; dr++)
                {
                    int r = row + dr;
                    if (r < 0 || r >= _rows)
                        continue;
                    for (int dc = -radiusCells; dc <= radiusCells; dc++)
                    {
                        int c = column + dc;
                        if (c < 0 || c >= _columns || (dc * dc) + (dr * dr) > radiusSquared)
                            continue;

                        int index = (r * _columns) + c;
                        if (_mark[index] == _markGeneration)
                            continue;
                        _mark[index] = _markGeneration;
                        yield return index;
                    }
                }
            }
        }

        private double Heuristic(int column, int row, int goalColumn, int goalRow)
        {
            int dc = column - goalColumn;
            int dr = row - goalRow;
            return _price.DistanceWeight * _cell * Math.Sqrt((dc * dc) + (dr * dr));
        }

        private bool TryMoveCost(int column, int row, int move, out double cost)
        {
            cost = 0.0;
            int dx = Dx[move], dy = Dy[move];
            double from = _grid.Height(column, row);
            double to = _grid.Height(column + dx, row + dy);
            if (double.IsNaN(from) || double.IsNaN(to))
                return false;

            int sx = Math.Sign(dx), sy = Math.Sign(dy);
            double middle;
            if (dx == 0 || dy == 0)
            {
                middle = 0.5 * (from + to);
            }
            else
            {
                double first, second;
                if (Math.Abs(dx) == 1 && Math.Abs(dy) == 1)
                {
                    first = _grid.Height(column + sx, row);
                    second = _grid.Height(column, row + sy);
                }
                else if (Math.Abs(dx) == 2)
                {
                    first = _grid.Height(column + sx, row);
                    second = _grid.Height(column + sx, row + sy);
                }
                else
                {
                    first = _grid.Height(column, row + sy);
                    second = _grid.Height(column + sx, row + sy);
                }

                if (double.IsNaN(first) || double.IsNaN(second))
                    return false;

                middle = Math.Abs(dx) == Math.Abs(dy)
                    ? 0.25 * (from + to + first + second)
                    : 0.5 * (first + second);
            }

            double length = UnitLength[move] * _cell;
            double grade = Math.Max(Math.Abs(middle - from), Math.Abs(to - middle)) / (0.5 * length);
            cost = length * _price.Factor(grade);
            return true;
        }

        private List<int> Reconstruct(int goal)
        {
            var path = new List<int>();
            for (int node = goal; node >= 0; node = _parent[node])
                path.Add(node);
            path.Reverse();
            return path;
        }

        private static double[] BuildUnitLengths()
        {
            var lengths = new double[Dx.Length];
            for (int move = 0; move < Dx.Length; move++)
                lengths[move] = Math.Sqrt((Dx[move] * Dx[move]) + (Dy[move] * Dy[move]));
            return lengths;
        }
    }
}
