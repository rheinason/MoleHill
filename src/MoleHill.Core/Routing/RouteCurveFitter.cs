using MoleHill.Core.Geometry;

namespace MoleHill.Core.Routing;

/// <summary>
/// Turns a route's dense line into the fewest control points that describe it: cubic B-splines fitted
/// within a plan and a vertical tolerance, whose grade still obeys the limit.
/// </summary>
/// <remarks>
/// <para>A route built from exact arcs and a thinned profile has a vertex at every 15 degrees of arc and
/// at every grade break, about a hundred in all, where a hand-drawn road of the same shape has a dozen.
/// Asked for after a user found the suggested routes hard to edit (2026-10-17).</para>
/// <para><b>Pieces.</b> Each leg (waypoint to waypoint) is cut at corners sharper than
/// <see cref="KinkAngleDegrees"/> (a corner left sharp, which a cubic cannot turn), and each piece is
/// fitted on its own, its ends held exactly, so waypoints and sharp corners stay where they were.</para>
/// <para><b>Fit.</b> A smoothing spline: least squares to samples of the line at most
/// <see cref="SampleSpacingTolerances"/> tolerances apart, plus a bending penalty raised as far as the
/// tolerances allow, parametrised by chord length and corrected towards each sample's nearest point.
/// Plain least squares rang where curvature jumps (a 20 m arc read 4 m beside its straights, worse the
/// more knots it had); the smoothest curve within tolerance ramps its curvature like a transition curve
/// instead. It starts from a single Bézier and adds a knot in every span outside tolerance, where it is
/// worst, until none is; then drops each knot in turn and keeps every drop that stays within.</para>
/// <para><b>Grade.</b> Enforced by construction, not by more knots (which tripled the count and still
/// missed): the curve's grade is at most its Z derivative's B-spline coefficients over its plan speed, so
/// the Z control points are fitted with <see cref="GradeLimitedProfile"/> to steps no larger than the plan
/// speed under each allows. The radius: knots are added where the curve turns more than
/// <see cref="RadiusSlack"/> tighter than the minimum (or than the line itself, where that was already
/// tighter). Where an arc meets a straight a smooth curve has to ramp its curvature, and held within
/// tolerance it overshoots; holding it to 5% cost two and a half times the control points on a plain bend,
/// so 10% is allowed and the readout reports the radius as drawn.</para>
/// <para>Deterministic: no randomness and no hash order.</para>
/// </remarks>
public static class RouteCurveFitter
{
    public const int Degree = 3;

    /// <summary>Grade slack, relative, for a line that is already over the limit.</summary>
    internal const double OverLimitSlack = 0.03;

    /// <summary>A corner deflecting more than this stays a corner.</summary>
    public const double KinkAngleDegrees = 20.0;

    /// <summary>Samples per span below which a span is not split again.</summary>
    private const int MinimumSamplesPerSpan = 6;

    /// <summary>The line is sampled at most this many plan tolerances apart.</summary>
    private const double SampleSpacingTolerances = 2.0;

    /// <summary>The most samples one piece is fitted to.</summary>
    private const int MaximumSamples = 2000;

    /// <summary>The fewest samples one piece is fitted to.</summary>
    private const int MinimumSamples = 40;

    /// <summary>Points measured along each knot span for grade, radius and plan speed.</summary>
    private const int ChecksPerSpan = 48;

    private const int MaximumRefinements = 60;

    private const int CorrectionRounds = 3;

    /// <summary>A plan radius this many times a piece's length is read as straight, not as its tightest bend.</summary>
    private const double StraightRadiusLengths = 1000.0;

    /// <summary>
    /// Fits each leg's dense XYZ line. The grade is held when <paramref name="maxGrade"/> is positive;
    /// complexity is scored at
    /// <paramref name="referenceRadius"/> (see <see cref="RouteComplexity"/>), and left at zero without one.
    /// </summary>
    public static FittedRouteCurve Fit(
        IReadOnlyList<IReadOnlyList<double>> legsXyz,
        double planTolerance,
        double verticalTolerance,
        double maxGrade,
        double minimumRadius,
        double referenceRadius) => FitCore(legsXyz, planTolerance, verticalTolerance, maxGrade,
            minimumRadius, referenceRadius, KinkAngleDegrees, default);

    /// <summary>Scan boundaries already distinguish supported corners from raster turns.</summary>
    internal static FittedRouteCurve FitContinuous(IReadOnlyList<double> xyz, double planTolerance,
        double verticalTolerance, CancellationToken token) =>
        FitCore(new[] { xyz }, planTolerance, verticalTolerance, 0, 0, 0, 160, token);

    private static FittedRouteCurve FitCore(IReadOnlyList<IReadOnlyList<double>> legsXyz,
        double planTolerance, double verticalTolerance, double maxGrade, double minimumRadius,
        double referenceRadius, double kinkAngle, CancellationToken token)
    {
        if (!(planTolerance > 0.0) || !(verticalTolerance > 0.0))
            throw new ArgumentOutOfRangeException(nameof(planTolerance), "Tolerances must be positive.");

        var limits = new Limits(planTolerance, verticalTolerance, maxGrade, minimumRadius);
        var pieces = new List<RouteSplineCurve>();
        double planDeviation = 0.0, verticalDeviation = 0.0;
        foreach (IReadOnlyList<double> leg in legsXyz)
        {
            foreach ((int first, int last) in SplitAtKinks(leg, kinkAngle))
            {
                token.ThrowIfCancellationRequested();
                Samples? samples = Samples.Create(leg, first, last, limits);
                if (samples == null)
                    continue;

                // A line already steeper than the limit (a route too short for its climb, graded at the grade
                // it needs) is held to its own grade, with 3% to spare: it breaks the limit anyway and says so,
                // and held to the limit, or to its own grade exactly, the fitter spent knots trying (79 control
                // points on a relaxed route live, 2026-10-17).
                double inputGrade = samples.MaxGrade;
                Limits pieceLimits = limits.MaxGrade > 0.0 && inputGrade > limits.MaxGrade * (1.0 + 1e-6)
                    ? limits with { MaxGrade = inputGrade * (1.0 + OverLimitSlack) }
                    : limits;
                double plan = 0, vertical = 0;
                RouteSplineCurve curve;
                if (last == first + 1)
                {
                    // A two-point piece is already a straight 3D segment. Its exact cubic avoids
                    // solving a smoothing system for every straight side of a scan area boundary.
                    var controls = new double[12];
                    for (int i = 0; i < 4; i++)
                        for (int axis = 0; axis < 3; axis++)
                            controls[i * 3 + axis] = i == 0 ? leg[first * 3 + axis] : i == 3 ? leg[last * 3 + axis] :
                                leg[first * 3 + axis] + (leg[last * 3 + axis] - leg[first * 3 + axis]) * (i / 3.0);
                    curve = new RouteSplineCurve(Degree, new double[] { 0, 0, 0, 0, 1, 1, 1, 1 }, controls);
                }
                else
                    curve = FitPiece(samples, pieceLimits, out plan, out vertical, token);
                pieces.Add(curve);
                planDeviation = Math.Max(planDeviation, plan);
                verticalDeviation = Math.Max(verticalDeviation, vertical);
            }
        }

        double[] points = SamplePieces(pieces);
        double grade = 0.0;
        double? radius = null;
        foreach (RouteSplineCurve piece in pieces)
        {
            grade = Math.Max(grade, MeasureMaxGrade(piece));
            if (MeasureSmallestRadius(piece) is double r)
                radius = radius is double best ? Math.Min(best, r) : r;
        }

        RouteComplexity complexity = referenceRadius > 0.0 && maxGrade > 0.0
            ? RouteComplexity.MeasureSampled(points, referenceRadius, maxGrade)
            : default;
        return new FittedRouteCurve(pieces, points, planDeviation, verticalDeviation, grade, radius, complexity);
    }

    /// <summary>Index ranges of a leg's vertices between its ends and every corner sharper than the kink angle.</summary>
    internal static List<(int First, int Last)> SplitAtKinks(IReadOnlyList<double> xyz, double angle = KinkAngleDegrees)
    {
        int count = xyz.Count / 3;
        var ranges = new List<(int, int)>();
        if (count < 2)
            return ranges;

        double cosine = Math.Cos(angle * Math.PI / 180.0);
        int first = 0;
        int previous = 0;
        for (int i = 1; i < count - 1; i++)
        {
            // Directions from the last distinct vertex, so a repeated point is not read as a corner.
            double ax = xyz[i * 3] - xyz[previous * 3], ay = xyz[(i * 3) + 1] - xyz[(previous * 3) + 1];
            int next = i + 1;
            while (next < count - 1 &&
                   Geometry2D.DistanceSquared(xyz[next * 3], xyz[(next * 3) + 1], xyz[i * 3], xyz[(i * 3) + 1]) <= 1e-24)
            {
                next++;
            }

            double bx = xyz[next * 3] - xyz[i * 3], by = xyz[(next * 3) + 1] - xyz[(i * 3) + 1];
            double la = Math.Sqrt((ax * ax) + (ay * ay)), lb = Math.Sqrt((bx * bx) + (by * by));
            if (la > 0.0)
                previous = i;
            if (!(la > 0.0) || !(lb > 0.0))
                continue;

            if (((ax * bx) + (ay * by)) / (la * lb) < cosine)
            {
                ranges.Add((first, i));
                first = i;
            }
        }

        ranges.Add((first, count - 1));
        return ranges;
    }

    private static RouteSplineCurve FitPiece(Samples samples, Limits limits, out double planDeviation, out double verticalDeviation, CancellationToken token = default)
    {
        var interior = new List<double>();
        Attempt best = Solve(samples, interior, limits);
        for (int round = 0; round < MaximumRefinements; round++)
        {
            token.ThrowIfCancellationRequested();
            List<(int Span, double Where)> broken = OutsideTolerance(best, samples, limits, interior);
            if (broken.Count == 0)
                break;

            var knots = Distinct(interior);
            bool inserted = false;
            foreach ((int span, double where) in broken)
            {
                double a = knots[span], b = knots[span + 1];
                if (samples.CountBetween(best.Parameters, a, b) < 2 * MinimumSamplesPerSpan)
                    continue;

                double width = b - a;
                interior.Add(Math.Clamp(where, a + (0.25 * width), b - (0.25 * width)));
                inserted = true;
            }

            if (!inserted)
                break;

            interior.Sort();
            best = Solve(samples, interior, limits);
        }

        // The fewest knots: drop each in turn and keep the drop if the curve stays within tolerance. Only worth
        // it once it is; a curve that could not get there is kept as refined.
        if (OutsideTolerance(best, samples, limits, interior).Count == 0)
        {
            for (int i = 0; i < interior.Count;)
            {
                var trial = new List<double>(interior);
                trial.RemoveAt(i);
                Attempt candidate = Solve(samples, trial, limits);
                if (OutsideTolerance(candidate, samples, limits, trial).Count == 0)
                {
                    interior = trial;
                    best = candidate;
                }
                else
                {
                    i++;
                }
            }
        }

        if (limits.MaxGrade > 0.0)
            best = HoldGrade(best, samples, limits);
        (planDeviation, verticalDeviation) = Deviations(best, samples);
        return best.Curve;
    }

    /// <summary>
    /// Refits the heights to the line's profile re-laid along the curve's own plan. Smoothing cuts the inside
    /// of every bend, so the curve is a little shorter than the line, and a profile built at the limit all the
    /// way (a switchback climb) no longer fits it: held to the line's heights, the grade had to give
    /// somewhere, and the correction moved heights by over a metre. Re-laid with
    /// <see cref="GradeLimitedProfile"/> at the curve's own stations, the shortfall is spread evenly and
    /// the heights stay within centimetres; where even that cannot join the ends, the least grade that does
    /// is used.
    /// </summary>
    private static RouteSplineCurve Regrade(Attempt attempt, Samples samples, Limits limits, double share, out bool joined)
    {
        RouteSplineCurve curve = attempt.Curve;
        double[] parameters = attempt.Parameters;
        var stations = new double[samples.Count];
        (double X, double Y, double Z) previous = curve.PointAt(parameters[0]);
        for (int k = 1; k < samples.Count; k++)
        {
            // Plan length along the curve between successive sample parameters, a few chords each.
            double a = parameters[k - 1], b = parameters[k];
            double travelled = 0.0;
            const int pieces = 4;
            for (int c = 1; c <= pieces; c++)
            {
                (double X, double Y, double Z) point = curve.PointAt(a + ((b - a) * c / pieces));
                travelled += Math.Sqrt(Geometry2D.DistanceSquared(previous.X, previous.Y, point.X, point.Y));
                previous = point;
            }

            stations[k] = stations[k - 1] + travelled;
        }

        double startZ = samples.Z[0], endZ = samples.Z[^1];
        joined = GradeLimitedProfile.TryFit(stations, samples.Z, limits.MaxGrade * share, startZ, endZ, out double[] profile, out double required);
        if (!joined &&
            !GradeLimitedProfile.TryFit(stations, samples.Z, required * (1.0 + 1e-9), startZ, endZ, out profile, out _))
        {
            return curve;
        }

        double[] knots = curve.Knots;
        double[] gram = HeightGram(knots, curve.ControlPoints, 3);
        double scale = samples.Count;
        var target = new List<double>(profile);
        double[] heights = LeastSquares(samples, parameters, knots, gram, scale * Math.Pow(10.0, MinimumSmoothingExponent), target);
        if (Deviation(heights) <= limits.Vertical)
        {
            double low = MinimumSmoothingExponent, high = MaximumSmoothingExponent;
            for (int step = 0; step < SmoothingBisections; step++)
            {
                double middle = 0.5 * (low + high);
                double[] trial = LeastSquares(samples, parameters, knots, gram, scale * Math.Pow(10.0, middle), target);
                if (Deviation(trial) <= limits.Vertical)
                {
                    heights = trial;
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }
        }

        var points = (double[])curve.ControlPoints.Clone();
        for (int i = 0; i < heights.Length; i++)
            points[(i * 3) + 2] = heights[i];
        return new RouteSplineCurve(Degree, knots, points);

        double Deviation(double[] values)
        {
            double worst = 0.0;
            for (int k = 0; k < samples.Count; k++)
                worst = Math.Max(worst, Math.Abs(Evaluate1(knots, values, parameters[k]) - target[k]));
            return worst;
        }
    }

    /// <summary>
    /// The heights, refitted so the curve obeys the grade limit. A smooth profile rounds the grade breaks of
    /// the line's, and to rise as far between rounded breaks the stretch between them has to be steeper: a
    /// route built at the limit came out 4-7% over it. Re-laying the profile at a gentler limit leaves the
    /// rounding room, so the profile is re-laid (<see cref="Regrade"/>) at the limit times a factor that
    /// shrinks by the measured overshoot until the curve keeps the limit. Moving the control points' heights
    /// directly was tried first: it moved heights by a metre and doubled the cut on a switchback climb.
    /// <para>Where re-laying gently cannot join the ends, or would move heights by more than twice the
    /// tolerance (a switchback climbing at the limit all the way has no grade to give), knots are inserted
    /// where the curve is still too steep, and the heights refitted on them.</para>
    /// </summary>
    private static Attempt HoldGrade(Attempt attempt, Samples samples, Limits limits)
    {
        for (int refinement = 0; refinement < GradeRefinements; refinement++)
        {
            Attempt held = ReLay(attempt, samples, limits, guardHeights: true);
            if (MeasureMaxGrade(held.Curve) <= limits.MaxGrade * (1.0 + GradeTolerance))
                return held;

            // Knots inserted exactly leave the plan as it is (re-solving it moved it outside tolerance and never
            // settled), and give the heights the freedom the plan's uneven speed through a bend asks of them.
            List<double> distinct = DistinctKnots(attempt.Curve);
            RouteSplineCurve refined = attempt.Curve;
            for (int span = 0; span < distinct.Count - 1; span++)
            {
                double a = distinct[span], b = distinct[span + 1];
                (double grade, double where) = SteepestBetween(held.Curve, a, b);
                if (grade <= limits.MaxGrade * (1.0 + GradeTolerance) ||
                    samples.CountBetween(attempt.Parameters, a, b) < MinimumSamplesPerSpan)
                {
                    continue;
                }

                double width = b - a;
                refined = refined.InsertKnot(Math.Clamp(where, a + (0.25 * width), b - (0.25 * width)));
            }

            if (refined.ControlPointCount == attempt.Curve.ControlPointCount)
                break;

            attempt = new Attempt(refined, attempt.Parameters);
        }

        // Out of knots to add: the grade is the requirement, so re-lay as gently as it takes, whatever it costs
        // the heights (the readout reports how far they moved).
        return ReLay(attempt, samples, limits, guardHeights: false);
    }

    /// <summary>
    /// Re-lays the profile at a share of the limit that shrinks by the measured overshoot. With
    /// <paramref name="guardHeights"/>, stops before the heights move more than twice the vertical tolerance.
    /// </summary>
    private static Attempt ReLay(Attempt attempt, Samples samples, Limits limits, bool guardHeights)
    {
        double share = 1.0;
        var held = new Attempt(Regrade(attempt, samples, limits, share, out _), attempt.Parameters);
        for (int round = 0; round < HoldGradeRounds; round++)
        {
            double grade = MeasureMaxGrade(held.Curve);
            if (grade <= limits.MaxGrade * (1.0 + GradeTolerance))
                break;

            double gentler = share * limits.MaxGrade / grade * (1.0 - (0.5 * GradeTolerance));
            var candidate = new Attempt(Regrade(attempt, samples, limits, gentler, out bool joined), attempt.Parameters);
            if (!joined || (guardHeights && Deviations(candidate, samples).Vertical > 2.0 * limits.Vertical))
                break;

            share = gentler;
            held = candidate;
        }

        return held;
    }

    private const int GradeRefinements = 3;

    /// <summary>The steepest grade between two parameters and where it is.</summary>
    private static (double Grade, double Where) SteepestBetween(RouteSplineCurve curve, double a, double b)
    {
        double worst = 0.0, where = 0.5 * (a + b);
        (double X, double Y, double Z) previous = curve.PointAt(a);
        for (int c = 1; c <= ChecksPerSpan; c++)
        {
            double u = a + ((b - a) * c / ChecksPerSpan);
            (double X, double Y, double Z) point = curve.PointAt(u);
            double run = Math.Sqrt(Geometry2D.DistanceSquared(previous.X, previous.Y, point.X, point.Y));
            if (run > 1e-12 && Math.Abs(point.Z - previous.Z) / run > worst)
            {
                worst = Math.Abs(point.Z - previous.Z) / run;
                where = u - (0.5 * (b - a) / ChecksPerSpan);
            }

            previous = point;
        }

        return (worst, where);
    }

    private const int HoldGradeRounds = 8;

    /// <summary>
    /// How far under the radius a smooth curve may turn, relative: where an arc meets a straight its curvature
    /// has to ramp up, and within tolerance it overshoots a little.
    /// </summary>
    internal const double RadiusSlack = 0.1;

    /// <summary>How far over the limit the measured grade may read: a part in ten thousand, the noise of measuring it.</summary>
    internal const double GradeTolerance = 1e-4;

    /// <summary>The distinct knot values: 0, the interior knots, 1. Span j runs from entry j to entry j + 1.</summary>
    private static List<double> Distinct(List<double> interior)
    {
        var knots = new List<double>(interior.Count + 2) { 0.0 };
        knots.AddRange(interior);
        knots.Add(1.0);
        return knots;
    }

    /// <summary>
    /// For each knot span with a sample outside tolerance or a bend tighter than allowed, the span and where it is
    /// worst. A radius is checked as well as the tolerances because a curve within tolerance of a hairpin can still
    /// kink where the hairpin's arc meets its straights; a knot there lets the curvature ramp instead.
    /// </summary>
    private static List<(int Span, double Where)> OutsideTolerance(Attempt attempt, Samples samples, Limits limits, List<double> interior)
    {
        List<double> knots = Distinct(interior);
        int spans = knots.Count - 1;
        var worst = new double[spans];
        var where = new double[spans];
        RouteSplineCurve curve = attempt.Curve;

        int span = 0;
        for (int k = 0; k < samples.Count; k++)
        {
            double u = attempt.Parameters[k];
            while (span < spans - 1 && u >= knots[span + 1])
                span++;

            (double x, double y, double z) = curve.PointAt(u);
            double plan = Math.Sqrt(Geometry2D.DistanceSquared(x, y, samples.X[k], samples.Y[k])) / limits.Plan;
            double vertical = Math.Abs(z - samples.Z[k]) / limits.Vertical;
            double ratio = Math.Max(plan, vertical);
            if (ratio > worst[span])
            {
                worst[span] = ratio;
                where[span] = u;
            }
        }

        // The radius: no tighter than the minimum, or than the line itself where it already was.
        if (limits.MinimumRadius > 0.0)
        {
            for (int s = 0; s < spans; s++)
            {
                double a = knots[s], b = knots[s + 1];
                for (int c = 0; c <= ChecksPerSpan; c++)
                {
                    double u = a + ((b - a) * c / ChecksPerSpan);
                    double curvature = Math.Abs(curve.PlanCurvatureAt(u));
                    double allowed = Math.Min(limits.MinimumRadius, samples.RadiusNear(attempt.Parameters, u)) * (1.0 - RadiusSlack);
                    double ratio = curvature * allowed;
                    if (ratio > 1.0 && ratio > worst[s])
                    {
                        worst[s] = ratio;
                        where[s] = u;
                    }
                }
            }
        }

        var outside = new List<(int, double)>();
        for (int s = 0; s < spans; s++)
        {
            if (worst[s] > 1.0)
                outside.Add((s, where[s]));
        }

        return outside;
    }

    private static (double Plan, double Vertical) Deviations(Attempt attempt, Samples samples)
    {
        double plan = 0.0, vertical = 0.0;
        for (int k = 0; k < samples.Count; k++)
        {
            (double x, double y, double z) = attempt.Curve.PointAt(attempt.Parameters[k]);
            plan = Math.Max(plan, Math.Sqrt(Geometry2D.DistanceSquared(x, y, samples.X[k], samples.Y[k])));
            vertical = Math.Max(vertical, Math.Abs(z - samples.Z[k]));
        }

        return (plan, vertical);
    }

    /// <summary>
    /// The smoothest curve on these knots within the tolerances: least squares plus a bending penalty (the
    /// integral of the squared second derivative), its weight raised by bisection as far as the tolerance
    /// allows, separately for the plan and for the heights. Plain least squares rang where curvature jumps:
    /// beside its straights a 20 m arc read 4 m, worse the more knots it had. The smoothest curve within
    /// tolerance ramps its curvature like a transition curve instead. The weights are separate because the
    /// profile's grade breaks need knots the plan does not, and with one weight the plan wiggled through
    /// them (a 3 m bend on a 10 m route, where the ground steps). When no weight meets a tolerance the
    /// least-penalised fit is returned, and the caller adds knots.
    /// </summary>
    private static Attempt Solve(Samples samples, List<double> interior, Limits limits)
    {
        double[] knots = FullKnots(interior);
        // The first fit's bending is per parameter, as if the plan ran at an even speed of its length L, which
        // makes it L^3 times the bending per plan length; later rounds weight by the fitted speed (PlanGram).
        // Both are then per plan length, and scaling by the sample count puts the useful weights around 1.
        double length = Math.Max(samples.Length, 1e-9);
        double[] gram = Gram(knots);
        for (int i = 0; i < gram.Length; i++)
            gram[i] /= length * length * length;
        double scale = samples.Count;

        (double[] plan, double[] parameters) = FitPlan(samples, knots, gram, scale * Math.Pow(10.0, MinimumSmoothingExponent), samples.ChordParameters);
        if (PlanDeviation(samples, knots, plan, parameters) <= limits.Plan)
        {
            double low = MinimumSmoothingExponent, high = MaximumSmoothingExponent;
            for (int step = 0; step < SmoothingBisections; step++)
            {
                double middle = 0.5 * (low + high);
                (double[] trialPlan, double[] trialParameters) = FitPlan(samples, knots, gram, scale * Math.Pow(10.0, middle), parameters);
                if (PlanDeviation(samples, knots, trialPlan, trialParameters) <= limits.Plan)
                {
                    (plan, parameters) = (trialPlan, trialParameters);
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }
        }

        // Heights bend against plan length, not the parameter: through a bend the parameter's plan speed dips,
        // and a penalty on d2Z/du2 spent the tolerance straightening the heights in u, which steepened the
        // grade there by up to 7% (a switchback's hairpins).
        double[] heightGram = HeightGram(knots, plan, 2);
        double heightScale = samples.Count;
        double[] heights = FitHeights(samples, knots, heightGram, heightScale * Math.Pow(10.0, MinimumSmoothingExponent), parameters);
        if (HeightDeviation(samples, knots, heights, parameters) <= limits.Vertical)
        {
            double low = MinimumSmoothingExponent, high = MaximumSmoothingExponent;
            for (int step = 0; step < SmoothingBisections; step++)
            {
                double middle = 0.5 * (low + high);
                double[] trial = FitHeights(samples, knots, heightGram, heightScale * Math.Pow(10.0, middle), parameters);
                if (HeightDeviation(samples, knots, trial, parameters) <= limits.Vertical)
                {
                    heights = trial;
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }
        }

        var points = new double[heights.Length * 3];
        for (int i = 0; i < heights.Length; i++)
        {
            points[i * 3] = plan[i * 2];
            points[(i * 3) + 1] = plan[(i * 2) + 1];
            points[(i * 3) + 2] = heights[i];
        }

        return new Attempt(new RouteSplineCurve(Degree, knots, points), parameters);
    }

    private const double MinimumSmoothingExponent = -10.0;

    private const double MaximumSmoothingExponent = 8.0;

    private const int SmoothingBisections = 10;

    /// <summary>
    /// The plan control points (flat XY) by least squares from the given parameters, then the parameters
    /// moved towards each sample's nearest point in plan and the plan refitted.
    /// </summary>
    private static (double[] Plan, double[] Parameters) FitPlan(Samples samples, double[] knots, double[] gram, double smoothing, double[] start)
    {
        double[] parameters = (double[])start.Clone();
        double[] plan = LeastSquares(samples, parameters, knots, gram, smoothing, samples.X, samples.Y);
        for (int round = 0; round < CorrectionRounds; round++)
        {
            Correct(knots, plan, samples, parameters);
            plan = LeastSquares(samples, parameters, knots, PlanGram(knots, plan), smoothing, samples.X, samples.Y);
        }

        return (plan, parameters);
    }

    /// <summary>
    /// The plan's bending Gram matrix weighted by the inverse cube of the previous fit's plan speed: the
    /// integral of |C''|^2 / s'^3 du, which is the integral of curvature squared over plan length plus a term
    /// that evens out the speed. Unweighted, a slow stretch bent for free, and a hairpin on a 10 m radius came
    /// out with a 5 m kink in it.
    /// </summary>
    private static double[] PlanGram(double[] knots, double[] plan)
    {
        int count = knots.Length - Degree - 1;
        int n = count - 1;
        var gram = new double[count * (Degree + 1)];
        var derivatives = new double[3, Degree + 1];
        for (int i = Degree; i < knots.Length - Degree - 1; i++)
        {
            double a = knots[i], b = knots[i + 1];
            if (!(b > a))
                continue;

            for (int q = 0; q < GaussNodes.Length; q++)
            {
                double u = a + ((b - a) * 0.5 * (GaussNodes[q] + 1.0));
                int span = RouteSplineCurve.FindSpan(n, Degree, u, knots);
                RouteSplineCurve.BasisDerivatives(span, u, Degree, knots, 2, derivatives);
                double dx = 0.0, dy = 0.0;
                for (int j = 0; j <= Degree; j++)
                {
                    int index = (span - Degree + j) * 2;
                    dx += derivatives[1, j] * plan[index];
                    dy += derivatives[1, j] * plan[index + 1];
                }

                double speed = Math.Sqrt((dx * dx) + (dy * dy));
                if (!(speed > 1e-12))
                    continue;

                double weight = 0.5 * (b - a) * GaussWeights[q] / (speed * speed * speed);
                for (int r = 0; r <= Degree; r++)
                {
                    int row = span - Degree + r;
                    for (int c = 0; c <= r; c++)
                        gram[(row * (Degree + 1)) + (r - c)] += weight * derivatives[2, r] * derivatives[2, c];
                }
            }
        }

        return gram;
    }

    private static readonly double[] GaussNodes = { -0.8611363115940526, -0.3399810435848563, 0.3399810435848563, 0.8611363115940526 };

    private static readonly double[] GaussWeights = { 0.3478548451374538, 0.6521451548625461, 0.6521451548625461, 0.3478548451374538 };

    /// <summary>The height control points by least squares at fixed parameters.</summary>
    private static double[] FitHeights(Samples samples, double[] knots, double[] gram, double smoothing, double[] parameters) =>
        LeastSquares(samples, parameters, knots, gram, smoothing, samples.Z);

    private static double PlanDeviation(Samples samples, double[] knots, double[] plan, double[] parameters)
    {
        double worst = 0.0;
        for (int k = 0; k < samples.Count; k++)
        {
            (double x, double y) = Evaluate2(knots, plan, parameters[k]);
            worst = Math.Max(worst, Math.Sqrt(Geometry2D.DistanceSquared(x, y, samples.X[k], samples.Y[k])));
        }

        return worst;
    }

    private static double HeightDeviation(Samples samples, double[] knots, double[] heights, double[] parameters)
    {
        double worst = 0.0;
        for (int k = 0; k < samples.Count; k++)
            worst = Math.Max(worst, Math.Abs(Evaluate1(knots, heights, parameters[k]) - samples.Z[k]));
        return worst;
    }

    private static (double X, double Y) Evaluate2(double[] knots, double[] plan, double u)
    {
        int n = (plan.Length / 2) - 1;
        int span = RouteSplineCurve.FindSpan(n, Degree, u, knots);
        Span<double> basis = stackalloc double[Degree + 1];
        RouteSplineCurve.Basis(span, u, Degree, knots, basis);
        double x = 0.0, y = 0.0;
        for (int j = 0; j <= Degree; j++)
        {
            int index = span - Degree + j;
            x += basis[j] * plan[index * 2];
            y += basis[j] * plan[(index * 2) + 1];
        }

        return (x, y);
    }

    private static double Evaluate1(double[] knots, double[] values, double u)
    {
        int n = values.Length - 1;
        int span = RouteSplineCurve.FindSpan(n, Degree, u, knots);
        Span<double> basis = stackalloc double[Degree + 1];
        RouteSplineCurve.Basis(span, u, Degree, knots, basis);
        double value = 0.0;
        for (int j = 0; j <= Degree; j++)
            value += basis[j] * values[span - Degree + j];
        return value;
    }

    /// <summary>
    /// The bending Gram matrix, the integral over [0, 1] of N_i'' N_j'' for every pair of basis functions, in
    /// band storage (row * (Degree + 1) + row - column, column &lt;= row). A cubic's second derivative is linear
    /// on each span, so two-point Gauss quadrature is exact.
    /// </summary>
    private static double[] Gram(double[] knots)
    {
        int count = knots.Length - Degree - 1;
        int n = count - 1;
        var gram = new double[count * (Degree + 1)];
        var derivatives = new double[3, Degree + 1];
        double offset = 0.5 / Math.Sqrt(3.0);
        for (int i = Degree; i < knots.Length - Degree - 1; i++)
        {
            double a = knots[i], b = knots[i + 1];
            if (!(b > a))
                continue;

            foreach (double t in new[] { 0.5 - offset, 0.5 + offset })
            {
                double u = a + ((b - a) * t);
                int span = RouteSplineCurve.FindSpan(n, Degree, u, knots);
                RouteSplineCurve.BasisDerivatives(span, u, Degree, knots, 2, derivatives);
                double weight = 0.5 * (b - a);
                for (int r = 0; r <= Degree; r++)
                {
                    int row = span - Degree + r;
                    for (int c = 0; c <= r; c++)
                    {
                        int column = span - Degree + c;
                        gram[(row * (Degree + 1)) + (row - column)] += weight * derivatives[2, r] * derivatives[2, c];
                    }
                }
            }
        }

        return gram;
    }

    /// <summary>
    /// The bending Gram matrix for heights against plan arc length s along the plan curve given by
    /// <paramref name="plan"/> (control points, <paramref name="stride"/> values each, X and Y first):
    /// the integral of (d2Z/ds2)^2 ds, with d2Z/ds2 = (Z'' s' - Z' s'') / s'^3 for the plan speed s'. A profile
    /// that is straight in plan length costs nothing however unevenly the parameter runs. Four-point Gauss
    /// per span, since the plan speed is not a polynomial.
    /// </summary>
    private static double[] HeightGram(double[] knots, double[] plan, int stride)
    {
        int count = knots.Length - Degree - 1;
        int n = count - 1;
        var gram = new double[count * (Degree + 1)];
        var derivatives = new double[3, Degree + 1];
        Span<double> values = stackalloc double[Degree + 1];
        double[] nodes = GaussNodes, weights = GaussWeights;
        for (int i = Degree; i < knots.Length - Degree - 1; i++)
        {
            double a = knots[i], b = knots[i + 1];
            if (!(b > a))
                continue;

            for (int q = 0; q < nodes.Length; q++)
            {
                double u = a + ((b - a) * 0.5 * (nodes[q] + 1.0));
                int span = RouteSplineCurve.FindSpan(n, Degree, u, knots);
                RouteSplineCurve.BasisDerivatives(span, u, Degree, knots, 2, derivatives);
                double dx = 0.0, dy = 0.0, ddx = 0.0, ddy = 0.0;
                for (int j = 0; j <= Degree; j++)
                {
                    int index = (span - Degree + j) * stride;
                    dx += derivatives[1, j] * plan[index];
                    dy += derivatives[1, j] * plan[index + 1];
                    ddx += derivatives[2, j] * plan[index];
                    ddy += derivatives[2, j] * plan[index + 1];
                }

                double speed = Math.Sqrt((dx * dx) + (dy * dy));
                if (!(speed > 1e-12))
                    continue;

                double acceleration = ((dx * ddx) + (dy * ddy)) / speed;
                for (int j = 0; j <= Degree; j++)
                    values[j] = ((derivatives[2, j] * speed) - (derivatives[1, j] * acceleration)) / (speed * speed * speed);

                double weight = 0.5 * (b - a) * weights[q] * speed;
                for (int r = 0; r <= Degree; r++)
                {
                    int row = span - Degree + r;
                    for (int c = 0; c <= r; c++)
                        gram[(row * (Degree + 1)) + (row - (span - Degree + c))] += weight * values[r] * values[c];
                }
            }
        }

        return gram;
    }

    private static double[] FullKnots(List<double> interior)
    {
        var knots = new double[interior.Count + (2 * (Degree + 1))];
        for (int i = 0; i <= Degree; i++)
        {
            knots[i] = 0.0;
            knots[^(i + 1)] = 1.0;
        }

        for (int i = 0; i < interior.Count; i++)
            knots[Degree + 1 + i] = interior[i];
        return knots;
    }

    /// <summary>
    /// Moves each sample's parameter towards the nearest point of the plan curve (one Gauss–Newton step), the
    /// ends held at 0 and 1 and the order kept.
    /// </summary>
    private static void Correct(double[] knots, double[] plan, Samples samples, double[] parameters)
    {
        int count = plan.Length / 2;
        var hodograph = new double[(count - 1) * 2];
        for (int i = 0; i < count - 1; i++)
        {
            double denominator = knots[i + Degree + 1] - knots[i + 1];
            double factor = denominator > 0.0 ? Degree / denominator : 0.0;
            hodograph[i * 2] = factor * (plan[(i + 1) * 2] - plan[i * 2]);
            hodograph[(i * 2) + 1] = factor * (plan[((i + 1) * 2) + 1] - plan[(i * 2) + 1]);
        }

        var hodographKnots = new double[knots.Length - 2];
        Array.Copy(knots, 1, hodographKnots, 0, hodographKnots.Length);
        Span<double> basis = stackalloc double[Degree];
        for (int k = 1; k < samples.Count - 1; k++)
        {
            double u = parameters[k];
            (double x, double y) = Evaluate2(knots, plan, u);
            int span = RouteSplineCurve.FindSpan(count - 2, Degree - 1, u, hodographKnots);
            RouteSplineCurve.Basis(span, u, Degree - 1, hodographKnots, basis);
            double dx = 0.0, dy = 0.0;
            for (int j = 0; j < Degree; j++)
            {
                int index = span - (Degree - 1) + j;
                dx += basis[j] * hodograph[index * 2];
                dy += basis[j] * hodograph[(index * 2) + 1];
            }

            double speed = (dx * dx) + (dy * dy);
            if (!(speed > 1e-24))
                continue;

            double step = (((samples.X[k] - x) * dx) + ((samples.Y[k] - y) * dy)) / speed;
            parameters[k] = Math.Clamp(u + step, parameters[k - 1], 1.0);
        }

        for (int k = samples.Count - 2; k > 0; k--)
            parameters[k] = Math.Min(parameters[k], parameters[k + 1]);
    }

    /// <summary>
    /// The control points of one or two coordinates (interleaved) minimising the squared distance to the
    /// samples at their parameters plus <paramref name="smoothing"/> times the bending energy, the first and
    /// last held at the piece's ends.
    /// </summary>
    private static double[] LeastSquares(
        Samples samples,
        double[] parameters,
        double[] knots,
        double[] gram,
        double smoothing,
        List<double> first,
        List<double>? second = null)
    {
        int dimensions = second == null ? 1 : 2;
        int count = knots.Length - Degree - 1;
        int n = count - 1;
        int unknowns = count - 2;
        int last = samples.Count - 1;
        var points = new double[count * dimensions];
        points[0] = first[0];
        points[n * dimensions] = first[last];
        if (second != null)
        {
            points[1] = second[0];
            points[(n * 2) + 1] = second[last];
        }

        if (unknowns <= 0)
            return points;

        var matrix = new BandMatrix(unknowns, Degree);
        var rhs = new double[unknowns * dimensions];
        Span<double> basis = stackalloc double[Degree + 1];
        Span<double> residual = stackalloc double[2];
        for (int k = 0; k < samples.Count; k++)
        {
            double u = parameters[k];
            int span = RouteSplineCurve.FindSpan(n, Degree, u, knots);
            RouteSplineCurve.Basis(span, u, Degree, knots, basis);
            residual[0] = first[k];
            residual[1] = second?[k] ?? 0.0;
            for (int j = 0; j <= Degree; j++)
            {
                int index = span - Degree + j;
                if (index == 0 || index == n)
                {
                    for (int d = 0; d < dimensions; d++)
                        residual[d] -= basis[j] * points[(index * dimensions) + d];
                }
            }

            for (int a = 0; a <= Degree; a++)
            {
                int row = span - Degree + a - 1;
                if (row < 0 || row >= unknowns)
                    continue;

                for (int d = 0; d < dimensions; d++)
                    rhs[(row * dimensions) + d] += basis[a] * residual[d];
                for (int b = 0; b <= a; b++)
                {
                    int column = span - Degree + b - 1;
                    if (column >= 0)
                        matrix.Add(row, column, basis[a] * basis[b]);
                }
            }
        }

        for (int row = 1; row < n; row++)
        {
            for (int column = Math.Max(0, row - Degree); column <= Math.Min(n, row + Degree); column++)
            {
                int low = Math.Min(row, column), high = Math.Max(row, column);
                double value = smoothing * gram[(high * (Degree + 1)) + (high - low)];
                if (column == 0 || column == n)
                {
                    for (int d = 0; d < dimensions; d++)
                        rhs[((row - 1) * dimensions) + d] -= value * points[(column * dimensions) + d];
                }
                else if (column <= row)
                {
                    matrix.Add(row - 1, column - 1, value);
                }
            }
        }

        if (!matrix.TrySolve(rhs, dimensions))
        {
            // Control points evenly along the chord: the fallback when the system cannot be solved.
            for (int i = 1; i < n; i++)
            {
                for (int d = 0; d < dimensions; d++)
                    points[(i * dimensions) + d] = Geometry2D.Lerp(points[d], points[(n * dimensions) + d], (double)i / n);
            }

            return points;
        }

        for (int i = 0; i < unknowns; i++)
        {
            for (int d = 0; d < dimensions; d++)
                points[((i + 1) * dimensions) + d] = rhs[(i * dimensions) + d];
        }

        return points;
    }

    private static double[] SamplePieces(List<RouteSplineCurve> pieces)
    {
        var output = new List<double>();
        foreach (RouteSplineCurve piece in pieces)
        {
            List<double> knots = DistinctKnots(piece);
            for (int s = 0; s < knots.Count - 1; s++)
            {
                const int perSpan = 16;
                for (int c = output.Count == 0 ? 0 : 1; c <= perSpan; c++)
                {
                    (double x, double y, double z) = piece.PointAt(knots[s] + ((knots[s + 1] - knots[s]) * c / perSpan));
                    output.Add(x);
                    output.Add(y);
                    output.Add(z);
                }
            }
        }

        return output.ToArray();
    }

    private static List<double> DistinctKnots(RouteSplineCurve curve)
    {
        var knots = new List<double>();
        foreach (double knot in curve.Knots)
        {
            if (knots.Count == 0 || knot > knots[^1])
                knots.Add(knot);
        }

        return knots;
    }

    internal static double MeasureMaxGrade(RouteSplineCurve curve)
    {
        double worst = 0.0;
        List<double> knots = DistinctKnots(curve);
        for (int s = 0; s < knots.Count - 1; s++)
            worst = Math.Max(worst, MeasureMaxGrade(curve, knots[s], knots[s + 1]));
        return worst;
    }

    /// <summary>The steepest grade between parameters <paramref name="a"/> and <paramref name="b"/>, measured over short chords.</summary>
    private static double MeasureMaxGrade(RouteSplineCurve curve, double a, double b)
    {
        double worst = 0.0;
        (double X, double Y, double Z) previous = curve.PointAt(a);
        for (int c = 1; c <= ChecksPerSpan; c++)
        {
            (double X, double Y, double Z) point = curve.PointAt(a + ((b - a) * c / ChecksPerSpan));
            double run = Math.Sqrt(Geometry2D.DistanceSquared(previous.X, previous.Y, point.X, point.Y));
            if (run > 1e-12)
                worst = Math.Max(worst, Math.Abs(point.Z - previous.Z) / run);
            previous = point;
        }

        return worst;
    }

    internal static double? MeasureSmallestRadius(RouteSplineCurve curve)
    {
        double largest = 0.0;
        List<double> knots = DistinctKnots(curve);
        for (int s = 0; s < knots.Count - 1; s++)
        {
            for (int c = 0; c <= ChecksPerSpan; c++)
                largest = Math.Max(largest, Math.Abs(curve.PlanCurvatureAt(knots[s] + ((knots[s + 1] - knots[s]) * c / ChecksPerSpan))));
        }

        // A radius a thousand times the piece's own length turns it less than a tenth of a degree: that is
        // a straight's rounding noise, not a bend. An absolute floor read a straight sketch as a
        // "tightest bend 102935701.4 m" (found live).
        double length = 0.0;
        double[] points = curve.ControlPoints;
        for (int i = 1; i < curve.ControlPointCount; i++)
            length += Math.Sqrt(Geometry2D.DistanceSquared(points[(i - 1) * 3], points[((i - 1) * 3) + 1], points[i * 3], points[(i * 3) + 1]));
        return largest > 0.0 && 1.0 / largest < StraightRadiusLengths * length ? 1.0 / largest : null;
    }

    private readonly record struct Limits(double Plan, double Vertical, double MaxGrade, double MinimumRadius);

    private sealed record Attempt(RouteSplineCurve Curve, double[] Parameters);

    /// <summary>One piece's line resampled for fitting, with the line's own plan radius at every sample.</summary>
    private sealed class Samples
    {
        private Samples(List<double> x, List<double> y, List<double> z, double[] radius)
        {
            X = x;
            Y = y;
            Z = z;
            Radius = radius;
            Count = x.Count;
            ChordParameters = new double[Count];
            double total = 0.0;
            for (int k = 1; k < Count; k++)
            {
                double dz = z[k] - z[k - 1];
                total += Math.Sqrt(Geometry2D.DistanceSquared(x[k - 1], y[k - 1], x[k], y[k]) + (dz * dz));
                ChordParameters[k] = total;
            }

            Length = total;
            for (int k = 1; k < Count; k++)
                ChordParameters[k] = total > 0.0 ? ChordParameters[k] / total : (double)k / (Count - 1);
            ChordParameters[^1] = 1.0;
        }

        public List<double> X { get; }

        public List<double> Y { get; }

        public List<double> Z { get; }

        /// <summary>The line's plan radius at each sample: infinite on a straight.</summary>
        public double[] Radius { get; }

        public int Count { get; }

        /// <summary>The line's 3D length.</summary>
        public double Length { get; }

        /// <summary>The steepest grade between successive samples.</summary>
        public double MaxGrade
        {
            get
            {
                double worst = 0.0;
                for (int k = 1; k < Count; k++)
                {
                    double run = Math.Sqrt(Geometry2D.DistanceSquared(X[k - 1], Y[k - 1], X[k], Y[k]));
                    if (run > 1e-12)
                        worst = Math.Max(worst, Math.Abs(Z[k] - Z[k - 1]) / run);
                }

                return worst;
            }
        }

        public double[] ChordParameters { get; }

        /// <summary>Vertices <paramref name="first"/> to <paramref name="last"/> of a leg, long segments subdivided. Null when the piece has no length.</summary>
        public static Samples? Create(IReadOnlyList<double> xyz, int first, int last, Limits limits)
        {
            double length = 0.0;
            for (int i = first + 1; i <= last; i++)
                length += Math.Sqrt(Geometry2D.DistanceSquared(xyz[(i - 1) * 3], xyz[((i - 1) * 3) + 1], xyz[i * 3], xyz[(i * 3) + 1]));
            if (!(length > 0.0))
                return null;

            double spacing = Math.Max(SampleSpacingTolerances * limits.Plan, length / MaximumSamples);
            spacing = Math.Min(spacing, length / MinimumSamples);
            var x = new List<double> { xyz[first * 3] };
            var y = new List<double> { xyz[(first * 3) + 1] };
            var z = new List<double> { xyz[(first * 3) + 2] };
            var original = new List<bool> { true };
            for (int i = first + 1; i <= last; i++)
            {
                double ax = xyz[(i - 1) * 3], ay = xyz[((i - 1) * 3) + 1], az = xyz[((i - 1) * 3) + 2];
                double bx = xyz[i * 3], by = xyz[(i * 3) + 1], bz = xyz[(i * 3) + 2];
                double segment = Math.Sqrt(Geometry2D.DistanceSquared(ax, ay, bx, by));
                if (!(segment > 0.0))
                    continue;

                int pieces = Math.Max(1, (int)Math.Ceiling(segment / spacing));
                for (int k = 1; k <= pieces; k++)
                {
                    double t = (double)k / pieces;
                    x.Add(Geometry2D.Lerp(ax, bx, t));
                    y.Add(Geometry2D.Lerp(ay, by, t));
                    z.Add(Geometry2D.Lerp(az, bz, t));
                    original.Add(k == pieces);
                }
            }

            if (x.Count < 2)
                return null;

            // The line's own radius: the circle through each original vertex and its original neighbours.
            var radius = new double[x.Count];
            Array.Fill(radius, double.PositiveInfinity);
            var vertices = new List<int>();
            for (int k = 0; k < x.Count; k++)
            {
                if (original[k])
                    vertices.Add(k);
            }

            for (int v = 1; v < vertices.Count - 1; v++)
            {
                int a = vertices[v - 1], b = vertices[v], c = vertices[v + 1];
                radius[b] = Circumradius(x[a], y[a], x[b], y[b], x[c], y[c]);
            }

            return new Samples(x, y, z, radius);
        }

        /// <summary>The line's tightest radius among the samples around parameter <paramref name="u"/>.</summary>
        public double RadiusNear(double[] parameters, double u)
        {
            int index = Array.BinarySearch(parameters, u);
            if (index < 0)
                index = ~index;
            double smallest = double.PositiveInfinity;
            for (int k = Math.Max(0, index - 3); k <= Math.Min(Count - 1, index + 3); k++)
                smallest = Math.Min(smallest, Radius[k]);
            return smallest;
        }

        private static double Circumradius(double ax, double ay, double bx, double by, double cx, double cy)
        {
            double cross = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            if (Math.Abs(cross) < 1e-15)
                return double.PositiveInfinity;

            double ab = Math.Sqrt(Geometry2D.DistanceSquared(ax, ay, bx, by));
            double bc = Math.Sqrt(Geometry2D.DistanceSquared(bx, by, cx, cy));
            double ca = Math.Sqrt(Geometry2D.DistanceSquared(cx, cy, ax, ay));
            return ab * bc * ca / (2.0 * Math.Abs(cross));
        }

        public int CountBetween(double[] parameters, double a, double b)
        {
            int count = 0;
            foreach (double u in parameters)
            {
                if (u >= a && u <= b)
                    count++;
            }

            return count;
        }
    }

    /// <summary>A symmetric positive definite band matrix (lower half stored) solved by banded Cholesky.</summary>
    private sealed class BandMatrix
    {
        private readonly int _size;
        private readonly int _band;
        private readonly double[] _values;

        public BandMatrix(int size, int band)
        {
            _size = size;
            _band = band;
            _values = new double[size * (band + 1)];
        }

        /// <summary>Adds to entry (row, column) with column &lt;= row, within the band.</summary>
        public void Add(int row, int column, double value)
        {
            int offset = row - column;
            if (offset < 0 || offset > _band)
                return;
            _values[(row * (_band + 1)) + offset] += value;
        }

        private ref double At(int row, int column) => ref _values[(row * (_band + 1)) + (row - column)];

        /// <summary>Factorises in place and solves for <paramref name="columns"/> interleaved right-hand sides.</summary>
        public bool TrySolve(double[] rhs, int columns)
        {
            for (int i = 0; i < _size; i++)
            {
                for (int j = Math.Max(0, i - _band); j <= i; j++)
                {
                    double sum = At(i, j);
                    for (int k = Math.Max(0, i - _band); k < j; k++)
                    {
                        if (j - k > _band)
                            continue;
                        sum -= At(i, k) * At(j, k);
                    }

                    if (i == j)
                    {
                        if (!(sum > 1e-300))
                            return false;
                        At(i, i) = Math.Sqrt(sum);
                    }
                    else
                    {
                        At(i, j) = sum / At(j, j);
                    }
                }
            }

            for (int c = 0; c < columns; c++)
            {
                for (int i = 0; i < _size; i++)
                {
                    double sum = rhs[(i * columns) + c];
                    for (int k = Math.Max(0, i - _band); k < i; k++)
                        sum -= At(i, k) * rhs[(k * columns) + c];
                    rhs[(i * columns) + c] = sum / At(i, i);
                }

                for (int i = _size - 1; i >= 0; i--)
                {
                    double sum = rhs[(i * columns) + c];
                    for (int k = i + 1; k <= Math.Min(_size - 1, i + _band); k++)
                        sum -= At(k, i) * rhs[(k * columns) + c];
                    rhs[(i * columns) + c] = sum / At(i, i);
                }
            }

            return true;
        }
    }
}
