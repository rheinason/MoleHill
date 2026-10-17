using MoleHill.Core.Geometry;

namespace MoleHill.Core.Routing;

/// <summary>
/// Turns one leg's grid path into exact 3D spans: the plan straightened and, when asked, its corners
/// rounded to a minimum radius; the profile fitted to the ground along that plan within the grade
/// limit; and the profile thinned to the points that carry it.
/// </summary>
internal static class RouteLegBuilder
{
    /// <summary>Longest stretch of grid path one straightened chord may replace, which bounds the work per vertex.</summary>
    private const int MaxChordNodes = 400;

    /// <summary>
    /// Share of a chord sample's bilinear weight that must be ground. Edges between two ground nodes
    /// read 1; at 0.75 a chord may not cut more than a sliver off a cell with an off-terrain corner.
    /// </summary>
    internal const double ChordGroundWeight = 0.75;

    /// <summary>How many rejected chords in a row end the search for a longer one.</summary>
    private const int MaxChordMisses = 16;

    /// <summary>Ground sample spacing along the leg, in cells.</summary>
    internal const double StationSpacingCells = 0.5;

    /// <summary>
    /// A leg's grid path as plan points. The path runs between grid nodes; the leg itself starts and
    /// ends exactly at the picked points.
    /// </summary>
    public static List<(double X, double Y)> PlanFromNodes(
        RouteHeightGrid grid, List<int> nodes, (double X, double Y) start, (double X, double Y) end)
    {
        var plan = new List<(double X, double Y)>(nodes.Count + 1);
        foreach (int node in nodes)
            plan.Add((grid.NodeX(node % grid.Columns), grid.NodeY(node / grid.Columns)));
        if (plan.Count == 1)
            plan.Add(end);
        plan[0] = start;
        plan[^1] = end;
        return plan;
    }

    /// <summary>Widest arc one span may carry. A rational quadratic span's elevation runs between its ends
    /// almost, not exactly, in step with the angle; over 15 degrees the departure is a fraction of a
    /// percent of the grade, and the build measures and corrects what is left.</summary>
    private const double MaxArcSpan = Math.PI / 12.0;

    /// <summary>Angular step at which an arc span is sampled to measure its grade and draw its preview.</summary>
    private const double ArcSampleStep = Math.PI / 180.0;

    /// <summary>How many times finer than <see cref="ArcSampleStep"/> an arc's grade is measured.</summary>
    private const int ArcMeasureRefinement = 16;

    /// <summary>
    /// Builds one leg. <paramref name="fillet"/> rounds the plan corners to the request's minimum radius
    /// (a no-op at radius 0); the profile is then fitted along the rounded plan, so a fillet changes the
    /// elevations it carries, and the result is measured against the limit as built.
    /// </summary>
    public static bool TryBuild(
        RouteHeightGrid grid,
        RouteRequest request,
        List<(double X, double Y)> plan,
        double startZ,
        double endZ,
        bool fillet,
        double profileTolerance,
        out List<RouteSpan> spans,
        out RouteFilletSummary filletSummary,
        out double requiredGrade,
        double preferredRadius = 0.0)
    {
        // Bends are rounded towards the preferred radius when one is given (the minimum widened by
        // Simplicity) and only count as tight under the minimum: a corner with room for less than the
        // preferred radius takes what fits.
        double radius = preferredRadius > request.MinimumRadius ? preferredRadius : request.MinimumRadius;
        List<PlanPiece> pieces = PlanFillet.Fillet(
            plan, fillet ? radius : 0.0, out double? smallest, out int reduced, out int reversals,
            fillet ? request.MinimumRadius : 0.0);
        filletSummary = new RouteFilletSummary(smallest, reduced, reversals);
        spans = new List<RouteSpan>();
        requiredGrade = 0.0;
        if (pieces.Count == 0)
            return true; // Both ends on the same spot: the leg is just its start.

        // Stations every half cell along every piece; each arc is also cut into spans of at most
        // MaxArcSpan, whose ends must survive thinning.
        double spacing = StationSpacingCells * grid.CellSize;
        var stations = new List<double> { 0.0 };
        var xs = new List<double> { pieces[0].StartX };
        var ys = new List<double> { pieces[0].StartY };
        var pieceOf = new List<int> { 0 };
        var fraction = new List<double> { 0.0 };
        var forced = new List<int> { 0 };
        double station = 0.0;
        for (int p = 0; p < pieces.Count; p++)
        {
            PlanPiece piece = pieces[p];
            double length = piece.Length;
            int count = Math.Max(1, (int)Math.Ceiling(length / spacing));
            int arcSpans = piece.IsArc ? Math.Max(1, (int)Math.Ceiling(Math.Abs(piece.Sweep) / MaxArcSpan)) : 1;
            count = arcSpans * (int)Math.Ceiling((double)count / arcSpans);
            int forcedEvery = count / arcSpans;
            for (int k = 1; k <= count; k++)
            {
                double f = (double)k / count;
                (double x, double y) = piece.PointAt(f);
                stations.Add(station + (length * f));
                xs.Add(x);
                ys.Add(y);
                pieceOf.Add(p);
                fraction.Add(f);
                if (k % forcedEvery == 0)
                    forced.Add(stations.Count - 1);
            }

            station += length;
        }

        var ground = new double[stations.Count];
        double last = startZ;
        for (int i = 0; i < stations.Count; i++)
        {
            if (grid.TrySample(xs[i], ys[i], out double z))
                last = z;
            ground[i] = last;
        }

        double limit = request.MaxGrade;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (!GradeLimitedProfile.TryFit(stations, ground, limit, startZ, endZ, out double[] profile, out requiredGrade))
                return false;

            // Thin within each span only, so every plan corner and arc span end keeps its own elevation.
            var keep = new bool[stations.Count];
            foreach (int index in forced)
                keep[index] = true;
            for (int c = 1; c < forced.Count; c++)
                MarkProfileBreaks(stations, profile, forced[c - 1], forced[c], profileTolerance, keep);

            spans.Clear();
            int previous = 0;
            for (int i = 1; i < stations.Count; i++)
            {
                if (!keep[i])
                    continue;

                PlanPiece piece = pieces[pieceOf[i]];
                double fromFraction = pieceOf[previous] == pieceOf[i] ? fraction[previous] : 0.0;
                spans.Add(piece.IsArc
                    ? ArcSpan(piece, fromFraction, fraction[i], profile[previous], profile[i])
                    : RouteSpan.Line(xs[previous], ys[previous], profile[previous], xs[i], ys[i], profile[i]));
                previous = i;
            }

            double built = MeasureMaxGrade(spans);
            if (built <= (request.MaxGrade * (1.0 + 1e-9)) + 1e-12)
                return true;

            // An arc span's elevation strayed from the fitted profile by a hair: fit to a slightly
            // tighter limit so the curve as drawn obeys the real one.
            limit *= request.MaxGrade / built * (1.0 - 1e-4);
        }

        return false;
    }

    /// <summary>
    /// The steepest grade along the spans as built. A straight's grade is its own; an arc is measured
    /// over chords of a sixteenth of a degree, fine enough that the steepest chord is the steepest the
    /// curve gets to within a part in ten million (the drawn preview samples every degree, and a chord
    /// that long can read a hair gentler than the curve inside it).
    /// </summary>
    internal static double MeasureMaxGrade(IReadOnlyList<RouteSpan> spans)
    {
        double worst = 0.0;
        foreach (RouteSpan span in spans)
        {
            int samples = 1;
            if (span.IsArc)
            {
                double sweep = 2.0 * Math.Acos(Math.Clamp(span.Weight, -1.0, 1.0));
                samples = Math.Max(4, (int)Math.Ceiling(sweep / (ArcSampleStep / ArcMeasureRefinement)));
            }

            (double X, double Y, double Z) previous = (span.StartX, span.StartY, span.StartZ);
            for (int k = 1; k <= samples; k++)
            {
                (double X, double Y, double Z) point = span.IsArc
                    ? span.Evaluate((double)k / samples)
                    : (span.EndX, span.EndY, span.EndZ);
                double plan = Math.Sqrt(Geometry2D.DistanceSquared(previous.X, previous.Y, point.X, point.Y));
                if (plan > 0.0)
                    worst = Math.Max(worst, Math.Abs(point.Z - previous.Z) / plan);
                previous = point;
            }
        }

        return worst;
    }

    /// <summary>
    /// Appends the spans as XYZ points: a straight by its end, an arc sampled every degree. Starts with
    /// the first span's start when <paramref name="output"/> is empty.
    /// </summary>
    internal static void AppendDense(IReadOnlyList<RouteSpan> spans, List<double> output)
    {
        foreach (RouteSpan span in spans)
        {
            if (output.Count == 0)
            {
                output.Add(span.StartX);
                output.Add(span.StartY);
                output.Add(span.StartZ);
            }

            if (!span.IsArc)
            {
                output.Add(span.EndX);
                output.Add(span.EndY);
                output.Add(span.EndZ);
                continue;
            }

            double sweep = 2.0 * Math.Acos(Math.Clamp(span.Weight, -1.0, 1.0));
            int samples = Math.Max(4, (int)Math.Ceiling(sweep / ArcSampleStep));
            for (int k = 1; k <= samples; k++)
            {
                (double x, double y, double z) = span.Evaluate((double)k / samples);
                output.Add(x);
                output.Add(y);
                output.Add(z);
            }
        }
    }

    /// <summary>
    /// The rational quadratic span for the stretch of <paramref name="piece"/> between two fractions: the
    /// control point where the end tangents meet, weighted cos(half the sweep), so the plan is exactly
    /// the arc; its elevation is the mean of the ends, which keeps the span symmetric about its middle.
    /// </summary>
    private static RouteSpan ArcSpan(PlanPiece piece, double fromFraction, double toFraction, double fromZ, double toZ)
    {
        double a0 = piece.StartAngle + (piece.Sweep * fromFraction);
        double a1 = piece.StartAngle + (piece.Sweep * toFraction);
        double half = 0.5 * (a1 - a0);
        double weight = Math.Cos(half);
        double reach = piece.Radius / weight;
        double mid = a0 + half;
        return new RouteSpan(
            true,
            piece.CenterX + (piece.Radius * Math.Cos(a0)),
            piece.CenterY + (piece.Radius * Math.Sin(a0)),
            fromZ,
            piece.CenterX + (reach * Math.Cos(mid)),
            piece.CenterY + (reach * Math.Sin(mid)),
            0.5 * (fromZ + toZ),
            piece.CenterX + (piece.Radius * Math.Cos(a1)),
            piece.CenterY + (piece.Radius * Math.Sin(a1)),
            toZ,
            weight);
    }

    /// <summary>
    /// Cost-aware string pulling: from each kept vertex, jump to the farthest later path vertex whose
    /// straight chord costs no more than the stretch of path it replaces, priced by the same
    /// <see cref="RouteCost"/> the search used and sampled every half cell. On even ground that is the
    /// straight line (a grid path is only ever an approximation of one); round a hill it is the tightest
    /// line the costs allow; and a chord over a hole is never taken, because off-terrain samples make it
    /// infinitely expensive.
    /// <para>With a <paramref name="minimumLength"/>, a chord is taken only while the plan stays at least
    /// that long. A leg that has to climb further than its straight line allows winds up the slope, and
    /// unconstrained pulling straightens the winding away — the leg then cannot meet the grade, and used to
    /// fall back to the raw grid staircase. Held to the length the climb needs, it keeps enough of its
    /// switchbacks and straightens the rest.</para>
    /// </summary>
    internal static List<(double X, double Y)> StraightenPlan(
        RouteHeightGrid grid,
        RouteCost price,
        List<(double X, double Y)> plan,
        double minimumLength = 0.0)
    {
        int n = plan.Count;
        if (n <= 2)
            return plan;

        double spacing = StationSpacingCells * grid.CellSize;
        var prefix = new double[n];
        for (int i = 1; i < n; i++)
        {
            double cost = ChordCost(grid, price, plan[i - 1], plan[i], spacing, 1e-9);
            if (double.IsPositiveInfinity(cost))
                cost = Math.Sqrt(Geometry2D.DistanceSquared(plan[i - 1].X, plan[i - 1].Y, plan[i].X, plan[i].Y)) * price.Factor(0.0);
            prefix[i] = prefix[i - 1] + cost;
        }

        var along = new double[n];
        for (int i = 1; i < n; i++)
            along[i] = along[i - 1] + Math.Sqrt(Geometry2D.DistanceSquared(plan[i - 1].X, plan[i - 1].Y, plan[i].X, plan[i].Y));
        double length = along[n - 1];

        var straightened = new List<(double X, double Y)> { plan[0] };
        int anchor = 0;
        while (anchor < n - 1)
        {
            int best = anchor + 1;
            int misses = 0;
            for (int j = anchor + 2; j < n && j - anchor <= MaxChordNodes; j++)
            {
                double chord = ChordCost(grid, price, plan[anchor], plan[j], spacing, ChordGroundWeight);
                double path = prefix[j] - prefix[anchor];
                double shortening = along[j] - along[anchor] -
                    Math.Sqrt(Geometry2D.DistanceSquared(plan[anchor].X, plan[anchor].Y, plan[j].X, plan[j].Y));
                if (chord <= (path * (1.0 + 1e-9)) + 1e-12 && length - shortening >= minimumLength)
                {
                    best = j;
                    misses = 0;
                }
                else if (++misses > MaxChordMisses)
                {
                    break;
                }
            }

            straightened.Add(plan[best]);
            length -= along[best] - along[anchor] -
                Math.Sqrt(Geometry2D.DistanceSquared(plan[anchor].X, plan[anchor].Y, plan[best].X, plan[best].Y));
            anchor = best;
        }

        return straightened;
    }

    /// <summary>A polyline's cost by the search's pricing, from its plan coordinates and the ground under them.</summary>
    internal static double PolylineCost(RouteHeightGrid grid, RouteCost price, double[] pointsXyz)
    {
        double spacing = StationSpacingCells * grid.CellSize;
        double total = 0.0;
        for (int i = 1; i < pointsXyz.Length / 3; i++)
        {
            (double X, double Y) a = (pointsXyz[(i - 1) * 3], pointsXyz[((i - 1) * 3) + 1]);
            (double X, double Y) b = (pointsXyz[i * 3], pointsXyz[(i * 3) + 1]);
            double cost = ChordCost(grid, price, a, b, spacing, 1e-9);
            total += double.IsPositiveInfinity(cost)
                ? Math.Sqrt(Geometry2D.DistanceSquared(a.X, a.Y, b.X, b.Y)) * price.Factor(0.0)
                : cost;
        }

        return total;
    }

    /// <summary>Cost of a straight chord over the sampled ground; infinite where it leaves the terrain.</summary>
    internal static double ChordCost(
        RouteHeightGrid grid,
        RouteCost price,
        (double X, double Y) a,
        (double X, double Y) b,
        double spacing,
        double minimumWeight)
    {
        double length = Math.Sqrt(Geometry2D.DistanceSquared(a.X, a.Y, b.X, b.Y));
        if (length <= 0.0)
            return 0.0;

        int pieces = Math.Max(1, (int)Math.Ceiling(length / spacing));
        double piece = length / pieces;
        if (!grid.TrySample(a.X, a.Y, minimumWeight, out double previous))
            return double.PositiveInfinity;

        double total = 0.0;
        for (int k = 1; k <= pieces; k++)
        {
            double t = (double)k / pieces;
            if (!grid.TrySample(Geometry2D.Lerp(a.X, b.X, t), Geometry2D.Lerp(a.Y, b.Y, t), minimumWeight, out double z))
                return double.PositiveInfinity;

            total += piece * price.Factor(Math.Abs(z - previous) / piece);
            previous = z;
        }

        return total;
    }

    /// <summary>Douglas-Peucker on (station, elevation) between two kept stations, vertical deviation only.</summary>
    private static void MarkProfileBreaks(
        List<double> stations,
        double[] profile,
        int first,
        int last,
        double tolerance,
        bool[] keep)
    {
        var spans = new Stack<(int First, int Last)>();
        spans.Push((first, last));
        while (spans.Count > 0)
        {
            (int a, int b) = spans.Pop();
            if (b - a < 2)
                continue;

            double run = stations[b] - stations[a];
            int farthest = -1;
            double deviation = -1.0;
            for (int i = a + 1; i < b; i++)
            {
                double t = run > 0.0 ? (stations[i] - stations[a]) / run : 0.0;
                double d = Math.Abs(profile[i] - Geometry2D.Lerp(profile[a], profile[b], t));
                if (d > deviation)
                {
                    deviation = d;
                    farthest = i;
                }
            }

            if (deviation <= tolerance)
                continue;

            keep[farthest] = true;
            spans.Push((a, farthest));
            spans.Push((farthest, b));
        }
    }
}
