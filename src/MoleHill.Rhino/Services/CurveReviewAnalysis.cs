// Samples an inspected curve into a station profile with grade, kink, event, radius and terrain diagnostics.
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal enum CurveReviewEventKind
{
    Crest,
    Sag,
    VerticalBreak,
    TerrainGap,
    SharpRadius
}

/// <summary>One station along the inspected curve, with the terrain elevation underneath it when available.</summary>
internal readonly record struct CurveReviewSample(double Station, Point3d Point, double TerrainZ, bool HasTerrain)
{
    /// <summary>Curve elevation minus terrain elevation: positive is above grade (fill), negative is below (cut).</summary>
    public double TerrainDelta => HasTerrain ? Point.Z - TerrainZ : 0.0;

    public Point3d TerrainPoint => new(Point.X, Point.Y, TerrainZ);
}

/// <summary>A kink-to-kink (or span-to-span) stretch of the curve carrying a single representative grade.</summary>
internal readonly record struct CurveReviewSpan(
    double StartStation,
    double EndStation,
    Point3d Start,
    Point3d End,
    Point3d Label,
    double Grade,
    double PlanLength);

/// <summary>A contiguous stretch that violates a user limit (grade or plan radius).</summary>
internal sealed class CurveReviewRun
{
    public required double StartStation { get; init; }
    public required double EndStation { get; init; }
    public required double PlanLength { get; init; }
    public required double PeakValue { get; init; }
    public required Point3d PeakPoint { get; init; }
    public required Point3d[] Path { get; init; }
}

internal readonly record struct CurveReviewEvent(CurveReviewEventKind Kind, double Station, Point3d Point, string Label);

/// <summary>Everything the inspector panel reports and the inspector conduit draws.</summary>
internal sealed class CurveReviewAnalysis
{
    public required IReadOnlyList<CurveReviewSample> Samples { get; init; }

    /// <summary>Grade in percent for the interval between sample i and i + 1; NaN where the interval is vertical.</summary>
    public required IReadOnlyList<double> IntervalGrades { get; init; }

    public required IReadOnlyList<CurveReviewSpan> Spans { get; init; }
    public required IReadOnlyList<CurveReviewEvent> Events { get; init; }
    public required IReadOnlyList<CurveReviewRun> GradeExceedances { get; init; }
    public required IReadOnlyList<CurveReviewRun> RadiusViolations { get; init; }

    public required double PlanLength { get; init; }
    public required double Length3d { get; init; }
    public required double StartElevation { get; init; }
    public required double EndElevation { get; init; }
    public required double MinimumGrade { get; init; }
    public required double MaximumGrade { get; init; }
    public required double AverageGrade { get; init; }
    public required double SteepestGrade { get; init; }
    public required double SteepestGradeStation { get; init; }
    public required int ReversalCount { get; init; }
    public required int VerticalBreakCount { get; init; }
    public required int KinkCount { get; init; }
    public required double MinimumPlanRadius { get; init; }
    public required double MinimumPlanRadiusStation { get; init; }
    public required int TerrainSamples { get; init; }
    public required int TerrainMisses { get; init; }
    public required double MaximumFill { get; init; }
    public required double MaximumFillStation { get; init; }
    public required double MaximumCut { get; init; }
    public required double MaximumCutStation { get; init; }
    public required Point3d MaximumFillPoint { get; init; }
    public required Point3d MaximumCutPoint { get; init; }
    public required BoundingBox Bounds { get; init; }
    public required double? MaximumGradeLimit { get; init; }
    public required double? MinimumRadiusLimit { get; init; }

    public bool HasTerrain => TerrainSamples > 0;
    public bool GradeFails => GradeExceedances.Count > 0;
    public bool RadiusFails => RadiusViolations.Count > 0;

    /// <summary>Scale used to map an absolute grade onto the review color ramp.</summary>
    public double GradeColorScale => MaximumGradeLimit is > 0.0
        ? MaximumGradeLimit.Value
        : Math.Max(Math.Max(Math.Abs(MinimumGrade), Math.Abs(MaximumGrade)), 1.0);
}

internal static class CurveReviewAnalyzer
{
    private const int BaseSampleCount = 200;
    private const int MaximumSampleCount = 400;
    private const double VerticalBreakThresholdPercent = 10.0;
    private const double GradeNoiseFloorPercent = 0.05;

    /// <summary>
    /// Builds the full review model. <paramref name="terrainMesh"/> is sampled but never stored or mutated.
    /// </summary>
    public static CurveReviewAnalysis? Build(
        RhinoDoc doc,
        Curve curve,
        Mesh? terrainMesh,
        double? maximumGradePercent,
        double? minimumPlanRadius,
        out string? error)
    {
        error = null;
        if (curve == null || !curve.IsValid)
        {
            error = "Curve was deleted or is no longer valid.";
            return null;
        }

        double tolerance = doc.ModelAbsoluteTolerance;
        double length = curve.GetLength();
        double planLength = GeometryCommandAlgorithms.CalculatePlanLength(curve, curve.Domain.T0, curve.Domain.T1);
        if (!double.IsFinite(length) || !double.IsFinite(planLength) || planLength <= tolerance)
        {
            error = "Curve is too short to inspect.";
            return null;
        }

        List<double> kinkParameters = CollectKinkParameters(curve);
        List<double> stationParameters = BuildStationParameters(curve, length, kinkParameters);
        var samples = new List<CurveReviewSample>(stationParameters.Count);
        var bounds = BoundingBox.Empty;
        int terrainHits = 0;
        int terrainMisses = 0;
        foreach (double parameter in stationParameters)
        {
            Point3d point = curve.PointAt(parameter);
            double station = curve.GetLength(new Interval(curve.Domain.T0, parameter));
            double terrainZ = 0.0;
            bool hasTerrain = false;
            if (terrainMesh != null)
            {
                if (TerrainMeshProjection.TryProjectPointAlongWorldZ(
                        terrainMesh, point, tolerance, out Point3d projected))
                {
                    terrainZ = projected.Z;
                    hasTerrain = true;
                    terrainHits++;
                }
                else
                {
                    terrainMisses++;
                }
            }

            samples.Add(new CurveReviewSample(station, point, terrainZ, hasTerrain));
            bounds.Union(point);
            if (hasTerrain)
                bounds.Union(new Point3d(point.X, point.Y, terrainZ));
        }

        var intervalGrades = new double[Math.Max(samples.Count - 1, 0)];
        double weightedGradeTotal = 0.0;
        double totalPlanDistance = 0.0;
        double minimumGrade = double.MaxValue;
        double maximumGrade = double.MinValue;
        double steepestGrade = 0.0;
        double steepestStation = 0.0;
        for (int i = 0; i < intervalGrades.Length; i++)
        {
            double planDistance = GeometryCommandAlgorithms.CalculatePlanDistance(samples[i].Point, samples[i + 1].Point);
            if (planDistance <= tolerance)
            {
                intervalGrades[i] = double.NaN;
                continue;
            }

            double grade = (samples[i + 1].Point.Z - samples[i].Point.Z) / planDistance * 100.0;
            intervalGrades[i] = grade;
            weightedGradeTotal += grade * planDistance;
            totalPlanDistance += planDistance;
            minimumGrade = Math.Min(minimumGrade, grade);
            maximumGrade = Math.Max(maximumGrade, grade);
            if (Math.Abs(grade) > Math.Abs(steepestGrade))
            {
                steepestGrade = grade;
                steepestStation = 0.5 * (samples[i].Station + samples[i + 1].Station);
            }
        }

        if (minimumGrade > maximumGrade)
        {
            minimumGrade = 0.0;
            maximumGrade = 0.0;
        }

        double[] smoothedGrades = SmoothGrades(intervalGrades);
        double[] planRadii = ComputePlanRadii(samples);
        List<CurveReviewEvent> events = CollectEvents(samples, smoothedGrades, planRadii, minimumPlanRadius);
        List<CurveReviewRun> gradeRuns = maximumGradePercent is > 0.0
            ? CollectRuns(samples, i => Math.Abs(intervalGrades[i]), maximumGradePercent.Value, aboveLimit: true)
            : new List<CurveReviewRun>();
        List<CurveReviewRun> radiusRuns = minimumPlanRadius is > 0.0
            ? CollectRuns(
                samples,
                i => 0.5 * (SafeRadius(planRadii, i) + SafeRadius(planRadii, i + 1)),
                minimumPlanRadius.Value,
                aboveLimit: false)
            : new List<CurveReviewRun>();

        double minimumRadius = double.PositiveInfinity;
        double minimumRadiusStation = 0.0;
        for (int i = 0; i < planRadii.Length; i++)
        {
            if (double.IsFinite(planRadii[i]) && planRadii[i] < minimumRadius)
            {
                minimumRadius = planRadii[i];
                minimumRadiusStation = samples[i].Station;
            }
        }

        ResolveTerrainExtremes(
            samples,
            out double maximumFill, out double maximumFillStation, out Point3d maximumFillPoint,
            out double maximumCut, out double maximumCutStation, out Point3d maximumCutPoint);

        return new CurveReviewAnalysis
        {
            Samples = samples,
            IntervalGrades = intervalGrades,
            Spans = BuildSpans(curve, samples, kinkParameters, tolerance),
            Events = events,
            GradeExceedances = gradeRuns,
            RadiusViolations = radiusRuns,
            PlanLength = planLength,
            Length3d = length,
            StartElevation = curve.PointAtStart.Z,
            EndElevation = curve.PointAtEnd.Z,
            MinimumGrade = minimumGrade,
            MaximumGrade = maximumGrade,
            AverageGrade = totalPlanDistance <= RhinoMath.ZeroTolerance ? 0.0 : weightedGradeTotal / totalPlanDistance,
            SteepestGrade = steepestGrade,
            SteepestGradeStation = steepestStation,
            ReversalCount = events.Count(item => item.Kind is CurveReviewEventKind.Crest or CurveReviewEventKind.Sag),
            VerticalBreakCount = events.Count(item => item.Kind == CurveReviewEventKind.VerticalBreak),
            KinkCount = kinkParameters.Count,
            MinimumPlanRadius = minimumRadius,
            MinimumPlanRadiusStation = minimumRadiusStation,
            TerrainSamples = terrainHits,
            TerrainMisses = terrainMisses,
            MaximumFill = maximumFill,
            MaximumFillStation = maximumFillStation,
            MaximumCut = maximumCut,
            MaximumCutStation = maximumCutStation,
            MaximumFillPoint = maximumFillPoint,
            MaximumCutPoint = maximumCutPoint,
            Bounds = bounds,
            MaximumGradeLimit = maximumGradePercent,
            MinimumRadiusLimit = minimumPlanRadius
        };
    }

    /// <summary>G1 discontinuities: the vertical/horizontal PIs a designer actually cares about.</summary>
    private static List<double> CollectKinkParameters(Curve curve)
    {
        var parameters = new List<double>();
        double start = curve.Domain.T0;
        while (parameters.Count < 512 &&
               curve.GetNextDiscontinuity(Continuity.G1_locus_continuous, start, curve.Domain.T1, out double parameter))
        {
            parameters.Add(parameter);
            start = parameter;
        }

        return parameters;
    }

    private static List<double> BuildStationParameters(Curve curve, double length, IReadOnlyList<double> kinkParameters)
    {
        int sampleCount = Math.Min(BaseSampleCount + kinkParameters.Count * 4, MaximumSampleCount);
        var parameters = new List<double>(sampleCount + kinkParameters.Count + 1);
        for (int i = 0; i <= sampleCount; i++)
        {
            double distance = length * i / sampleCount;
            if (!curve.LengthParameter(distance, out double parameter))
                parameter = i == 0 ? curve.Domain.T0 : curve.Domain.T1;
            parameters.Add(parameter);
        }

        parameters.AddRange(kinkParameters);
        parameters.Sort();

        double minimumSpacing = curve.Domain.Length * 1e-7;
        var result = new List<double>(parameters.Count) { parameters[0] };
        for (int i = 1; i < parameters.Count; i++)
        {
            if (parameters[i] - result[^1] > minimumSpacing)
                result.Add(parameters[i]);
        }

        return result;
    }

    /// <summary>
    /// Spans are kink-to-kink where the curve has kinks, and knot-span boundaries where it is smooth,
    /// so a single grade label per stretch always reads as a real design segment.
    /// </summary>
    private static List<CurveReviewSpan> BuildSpans(
        Curve curve,
        IReadOnlyList<CurveReviewSample> samples,
        IReadOnlyList<double> kinkParameters,
        double tolerance)
    {
        var breakParameters = new List<double>(kinkParameters);
        if (breakParameters.Count == 0)
        {
            int spanCount = Math.Min(curve.SpanCount, 24);
            for (int i = 1; i < spanCount; i++)
                breakParameters.Add(curve.SpanDomain(i).T0);
        }

        breakParameters.Insert(0, curve.Domain.T0);
        breakParameters.Add(curve.Domain.T1);
        breakParameters.Sort();

        var spans = new List<CurveReviewSpan>(breakParameters.Count);
        for (int i = 1; i < breakParameters.Count; i++)
        {
            double t0 = breakParameters[i - 1];
            double t1 = breakParameters[i];
            if (t1 - t0 <= RhinoMath.ZeroTolerance)
                continue;

            double spanPlanLength = GeometryCommandAlgorithms.CalculatePlanLength(curve, t0, t1);
            if (spanPlanLength <= tolerance)
                continue;

            Point3d start = curve.PointAt(t0);
            Point3d end = curve.PointAt(t1);
            double grade = (end.Z - start.Z) / spanPlanLength * 100.0;
            spans.Add(new CurveReviewSpan(
                StationAt(samples, start),
                StationAt(samples, end),
                start,
                end,
                curve.PointAt(0.5 * (t0 + t1)),
                grade,
                spanPlanLength));
        }

        return spans;
    }

    private static double StationAt(IReadOnlyList<CurveReviewSample> samples, Point3d point)
    {
        double best = 0.0;
        double bestDistance = double.MaxValue;
        foreach (CurveReviewSample sample in samples)
        {
            double distance = sample.Point.DistanceToSquared(point);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = sample.Station;
            }
        }

        return best;
    }

    /// <summary>Three-point moving average so crest/sag detection ignores sampling noise on smooth curves.</summary>
    private static double[] SmoothGrades(IReadOnlyList<double> grades)
    {
        var smoothed = new double[grades.Count];
        for (int i = 0; i < grades.Count; i++)
        {
            double total = 0.0;
            int count = 0;
            for (int offset = -1; offset <= 1; offset++)
            {
                int index = i + offset;
                if (index < 0 || index >= grades.Count || double.IsNaN(grades[index]))
                    continue;
                total += grades[index];
                count++;
            }

            smoothed[i] = count == 0 ? double.NaN : total / count;
        }

        return smoothed;
    }

    /// <summary>Plan radius at each sample from the circumradius of its XY neighbours.</summary>
    private static double[] ComputePlanRadii(IReadOnlyList<CurveReviewSample> samples)
    {
        var radii = new double[samples.Count];
        for (int i = 0; i < radii.Length; i++)
            radii[i] = double.PositiveInfinity;

        for (int i = 1; i < samples.Count - 1; i++)
        {
            Point3d p0 = Flatten(samples[i - 1].Point);
            Point3d p1 = Flatten(samples[i].Point);
            Point3d p2 = Flatten(samples[i + 1].Point);
            double a = p0.DistanceTo(p1);
            double b = p1.DistanceTo(p2);
            double c = p0.DistanceTo(p2);
            if (a <= RhinoMath.ZeroTolerance || b <= RhinoMath.ZeroTolerance || c <= RhinoMath.ZeroTolerance)
                continue;

            double area = 0.5 * Math.Abs(Vector3d.CrossProduct(p1 - p0, p2 - p0).Z);
            if (area <= RhinoMath.ZeroTolerance)
                continue;

            radii[i] = a * b * c / (4.0 * area);
        }

        return radii;
    }

    private static Point3d Flatten(Point3d point) => new(point.X, point.Y, 0.0);

    private static double SafeRadius(IReadOnlyList<double> radii, int index) =>
        index >= 0 && index < radii.Count ? radii[index] : double.PositiveInfinity;

    private static List<CurveReviewEvent> CollectEvents(
        IReadOnlyList<CurveReviewSample> samples,
        IReadOnlyList<double> smoothedGrades,
        IReadOnlyList<double> planRadii,
        double? minimumPlanRadius)
    {
        var events = new List<CurveReviewEvent>();
        double? previous = null;
        for (int i = 0; i < smoothedGrades.Count; i++)
        {
            double grade = smoothedGrades[i];
            if (double.IsNaN(grade))
                continue;

            if (previous.HasValue)
            {
                if (Math.Abs(grade - previous.Value) > VerticalBreakThresholdPercent)
                {
                    events.Add(new CurveReviewEvent(
                        CurveReviewEventKind.VerticalBreak,
                        samples[i].Station,
                        samples[i].Point,
                        $"break {previous.Value:F1}% to {grade:F1}%"));
                }
                else if (Math.Abs(previous.Value) > GradeNoiseFloorPercent &&
                         Math.Abs(grade) > GradeNoiseFloorPercent &&
                         Math.Sign(previous.Value) != Math.Sign(grade))
                {
                    bool crest = previous.Value > 0.0;
                    events.Add(new CurveReviewEvent(
                        crest ? CurveReviewEventKind.Crest : CurveReviewEventKind.Sag,
                        samples[i].Station,
                        samples[i].Point,
                        $"{(crest ? "crest" : "sag")} Z {samples[i].Point.Z:F2}"));
                }
            }

            previous = grade;
        }

        if (minimumPlanRadius is > 0.0)
        {
            for (int i = 1; i < planRadii.Count - 1; i++)
            {
                double radius = planRadii[i];
                if (!double.IsFinite(radius) || radius >= minimumPlanRadius.Value)
                    continue;
                if (radius > planRadii[i - 1] || radius > planRadii[i + 1])
                    continue;

                events.Add(new CurveReviewEvent(
                    CurveReviewEventKind.SharpRadius,
                    samples[i].Station,
                    samples[i].Point,
                    $"R {radius:F1} < {minimumPlanRadius.Value:F1}"));
            }
        }

        int gapStart = -1;
        for (int i = 0; i <= samples.Count; i++)
        {
            bool missing = i < samples.Count && !samples[i].HasTerrain;
            if (missing && gapStart < 0)
            {
                gapStart = i;
            }
            else if (!missing && gapStart >= 0)
            {
                int middle = (gapStart + i - 1) / 2;
                events.Add(new CurveReviewEvent(
                    CurveReviewEventKind.TerrainGap,
                    samples[middle].Station,
                    samples[middle].Point,
                    "off terrain"));
                gapStart = -1;
            }
        }

        return events;
    }

    /// <summary>Merges consecutive violating intervals into runs so one label covers one stretch.</summary>
    private static List<CurveReviewRun> CollectRuns(
        IReadOnlyList<CurveReviewSample> samples,
        Func<int, double> valueAt,
        double limit,
        bool aboveLimit)
    {
        var runs = new List<CurveReviewRun>();
        int start = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            bool violating = false;
            if (i < samples.Count - 1)
            {
                double value = valueAt(i);
                violating = double.IsFinite(value) && (aboveLimit ? value > limit : value < limit);
            }

            if (violating && start < 0)
            {
                start = i;
            }
            else if (!violating && start >= 0)
            {
                runs.Add(BuildRun(samples, valueAt, start, i, aboveLimit));
                start = -1;
            }
        }

        return runs;
    }

    private static CurveReviewRun BuildRun(
        IReadOnlyList<CurveReviewSample> samples,
        Func<int, double> valueAt,
        int startInterval,
        int endInterval,
        bool aboveLimit)
    {
        var path = new Point3d[endInterval - startInterval + 1];
        double peak = aboveLimit ? double.MinValue : double.MaxValue;
        Point3d peakPoint = samples[startInterval].Point;
        double planLength = 0.0;
        for (int i = startInterval; i <= endInterval; i++)
        {
            path[i - startInterval] = samples[i].Point;
            if (i >= endInterval)
                continue;

            planLength += GeometryCommandAlgorithms.CalculatePlanDistance(samples[i].Point, samples[i + 1].Point);
            double value = valueAt(i);
            if (!double.IsFinite(value))
                continue;
            if (aboveLimit ? value > peak : value < peak)
            {
                peak = value;
                peakPoint = samples[i].Point;
            }
        }

        return new CurveReviewRun
        {
            StartStation = samples[startInterval].Station,
            EndStation = samples[endInterval].Station,
            PlanLength = planLength,
            PeakValue = peak,
            PeakPoint = peakPoint,
            Path = path
        };
    }

    private static void ResolveTerrainExtremes(
        IReadOnlyList<CurveReviewSample> samples,
        out double maximumFill,
        out double maximumFillStation,
        out Point3d maximumFillPoint,
        out double maximumCut,
        out double maximumCutStation,
        out Point3d maximumCutPoint)
    {
        maximumFill = 0.0;
        maximumFillStation = 0.0;
        maximumFillPoint = Point3d.Unset;
        maximumCut = 0.0;
        maximumCutStation = 0.0;
        maximumCutPoint = Point3d.Unset;
        foreach (CurveReviewSample sample in samples)
        {
            if (!sample.HasTerrain)
                continue;

            double delta = sample.TerrainDelta;
            if (delta > maximumFill)
            {
                maximumFill = delta;
                maximumFillStation = sample.Station;
                maximumFillPoint = sample.Point;
            }
            else if (-delta > maximumCut)
            {
                maximumCut = -delta;
                maximumCutStation = sample.Station;
                maximumCutPoint = sample.Point;
            }
        }
    }
}
