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
/// two ends never move.
/// </remarks>
internal static class PlanFillet
{
    private const double StraightTolerance = 1e-6;
    private const double ReversalTolerance = 1e-3;

    public static List<PlanPiece> Fillet(
        IReadOnlyList<(double X, double Y)> plan,
        double minimumRadius,
        out double? smallestRadius,
        out int reducedCorners)
    {
        smallestRadius = null;
        reducedCorners = 0;
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
                if (segmentLength[i - 1] <= 0.0 || segmentLength[i] <= 0.0)
                    continue;

                (double ux, double uy) = Direction(plan[i - 1], plan[i]);
                (double vx, double vy) = Direction(plan[i], plan[i + 1]);
                double theta = Math.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
                deflection[i] = theta;
                double magnitude = Math.Abs(theta);
                if (magnitude < StraightTolerance)
                    continue;
                if (magnitude > Math.PI - ReversalTolerance)
                {
                    reducedCorners++;
                    smallestRadius = 0.0;
                    continue;
                }

                tangent[i] = minimumRadius * Math.Tan(0.5 * magnitude);
            }

            // Each segment can give its two corners no more than its own length between them.
            var scale = new double[n - 1];
            for (int j = 0; j < n - 1; j++)
            {
                double demand = tangent[j] + tangent[j + 1];
                scale[j] = demand > segmentLength[j] && demand > 0.0 ? segmentLength[j] / demand : 1.0;
            }

            for (int i = 1; i < n - 1; i++)
            {
                if (tangent[i] <= 0.0)
                    continue;

                tangent[i] *= Math.Min(scale[i - 1], scale[i]);
                double radius = tangent[i] / Math.Tan(0.5 * Math.Abs(deflection[i]));
                if (radius < minimumRadius * (1.0 - 1e-9))
                    reducedCorners++;
                smallestRadius = smallestRadius is double known ? Math.Min(known, radius) : radius;
            }
        }

        (double X, double Y) current = plan[0];
        for (int i = 1; i < n - 1; i++)
        {
            if (tangent[i] <= 0.0)
            {
                AddLine(pieces, current, plan[i]);
                current = plan[i];
                continue;
            }

            (double ux, double uy) = Direction(plan[i - 1], plan[i]);
            (double vx, double vy) = Direction(plan[i], plan[i + 1]);
            double t = tangent[i];
            var a = (X: plan[i].X - (ux * t), Y: plan[i].Y - (uy * t));
            var b = (X: plan[i].X + (vx * t), Y: plan[i].Y + (vy * t));
            double theta = deflection[i];
            double radius = t / Math.Tan(0.5 * Math.Abs(theta));
            double side = Math.Sign(theta);
            // The centre lies to the left of travel for a left (counter-clockwise) turn.
            double cx = a.X - (uy * radius * side);
            double cy = a.Y + (ux * radius * side);

            AddLine(pieces, current, a);
            pieces.Add(new PlanPiece(
                true, a.X, a.Y, b.X, b.Y, cx, cy, radius, Math.Atan2(a.Y - cy, a.X - cx), theta));
            current = b;
        }

        AddLine(pieces, current, plan[n - 1]);
        return pieces;
    }

    private static void AddLine(List<PlanPiece> pieces, (double X, double Y) a, (double X, double Y) b)
    {
        if (Distance(a, b) > 1e-12)
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
