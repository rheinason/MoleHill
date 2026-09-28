namespace MoleHill.Core.Analysis;

/// <summary>
/// Follows accessible routes along their length and splits them into landings and runs, then checks each
/// run's rise and going. Stage three of the gradient compliance analysis (backlog B13).
/// </summary>
/// <remarks>
/// <para><b>Landings are detected, not drawn.</b> A stretch of route whose running slope is within the
/// landing limit, over at least the minimum landing length, is a landing. A flat stretch shorter than
/// that is not a landing: it does not end the run it sits in, so it cannot reset the rise count. That is
/// the conservative reading, and it means "the landing is too short" shows up as the run it failed to
/// break being too high or too long, which is the consequence that matters.</para>
///
/// <para><b>Running slope is measured from heights along the route</b>, not from face gradients. The
/// route is sampled at even stations, and each station's slope is the rise over the run across a window
/// of the smoothing length centred on it: a level laid along the route. Rise and going are then read off
/// the same station heights, so a run's figures and its classification cannot disagree.</para>
///
/// <para><b>A run is a walk or a ramp by its steepest station.</b> Walks may still have a rise limit
/// (Approved Document M wants a landing every 500 mm of rise on anything steeper than level); ramps have
/// a rise limit and optionally a going limit that depends on their gradient. The gradient used for the
/// going limit is the run's mean, rise over going, because that is how a flight's gradient is stated.</para>
/// </remarks>
public static class RouteRunAnalyzer
{
    public enum RunKind : byte
    {
        /// <summary>A detected landing: level enough, and long enough.</summary>
        Landing = 0,

        /// <summary>Between landings, but never steeper than the landing limit: nothing to check.</summary>
        Level = 1,

        /// <summary>Steeper than level, within the walk limit throughout.</summary>
        Walk = 2,

        /// <summary>Steeper than the walk limit somewhere.</summary>
        Ramp = 3,
    }

    [Flags]
    public enum RunFailure : byte
    {
        None = 0,

        /// <summary>The run climbs more than its band allows between landings.</summary>
        RiseExceeded = 1,

        /// <summary>The run is longer than its gradient allows.</summary>
        GoingExceeded = 2,
    }

    /// <summary>The longest going allowed at one gradient. Approved Document M Table 1 is three of these.</summary>
    public readonly record struct GoingLimit(double SlopeRatio, double MaxGoing);

    public sealed class Options
    {
        /// <summary>The steepest running slope a landing may have, as a ratio.</summary>
        public double LandingMaxRatio { get; init; }

        /// <summary>The steepest running slope that is still a walk, as a ratio.</summary>
        public double WalkMaxRatio { get; init; }

        /// <summary>The shortest flat stretch that counts as a landing, in model units.</summary>
        public double LandingMinLength { get; init; }

        /// <summary>Largest rise of a walk between landings. Positive infinity: no limit.</summary>
        public double WalkMaxRise { get; init; } = double.PositiveInfinity;

        /// <summary>Largest rise of a ramp between landings. Positive infinity: no limit.</summary>
        public double RampMaxRise { get; init; } = double.PositiveInfinity;

        /// <summary>Longest going per gradient for ramps. Empty: no going limit.</summary>
        public IReadOnlyList<GoingLimit> RampGoingLimits { get; init; } = Array.Empty<GoingLimit>();

        /// <summary>Interpolate between going limits, as Approved Document M allows; otherwise take the stricter.</summary>
        public bool InterpolateGoing { get; init; }

        /// <summary>Distance between stations along the route, in model units.</summary>
        public double StationSpacing { get; init; }

        /// <summary>Window the running slope is measured over. Zero uses neighbouring stations only.</summary>
        public double SmoothingLength { get; init; }

        /// <summary>Model tolerance, for the height lookup.</summary>
        public double Tolerance { get; init; }

        public Func<bool>? CancellationRequested { get; init; }
    }

    public sealed class Run
    {
        public int RouteIndex { get; init; }
        public RunKind Kind { get; init; }
        public RunFailure Failures { get; init; }

        /// <summary>Distance along the route where the run starts and ends.</summary>
        public double StartDistance { get; init; }
        public double EndDistance { get; init; }

        /// <summary>Plan length along the route.</summary>
        public double Going => EndDistance - StartDistance;

        /// <summary>Highest station minus lowest station.</summary>
        public double Rise { get; init; }

        /// <summary>Rise over going.</summary>
        public double MeanRatio => Going > 0.0 ? Rise / Going : 0.0;

        /// <summary>Steepest smoothed station slope in the run.</summary>
        public double SteepestRatio { get; init; }

        /// <summary>Longest going allowed at this run's mean gradient, or positive infinity when unlimited.</summary>
        public double AllowedGoing { get; init; } = double.PositiveInfinity;

        /// <summary>The run's stations as <c>[x0, y0, z0, x1, …]</c>, for drawing and colouring.</summary>
        public required double[] PointsXyz { get; init; }
    }

    /// <summary>
    /// Splits each route into landings and runs. A route leaving the terrain is split where it leaves, and
    /// each piece on the terrain is followed on its own.
    /// </summary>
    /// <param name="routes">Route centrelines as flat XY arrays (<c>[x0, y0, x1, y1, …]</c>).</param>
    public static IReadOnlyList<Run> Analyze(
        MeshHeightProjector projector,
        IReadOnlyList<double[]> routes,
        Options options)
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(options);

        var runs = new List<Run>();
        for (int routeIndex = 0; routeIndex < routes.Count; routeIndex++)
        {
            if (options.CancellationRequested?.Invoke() == true)
                throw new OperationCanceledException();

            foreach (Station[] piece in SampleRoute(projector, routes[routeIndex], options))
                SplitPiece(piece, routeIndex, options, runs);
        }

        return runs;
    }

    /// <summary>
    /// The longest going allowed at <paramref name="ratio"/>. Between two listed gradients the going is
    /// interpolated linearly in the n of 1:n, which reproduces Approved Document M's own worked examples
    /// (1:14 at 4 m, 1:19 at 9 m); without interpolation the stricter of the two applies.
    /// </summary>
    public static double AllowedGoing(double ratio, IReadOnlyList<GoingLimit> limits, bool interpolate)
    {
        if (limits.Count == 0)
            return double.PositiveInfinity;

        var sorted = limits.Where(limit => limit.SlopeRatio > 0.0).OrderBy(limit => limit.SlopeRatio).ToArray();
        if (sorted.Length == 0)
            return double.PositiveInfinity;
        if (ratio <= sorted[0].SlopeRatio)
            return sorted[0].MaxGoing;
        if (ratio >= sorted[^1].SlopeRatio)
            return sorted[^1].MaxGoing;

        for (int i = 1; i < sorted.Length; i++)
        {
            GoingLimit gentle = sorted[i - 1];
            GoingLimit steep = sorted[i];
            if (ratio > steep.SlopeRatio)
                continue;
            if (!interpolate)
                return steep.MaxGoing;

            double nGentle = 1.0 / gentle.SlopeRatio;
            double nSteep = 1.0 / steep.SlopeRatio;
            double n = 1.0 / ratio;
            return gentle.MaxGoing + ((steep.MaxGoing - gentle.MaxGoing) * (nGentle - n) / (nGentle - nSteep));
        }

        return sorted[^1].MaxGoing;
    }

    private readonly record struct Station(double X, double Y, double Z, double Distance);

    private static IEnumerable<Station[]> SampleRoute(MeshHeightProjector projector, double[] route, Options options)
    {
        int pointCount = route.Length / 2;
        if (pointCount < 2)
            yield break;

        double length = 0.0;
        for (int i = 1; i < pointCount; i++)
            length += Math.Sqrt(Sq(route[i * 2] - route[(i - 1) * 2]) + Sq(route[(i * 2) + 1] - route[((i - 1) * 2) + 1]));
        if (length <= 0.0)
            yield break;

        double spacing = options.StationSpacing > 0.0 ? options.StationSpacing : length / 100.0;
        spacing = Math.Max(spacing, length / 100_000.0);
        int stationCount = (int)Math.Ceiling(length / spacing) + 1;
        double step = length / (stationCount - 1);

        var piece = new List<Station>();
        int segment = 1;
        double segmentStart = 0.0;
        double previousZ = 0.0;
        for (int s = 0; s < stationCount; s++)
        {
            double distance = Math.Min(s * step, length);
            double segmentLength = SegmentLength(route, segment);
            while (segment < pointCount - 1 && distance > segmentStart + segmentLength)
            {
                segmentStart += segmentLength;
                segment++;
                segmentLength = SegmentLength(route, segment);
            }

            double t = segmentLength > 0.0 ? Math.Clamp((distance - segmentStart) / segmentLength, 0.0, 1.0) : 0.0;
            double x = route[(segment - 1) * 2] + (t * (route[segment * 2] - route[(segment - 1) * 2]));
            double y = route[((segment - 1) * 2) + 1] + (t * (route[(segment * 2) + 1] - route[((segment - 1) * 2) + 1]));

            if (projector.TryProjectZ(x, y, previousZ, options.Tolerance, out double z, out _))
            {
                piece.Add(new Station(x, y, z, distance));
                previousZ = z;
            }
            else if (piece.Count > 0)
            {
                if (piece.Count >= 2)
                    yield return piece.ToArray();
                piece.Clear();
            }
        }

        if (piece.Count >= 2)
            yield return piece.ToArray();
    }

    private static void SplitPiece(Station[] stations, int routeIndex, Options options, List<Run> runs)
    {
        int count = stations.Length;
        var slopes = new double[count];
        var flat = new bool[count];
        double halfWindow = Math.Max(0.0, options.SmoothingLength) * 0.5;
        const double tolerance = 1e-9;
        for (int i = 0; i < count; i++)
        {
            int lo = i;
            int hi = i;
            while (lo > 0 && stations[i].Distance - stations[lo - 1].Distance <= halfWindow + tolerance)
                lo--;
            while (hi < count - 1 && stations[hi + 1].Distance - stations[i].Distance <= halfWindow + tolerance)
                hi++;
            lo = Math.Min(lo, Math.Max(0, i - 1));
            hi = Math.Max(hi, Math.Min(count - 1, i + 1));

            // The centred slope classifies the run: a level laid across the station.
            slopes[i] = Slope(stations[lo], stations[hi]);

            // A station is flat when the ground is flat on either side of it. The centred window would
            // blur a landing's ends into the ramps beside it and shorten every landing by a window width,
            // so a real 1.5 m landing would stop counting; taking the flatter side keeps its ends sharp
            // while a bump inside it is still smoothed from both.
            double back = i > lo ? Slope(stations[lo], stations[i]) : double.PositiveInfinity;
            double forward = hi > i ? Slope(stations[i], stations[hi]) : double.PositiveInfinity;
            flat[i] = Math.Min(back, forward) <= options.LandingMaxRatio + tolerance;
        }

        // Landings first: maximal flat stretches long enough to count.
        var isLanding = new bool[count];
        for (int i = 0; i < count;)
        {
            if (!flat[i])
            {
                i++;
                continue;
            }

            int j = i;
            while (j + 1 < count && flat[j + 1])
                j++;
            if (stations[j].Distance - stations[i].Distance >= options.LandingMinLength - tolerance)
            {
                for (int k = i; k <= j; k++)
                    isLanding[k] = true;
            }

            i = j + 1;
        }

        // Then everything between landings is one run, short flat spots included.
        for (int i = 0; i < count;)
        {
            int j = i;
            while (j + 1 < count && isLanding[j + 1] == isLanding[i])
                j++;

            // A run shares its end stations with the landings either side, so its rise and going span
            // the whole climb between them.
            int start = isLanding[i] ? i : Math.Max(0, i - 1);
            int end = isLanding[i] ? j : Math.Min(count - 1, j + 1);
            runs.Add(isLanding[i]
                ? MakeRun(stations, slopes, start, end, routeIndex, RunKind.Landing, options)
                : ClassifyRun(stations, slopes, start, end, routeIndex, options));
            i = j + 1;
        }
    }

    private static Run ClassifyRun(Station[] stations, double[] slopes, int start, int end, int routeIndex, Options options)
    {
        double steepest = 0.0;
        for (int i = start; i <= end; i++)
            steepest = Math.Max(steepest, slopes[i]);

        const double tolerance = 1e-9;
        RunKind kind = steepest <= options.LandingMaxRatio + tolerance ? RunKind.Level
            : steepest <= options.WalkMaxRatio + tolerance ? RunKind.Walk
            : RunKind.Ramp;
        return MakeRun(stations, slopes, start, end, routeIndex, kind, options);
    }

    private static Run MakeRun(
        Station[] stations, double[] slopes, int start, int end, int routeIndex, RunKind kind, Options options)
    {
        double minZ = double.PositiveInfinity;
        double maxZ = double.NegativeInfinity;
        double steepest = 0.0;
        var points = new double[(end - start + 1) * 3];
        for (int i = start; i <= end; i++)
        {
            minZ = Math.Min(minZ, stations[i].Z);
            maxZ = Math.Max(maxZ, stations[i].Z);
            steepest = Math.Max(steepest, slopes[i]);
            int p = (i - start) * 3;
            points[p] = stations[i].X;
            points[p + 1] = stations[i].Y;
            points[p + 2] = stations[i].Z;
        }

        double rise = maxZ - minZ;
        double going = stations[end].Distance - stations[start].Distance;
        const double tolerance = 1e-9;
        RunFailure failures = RunFailure.None;
        double allowedGoing = double.PositiveInfinity;
        if (kind == RunKind.Walk && rise > options.WalkMaxRise + tolerance)
            failures |= RunFailure.RiseExceeded;
        if (kind == RunKind.Ramp)
        {
            if (rise > options.RampMaxRise + tolerance)
                failures |= RunFailure.RiseExceeded;
            allowedGoing = AllowedGoing(going > 0.0 ? rise / going : 0.0, options.RampGoingLimits, options.InterpolateGoing);
            if (going > allowedGoing + tolerance)
                failures |= RunFailure.GoingExceeded;
        }

        return new Run
        {
            RouteIndex = routeIndex,
            Kind = kind,
            Failures = failures,
            StartDistance = stations[start].Distance,
            EndDistance = stations[end].Distance,
            Rise = rise,
            SteepestRatio = steepest,
            AllowedGoing = allowedGoing,
            PointsXyz = points,
        };
    }

    private static double Slope(Station from, Station to)
    {
        double run = to.Distance - from.Distance;
        return run > 0.0 ? Math.Abs(to.Z - from.Z) / run : 0.0;
    }

    private static double SegmentLength(double[] route, int segment) =>
        Math.Sqrt(Sq(route[segment * 2] - route[(segment - 1) * 2]) + Sq(route[(segment * 2) + 1] - route[((segment - 1) * 2) + 1]));

    private static double Sq(double value) => value * value;
}
