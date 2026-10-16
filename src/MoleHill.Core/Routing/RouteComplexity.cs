// Scores how complicated a route is to read and to build: how much its curvature and its grade change.
namespace MoleHill.Core.Routing;

/// <summary>
/// How complicated a route is, as two counts that add up.
/// <para><see cref="Plan"/> is the total change in plan curvature times the reference radius: a straight
/// that bends at the reference radius and straightens again counts 2 (in and out), a long gentle curve
/// little, and a line that keeps switching between straights and tight arcs a lot. A sharp corner counts
/// as a bend at the reference radius.</para>
/// <para><see cref="Profile"/> is the total change in grade over the grade limit: one swing from level to
/// the limit counts 1.</para>
/// <para>Control points were the first idea (a hand-drawn road had 11, suggested routes about 100), but
/// they count how a curve is built — arcs split for the profile, a point at every grade break — and
/// counting the straight pieces that trace a line scores a smooth curve as the most complicated of all.
/// What made the hand-drawn line read as simple was that its curvature and its grade changed gently and
/// rarely; this measures exactly that, the same way for any route (2026-10-16).</para>
/// </summary>
public readonly record struct RouteComplexity(double Plan, double Profile)
{
    public double Total => Plan + Profile;

    /// <summary>Grade differences below this are the noise of arc spans, not grade breaks.</summary>
    private const double GradeNoise = 1e-4;

    /// <summary>Scores built spans. <paramref name="referenceRadius"/> is the radius a bend counts 2 at.</summary>
    public static RouteComplexity Measure(IReadOnlyList<RouteSpan> spans, double referenceRadius, double maxGrade)
    {
        if (spans.Count == 0 || !(referenceRadius > 0.0) || !(maxGrade > 0.0))
            return default;

        double curvatureChange = 0.0;
        double gradeChange = 0.0;
        double previousCurvature = 0.0;
        double? previousGrade = null;
        (double X, double Y)? previousDirection = null;
        foreach (RouteSpan span in spans)
        {
            double curvature = Curvature(span, out double planLength);
            if (!(planLength > 0.0))
                continue;

            // Two straights meeting at an angle: a corner with no curve, counted as a bend at the reference radius.
            (double X, double Y) startDirection = StartDirection(span);
            if (!span.IsArc && previousCurvature == 0.0 && previousDirection is { } before &&
                (before.X * startDirection.X) + (before.Y * startDirection.Y) < 1.0 - 1e-9)
            {
                curvatureChange += 2.0 / referenceRadius;
            }

            curvatureChange += Math.Abs(curvature - previousCurvature);
            previousCurvature = curvature;
            previousDirection = EndDirection(span);

            double grade = (span.EndZ - span.StartZ) / planLength;
            if (previousGrade is double last && Math.Abs(grade - last) > GradeNoise)
                gradeChange += Math.Abs(grade - last);
            previousGrade = grade;
        }

        curvatureChange += Math.Abs(previousCurvature); // straightening out at the end
        return new RouteComplexity(curvatureChange * referenceRadius, gradeChange / maxGrade);
    }

    /// <summary>Signed plan curvature of a span (0 for a straight; positive turning left) and its plan length.</summary>
    private static double Curvature(RouteSpan span, out double planLength)
    {
        double chord = Math.Sqrt(Square(span.EndX - span.StartX) + Square(span.EndY - span.StartY));
        if (!span.IsArc)
        {
            planLength = chord;
            return 0.0;
        }

        double sweep = 2.0 * Math.Acos(Math.Clamp(span.Weight, -1.0, 1.0));
        double sine = Math.Sin(0.5 * sweep);
        if (!(sine > 1e-12) || !(chord > 0.0))
        {
            planLength = chord;
            return 0.0;
        }

        double radius = chord / (2.0 * sine);
        planLength = radius * sweep;
        double cross = ((span.ControlX - span.StartX) * (span.EndY - span.ControlY)) -
            ((span.ControlY - span.StartY) * (span.EndX - span.ControlX));
        return Math.Sign(cross) / radius;
    }

    private static (double X, double Y) StartDirection(RouteSpan span) => span.IsArc
        ? Unit(span.ControlX - span.StartX, span.ControlY - span.StartY)
        : Unit(span.EndX - span.StartX, span.EndY - span.StartY);

    private static (double X, double Y) EndDirection(RouteSpan span) => span.IsArc
        ? Unit(span.EndX - span.ControlX, span.EndY - span.ControlY)
        : Unit(span.EndX - span.StartX, span.EndY - span.StartY);

    private static (double X, double Y) Unit(double x, double y)
    {
        double length = Math.Sqrt((x * x) + (y * y));
        return length > 0.0 ? (x / length, y / length) : (0.0, 0.0);
    }

    private static double Square(double value) => value * value;
}
