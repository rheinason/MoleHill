using MoleHill.Core.Geometry;

namespace MoleHill.Core.Routing;

/// <summary>
/// Makes room on a straightened plan for its corners to take the minimum radius, before
/// <see cref="PlanFillet"/> rounds them. A corner deflecting by θ needs <c>R tan(θ/2)</c> of both its
/// segments; where a segment is too short for its two corners, the plan is edited around it: a run of
/// corners merged into one where the segments either side meet (one arc across several short
/// segments), a run dropped (a longer chord replaces a short zig-zag), or the segment's corners slid
/// apart until it has room.
/// </summary>
/// <remarks>
/// <para>Every segment without room is priced at its cheapest edit, and the cheapest of those is made
/// first, so the budget goes as far as it can and a spot no affordable edit can fix does not spend it.
/// An edit is priced as the leg will be drawn — rounded, exactly as <see cref="PlanFillet"/> rounds it —
/// by the same <see cref="RouteCost"/> the straightening used, and is never taken where the rounded leg
/// would leave the terrain (at the straightening's ground weight). Edits stop when the cheapest one left
/// would make the leg more than <see cref="CostTolerance"/> dearer than the dearer of the sharp polyline
/// the straightening settled on and its plain rounding. A segment still without room keeps its corners
/// at the largest radius that fits — a tight corner the caller reports.</para>
/// <para>Pricing is local. A corner's fillet depends on the vertices two either side of it, so an edit
/// changes only the corners and segments within a few vertices of it, and only those are priced; a
/// segment is sampled once into running totals (<see cref="Chord"/>) and an arc once per shape, so
/// trying an edit costs a handful of lookups. An edit's price stays valid until an edit is made near
/// it.</para>
/// <para>The leg's ends never move: they are the picked points, and a waypoint is a leg's end.</para>
/// </remarks>
internal static class PlanRadiusSimplifier
{
    /// <summary>
    /// How much dearer than the route the search settled on the simplified, rounded leg may be: 2.7%,
    /// the most a 16-connected grid path can run longer than the straight line it stands for (a bearing
    /// halfway between a straight and a knight move). Costs closer than that are within the search's own
    /// resolution, so the search could as well have drawn either.
    /// </summary>
    internal const double CostTolerance = 0.027;

    private const double StraightTolerance = 1e-6;
    private const double ReversalTolerance = 1e-3;

    /// <summary>The most consecutive corners one edit merges or drops.</summary>
    private const int MaxWindow = 4;

    /// <summary>
    /// How far, in vertices, an edit's price reaches: it moves vertices up to a window and one either
    /// side away from its segment, and a corner's fillet depends on the vertices two either side of it.
    /// </summary>
    private const int PriceReach = MaxWindow + 5;

    /// <summary>The most of a neighbouring segment a slide may take.</summary>
    private const double MaxSlideShare = 0.9;

    /// <summary>
    /// Safety bound on edits per vertex of the input. Merges and drops remove a vertex and a vertex
    /// slides at most once, so the edits end anyway; this only caps the work if that ever changed.
    /// </summary>
    private const int MaxEditsPerVertex = 4;

    private static readonly bool[] SlideModes = { false, true };

    public static List<(double X, double Y)> MakeRoom(
        RouteHeightGrid grid,
        RouteCost price,
        List<(double X, double Y)> plan,
        double radius)
    {
        if (!(radius > 0.0) || plan.Count < 3)
            return plan;

        var pricer = new Pricer(grid, price, radius);
        double sharp = 0.0;
        for (int j = 0; j < plan.Count - 1; j++)
            sharp += pricer.Straight(plan, j, 0.0, 0.0, out _);

        var points = new List<(double X, double Y)>(plan);
        Rounding current = pricer.Round(points);
        double allowance = Math.Max(sharp, current.Total) * (1.0 + CostTolerance);

        // The cheapest edit found for each segment, by the segment's first vertex; null until priced.
        var cheapest = new List<Priced?>(new Priced?[points.Count]);
        var slid = new List<bool>(new bool[points.Count]);
        var candidate = new List<(double X, double Y)>(points.Count);
        for (int round = 0; round < MaxEditsPerVertex * plan.Count; round++)
        {
            double headroom = allowance - current.Total;
            int chosen = -1;
            for (int j = 0; j < points.Count - 1; j++)
            {
                if (!Overfilled(points, j, radius))
                    continue;

                // A segment found unaffordable is priced again only if an edit has since made room.
                if (cheapest[j] is not Priced known || (double.IsPositiveInfinity(known.Delta) && known.Headroom < headroom))
                    cheapest[j] = Cheapest(pricer, points, current, slid, j, headroom, candidate);
                if (cheapest[j]!.Value.Delta < (chosen < 0 ? double.PositiveInfinity : cheapest[chosen]!.Value.Delta))
                    chosen = j;
            }

            if (chosen < 0 || !(cheapest[chosen]!.Value.Delta <= headroom))
                break;

            Edit edit = cheapest[chosen]!.Value.Edit;
            Apply(points, edit, candidate);
            points.Clear();
            points.AddRange(candidate);
            current = pricer.Round(points);

            cheapest.RemoveRange(edit.First, edit.Remove);
            cheapest.InsertRange(edit.First, new Priced?[edit.Count]);
            bool slide = edit.Count == edit.Remove;
            slid.RemoveRange(edit.First, edit.Remove);
            slid.InsertRange(edit.First, Enumerable.Repeat(slide, edit.Count));
            int from = Math.Max(0, edit.First - PriceReach);
            int to = Math.Min(cheapest.Count - 1, edit.First + edit.Count + PriceReach);
            for (int j = from; j <= to; j++)
                cheapest[j] = null;

            // Prices further on still hold, but their edits name vertices that have moved along.
            int shift = edit.Count - edit.Remove;
            for (int j = to + 1; j < cheapest.Count && shift != 0; j++)
            {
                if (cheapest[j] is Priced kept)
                    cheapest[j] = kept with { Edit = kept.Edit with { First = kept.Edit.First + shift } };
            }
        }

        // A slide or merge can leave a vertex with no turn at it between two corners that fill the
        // segments either side; it is no corner, and keeping it would leave a straight a few ulps long.
        for (int i = points.Count - 2; i >= 1; i--)
        {
            if (Math.Abs(PlanFillet.Deflection(points[i - 1], points[i], points[i + 1])) < StraightTolerance)
                points.RemoveAt(i);
        }

        return points;
    }

    /// <summary>
    /// The cheapest edit that gives segment <paramref name="segment"/> room within
    /// <paramref name="headroom"/>, as the change it makes to the rounded leg's cost; infinite when no
    /// edit is that cheap and stays on the terrain.
    /// </summary>
    private static Priced Cheapest(
        Pricer pricer,
        List<(double X, double Y)> points,
        Rounding current,
        List<bool> slid,
        int segment,
        double headroom,
        List<(double X, double Y)> candidate)
    {
        var best = new Priced(double.PositiveInfinity, default, headroom);
        foreach (Edit edit in Edits(points, segment, pricer.Radius))
        {
            // A vertex slides once: two neighbouring slides could otherwise trade room back and forth.
            if (edit.Count == edit.Remove && (slid[edit.First] || slid[edit.First + 1]))
                continue;

            // The corners from two before the edit to two after its new vertices change, and with them
            // the segments from the one ending at the first of those to the one starting at the last.
            Apply(points, edit, candidate);
            double delta = 0.0;
            for (int i = Math.Max(1, edit.First - 2); i <= Math.Min(points.Count - 2, edit.First + edit.Remove + 1); i++)
                delta -= current.Corner[i];
            for (int j = Math.Max(0, edit.First - 3); j <= Math.Min(points.Count - 2, edit.First + edit.Remove + 1); j++)
                delta -= current.Segment[j];

            double bound = Math.Min(best.Delta, headroom);
            if (pricer.TryLocal(candidate, edit.First - 3, edit.First + edit.Count + 2, bound - delta, out double added))
            {
                delta += added;
                if (delta <= bound && delta < best.Delta)
                    best = new Priced(delta, edit, headroom);
            }
        }

        return best;
    }

    /// <summary>True when segment <paramref name="j"/>'s two corners want more of it than it has.</summary>
    private static bool Overfilled(List<(double X, double Y)> points, int j, double radius)
    {
        double demand = (j >= 1 ? Tangent(points[j - 1], points[j], points[j + 1], radius) : 0.0) +
            (j + 2 < points.Count ? Tangent(points[j], points[j + 1], points[j + 2], radius) : 0.0);
        return demand > Distance(points[j], points[j + 1]) * (1.0 + 1e-9);
    }

    /// <summary>
    /// The ways of giving segment <paramref name="segment"/> room: for every run of up to
    /// <see cref="MaxWindow"/> consecutive corners that includes one of its two, drop the run (a chord
    /// replaces it) or, for a run of two or more, merge it into one corner where the segments either side
    /// meet (one arc across the run); and slide its two corners apart (<see cref="TrySlide"/>). The
    /// leg's ends are never touched.
    /// </summary>
    private static IEnumerable<Edit> Edits(List<(double X, double Y)> points, int segment, double radius)
    {
        int lastInterior = points.Count - 2;
        for (int size = 1; size <= MaxWindow; size++)
        {
            for (int first = Math.Max(1, segment - size + 1); first <= segment + 1; first++)
            {
                int last = first + size - 1;
                if (last > lastInterior || last < segment)
                    continue;

                if (size >= 2 && TryMeet(points[first - 1], points[first], points[last], points[last + 1], out (double X, double Y) meet))
                    yield return new Edit(first, size, 1, meet, default);
                yield return new Edit(first, size, 0, default, default);
            }
        }

        // Slide the segment's corners apart along the segments either side, as far as it takes to give
        // it room (a zig-zag becomes a longer, shallower crossing, a bend a longer middle straight), or
        // stretch the segment along its own line, which keeps the bearing it crosses the ground at.
        int a = segment, b = segment + 1;
        bool aInterior = a >= 1, bInterior = b <= lastInterior;
        foreach (bool alongSegment in SlideModes)
        {
            if (aInterior && bInterior && TrySlide(points, segment, radius, true, true, alongSegment, out Edit both))
                yield return both;
            if (aInterior && TrySlide(points, segment, radius, true, false, alongSegment, out Edit back))
                yield return back;
            if (bInterior && TrySlide(points, segment, radius, false, true, alongSegment, out Edit forward))
                yield return forward;
        }
    }

    /// <summary>
    /// The shortest slide that gives segment <paramref name="segment"/> room for its corners: its start
    /// moved back and/or its end moved forward by the same distance, at most <see cref="MaxSlideShare"/>
    /// of the segment either side. Along the neighbouring segments (the corners before and after keep
    /// their turn), or, with <paramref name="alongSegment"/>, along the segment's own line (it keeps its
    /// bearing).
    /// </summary>
    private static bool TrySlide(
        List<(double X, double Y)> points,
        int segment,
        double radius,
        bool moveStart,
        bool moveEnd,
        bool alongSegment,
        out Edit edit)
    {
        edit = default;
        int a = segment, b = segment + 1;
        (double X, double Y) pa = points[a], pb = points[b];
        double reach = double.PositiveInfinity;
        (double X, double Y) back = default, forward = default;
        if (moveStart)
        {
            reach = Math.Min(reach, MaxSlideShare * Distance(points[a - 1], pa));
            back = alongSegment ? Direction(pb, pa) : Direction(pa, points[a - 1]);
        }

        if (moveEnd)
        {
            reach = Math.Min(reach, MaxSlideShare * Distance(pb, points[b + 1]));
            forward = alongSegment ? Direction(pa, pb) : Direction(pb, points[b + 1]);
        }

        if (!(reach > 0.0) || !Fits(reach))
            return false;

        // Bisect for the least slide that fits: the line the search drew is kept as closely as it can be.
        double lo = 0.0, hi = reach;
        for (int k = 0; k < 40; k++)
        {
            double mid = 0.5 * (lo + hi);
            if (Fits(mid))
                hi = mid;
            else
                lo = mid;
        }

        (double X, double Y) na = At(pa, back, hi), nb = At(pb, forward, hi);
        edit = new Edit(a, 2, 2, na, nb);
        return true;

        bool Fits(double distance)
        {
            (double X, double Y) sa = At(pa, back, distance), sb = At(pb, forward, distance);
            double demand = (a >= 1 ? Tangent(points[a - 1], sa, sb, radius) : 0.0) +
                (b <= points.Count - 2 ? Tangent(sa, sb, points[b + 1], radius) : 0.0);
            return demand <= Distance(sa, sb);
        }

        static (double X, double Y) At((double X, double Y) p, (double X, double Y) direction, double distance) =>
            (p.X + (direction.X * distance), p.Y + (direction.Y * distance));
    }

    /// <summary>The tangent length the corner at <paramref name="b"/> needs for the radius; infinite for a reversal.</summary>
    private static double Tangent((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, double radius)
    {
        double turn = Math.Abs(PlanFillet.Deflection(a, b, c));
        return turn < StraightTolerance ? 0.0
            : turn > Math.PI - ReversalTolerance ? double.PositiveInfinity
            : radius * Math.Tan(0.5 * turn);
    }

    private static (double X, double Y) Direction((double X, double Y) from, (double X, double Y) to)
    {
        double length = Distance(from, to);
        return length > 0.0 ? ((to.X - from.X) / length, (to.Y - from.Y) / length) : (0.0, 0.0);
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(Geometry2D.DistanceSquared(a.X, a.Y, b.X, b.Y));

    /// <summary>
    /// Where the line through <paramref name="p0"/> and <paramref name="p1"/> continued forward meets the
    /// line through <paramref name="p2"/> and <paramref name="p3"/> continued back — the single corner
    /// that turns as far as the two at <paramref name="p1"/> and <paramref name="p2"/> together. False
    /// when they turn opposite ways, run parallel, or together turn back on themselves.
    /// </summary>
    private static bool TryMeet(
        (double X, double Y) p0,
        (double X, double Y) p1,
        (double X, double Y) p2,
        (double X, double Y) p3,
        out (double X, double Y) meet)
    {
        meet = default;
        double ux = p1.X - p0.X, uy = p1.Y - p0.Y;
        double vx = p3.X - p2.X, vy = p3.Y - p2.Y;
        double cross = (ux * vy) - (uy * vx);
        double dot = (ux * vx) + (uy * vy);
        if (Math.Abs(Math.Atan2(cross, dot)) > Math.PI - ReversalTolerance)
            return false;
        double scale = Math.Sqrt(((ux * ux) + (uy * uy)) * ((vx * vx) + (vy * vy)));
        if (!(Math.Abs(cross) > scale * 1e-9))
            return false;

        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        double s = ((dx * vy) - (dy * vx)) / cross;
        double t = ((ux * dy) - (uy * dx)) / cross;
        if (s < 0.0 || t < 0.0)
            return false;

        meet = (p1.X + (s * ux), p1.Y + (s * uy));
        return true;
    }

    private static void Apply(List<(double X, double Y)> points, Edit edit, List<(double X, double Y)> output)
    {
        output.Clear();
        for (int i = 0; i < edit.First; i++)
            output.Add(points[i]);
        if (edit.Count > 0)
            output.Add(edit.A);
        if (edit.Count > 1)
            output.Add(edit.B);
        for (int i = edit.First + edit.Remove; i < points.Count; i++)
            output.Add(points[i]);
    }

    /// <summary>A rounded plan's cost, corner by corner (arcs, 0 where none) and segment by segment (straights).</summary>
    private sealed record Rounding(double[] Corner, double[] Segment, double Total);

    /// <summary>
    /// Prices the plan as <see cref="PlanFillet"/> rounds it, over the sampled ground every half cell, by
    /// the search's pricing. Off the terrain (at the straightening's ground weight) a stretch is priced as
    /// level ground and flagged.
    /// </summary>
    private sealed class Pricer
    {
        private readonly RouteHeightGrid _grid;
        private readonly RouteCost _price;
        private readonly double _spacing;
        private readonly Dictionary<(double, double, double, double), Chord> _chords = new();
        private readonly Dictionary<PlanPiece, (double Cost, bool OnGround)> _arcs = new();

        public Pricer(RouteHeightGrid grid, RouteCost price, double radius)
        {
            _grid = grid;
            _price = price;
            Radius = radius;
            _spacing = RouteLegBuilder.StationSpacingCells * grid.CellSize;
        }

        public double Radius { get; }

        /// <summary>The whole plan, rounded and priced.</summary>
        public Rounding Round(List<(double X, double Y)> plan)
        {
            int n = plan.Count;
            double[] tangent = Tangents(plan, 0, n - 1, out double[] deflection);
            var corner = new double[n];
            var segment = new double[n - 1];
            double total = 0.0;
            for (int i = 1; i < n - 1; i++)
            {
                corner[i] = tangent[i] > 0.0 ? ArcCost(plan, i, tangent[i], deflection[i], out _) : 0.0;
                total += corner[i];
            }

            for (int j = 0; j < n - 1; j++)
            {
                segment[j] = Straight(plan, j, tangent[j], tangent[j + 1], out _);
                total += segment[j];
            }

            return new Rounding(corner, segment, total);
        }

        /// <summary>
        /// The cost of the corners and segments of <paramref name="plan"/> from vertex
        /// <paramref name="first"/> + 1 to <paramref name="last"/> - 1 (corners) and from segment
        /// <paramref name="first"/> to <paramref name="last"/> - 1, clamped to the plan. False when it
        /// leaves the terrain or passes <paramref name="ceiling"/>.
        /// </summary>
        public bool TryLocal(List<(double X, double Y)> plan, int first, int last, double ceiling, out double cost)
        {
            cost = 0.0;
            int n = plan.Count;
            first = Math.Max(0, first);
            last = Math.Min(n - 1, last);
            double[] tangent = Tangents(plan, first, last, out double[] deflection);
            for (int i = Math.Max(1, first + 1); i <= Math.Min(n - 2, last - 1); i++)
            {
                if (tangent[i - first] > 0.0)
                    cost += ArcCost(plan, i, tangent[i - first], deflection[i - first], out bool onGround) + (onGround ? 0.0 : double.PositiveInfinity);
                if (!(cost <= ceiling))
                    return false;
            }

            for (int j = first; j <= last - 1; j++)
            {
                cost += Straight(plan, j, tangent[j - first], tangent[j + 1 - first], out bool onGround) + (onGround ? 0.0 : double.PositiveInfinity);
                if (!(cost <= ceiling))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The tangent lengths <see cref="PlanFillet"/> gives the corners <paramref name="first"/> to
        /// <paramref name="last"/>, indexed from <paramref name="first"/>: each corner's wanted tangent,
        /// shrunk by the smaller share of its two segments (which depend on the corners either side).
        /// </summary>
        private double[] Tangents(List<(double X, double Y)> plan, int first, int last, out double[] deflection)
        {
            int n = plan.Count;
            int lo = Math.Max(0, first - 1), hi = Math.Min(n - 1, last + 1);
            var demand = new double[hi - lo + 1];
            var turn = new double[hi - lo + 1];
            for (int i = Math.Max(1, lo); i <= Math.Min(n - 2, hi); i++)
                demand[i - lo] = PlanFillet.Demand(plan[i - 1], plan[i], plan[i + 1], Radius, out turn[i - lo]);

            var tangent = new double[last - first + 1];
            deflection = new double[last - first + 1];
            for (int i = Math.Max(1, first); i <= Math.Min(n - 2, last); i++)
            {
                double wanted = demand[i - lo];
                deflection[i - first] = turn[i - lo];
                if (wanted <= 0.0)
                    continue;

                double before = PlanFillet.Scale(demand[i - 1 - lo] + wanted, Distance(plan[i - 1], plan[i]));
                double after = PlanFillet.Scale(wanted + demand[i + 1 - lo], Distance(plan[i], plan[i + 1]));
                tangent[i - first] = wanted * Math.Min(before, after);
            }

            return tangent;
        }

        /// <summary>The straight left on segment <paramref name="j"/> between tangent points at the given distances from its ends.</summary>
        public double Straight(List<(double X, double Y)> plan, int j, double fromStart, double fromEnd, out bool onGround)
        {
            onGround = true;
            (double X, double Y) a = plan[j], b = plan[j + 1];
            double length = Distance(a, b);
            if (!PlanFillet.IsStraight(length - fromStart - fromEnd, length))
                return 0.0;

            var key = (a.X, a.Y, b.X, b.Y);
            if (!_chords.TryGetValue(key, out Chord? chord))
            {
                chord = new Chord(_grid, _price, a, b, _spacing);
                _chords.Add(key, chord);
            }

            return chord.Between(fromStart, length - fromEnd, out onGround);
        }

        private double ArcCost(List<(double X, double Y)> plan, int i, double tangent, double deflection, out bool onGround)
        {
            PlanPiece arc = PlanFillet.Arc(plan[i - 1], plan[i], plan[i + 1], tangent, deflection);
            if (!_arcs.TryGetValue(arc, out (double Cost, bool OnGround) priced))
            {
                priced = (SampledCost(arc, out bool on), on);
                _arcs.Add(arc, priced);
            }

            onGround = priced.OnGround;
            return priced.Cost;
        }

        private double SampledCost(PlanPiece piece, out bool onGround)
        {
            onGround = true;
            double length = piece.Length;
            int count = Math.Max(1, (int)Math.Ceiling(length / _spacing));
            double step = length / count;
            if (_grid.TrySample(piece.StartX, piece.StartY, RouteLegBuilder.ChordGroundWeight, out double previous))
            {
                double total = 0.0;
                for (int k = 1; k <= count; k++)
                {
                    (double x, double y) = piece.PointAt((double)k / count);
                    if (!_grid.TrySample(x, y, RouteLegBuilder.ChordGroundWeight, out double z))
                        break;

                    total += step * _price.Factor(Math.Abs(z - previous) / step);
                    previous = z;
                    if (k == count)
                        return total;
                }
            }

            onGround = false;
            return length * _price.Factor(0.0);
        }
    }

    /// <summary>
    /// A plan segment sampled at even stations: the running cost from its start, and the running count
    /// of off-terrain intervals, so any stretch of it is priced in constant time.
    /// </summary>
    private sealed class Chord
    {
        private readonly double _step;
        private readonly double[] _cost;
        private readonly int[] _off;

        public Chord(RouteHeightGrid grid, RouteCost price, (double X, double Y) from, (double X, double Y) to, double spacing)
        {
            double length = Distance(from, to);
            int count = Math.Max(1, (int)Math.Ceiling(length / spacing));
            _step = length / count;
            _cost = new double[count + 1];
            _off = new int[count + 1];
            bool previousOn = grid.TrySample(from.X, from.Y, RouteLegBuilder.ChordGroundWeight, out double previous);
            for (int k = 1; k <= count; k++)
            {
                double t = (double)k / count;
                bool on = grid.TrySample(
                    Geometry2D.Lerp(from.X, to.X, t), Geometry2D.Lerp(from.Y, to.Y, t), RouteLegBuilder.ChordGroundWeight, out double z);
                bool both = previousOn && on && _step > 0.0;
                _cost[k] = _cost[k - 1] + (_step * price.Factor(both ? Math.Abs(z - previous) / _step : 0.0));
                _off[k] = _off[k - 1] + (both || _step <= 0.0 ? 0 : 1);
                previousOn = on;
                previous = z;
            }
        }

        /// <summary>The cost from station <paramref name="a"/> to station <paramref name="b"/> along the segment.</summary>
        public double Between(double a, double b, out bool onGround)
        {
            if (b < a)
                (a, b) = (b, a);
            if (!(_step > 0.0))
            {
                onGround = _off[^1] == 0;
                return 0.0;
            }

            int last = _cost.Length - 1;
            int first = Math.Clamp((int)Math.Floor(a / _step), 0, last);
            int end = Math.Clamp((int)Math.Ceiling(b / _step), 0, last);
            onGround = _off[end] == _off[first];
            return At(b) - At(a);
        }

        private double At(double station)
        {
            double k = Math.Clamp(station / _step, 0.0, _cost.Length - 1);
            int below = Math.Min((int)Math.Floor(k), _cost.Length - 2);
            return Geometry2D.Lerp(_cost[below], _cost[below + 1], k - below);
        }
    }

    /// <summary>Replace <see cref="Remove"/> vertices from <see cref="First"/> with the first <see cref="Count"/> of <see cref="A"/> and <see cref="B"/>.</summary>
    private readonly record struct Edit(int First, int Remove, int Count, (double X, double Y) A, (double X, double Y) B);

    /// <summary>
    /// An edit and how much it changes the rounded leg's cost, found with <see cref="Headroom"/> left in
    /// the budget; infinite when no edit within that headroom stays on the terrain.
    /// </summary>
    private readonly record struct Priced(double Delta, Edit Edit, double Headroom);
}
