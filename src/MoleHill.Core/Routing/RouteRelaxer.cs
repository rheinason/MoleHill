using MoleHill.Core.Geometry;

namespace MoleHill.Core.Routing;

/// <summary>
/// Relaxes a sketched route over the ground: an elastic band of stations along the sketch that slides,
/// sweep by sweep, towards a route the route search would price well, while keeping to the sketch as
/// firmly as the user asks. Built for a live session: each <see cref="Step"/> is a few milliseconds, the
/// band can be pinned and dragged between steps, the weights changed at any time, and
/// <see cref="Evaluate"/> scores it on the same terms as a suggested route.
/// </summary>
/// <remarks>
/// <para><b>Energy.</b> The band's energy is the sum of</para>
/// <list type="bullet">
/// <item>its cost by the route search's own pricing (<see cref="RouteCost"/>: distance, steepness and
/// earthworks per unit length, sampled every half cell), which is the score the session shows, so the
/// band minimises exactly what it is judged by;</item>
/// <item>bending, <c>R^2 * theta^2 / l</c> at each station, which integrates to <c>R * Theta</c> over a
/// bend of radius R (the scale of the search's turn price) and grows as the bend tightens, with R the
/// minimum radius (or corridor width) widened by Simplicity; and a steep penalty on any bend tighter than
/// the minimum radius;</item>
/// <item>fidelity, <c>Fidelity * l * (d / D)^2</c>, with d the distance to the guide line (the sketch,
/// or the line Help me found) and D twice the corridor width or the minimum radius: at Fidelity 1,
/// straying D from the sketch costs as much as the route's own length there;</item>
/// <item>once Help me has seeded the band, a shortfall penalty on any stretch between pins shorter than
/// its height difference over the limit (with 5% to spare: the editable curve, smoothed, cuts every hairpin a little short), without which the band unwound the switchbacks
/// it was given.</item>
/// </list>
/// <para><b>Descent.</b> A Sobolev gradient: each station's partial derivative by finite differences,
/// smoothed along the band at a scale that cycles from a quarter of it down to one station, then a line
/// search on the whole band's energy, so the band never gets worse. A move off the terrain is infinitely
/// dear and never taken. Every eight sweeps the band is resampled to even spacing between its pins, so it
/// can lengthen and shorten. A sketch straight over a 12 m hill settles in about 200 sweeps (0.3 s),
/// three quarters cheaper and with a tenth of the earthwork.</para>
/// <para><b>Limits.</b> A band can only slide; it cannot change its topology. A sketch straight up a slope
/// steeper than the limit is improved but cannot become switchbacks; <see cref="Evaluate"/> reports such
/// stretches as <see cref="RouteRelaxIssueKind.Climb"/>, and <see cref="HelpMe"/> searches for the route
/// with the grid search kept near the band.</para>
/// <para>Deterministic and single-threaded; a host serialises access.</para>
/// </remarks>
public sealed class RouteRelaxer
{
    public const int MaximumStations = 600;

    public const int MinimumStations = 40;

    /// <summary>Pin ids of the ends, which are always pinned.</summary>
    public const int StartPin = 0;

    public const int EndPin = 1;

    private const int ResampleEvery = 8;

    private const double InitialStep = 0.25;

    private const double MinimumStep = 1e-4;

    /// <summary>A sweep whose largest move is under this share of the spacing is quiet.</summary>
    private const double QuietMove = 1e-3;

    private const int QuietSweepsToSettle = 8;

    private const int GuideWindow = 8;

    /// <summary>Multiplier of the penalty on a bend tighter than the minimum radius.</summary>
    private const double RadiusPenalty = 50.0;

    private readonly RouteHeightGrid _grid;
    private readonly List<(double X, double Y)> _sketch;
    private readonly double _sketchCost;
    private readonly List<double> _x = new();
    private readonly List<double> _y = new();
    private readonly List<int> _pin = new();
    /// <summary>The line search's step length at each smoothing scale, carried from sweep to sweep.</summary>
    private readonly double[] _step = new double[SmoothingScales.Length];
    private readonly List<int> _guideSegment = new();
    private List<(double X, double Y)> _guide;
    private RouteRelaxSettings _settings;
    private RouteCost _price;
    private int _nextPin = 2;
    private int _sweeps;
    private int _quietSweeps;

    /// <summary>
    /// True once the band was seeded with a found route, whose length the shortfall penalty then holds. A
    /// user's sketch is never lengthened by it: a straight line up a hillside, pushed to the length its
    /// climb needs, snaked into 0.2 m bends (live, 2026-10-17); finding switchbacks is Help me's job.
    /// </summary>
    private bool _holdLength;

    private RouteRelaxer(RouteHeightGrid grid, List<(double X, double Y)> sketch, RouteRelaxSettings settings, double spacing)
    {
        _grid = grid;
        _sketch = sketch;
        _settings = settings;
        _price = new RouteCost(settings.ToRequest(sketch));
        Spacing = spacing;
        _guide = Resampled(sketch, 0.5 * spacing);
        LoadBand(sketch, keepPins: false);
        _sketchCost = PlanCost(sketch);
    }

    /// <summary>The line as drawn.</summary>
    public IReadOnlyList<(double X, double Y)> Sketch => _sketch;

    /// <summary>The line fidelity holds the band to: the sketch, or the route <see cref="HelpMe"/> found.</summary>
    public IReadOnlyList<(double X, double Y)> Guide => _guide;

    /// <summary>The station spacing the band is kept at, model units.</summary>
    public double Spacing { get; }

    public int StationCount => _x.Count;

    /// <summary>Sweeps run since the band was created.</summary>
    public int Sweeps => _sweeps;

    /// <summary>True once the band has stopped moving; a change of settings or pins wakes it.</summary>
    public bool IsSettled => _quietSweeps >= QuietSweepsToSettle;

    public RouteRelaxSettings Settings
    {
        get => _settings;
        set
        {
            if (value == _settings)
                return;
            if (!value.IsValid)
                throw new ArgumentException("The relax settings are not valid.", nameof(value));
            _settings = value;
            _price = new RouteCost(value.ToRequest(_sketch));
            Wake();
        }
    }

    /// <summary>
    /// Starts a band along a sketch, or fails: settings that cannot be used, a sketch with no length, or a
    /// sketch that leaves the terrain (<paramref name="where"/> is the first point off it).
    /// </summary>
    public static RouteRelaxer? TryCreate(
        RouteHeightGrid grid,
        IReadOnlyList<(double X, double Y)> sketch,
        RouteRelaxSettings settings,
        out RouteRelaxFailure failure,
        out (double X, double Y) where)
    {
        where = default;
        if (!settings.IsValid)
        {
            failure = RouteRelaxFailure.InvalidSettings;
            return null;
        }

        var points = new List<(double X, double Y)>();
        foreach ((double x, double y) in sketch)
        {
            if (points.Count == 0 || Geometry2D.DistanceSquared(points[^1].X, points[^1].Y, x, y) > 1e-18)
                points.Add((x, y));
        }

        double length = Length(points);
        if (points.Count < 2 || !(length > 0.0))
        {
            failure = RouteRelaxFailure.NoLength;
            return null;
        }

        double spacing = Math.Max(grid.CellSize, length / MaximumStations);
        spacing = Math.Min(spacing, length / MinimumStations);
        foreach ((double x, double y) in Resampled(points, 0.5 * spacing))
        {
            if (!grid.TrySample(x, y, RouteLegBuilder.ChordGroundWeight, out _))
            {
                failure = RouteRelaxFailure.OffTerrain;
                where = (x, y);
                return null;
            }
        }

        failure = RouteRelaxFailure.None;
        return new RouteRelaxer(grid, points, settings, spacing);
    }

    /// <summary>The band's stations as flat XY.</summary>
    public double[] PlanXy()
    {
        var xy = new double[_x.Count * 2];
        for (int i = 0; i < _x.Count; i++)
        {
            xy[i * 2] = _x[i];
            xy[(i * 2) + 1] = _y[i];
        }

        return xy;
    }

    /// <summary>The pins, ends first: their ids and where they hold the band.</summary>
    public IReadOnlyList<(int Id, double X, double Y)> Pins()
    {
        var pins = new List<(int, double, double)>();
        for (int i = 0; i < _x.Count; i++)
        {
            if (_pin[i] >= 0)
                pins.Add((_pin[i], _x[i], _y[i]));
        }

        return pins.OrderBy(pin => pin.Item1 == StartPin ? 0 : pin.Item1 == EndPin ? 1 : 2).ToList();
    }

    /// <summary>
    /// Pins the band at the point of it nearest (<paramref name="x"/>, <paramref name="y"/>), adding a station
    /// there, and returns the new pin's id; or the id of a pin already within half a station of it.
    /// </summary>
    public int AddPin(double x, double y)
    {
        int best = 0;
        double bestDistance = double.MaxValue, bestT = 0.0;
        for (int i = 0; i < _x.Count - 1; i++)
        {
            double t = Geometry2D.ParameterOnSegmentClamped(_x[i], _y[i], _x[i + 1], _y[i + 1], x, y);
            double px = Geometry2D.Lerp(_x[i], _x[i + 1], t), py = Geometry2D.Lerp(_y[i], _y[i + 1], t);
            double d = Geometry2D.DistanceSquared(px, py, x, y);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
                bestT = t;
            }
        }

        double qx = Geometry2D.Lerp(_x[best], _x[best + 1], bestT), qy = Geometry2D.Lerp(_y[best], _y[best + 1], bestT);
        foreach (int i in new[] { best, best + 1 })
        {
            if (_pin[i] >= 0 && Geometry2D.DistanceSquared(_x[i], _y[i], qx, qy) <= 0.25 * Spacing * Spacing)
                return _pin[i];
        }

        // Near a free station, pin it; otherwise add a station on the band.
        int at;
        if (Geometry2D.DistanceSquared(_x[best], _y[best], qx, qy) <= 0.0625 * Spacing * Spacing && best > 0)
        {
            at = best;
        }
        else if (Geometry2D.DistanceSquared(_x[best + 1], _y[best + 1], qx, qy) <= 0.0625 * Spacing * Spacing && best + 1 < _x.Count - 1)
        {
            at = best + 1;
        }
        else
        {
            at = best + 1;
            _x.Insert(at, qx);
            _y.Insert(at, qy);
            _pin.Insert(at, -1);
            _guideSegment.Insert(at, _guideSegment[best]);
        }

        _x[at] = qx;
        _y[at] = qy;
        _pin[at] = _nextPin++;
        Wake();
        return _pin[at];
    }

    /// <summary>
    /// Moves a pin to (<paramref name="x"/>, <paramref name="y"/>). False, with the pin left where it was,
    /// when that point is off the terrain or the pin no longer exists.
    /// </summary>
    public bool MovePin(int id, double x, double y)
    {
        int index = _pin.IndexOf(id);
        if (index < 0 || !_grid.TrySample(x, y, RouteLegBuilder.ChordGroundWeight, out _))
            return false;

        double dx = x - _x[index], dy = y - _y[index];
        double oldX = _x[index], oldY = _y[index];
        _x[index] = x;
        _y[index] = y;

        // A drag is new intent, so the line the band is held to moves with it, and the band near the pin
        // follows at once, both with a smooth falloff that ends at the neighbouring pins. Moving the pin alone
        // left the guide behind: fidelity held the band on the sketch right up to the pin, which it reached
        // by a narrow out-and-back spike (live, 2026-10-18). Pulled like this, it bulges like a rope.
        double strayed = Math.Sqrt(GuideDistanceSquared(NearestGuideSegment(x, y, _guideSegment[index], _guide.Count), x, y));
        double reach = Math.Max(Math.Max(3.0 * _settings.BendRadius, 3.0 * strayed), 4.0 * Spacing);
        DragStations(index, dx, dy, reach);
        DragGuide(index, oldX, oldY, dx, dy, reach);
        Wake();
        return true;
    }

    /// <summary>The soft-selection falloff: 1 at the dragged point, 0 at the reach, smooth at both.</summary>
    private static double Falloff(double distance, double reach)
    {
        if (!(reach > 0.0) || distance >= reach)
            return 0.0;
        double t = 1.0 - ((distance / reach) * (distance / reach));
        return t * t;
    }

    /// <summary>Moves the free stations either side of a dragged pin by the falloff of their distance along the band.</summary>
    private void DragStations(int index, double dx, double dy, double reach)
    {
        foreach (int step in new[] { -1, 1 })
        {
            // The stretch to the next pin; the falloff reaches zero there however long the reach.
            double limit = 0.0;
            int end = index + step;
            while (end > 0 && end < _x.Count - 1 && _pin[end] < 0)
            {
                limit += StationGap(end - step, end);
                end += step;
            }

            if (end >= 0 && end < _x.Count)
                limit += StationGap(end - step, end);
            double side = Math.Min(reach, limit);

            double along = 0.0;
            for (int j = index + step; j > 0 && j < _x.Count - 1 && _pin[j] < 0; j += step)
            {
                along += StationGap(j - step, j);
                double weight = Falloff(along, side);
                if (weight <= 0.0)
                    break;

                double nx = _x[j] + (weight * dx), ny = _y[j] + (weight * dy);
                if (_grid.TrySample(nx, ny, RouteLegBuilder.ChordGroundWeight, out _))
                {
                    _x[j] = nx;
                    _y[j] = ny;
                }
            }
        }
    }

    private double StationGap(int a, int b) => Math.Sqrt(Geometry2D.DistanceSquared(_x[a], _y[a], _x[b], _y[b]));

    /// <summary>
    /// Moves the guide line near where the pin was by the falloff of distance along the guide, stopping at
    /// the guide's points nearest the neighbouring pins.
    /// </summary>
    private void DragGuide(int index, double oldX, double oldY, double dx, double dy, double reach)
    {
        int count = _guide.Count;
        var along = new double[count];
        for (int g = 1; g < count; g++)
            along[g] = along[g - 1] + Math.Sqrt(Geometry2D.DistanceSquared(_guide[g - 1].X, _guide[g - 1].Y, _guide[g].X, _guide[g].Y));

        double at = AlongGuide(oldX, oldY, along);
        double low = 0.0, high = along[^1];
        for (int j = index - 1; j >= 0; j--)
        {
            if (_pin[j] >= 0)
            {
                low = AlongGuide(_x[j], _y[j], along);
                break;
            }
        }

        for (int j = index + 1; j < _x.Count; j++)
        {
            if (_pin[j] >= 0)
            {
                high = AlongGuide(_x[j], _y[j], along);
                break;
            }
        }

        for (int g = 0; g < count; g++)
        {
            double distance = along[g] - at;
            double side = distance < 0.0 ? Math.Min(reach, at - low) : Math.Min(reach, high - at);
            double weight = Falloff(Math.Abs(distance), side);
            if (weight > 0.0)
                _guide[g] = (_guide[g].X + (weight * dx), _guide[g].Y + (weight * dy));
        }
    }

    /// <summary>The distance along the guide of its point nearest (x, y).</summary>
    private double AlongGuide(double x, double y, double[] along)
    {
        double best = double.MaxValue, at = 0.0;
        for (int g = 1; g < _guide.Count; g++)
        {
            (double ax, double ay) = _guide[g - 1];
            (double bx, double by) = _guide[g];
            double t = Geometry2D.ParameterOnSegmentClamped(ax, ay, bx, by, x, y);
            double d = Geometry2D.DistanceSquared(Geometry2D.Lerp(ax, bx, t), Geometry2D.Lerp(ay, by, t), x, y);
            if (d < best)
            {
                best = d;
                at = along[g - 1] + ((along[g] - along[g - 1]) * t);
            }
        }

        return at;
    }

    /// <summary>Releases a pin; the ends stay pinned.</summary>
    public bool RemovePin(int id)
    {
        int index = _pin.IndexOf(id);
        if (index < 0 || id == StartPin || id == EndPin)
            return false;

        _pin[index] = -1;
        Wake();
        return true;
    }

    public void RemoveAllPins()
    {
        for (int i = 0; i < _pin.Count; i++)
        {
            if (_pin[i] != StartPin && _pin[i] != EndPin)
                _pin[i] = -1;
        }

        Wake();
    }

    /// <summary>The nearest pin to a point within <paramref name="radius"/>, or -1.</summary>
    public int PinNear(double x, double y, double radius)
    {
        int best = -1;
        double bestDistance = radius * radius;
        for (int i = 0; i < _x.Count; i++)
        {
            if (_pin[i] < 0)
                continue;
            double d = Geometry2D.DistanceSquared(_x[i], _y[i], x, y);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = _pin[i];
            }
        }

        return best;
    }

    /// <summary>The distance from a point to the band, in plan.</summary>
    public double DistanceToBand(double x, double y)
    {
        double best = double.MaxValue;
        for (int i = 0; i < _x.Count - 1; i++)
        {
            double t = Geometry2D.ParameterOnSegmentClamped(_x[i], _y[i], _x[i + 1], _y[i + 1], x, y);
            best = Math.Min(best, Geometry2D.DistanceSquared(Geometry2D.Lerp(_x[i], _x[i + 1], t), Geometry2D.Lerp(_y[i], _y[i + 1], t), x, y));
        }

        return Math.Sqrt(best);
    }

    /// <summary>Back to the sketch as drawn, held by its ends only.</summary>
    public void Reset()
    {
        _holdLength = false;
        _guide = Resampled(_sketch, 0.5 * Spacing);
        LoadBand(_sketch, keepPins: false);
    }

    /// <summary>
    /// Replaces the band and the guide with <paramref name="plan"/> (a route found for the band), keeping
    /// the ends and every pin the plan passes within a station of.
    /// </summary>
    public void Reseed(IReadOnlyList<(double X, double Y)> plan)
    {
        if (plan.Count < 2 || !(Length(plan) > 0.0))
            return;

        _holdLength = true;
        _guide = Resampled(plan, 0.5 * Spacing);
        LoadBand(plan, keepPins: true);
    }

    /// <summary>
    /// Runs up to <paramref name="sweeps"/> sweeps, stopping early once settled. Returns the sweeps run.
    /// </summary>
    public int Step(int sweeps)
    {
        int run = 0;
        for (; run < sweeps && !IsSettled; run++)
            Sweep();
        return run;
    }

    /// <summary>The band priced by the route search's cost function: the score.</summary>
    public double Cost() => PlanCost(Stations());

    /// <summary>The sketch as drawn, priced the same way.</summary>
    public double SketchCost => _sketchCost;

    /// <summary>
    /// Scores the band: a grade-limited profile along it (held to the ground at its ends), the readout
    /// figures, the stretches it does not fit, and with <paramref name="fitCurve"/> the editable curve.
    /// </summary>
    public RouteRelaxEvaluation Evaluate(bool fitCurve)
    {
        RouteRequest request = _settings.ToRequest(Stations());
        double cell = _grid.CellSize;
        double spacing = RouteLegBuilder.StationSpacingCells * cell;
        var stations = new List<double>();
        var xs = new List<double>();
        var ys = new List<double>();
        double along = 0.0;
        for (int i = 0; i < _x.Count - 1; i++)
        {
            double length = Math.Sqrt(Geometry2D.DistanceSquared(_x[i], _y[i], _x[i + 1], _y[i + 1]));
            int pieces = Math.Max(1, (int)Math.Ceiling(length / spacing));
            for (int k = i == 0 ? 0 : 1; k <= pieces; k++)
            {
                double t = (double)k / pieces;
                xs.Add(Geometry2D.Lerp(_x[i], _x[i + 1], t));
                ys.Add(Geometry2D.Lerp(_y[i], _y[i + 1], t));
                stations.Add(along + (length * t));
            }

            along += length;
        }

        // The band stays on the terrain; a sample that still misses (a sliver at a hole's edge) takes the last ground.
        var ground = new double[xs.Count];
        double last = _grid.TrySample(xs[0], ys[0], out double first) ? first : 0.0;
        for (int k = 0; k < xs.Count; k++)
        {
            if (_grid.TrySample(xs[k], ys[k], out double z))
                last = z;
            ground[k] = last;
        }

        double startZ = ground[0], endZ = ground[^1];
        bool fits = GradeLimitedProfile.TryFit(stations, ground, _settings.MaxGrade, startZ, endZ, out double[] profile, out double required);
        if (!fits)
            GradeLimitedProfile.TryFit(stations, ground, required * (1.0 + 1e-9), startZ, endZ, out profile, out _);

        var points = new double[xs.Count * 3];
        for (int k = 0; k < xs.Count; k++)
        {
            points[k * 3] = xs[k];
            points[(k * 3) + 1] = ys[k];
            points[(k * 3) + 2] = profile[k];
        }

        RouteMetrics metrics = RouteSuggester.MeasureOnGrid(_grid, request, points);
        double cost = RouteLegBuilder.PolylineCost(_grid, _price, points);
        List<RouteRelaxIssue> issues = FindIssues(stations, xs, ys, ground, profile);
        if (!fits)
        {
            double length = stations[^1];
            issues.Insert(0, new RouteRelaxIssue(
                RouteRelaxIssueKind.TooShort, 0.0, length, xs[xs.Count / 2], ys[ys.Count / 2], 0.0, required,
                (Math.Abs(endZ - startZ) / _settings.MaxGrade) - length));
        }

        var evaluation = new RouteRelaxEvaluation(points, ground, metrics, cost, _sketchCost, issues, null, null, required, _settings);
        if (!fitCurve)
            return evaluation;

        (FittedRouteCurve curve, RouteMetrics curveMetrics) = FitCurve(evaluation);
        return evaluation.WithCurve(curve, curveMetrics);
    }

    /// <summary>
    /// The editable curve for an evaluation, fitted to the settings it was made with, and its figures. It
    /// reads only the evaluation and the height grid, never the band, so a host can fit on another thread
    /// while the band keeps stepping (a fit takes a fifth of a second to a second and a half).
    /// </summary>
    public (FittedRouteCurve Curve, RouteMetrics Metrics) FitCurve(RouteRelaxEvaluation evaluation)
    {
        RouteRelaxSettings settings = evaluation.Settings;
        RouteRequest request = settings.ToRequest(new[] { (evaluation.PointsXyz[0], evaluation.PointsXyz[1]) });
        double profileTolerance = (settings.ProfileTolerance > 0.0 ? settings.ProfileTolerance : 0.01 * _grid.CellSize) * settings.SimplicityScale;
        FittedRouteCurve curve = RouteCurveFitter.Fit(
            new[] { evaluation.PointsXyz }, request.EffectiveCurveTolerance, profileTolerance, settings.MaxGrade,
            settings.MinimumRadius, request.ReferenceRadius);
        return (curve, RouteSuggester.MeasureOnGrid(_grid, request, curve.PointsXyz));
    }

    /// <summary>
    /// Searches for a route through the band's ends and pins, kept near the band
    /// (<see cref="RouteRequest.Guide"/>), and on success reseeds the band with it: the search can find the
    /// switchbacks a band cannot slide into. The band is left as it was on failure.
    /// </summary>
    public RouteSuggestionResult HelpMe(CancellationToken cancellationToken = default)
    {
        var points = new List<(double X, double Y)>();
        for (int i = 0; i < _x.Count; i++)
        {
            if (_pin[i] >= 0)
                points.Add((_x[i], _y[i]));
        }

        List<(double X, double Y)> band = Stations();
        double length = Length(band);

        // Room to wander: a few bends, a share of the band, and a quarter of the length the climb needs, so a
        // straight sketch up a hillside has room for its switchbacks. Too tight a corridor came out a hair too
        // short to climb (8.13% on 8%, live); so it widens, and at the last searches freely.
        double climb = 0.0;
        if (_grid.TrySample(points[0].X, points[0].Y, out double startZ) && _grid.TrySample(points[^1].X, points[^1].Y, out double endZ))
            climb = Math.Abs(endZ - startZ) / _settings.MaxGrade;
        double width = Math.Max(Math.Max(2.0 * _settings.BendRadius, 2.0 * _settings.CorridorWidth), Math.Max(0.08 * length, 0.25 * climb));
        RouteSuggestionResult result = RouteSuggestionResult.Fail(RouteFailure.NotConnected);
        foreach (double guideWidth in new[] { width, 2.0 * width, 0.0 })
        {
            RouteRequest request = guideWidth > 0.0 ? _settings.ToRequest(points, band, guideWidth) : _settings.ToRequest(points);
            result = RouteSuggester.Suggest(_grid, request, cancellationToken);
            if (result.Succeeded || result.Failure is not (RouteFailure.ExceedsMaxGrade or RouteFailure.NotConnected))
                break;
        }

        if (result.Succeeded)
        {
            double[] found = result.Alternatives[0].PointsXyz;
            var plan = new List<(double X, double Y)>(found.Length / 3);
            for (int i = 0; i < found.Length / 3; i++)
                plan.Add((found[i * 3], found[(i * 3) + 1]));
            Reseed(plan);
        }

        return result;
    }

    private void Wake()
    {
        _quietSweeps = 0;
        for (int i = 0; i < _step.Length; i++)
            _step[i] = Math.Max(_step[i], InitialStep * Spacing);
    }

    /// <summary>
    /// One descent step of the whole band. The gradient (each station's partial derivative, by finite
    /// differences of the energy around it) is smoothed along the band, at a scale that cycles from a
    /// quarter of the band down to a single station, and the band moves along it as far as a line search on
    /// the total energy allows. Moving one station at a time against its own gradient was tried first: a
    /// station stepping sideways alone kinks the band, so bending refused every move worth making, and
    /// after 2000 sweeps a sketch over a hill had moved a few metres and come out wiggly (142 control points).
    /// Smoothed, the band moves as a whole, the way a drawn line is pushed (a Sobolev gradient).
    /// </summary>
    private void Sweep()
    {
        int count = _x.Count;
        double delta = 0.05 * Spacing;
        MeasureStretches();
        var gx = new double[count];
        var gy = new double[count];
        for (int i = 1; i < count - 1; i++)
        {
            if (_pin[i] >= 0)
                continue;

            double x = _x[i], y = _y[i];
            double here = Local(i, x, y);
            if (!double.IsFinite(here))
                continue;

            gx[i] = Slope(Local(i, x + delta, y), Local(i, x - delta, y), here);
            gy[i] = Slope(Local(i, x, y + delta), Local(i, x, y - delta), here);
        }

        double scale = SmoothingScales[_sweeps % SmoothingScales.Length];
        double smoothing = Math.Max(1.0, scale * scale * count * count);
        Smooth(gx, smoothing);
        Smooth(gy, smoothing);

        double largest = 0.0;
        for (int i = 0; i < count; i++)
            largest = Math.Max(largest, Math.Sqrt((gx[i] * gx[i]) + (gy[i] * gy[i])));

        int level = _sweeps % SmoothingScales.Length;
        double moved = 0.0;
        if (largest > 1e-15)
        {
            double energy = Energy();
            var x0 = _x.ToArray();
            var y0 = _y.ToArray();
            double step = _step[level];
            for (int attempt = 0; attempt < LineSearchAttempts; attempt++)
            {
                for (int i = 0; i < count; i++)
                {
                    _x[i] = x0[i] - (step * gx[i] / largest);
                    _y[i] = y0[i] - (step * gy[i] / largest);
                }

                if (Energy() < energy - (1e-12 * Math.Abs(energy)))
                {
                    moved = step;
                    _step[level] = Math.Min(step * 1.5, StepCaps[level] * Spacing);
                    break;
                }

                step *= 0.5;
                if (attempt == LineSearchAttempts - 1)
                {
                    for (int i = 0; i < count; i++)
                    {
                        _x[i] = x0[i];
                        _y[i] = y0[i];
                    }

                    _step[level] = Math.Max(step, MinimumStep * Spacing);
                }
            }

            if (moved > 0.0)
            {
                for (int i = 0; i < count; i++)
                    _guideSegment[i] = NearestGuideSegment(_x[i], _y[i], _guideSegment[i], GuideWindow);
            }
        }

        _sweeps++;
        _quietSweeps = moved < QuietMove * Spacing ? _quietSweeps + 1 : 0;
        if (_sweeps % ResampleEvery == 0 && NeedsResampling())
            LoadBand(Stations(), keepPins: true);

        double Slope(double plus, double minus, double centre)
        {
            if (double.IsFinite(plus) && double.IsFinite(minus))
                return (plus - minus) / (2.0 * delta);
            if (double.IsFinite(plus))
                return (plus - centre) / delta;
            if (double.IsFinite(minus))
                return (centre - minus) / delta;
            return 0.0;
        }
    }

    /// <summary>The scales the gradient is smoothed at, as shares of the band, cycled sweep by sweep.</summary>
    private static readonly double[] SmoothingScales = { 0.25, 0.08, 0.02, 0.0 };

    /// <summary>
    /// The longest move at each scale, in stations. A broad move can be long; a station-scale move longer
    /// than a fraction of the spacing lowered the energy by trading wiggles for cost, and a band seeded from
    /// a found route came out needing five times the control points.
    /// </summary>
    private static readonly double[] StepCaps = { 4.0, 2.0, 0.5, 0.1 };

    private const int LineSearchAttempts = 8;

    /// <summary>
    /// Solves <c>(I - s * Laplacian) d = g</c> along each stretch between pins, which hold still (d = 0): the
    /// gradient spread over about sqrt(s) stations. Tridiagonal, by the Thomas algorithm.
    /// </summary>
    private void Smooth(double[] g, double s)
    {
        int count = _x.Count;
        int start = 0;
        var c = new double[count];
        var d = new double[count];
        for (int i = 1; i < count; i++)
        {
            if (_pin[i] < 0)
                continue;

            // Free stations start + 1 .. i - 1, between pinned start and i.
            int first = start + 1, last = i - 1;
            if (last >= first)
            {
                double diagonal = 1.0 + (2.0 * s);
                c[first] = -s / diagonal;
                d[first] = g[first] / diagonal;
                for (int k = first + 1; k <= last; k++)
                {
                    double m = diagonal + (s * c[k - 1]);
                    c[k] = -s / m;
                    d[k] = (g[k] + (s * d[k - 1])) / m;
                }

                g[last] = d[last];
                for (int k = last - 1; k >= first; k--)
                    g[k] = d[k] - (c[k] * g[k + 1]);
            }

            g[i] = 0.0;
            start = i;
        }

        g[0] = 0.0;
    }

    /// <summary>
    /// The band's whole energy: cost, bending and fidelity over every station. It never rises from step to
    /// step; the cost alone can, a little, when the band trades it for a gentler bend.
    /// </summary>
    public double Energy()
    {
        int count = _x.Count;
        double energy = 0.0;
        for (int i = 0; i < count - 1; i++)
        {
            energy += SegmentCost(_x[i], _y[i], _x[i + 1], _y[i + 1]);
            if (!double.IsFinite(energy))
                return double.PositiveInfinity;
        }

        for (int i = 1; i < count - 1; i++)
            energy += Bend(_x[i - 1], _y[i - 1], _x[i], _y[i], _x[i + 1], _y[i + 1]);

        if (_settings.Fidelity > 0.0)
        {
            for (int i = 0; i < count; i++)
                energy += FidelityAt(i, _x[i], _y[i]);
        }

        MeasureStretches();
        for (int s = 0; s < _stretchLength.Count; s++)
            energy += Shortfall(s, _stretchLength[s]);

        return energy;
    }

    /// <summary>
    /// Each stretch between pins needs at least its height difference over the limit in length. Nothing else
    /// in the energy knows it: bending is cheaper straightened, so a band seeded with switchbacks unwound them
    /// until it was 50 m too short to climb between its ends, and the profile then broke the grade.
    /// </summary>
    private double Shortfall(int stretch, double length)
    {
        double missing = _stretchNeed[stretch] - length;
        return missing > 0.0 ? ShortfallWeight * BendWeight * missing * missing / Spacing : 0.0;
    }

    private const double ShortfallWeight = 20.0;

    private const double LengthMargin = 0.05;

    /// <summary>The stretch (between successive pins) of each station's following segment, and each stretch's length and need.</summary>
    private void MeasureStretches()
    {
        int count = _x.Count;
        _stretchOf.Clear();
        _stretchLength.Clear();
        _stretchNeed.Clear();
        int stretch = -1;
        double startZ = 0.0;
        for (int i = 0; i < count; i++)
        {
            if (_pin[i] >= 0)
            {
                double z = _grid.TrySample(_x[i], _y[i], out double ground) ? ground : startZ;
                // A little over the bare need: the penalty settles a hair short of its target, and the editable
                // curve, smoothed, is a little shorter than the band.
                if (stretch >= 0 && _holdLength)
                    _stretchNeed[stretch] = Math.Abs(z - startZ) / _settings.MaxGrade * (1.0 + LengthMargin);
                if (i < count - 1)
                {
                    stretch++;
                    _stretchLength.Add(0.0);
                    _stretchNeed.Add(0.0);
                }

                startZ = z;
            }

            _stretchOf.Add(Math.Max(stretch, 0));
            if (i < count - 1)
                _stretchLength[stretch] += Math.Sqrt(Geometry2D.DistanceSquared(_x[i], _y[i], _x[i + 1], _y[i + 1]));
        }
    }

    private readonly List<int> _stretchOf = new();
    private readonly List<double> _stretchLength = new();
    private readonly List<double> _stretchNeed = new();

    /// <summary>Every term of the energy that involves station <paramref name="i"/>, with it at (x, y).</summary>
    private double Local(int i, double x, double y)
    {
        int count = _x.Count;
        double energy = 0.0;
        if (i > 0)
            energy += SegmentCost(_x[i - 1], _y[i - 1], x, y);
        if (i < count - 1)
            energy += SegmentCost(x, y, _x[i + 1], _y[i + 1]);
        if (!double.IsFinite(energy))
            return double.PositiveInfinity;

        if (i > 1)
            energy += Bend(_x[i - 2], _y[i - 2], _x[i - 1], _y[i - 1], x, y);
        if (i > 0 && i < count - 1)
            energy += Bend(_x[i - 1], _y[i - 1], x, y, _x[i + 1], _y[i + 1]);
        if (i < count - 2)
            energy += Bend(x, y, _x[i + 1], _y[i + 1], _x[i + 2], _y[i + 2]);

        // Fidelity at i weighs its distance by the length either side, which moves with i's neighbours too.
        if (_settings.Fidelity > 0.0)
        {
            for (int j = Math.Max(0, i - 1); j <= Math.Min(count - 1, i + 1); j++)
                energy += j == i ? FidelityAt(i, x, y) : FidelityAt(j, _x[j], _y[j], i, x, y);
        }

        // A free station lies inside one stretch; moving it changes that stretch's length by its two segments.
        if (i > 0 && i < count - 1 && _stretchOf.Count == count)
        {
            int stretch = _stretchOf[i];
            double before = Math.Sqrt(Geometry2D.DistanceSquared(_x[i - 1], _y[i - 1], _x[i], _y[i])) +
                Math.Sqrt(Geometry2D.DistanceSquared(_x[i], _y[i], _x[i + 1], _y[i + 1]));
            double after = Math.Sqrt(Geometry2D.DistanceSquared(_x[i - 1], _y[i - 1], x, y)) +
                Math.Sqrt(Geometry2D.DistanceSquared(x, y, _x[i + 1], _y[i + 1]));
            energy += Shortfall(stretch, _stretchLength[stretch] - before + after);
        }

        return energy;
    }

    /// <summary>
    /// <c>Fidelity * l * (d / D)^2</c> at station <paramref name="j"/> placed at (x, y), l being half the
    /// band either side of it, with station <paramref name="moved"/> (if any) at (mx, my) instead.
    /// </summary>
    private double FidelityAt(int j, double x, double y, int moved = -1, double mx = 0.0, double my = 0.0)
    {
        int count = _x.Count;
        double before = 0.0, after = 0.0;
        if (j > 0)
        {
            (double px, double py) = j - 1 == moved ? (mx, my) : (_x[j - 1], _y[j - 1]);
            before = Math.Sqrt(Geometry2D.DistanceSquared(px, py, x, y));
        }

        if (j < count - 1)
        {
            (double nx, double ny) = j + 1 == moved ? (mx, my) : (_x[j + 1], _y[j + 1]);
            after = Math.Sqrt(Geometry2D.DistanceSquared(x, y, nx, ny));
        }

        int segment = NearestGuideSegment(x, y, _guideSegment[j], GuideWindow);
        double distance = Math.Sqrt(GuideDistanceSquared(segment, x, y)) / _settings.FidelityScale;
        return _settings.Fidelity * BendWeight * 0.5 * (before + after) * distance * distance;
    }

    private double BendWeight => _settings.DistanceWeight > 0.0 ? _settings.DistanceWeight : 1.0;

    private double SegmentCost(double ax, double ay, double bx, double by) =>
        RouteLegBuilder.ChordCost(
            _grid, _price, (ax, ay), (bx, by), RouteLegBuilder.StationSpacingCells * _grid.CellSize, RouteLegBuilder.ChordGroundWeight);

    /// <summary>Bending at b between a and c: <c>R^2 theta^2 / l</c>, plus the penalty under the minimum radius.</summary>
    private double Bend(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double ux = bx - ax, uy = by - ay, vx = cx - bx, vy = cy - by;
        double lu = Math.Sqrt((ux * ux) + (uy * uy)), lv = Math.Sqrt((vx * vx) + (vy * vy));
        if (!(lu > 0.0) || !(lv > 0.0))
            return 0.0;

        double theta = Math.Atan2(Math.Abs((ux * vy) - (uy * vx)), (ux * vx) + (uy * vy));
        double length = 0.5 * (lu + lv);
        double radius = _settings.BendRadius;
        double energy = BendWeight * radius * radius * theta * theta / length;
        if (_settings.MinimumRadius > 0.0)
        {
            double over = (theta / length * _settings.MinimumRadius) - 1.0;
            if (over > 0.0)
                energy += RadiusPenalty * BendWeight * length * over * over;
        }

        return energy;
    }

    private int NearestGuideSegment(double x, double y, int around, int window)
    {
        int best = Math.Clamp(around, 0, _guide.Count - 2);
        double bestDistance = GuideDistanceSquared(best, x, y);
        for (int s = Math.Max(0, around - window); s <= Math.Min(_guide.Count - 2, around + window); s++)
        {
            double d = GuideDistanceSquared(s, x, y);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = s;
            }
        }

        return best;
    }

    private double GuideDistanceSquared(int segment, double x, double y)
    {
        (double ax, double ay) = _guide[segment];
        (double bx, double by) = _guide[segment + 1];
        double t = Geometry2D.ParameterOnSegmentClamped(ax, ay, bx, by, x, y);
        return Geometry2D.DistanceSquared(Geometry2D.Lerp(ax, bx, t), Geometry2D.Lerp(ay, by, t), x, y);
    }

    private bool NeedsResampling()
    {
        for (int i = 1; i < _x.Count; i++)
        {
            double length = Math.Sqrt(Geometry2D.DistanceSquared(_x[i - 1], _y[i - 1], _x[i], _y[i]));
            if (length > 1.6 * Spacing || (length < 0.4 * Spacing && _pin[i] < 0 && _pin[i - 1] < 0))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Lays the band along <paramref name="line"/> at even spacing between its pins. With
    /// <paramref name="keepPins"/>, every current pin is carried to the line's nearest point (the ends to
    /// its ends), and the line is cut there; otherwise only the ends are pinned.
    /// </summary>
    private void LoadBand(IReadOnlyList<(double X, double Y)> line, bool keepPins)
    {
        // Pins with their positions on the line, as distance along it, ends first and last.
        var cumulative = new double[line.Count];
        for (int i = 1; i < line.Count; i++)
            cumulative[i] = cumulative[i - 1] + Math.Sqrt(Geometry2D.DistanceSquared(line[i - 1].X, line[i - 1].Y, line[i].X, line[i].Y));
        double total = cumulative[^1];

        var anchors = new List<(double Along, int Id, double X, double Y)> { (0.0, StartPin, line[0].X, line[0].Y) };
        if (keepPins)
        {
            for (int i = 0; i < _x.Count; i++)
            {
                if (_pin[i] < 0 || _pin[i] == StartPin || _pin[i] == EndPin)
                    continue;
                double along = Project(_x[i], _y[i]);
                anchors.Add((along, _pin[i], _x[i], _y[i]));
            }
        }

        anchors.Add((total, EndPin, line[^1].X, line[^1].Y));
        anchors = anchors.OrderBy(anchor => anchor.Along).ThenBy(anchor => anchor.Id == EndPin ? 1 : 0).ToList();
        // The ends are where they are; a pin projecting onto an end is dropped to keep stations apart.
        anchors[0] = (0.0, StartPin, line[0].X, line[0].Y);
        anchors[^1] = (total, EndPin, line[^1].X, line[^1].Y);
        for (int a = anchors.Count - 2; a >= 1; a--)
        {
            if (anchors[a].Id == StartPin || anchors[a].Id == EndPin ||
                anchors[a].Along - anchors[a - 1].Along < 0.25 * Spacing || anchors[a + 1].Along - anchors[a].Along < 0.25 * Spacing)
            {
                anchors.RemoveAt(a);
            }
        }

        _x.Clear();
        _y.Clear();
        _pin.Clear();
        _guideSegment.Clear();
        for (int level = 0; level < _step.Length; level++)
            _step[level] = Math.Max(_step[level], InitialStep * Spacing);
        Add(anchors[0].X, anchors[0].Y, anchors[0].Id);
        for (int a = 1; a < anchors.Count; a++)
        {
            double from = anchors[a - 1].Along, to = anchors[a].Along;
            int pieces = Math.Max(1, (int)Math.Round((to - from) / Spacing));
            for (int k = 1; k < pieces; k++)
            {
                (double x, double y) = PointAt(from + ((to - from) * k / pieces));
                Add(x, y, -1);
            }

            Add(anchors[a].X, anchors[a].Y, anchors[a].Id);
        }

        int segment = 0;
        for (int i = 0; i < _x.Count; i++)
        {
            segment = NearestGuideSegment(_x[i], _y[i], segment, _guide.Count);
            _guideSegment[i] = segment;
        }

        _quietSweeps = 0;

        void Add(double x, double y, int id)
        {
            _x.Add(x);
            _y.Add(y);
            _pin.Add(id);
            _guideSegment.Add(0);
        }

        (double X, double Y) PointAt(double along)
        {
            int index = Array.BinarySearch(cumulative, along);
            if (index < 0)
                index = ~index;
            index = Math.Clamp(index, 1, line.Count - 1);
            double span = cumulative[index] - cumulative[index - 1];
            double t = span > 0.0 ? (along - cumulative[index - 1]) / span : 0.0;
            return (Geometry2D.Lerp(line[index - 1].X, line[index].X, t), Geometry2D.Lerp(line[index - 1].Y, line[index].Y, t));
        }

        double Project(double x, double y)
        {
            double best = double.MaxValue, at = 0.0;
            for (int i = 1; i < line.Count; i++)
            {
                double t = Geometry2D.ParameterOnSegmentClamped(line[i - 1].X, line[i - 1].Y, line[i].X, line[i].Y, x, y);
                double d = Geometry2D.DistanceSquared(Geometry2D.Lerp(line[i - 1].X, line[i].X, t), Geometry2D.Lerp(line[i - 1].Y, line[i].Y, t), x, y);
                if (d < best)
                {
                    best = d;
                    at = cumulative[i - 1] + ((cumulative[i] - cumulative[i - 1]) * t);
                }
            }

            return at;
        }
    }

    /// <summary>
    /// Stretches cut or filled deeper than the reporting depth, merged where they touch. One across ground
    /// steeper than the limit, which only more route can climb, is a <see cref="RouteRelaxIssueKind.Climb"/>
    /// with the extra length it needs; otherwise it is the cut or fill at its deepest point.
    /// </summary>
    private List<RouteRelaxIssue> FindIssues(List<double> stations, List<double> xs, List<double> ys, double[] ground, double[] profile)
    {
        var issues = new List<RouteRelaxIssue>();
        double deep = _settings.DeepDepth;
        double shallow = 0.1 * deep;
        int count = stations.Count;
        int k = 0;
        while (k < count)
        {
            if (Math.Abs(profile[k] - ground[k]) <= deep)
            {
                k++;
                continue;
            }

            // Grow the stretch outwards while the route is still off the ground, and across any run it touches.
            int first = k, last = k;
            while (first > 0 && Math.Abs(profile[first - 1] - ground[first - 1]) > shallow)
                first--;
            while (true)
            {
                while (last < count - 1 && Math.Abs(profile[last + 1] - ground[last + 1]) > shallow)
                    last++;
                // A climb crosses the ground once, from fill to cut: step over a crossing a couple of samples wide.
                int resume = last + 1;
                while (resume < count && resume <= last + 3 && Math.Abs(profile[resume] - ground[resume]) <= shallow)
                    resume++;
                if (resume < count && resume <= last + 3 && Math.Abs(profile[resume] - ground[resume]) > shallow)
                {
                    last = resume;
                    continue;
                }

                break;
            }

            int deepest = first;
            for (int j = first; j <= last; j++)
            {
                if (Math.Abs(profile[j] - ground[j]) > Math.Abs(profile[deepest] - ground[deepest]))
                    deepest = j;
            }

            double window = stations[last] - stations[first];
            double rise = Math.Abs(ground[last] - ground[first]);
            double groundGrade = window > 0.0 ? rise / window : 0.0;
            double extra = (rise / _settings.MaxGrade) - window;
            double depth = profile[deepest] - ground[deepest];
            RouteRelaxIssueKind kind = groundGrade > _settings.MaxGrade * 1.2 && extra > 0.25 * window
                ? RouteRelaxIssueKind.Climb
                : depth > 0.0 ? RouteRelaxIssueKind.Fill : RouteRelaxIssueKind.Cut;
            issues.Add(new RouteRelaxIssue(
                kind, stations[first], stations[last], xs[deepest], ys[deepest], Math.Abs(depth), groundGrade,
                kind == RouteRelaxIssueKind.Climb ? extra : 0.0));
            k = last + 1;
        }

        return issues;
    }

    private List<(double X, double Y)> Stations()
    {
        var stations = new List<(double X, double Y)>(_x.Count);
        for (int i = 0; i < _x.Count; i++)
            stations.Add((_x[i], _y[i]));
        return stations;
    }

    private double PlanCost(IReadOnlyList<(double X, double Y)> plan)
    {
        var xyz = new double[plan.Count * 3];
        for (int i = 0; i < plan.Count; i++)
        {
            xyz[i * 3] = plan[i].X;
            xyz[(i * 3) + 1] = plan[i].Y;
        }

        return RouteLegBuilder.PolylineCost(_grid, _price, xyz);
    }

    private static double Length(IReadOnlyList<(double X, double Y)> line)
    {
        double length = 0.0;
        for (int i = 1; i < line.Count; i++)
            length += Math.Sqrt(Geometry2D.DistanceSquared(line[i - 1].X, line[i - 1].Y, line[i].X, line[i].Y));
        return length;
    }

    /// <summary>The line with extra points so no two are further apart than <paramref name="spacing"/>.</summary>
    private static List<(double X, double Y)> Resampled(IReadOnlyList<(double X, double Y)> line, double spacing)
    {
        var points = new List<(double X, double Y)> { line[0] };
        for (int i = 1; i < line.Count; i++)
        {
            double length = Math.Sqrt(Geometry2D.DistanceSquared(line[i - 1].X, line[i - 1].Y, line[i].X, line[i].Y));
            int pieces = Math.Max(1, (int)Math.Ceiling(length / spacing));
            for (int k = 1; k <= pieces; k++)
                points.Add((Geometry2D.Lerp(line[i - 1].X, line[i].X, (double)k / pieces), Geometry2D.Lerp(line[i - 1].Y, line[i].Y, (double)k / pieces)));
        }

        return points;
    }
}
