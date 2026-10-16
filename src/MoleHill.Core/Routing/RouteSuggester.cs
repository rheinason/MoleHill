using MoleHill.Core.Geometry;

namespace MoleHill.Core.Routing;

/// <summary>
/// Suggests up to three distinct routes between picked points over a sampled terrain, each returned as a
/// simplified 3D polyline whose profile never exceeds the grade limit.
/// </summary>
/// <remarks>
/// <para><b>Search.</b> A* over the nodes of a <see cref="RouteHeightGrid"/> and the move each was reached
/// by, 16-connected (the eight neighbours plus the eight knight moves, so a route can hold a bearing
/// within ~13 degrees of any direction instead of zig-zagging between 45-degree ones). A turn through θ
/// costs 2R·tan(θ/2) (see <c>GridSearch.FindPath</c>), and a move of plan length L costs</para>
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
/// approximates, and a chord over a hole is never taken), and, if a minimum radius is asked for, edited
/// so its corners have room for it (<see cref="PlanRadiusSimplifier"/>) and rounded
/// (<see cref="PlanFillet"/>); then sampled along its length, and given the grade-limited profile of
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
    /// How much longer than its climb strictly needs a leg is held when straightening has to be limited:
    /// rounding a switchback shortens a leg by a few metres, so the first try leaves 5% for it.
    /// </summary>
    private static readonly double[] LengthSlacks = { 1.05, 1.15, 1.3 };

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
            if (!TryBuildRoute(grid, request, legs, pins, profileTolerance,
                    out List<RouteSpan> spans, out double[] points, out RouteFilletSummary fillet, out int failedLeg, out double required))
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
            accepted.Add(new RouteAlternative(
                spans, points, metrics, fillet, RouteLegBuilder.PolylineCost(grid, new RouteCost(request), points)));
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
            request.CorridorWidth > 0.0 && double.IsFinite(request.CorridorWidth) &&
            NonNegative(request.MinimumRadius);
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
        out List<RouteSpan> spans,
        out double[] points,
        out RouteFilletSummary fillet,
        out int failedLeg,
        out double requiredGrade)
    {
        spans = new List<RouteSpan>();
        fillet = RouteFilletSummary.None;
        failedLeg = -1;
        requiredGrade = 0.0;
        bool rounded = request.MinimumRadius > 0.0;
        for (int leg = 0; leg < legs.Count; leg++)
        {
            // Straightening shortens a leg, and so can simplifying it for the radius and rounding its
            // corners; if that is what tips it over the limit, the rounded unsimplified line, the
            // sharp-cornered line and then the raw grid path — the longest honest version of the same
            // route — are tried before giving up. A leg left sharp reports its corners as limited by grade.
            // Straightening is the expensive step, so each plan is made once and tried as many ways as needed.
            var price = new RouteCost(request);
            List<(double X, double Y)> rawPlan = RouteLegBuilder.PlanFromNodes(grid, legs[leg], request.Points[leg], request.Points[leg + 1]);
            List<(double X, double Y)> straightPlan = RouteLegBuilder.StraightenPlan(grid, price, rawPlan);
            List<(double X, double Y)> roomyPlan = rounded
                ? PlanRadiusSimplifier.MakeRoom(grid, price, straightPlan, request.MinimumRadius)
                : straightPlan;
            List<(double X, double Y)>? sharpPlan = null;
            if (!TryBuildLeg(roomyPlan, rounded, out List<RouteSpan> legSpans, out RouteFilletSummary legFillet, out double required) &&
                !(rounded && !roomyPlan.SequenceEqual(straightPlan) &&
                    TryBuildLeg(straightPlan, true, out legSpans, out legFillet, out required)) &&
                !TryHeldToLength(out legSpans, out legFillet, out required))
            {
                sharpPlan = TryBuildLeg(straightPlan, false, out legSpans, out legFillet, out required) ? straightPlan
                    : TryBuildLeg(rawPlan, false, out legSpans, out legFillet, out required) ? rawPlan
                    : null;
                if (sharpPlan == null)
                {
                    points = Array.Empty<double>();
                    failedLeg = leg;
                    requiredGrade = required;
                    return false;
                }
            }

            if (rounded && sharpPlan != null && PlanFillet.CountCorners(sharpPlan) is int sharp and > 0)
                legFillet = new RouteFilletSummary(0.0, sharp, 0, sharp);

            spans.AddRange(legSpans);
            fillet = fillet.Merge(legFillet);

            // Straightened too short for its climb: straighten again, held to the length the climb needs plus
            // room for rounding to shorten it, before falling back to sharp corners and the grid staircase.
            bool TryHeldToLength(out List<RouteSpan> built, out RouteFilletSummary summary, out double need)
            {
                double climb = Math.Abs(pins[leg + 1] - pins[leg]) / request.MaxGrade;
                foreach (double slack in LengthSlacks)
                {
                    List<(double X, double Y)> held = RouteLegBuilder.StraightenPlan(grid, price, rawPlan, climb * slack);
                    List<(double X, double Y)> heldRoomy = rounded
                        ? PlanRadiusSimplifier.MakeRoom(grid, price, held, request.MinimumRadius, climb * slack)
                        : held;
                    if (TryBuildLeg(heldRoomy, rounded, out built, out summary, out need))
                    {
                        // Whatever stayed under the radius here did so because the climb needed the length.
                        summary = summary with { GradeLimitedCorners = summary.ReducedCorners - summary.ReversalCorners };
                        return true;
                    }
                }

                built = new List<RouteSpan>();
                summary = RouteFilletSummary.None;
                need = 0.0;
                return false;
            }

            bool TryBuildLeg(List<(double X, double Y)> plan, bool round, out List<RouteSpan> built, out RouteFilletSummary summary, out double need) =>
                RouteLegBuilder.TryBuild(grid, request, plan, pins[leg], pins[leg + 1], round, profileTolerance,
                    out built, out summary, out need);
        }

        var dense = new List<double>();
        RouteLegBuilder.AppendDense(spans, dense);
        if (dense.Count == 0)
            dense.AddRange(new[] { request.Points[0].X, request.Points[0].Y, pins[0] });
        points = dense.ToArray();
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
        private readonly double _turnRadiusCost;
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
            _turnRadiusCost = TurnRadiusCost(request);
            int count = grid.NodeCount;
            int states = count * StatesPerNode;
            _cost = new double[states];
            _parent = new int[states];
            _seen = new int[states];
            _closed = new int[states];
            _mark = new int[count];
            _penalty = new float[count];
        }

        public double PlanDistance(int a, int b)
        {
            int dc = (a % _columns) - (b % _columns);
            int dr = (a / _columns) - (b / _columns);
            return _cell * Math.Sqrt((dc * dc) + (dr * dr));
        }

        /// <summary>
        /// A* over (node, heading): the heading is the move the node was reached by, so a move that changes
        /// direction can be charged for the turn. Without it every path of equal length and grade cost the
        /// same however often it turned, and where a route had to be longer than the straight line to keep
        /// its grade it lengthened itself by zig-zagging a cell or two at a time (2026-10-16): a staircase no
        /// rounding can turn into a road. A turn through θ costs the line its rounding occupies,
        /// 2R·tan(θ/2) (at the distance weight), so a long leg with one wide switchback is cheap and a
        /// jitter every few metres is dear.
        /// </summary>
        public List<int>? FindPath(int start, int goal, CancellationToken cancellationToken)
        {
            _generation++;
            _open.Clear();
            int goalColumn = goal % _columns;
            int goalRow = goal / _columns;
            int first = State(start, NoHeading);
            _cost[first] = 0.0;
            _seen[first] = _generation;
            _parent[first] = -1;
            _open.Enqueue(first, Heuristic(start % _columns, start / _columns, goalColumn, goalRow));
            int pops = 0;

            while (_open.TryDequeue(out int state, out _))
            {
                if (_closed[state] == _generation)
                    continue;
                _closed[state] = _generation;
                int node = state / StatesPerNode;
                int heading = state % StatesPerNode;
                if (node == goal)
                    return Reconstruct(state);

                if ((++pops & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int column = node % _columns;
                int row = node / _columns;
                double reached = _cost[state];
                for (int move = 0; move < Dx.Length; move++)
                {
                    int toColumn = column + Dx[move];
                    int toRow = row + Dy[move];
                    if (toColumn < 0 || toColumn >= _columns || toRow < 0 || toRow >= _rows)
                        continue;

                    int to = (toRow * _columns) + toColumn;
                    int toState = State(to, move);
                    if (_closed[toState] == _generation || !TryMoveCost(column, row, move, out double moveCost))
                        continue;

                    double turn = heading == NoHeading ? 0.0 : TurnCost[(heading * Dx.Length) + move] * _turnRadiusCost;
                    double cost = reached + (moveCost * (1.0 + _penalty[to])) + turn;
                    if (_seen[toState] == _generation && cost >= _cost[toState])
                        continue;

                    _seen[toState] = _generation;
                    _cost[toState] = cost;
                    _parent[toState] = state;
                    _open.Enqueue(toState, cost + Heuristic(toColumn, toRow, goalColumn, goalRow));
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

        private List<int> Reconstruct(int goalState)
        {
            var path = new List<int>();
            for (int state = goalState; state >= 0; state = _parent[state])
                path.Add(state / StatesPerNode);
            path.Reverse();
            return path;
        }

        /// <summary>One state per arrival move, plus one for the start, which was reached by none.</summary>
        private const int StatesPerNode = 17;

        private const int NoHeading = 16;

        private static int State(int node, int heading) => (node * StatesPerNode) + heading;

        /// <summary>
        /// The room a turn between every pair of moves takes, per unit of turning radius, by [from * 16 + to]:
        /// <c>2 tan(θ/2)</c>, the length of line a corner through θ occupies once rounded. It is what a turn
        /// really costs the plan — a sharper corner eats far more of the legs either side and shortens the
        /// route more when rounded — so a near-reversal (whose rounding would need legs tens of metres long)
        /// is dear, a 90-degree switchback twice its radius, and a slight bend almost free. Capped at 170
        /// degrees, past which the tangent runs away.
        /// </summary>
        private static readonly double[] TurnCost = BuildTurnCosts();

        private static double[] BuildTurnCosts()
        {
            var angles = new double[Dx.Length * Dx.Length];
            for (int a = 0; a < Dx.Length; a++)
            {
                for (int b = 0; b < Dx.Length; b++)
                {
                    double dot = (Dx[a] * Dx[b]) + (Dy[a] * Dy[b]);
                    double lengths = Math.Sqrt(((Dx[a] * Dx[a]) + (Dy[a] * Dy[a])) * ((Dx[b] * Dx[b]) + (Dy[b] * Dy[b])));
                    double turn = Math.Min(Math.Acos(Math.Clamp(dot / lengths, -1.0, 1.0)), MaximumPricedTurn);
                    angles[(a * Dx.Length) + b] = 2.0 * Math.Tan(0.5 * turn);
                }
            }

            return angles;
        }

        private const double MaximumPricedTurn = 170.0 * Math.PI / 180.0;

        /// <summary>
        /// The turning radius the turn cost is scaled by, priced at the distance weight (or 1 where distance is
        /// weighted at nothing): the minimum radius, or the corridor width when none is asked for, since a road
        /// of that width cannot turn on the spot either.
        /// </summary>
        internal static double TurnRadiusCost(RouteRequest request)
        {
            double radius = request.MinimumRadius > 0.0 ? request.MinimumRadius : request.CorridorWidth;
            double weight = request.DistanceWeight > 0.0 ? request.DistanceWeight : 1.0;
            return radius * weight;
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
