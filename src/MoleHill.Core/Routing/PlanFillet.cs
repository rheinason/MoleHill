namespace MoleHill.Core.Routing;

/// <summary>
/// Rounds the corners of a plan polyline with tangent circular arcs of at least a minimum radius — the
/// horizontal curves of an alignment.
/// </summary>
/// <remarks>
/// A corner deflecting by θ with radius R needs a tangent length T = R tan(θ/2) along both of its
/// segments. Where two corners (or a corner and an end) want more of one segment than it has, both
/// tangents on that segment shrink in proportion until they meet, and each corner takes the smaller of
/// the two shrinkings its segments impose — so a corner on a short segment gets the largest radius that
/// still fits rather than none, and the result is always G1 (tangent-continuous). A corner that turns
/// back on itself (deflection near 180 degrees) cannot be rounded and is left as a corner. The polyline's
/// two ends never move. Corners under the radius are counted as the curve shows them: two that turn the
/// same way and fill the segment between them at one radius lie on one circle, and count once.
/// <see cref="PlanRadiusSimplifier"/> edits the plan first so that few corners need shrinking at all.
/// </remarks>
internal static class PlanFillet
{
    private const double StraightTolerance = 1e-6;
    private const double ReversalTolerance = 1e-3;

    /// <summary>Relative share of a segment below which what its corners leave of it is no straight at all.</summary>
    private const double FillTolerance = 1e-9;

    public static List<PlanPiece> Fillet(
        IReadOnlyList<(double X, double Y)> plan,
        double minimumRadius,
        out double? smallestRadius,
        out int reducedCorners) =>
        Fillet(plan, minimumRadius, out smallestRadius, out reducedCorners, out _);

    /// <summary>As <see cref="Fillet(IReadOnlyList{ValueTuple{double, double}}, double, out double?, out int)"/>,
    /// also counting the reduced corners that are near-reversals (left sharp).</summary>
    public static List<PlanPiece> Fillet(
        IReadOnlyList<(double X, double Y)> plan,
        double minimumRadius,
        out double? smallestRadius,
        out int reducedCorners,
        out int reversalCorners)
    {
        smallestRadius = null;
        reducedCorners = 0;
        reversalCorners = 0;
        int n = plan.Count;
        var pieces = new List<PlanPiece>();
        if (n < 2)
            return pieces;

        var segmentLength = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
            segmentLength[i] = Distance(plan[i], plan[i + 1]);

        var deflection = new double[n];
        var tangent = new double[n];
        if (minimumRadius > 0.0)
        {
            for (int i = 1; i < n - 1; i++)
            {
                tangent[i] = Demand(plan[i - 1], plan[i], plan[i + 1], minimumRadius, out deflection[i]);
                if (IsReversal(deflection[i]))
                {
                    reducedCorners++;
                    reversalCorners++;
                    smallestRadius = 0.0;
                }
            }

            // Each segment can give its two corners no more than its own length between them. A segment
            // they all but fill, they fill exactly: the tangent points then meet, rather than leaving a
            // straight a few ulps long whose direction is noise.
            var scale = new double[n - 1];
            for (int j = 0; j < n - 1; j++)
                scale[j] = Scale(tangent[j] + tangent[j + 1], segmentLength[j]);

            var radii = new double[n];
            for (int i = 1; i < n - 1; i++)
            {
                if (tangent[i] <= 0.0)
                    continue;

                tangent[i] *= Math.Min(scale[i - 1], scale[i]);
                double radius = tangent[i] / Math.Tan(0.5 * Math.Abs(deflection[i]));
                radii[i] = radius;
                smallestRadius = smallestRadius is double known ? Math.Min(known, radius) : radius;
                if (!(radius < minimumRadius * (1.0 - (2.0 * FillTolerance))))
                    continue;

                // Two corners turning the same way that share a filled segment at the same radius meet
                // tangentially on one circle: the drawn curve has one tight corner there, not two.
                bool sameArc = scale[i - 1] < 1.0 && radii[i - 1] > 0.0 &&
                    Math.Sign(deflection[i - 1]) == Math.Sign(deflection[i]) &&
                    Math.Abs(radii[i - 1] - radius) <= 1e-9 * radius;
                if (!sameArc)
                    reducedCorners++;
            }
        }

        (double X, double Y) current = plan[0];
        for (int i = 1; i < n - 1; i++)
        {
            if (tangent[i] <= 0.0)
            {
                AddLine(pieces, current, plan[i], segmentLength[i - 1]);
                current = plan[i];
                continue;
            }

            PlanPiece arc = Arc(plan[i - 1], plan[i], plan[i + 1], tangent[i], deflection[i]);
            AddLine(pieces, current, (arc.StartX, arc.StartY), segmentLength[i - 1]);
            pieces.Add(arc);
            current = (arc.EndX, arc.EndY);
        }

        AddLine(pieces, current, plan[n - 1], segmentLength[n - 2]);
        return pieces;
    }

    /// <summary>
    /// The tangent length the corner at <paramref name="b"/> wants for <paramref name="radius"/>,
    /// <c>R tan(θ/2)</c>, with its signed deflection θ; 0 where it does not turn, has a segment of no
    /// length, or turns back on itself (see <see cref="IsReversal"/>).
    /// </summary>
    internal static double Demand(
        (double X, double Y) a, (double X, double Y) b, (double X, double Y) c, double radius, out double deflection)
    {
        deflection = 0.0;
        if (!(radius > 0.0) || Distance(a, b) <= 0.0 || Distance(b, c) <= 0.0)
            return 0.0;

        deflection = Deflection(a, b, c);
        double magnitude = Math.Abs(deflection);
        return magnitude < StraightTolerance || IsReversal(deflection) ? 0.0 : radius * Math.Tan(0.5 * magnitude);
    }

    /// <summary>A turn so near a reversal that it cannot be rounded and stays a corner.</summary>
    internal static bool IsReversal(double deflection) => Math.Abs(deflection) > Math.PI - ReversalTolerance;

    /// <summary>
    /// The share of their wanted tangents two corners can have of a segment of <paramref name="length"/>:
    /// 1 when it is long enough, and exactly what fits when it is not or all but is — the tangent points
    /// then meet, rather than leaving a straight a few ulps long whose direction is noise.
    /// </summary>
    internal static double Scale(double demand, double length) =>
        demand > length * (1.0 - FillTolerance) && demand > 0.0 ? length / demand : 1.0;

    /// <summary>The tangent arc at corner <paramref name="b"/> whose tangent points lie <paramref name="tangent"/> either side of it.</summary>
    internal static PlanPiece Arc(
        (double X, double Y) a, (double X, double Y) b, (double X, double Y) c, double tangent, double deflection)
    {
        (double ux, double uy) = Direction(a, b);
        (double vx, double vy) = Direction(b, c);
        var start = (X: b.X - (ux * tangent), Y: b.Y - (uy * tangent));
        var end = (X: b.X + (vx * tangent), Y: b.Y + (vy * tangent));
        double radius = tangent / Math.Tan(0.5 * Math.Abs(deflection));
        double side = Math.Sign(deflection);
        // The centre lies to the left of travel for a left (counter-clockwise) turn.
        double cx = start.X - (uy * radius * side);
        double cy = start.Y + (ux * radius * side);
        return new PlanPiece(
            true, start.X, start.Y, end.X, end.Y, cx, cy, radius, Math.Atan2(start.Y - cy, start.X - cx), deflection);
    }

    /// <summary>
    /// Whether a straight of <paramref name="length"/> left on a segment of <paramref name="segmentLength"/>
    /// is drawn: corners that fill a segment to within <see cref="FillTolerance"/> leave only a sliver
    /// whose direction is rounding noise.
    /// </summary>
    internal static bool IsStraight(double length, double segmentLength) =>
        length > Math.Max(1e-12, FillTolerance * segmentLength);

    /// <summary>The corners of a plan polyline: interior vertices where its direction changes.</summary>
    public static int CountCorners(IReadOnlyList<(double X, double Y)> plan)
    {
        int corners = 0;
        for (int i = 1; i < plan.Count - 1; i++)
        {
            if (Math.Abs(Deflection(plan[i - 1], plan[i], plan[i + 1])) >= StraightTolerance)
                corners++;
        }

        return corners;
    }

    /// <summary>The signed turn at <paramref name="b"/> (counter-clockwise positive); 0 where a segment has no length.</summary>
    internal static double Deflection((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        (double ux, double uy) = Direction(a, b);
        (double vx, double vy) = Direction(b, c);
        return Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
    }

    /// <summary>
    /// Adds the straight from <paramref name="a"/> to <paramref name="b"/> on a segment of
    /// <paramref name="segmentLength"/>, unless it is no straight at all (see <see cref="IsStraight"/>).
    /// </summary>
    private static void AddLine(List<PlanPiece> pieces, (double X, double Y) a, (double X, double Y) b, double segmentLength)
    {
        if (IsStraight(Distance(a, b), segmentLength))
            pieces.Add(PlanPiece.Line(a.X, a.Y, b.X, b.Y));
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static (double X, double Y) Direction((double X, double Y) a, (double X, double Y) b)
    {
        double length = Distance(a, b);
        return length > 0.0 ? ((b.X - a.X) / length, (b.Y - a.Y) / length) : (0.0, 0.0);
    }
}
