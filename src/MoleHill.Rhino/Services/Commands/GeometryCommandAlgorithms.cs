using MoleHill.Core.Analysis;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal enum SoftEditFalloff
{
    Linear,
    Smooth
}

internal enum CurveSectionEditMode
{
    GradePercent,
    BetweenCurrentElevations,
    BlendToTerrain
}

/// <summary>
/// Which end of an edited curve section keeps its existing elevation. Grading a section at a chosen
/// slope has to give up one of the two end elevations — this is the user's say in which one, and it
/// is the difference between a re-grade that pivots around where the work starts and one that pivots
/// around where it ties back in. Only <see cref="CurveSectionEditMode.GradePercent"/> has a free end
/// to anchor: the other modes hold both ends by construction.
/// </summary>
internal enum CurveSectionAnchor
{
    /// <summary>Hold the first picked point; the second end moves.</summary>
    Start,

    /// <summary>Hold the second picked point; the first end moves.</summary>
    End,

    /// <summary>Hold the mid elevation of the two picks; both ends move by half the delta each.</summary>
    Middle
}

/// <summary>
/// What an edited section actually came out as, measured at the picked ends in pick order, so the
/// command can report the achieved grade rather than only the requested one.
/// </summary>
internal readonly record struct CurveSectionEditResult(
    double FirstElevation,
    double SecondElevation,
    double SlopeRatio,
    double PlanLength,
    GeometryCommandAlgorithms.CurveSlopeDeviation Deviation);

/// <summary>
/// How the vertical component of an offset feature line is specified.
/// Every mode resolves to a single delta Z applied along the whole offset line.
/// </summary>
internal enum OffsetVerticalMode
{
    /// <summary>Delta Z entered directly; the horizontal offset is irrelevant.</summary>
    Elevation,

    /// <summary>Grade in percent (rise/run * 100) applied over the horizontal offset.</summary>
    Percent,

    /// <summary>Batter angle in degrees applied over the horizontal offset.</summary>
    Degrees,

    /// <summary>Ratio entered as the run of 1:n (run:rise) applied over the horizontal offset.</summary>
    Ratio
}

internal static class GeometryCommandAlgorithms
{
    /// <summary>
    /// Re-elevates the picked stretch of a curve and hands back the rest of it unchanged.
    ///
    /// <para>The overload without an anchor, a transition or a falloff is the original behaviour and
    /// is kept for callers that only ever graded from the section start: it holds the first pick,
    /// absorbs nothing, and leaves the vertical step at the far end that the full overload's
    /// <c>transitionLength</c> exists to remove.</para>
    /// </summary>
    public static bool TryCreateCurveSectionEdit(
        Curve sourceCurve,
        double firstParameter,
        double secondParameter,
        CurveSectionEditMode mode,
        double gradePercent,
        double terrainBlend,
        Mesh? terrain,
        double tolerance,
        out Curve? resultCurve,
        out string? error)
    {
        return TryCreateCurveSectionEdit(
            sourceCurve, firstParameter, secondParameter, mode, gradePercent, terrainBlend, terrain,
            CurveSectionAnchor.Start, 0.0, SoftEditFalloff.Smooth, measureDeviation: false, tolerance,
            out resultCurve, out _, out error);
    }

    /// <summary>
    /// Re-elevates the picked stretch of a curve, easing the elevation it gains or loses back into the
    /// untouched remainder instead of stepping.
    ///
    /// <para>Grading a section to a chosen slope moves one of its ends, and that end is joined to a
    /// stretch of curve that did not move. With <paramref name="transitionLength" /> at zero the two
    /// are joined by a vertical line — same plan position, two elevations — which is a wall in the
    /// terrain that follows. Given a length, the moved end's delta is instead distributed into the
    /// adjoining stretch, decaying to nothing over that plan distance, so the curve ties back into its
    /// original alignment. The length is clamped to the adjoining stretch, because a transition longer
    /// than the curve it has to die out in would drag the curve's own endpoint with it.</para>
    ///
    /// <para><see cref="CurveSectionEditMode.BlendToTerrain" /> spends the same length the other way
    /// round — inside the section, ramping the blend up from each picked end — because the elevation
    /// it pulls towards is the terrain's and can differ from the curve by any amount at all. Feathering
    /// inwards keeps both picked ends where they are and confines the edit to what was picked; pushing
    /// an arbitrary terrain delta outwards would move curve the user did not select.</para>
    /// </summary>
    /// <param name="firstParameter">Curve parameter of the first pick. Pick order sets the direction
    /// the grade runs, so this need not be the lower parameter.</param>
    /// <param name="anchor">Which picked end keeps its elevation. Only meaningful for
    /// <see cref="CurveSectionEditMode.GradePercent" />; the other modes hold both ends.</param>
    /// <param name="transitionLength">Plan distance over which a moved end is eased back into the
    /// curve, or for blend-to-terrain, ramped in from each end. Zero reproduces the vertical step.</param>
    /// <param name="measureDeviation">Measures how far the Greville-edited section actually landed from
    /// the prescribed elevations. Off for the live preview: under blend-to-terrain each sample is a mesh
    /// ray, and the preview redraws on every mouse move.</param>
    /// <param name="report">The achieved elevations and slope at the picks, in pick order.</param>
    public static bool TryCreateCurveSectionEdit(
        Curve sourceCurve,
        double firstParameter,
        double secondParameter,
        CurveSectionEditMode mode,
        double gradePercent,
        double terrainBlend,
        Mesh? terrain,
        CurveSectionAnchor anchor,
        double transitionLength,
        SoftEditFalloff falloff,
        bool measureDeviation,
        double tolerance,
        out Curve? resultCurve,
        out CurveSectionEditResult report,
        out string? error)
    {
        resultCurve = null;
        report = default;
        error = null;

        if (sourceCurve == null || !sourceCurve.IsValid)
        {
            error = "The selected curve is invalid.";
            return false;
        }

        Interval domain = sourceCurve.Domain;
        firstParameter = Math.Clamp(firstParameter, domain.T0, domain.T1);
        secondParameter = Math.Clamp(secondParameter, domain.T0, domain.T1);

        // Pick order is the grade's direction. The section itself is always trimmed low-to-high
        // because that is the only way a curve can be cut, but "from the first point I picked to the
        // second" is what the user meant by a falling grade, and that is often against the curve's
        // own parameterisation.
        bool reversed = secondParameter < firstParameter;
        double lowParameter = reversed ? secondParameter : firstParameter;
        double highParameter = reversed ? firstParameter : secondParameter;

        double firstZ = sourceCurve.PointAt(firstParameter).Z;
        double secondZ = sourceCurve.PointAt(secondParameter).Z;

        if (mode == CurveSectionEditMode.BlendToTerrain && terrain == null)
        {
            error = "Blend to terrain requires a completed active MoleHill terrain.";
            return false;
        }

        Curve? section = sourceCurve.Trim(lowParameter, highParameter);
        if (section == null || !section.IsValid)
        {
            section?.Dispose();
            error = "Failed to isolate the selected curve section.";
            return false;
        }

        terrainBlend = Math.Clamp(terrainBlend, 0.0, 100.0) / 100.0;
        transitionLength = Math.Max(0.0, transitionLength);
        NurbsCurve edited = section.ToNurbsCurve();
        section.Dispose();

        // One plan projection for the whole pass. The projection preserves the domain, so every
        // station below is a parameter query against this curve rather than another projection —
        // stationing used to re-project the section once per control point.
        Curve? planSection = CreatePlanCurve(edited);
        if (planSection == null)
        {
            edited.Dispose();
            error = "Failed to measure the selected curve section in plan.";
            return false;
        }

        double sectionPlanLength = planSection.GetLength();
        if (!double.IsFinite(sectionPlanLength) ||
            sectionPlanLength <= Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance))
        {
            planSection.Dispose();
            edited.Dispose();
            error = "The selected curve section is too short.";
            return false;
        }

        // A grade is linear in station and so is exact at whatever control points the section already
        // has. Blending is not: it samples the terrain per control point, and its edge feather needs
        // somewhere to ramp.
        if (mode == CurveSectionEditMode.BlendToTerrain)
            InsertStationKnots(edited, planSection, 0.0, sectionPlanLength, BlendSectionSamples);

        double[] greville = edited.GrevilleParameters();
        if (greville.Length == 0)
        {
            planSection.Dispose();
            edited.Dispose();
            error = "The curve does not expose editable control points.";
            return false;
        }

        double grade = gradePercent * 0.01;
        double midZ = (firstZ + secondZ) * 0.5;

        // Station is measured from the first pick, so a positive grade always climbs in the direction
        // the user picked.
        double GradeElevationAt(double station) => anchor switch
        {
            CurveSectionAnchor.End => secondZ + (grade * (station - sectionPlanLength)),
            CurveSectionAnchor.Middle => midZ + (grade * (station - (sectionPlanLength * 0.5))),
            _ => firstZ + (grade * station)
        };

        var points = new List<Point3d>(greville.Length);
        double lowEndZ = double.NaN;
        double highEndZ = double.NaN;

        for (int index = 0; index < greville.Length; index++)
        {
            double parameter = greville[index];
            Point3d point = edited.PointAt(parameter);
            double ascendingStation = Math.Clamp(
                planSection.GetLength(new Interval(planSection.Domain.T0, parameter)),
                0.0,
                sectionPlanLength);
            double station = reversed ? sectionPlanLength - ascendingStation : ascendingStation;
            double z = point.Z;

            switch (mode)
            {
                case CurveSectionEditMode.GradePercent:
                    z = GradeElevationAt(station);
                    break;

                case CurveSectionEditMode.BetweenCurrentElevations:
                    z = firstZ + ((secondZ - firstZ) * station / sectionPlanLength);
                    break;

                case CurveSectionEditMode.BlendToTerrain:
                    if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(
                            terrain!,
                            new Point3d(point.X, point.Y, 0.0),
                            tolerance,
                            out Point3d terrainPoint))
                    {
                        planSection.Dispose();
                        edited.Dispose();
                        error = "The curve section extends outside the active terrain.";
                        return false;
                    }

                    double feather = CalculateEdgeFeather(
                        ascendingStation, sectionPlanLength, transitionLength, falloff);
                    z += (terrainPoint.Z - z) * terrainBlend * feather;
                    break;
            }

            if (index == 0)
                lowEndZ = z;
            if (index == greville.Length - 1)
                highEndZ = z;

            points.Add(new Point3d(point.X, point.Y, z));
        }

        // Kept only for the deviation measurement, whose blend rule needs the elevations the section
        // had before the edit. Re-reading them from the source curve would lean on Trim having
        // preserved the parameterisation, which is the assumption this method stopped trusting.
        Curve? originalSection = measureDeviation ? edited.DuplicateCurve() : null;

        if (!edited.SetGrevillePoints(points))
        {
            planSection.Dispose();
            originalSection?.Dispose();
            error = "Failed to rebuild the curve section.";
            edited.Dispose();
            return false;
        }

        // The rule the edit applied, evaluable at any station rather than only at a control point —
        // which is exactly what asking "how far off is it in between?" requires.
        double PrescribedElevationAt(double ascendingStation)
        {
            double station = reversed ? sectionPlanLength - ascendingStation : ascendingStation;
            switch (mode)
            {
                case CurveSectionEditMode.GradePercent:
                    return GradeElevationAt(station);

                case CurveSectionEditMode.BetweenCurrentElevations:
                    return firstZ + ((secondZ - firstZ) * station / sectionPlanLength);

                default:
                    if (!planSection.LengthParameter(ascendingStation, out double parameter))
                        return double.NaN;

                    Point3d before = originalSection!.PointAt(parameter);
                    if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(
                            terrain!, new Point3d(before.X, before.Y, 0.0), tolerance,
                            out Point3d sampledTerrain))
                    {
                        return before.Z;
                    }

                    double sampleFeather = CalculateEdgeFeather(
                        ascendingStation, sectionPlanLength, transitionLength, falloff);
                    return before.Z + ((sampledTerrain.Z - before.Z) * terrainBlend * sampleFeather);
            }
        }

        CurveSlopeDeviation deviation = measureDeviation
            ? MeasureElevationDeviation(edited, PrescribedElevationAt)
            : default;

        planSection.Dispose();
        originalSection?.Dispose();

        double editedFirstZ = reversed ? highEndZ : lowEndZ;
        double editedSecondZ = reversed ? lowEndZ : highEndZ;
        report = new CurveSectionEditResult(
            editedFirstZ,
            editedSecondZ,
            (editedSecondZ - editedFirstZ) / sectionPlanLength,
            sectionPlanLength,
            deviation);

        // How far each joint moved, which is what the neighbouring stretch has to absorb.
        double deltaAtLowEnd = lowEndZ - sourceCurve.PointAt(lowParameter).Z;
        double deltaAtHighEnd = highEndZ - sourceCurve.PointAt(highParameter).Z;

        var pieces = new List<Curve>();
        Curve? leading = lowParameter > domain.T0
            ? sourceCurve.Trim(domain.T0, lowParameter)
            : null;
        Curve? trailing = highParameter < domain.T1
            ? sourceCurve.Trim(highParameter, domain.T1)
            : null;

        if (leading != null)
        {
            leading = ApplyTransitionDelta(leading, jointAtStart: false, deltaAtLowEnd, transitionLength, falloff);
            pieces.Add(leading);
            AddSectionBoundaryTransition(pieces, leading.PointAtEnd, edited.PointAtStart, tolerance);
        }

        pieces.Add(edited);
        if (trailing != null)
        {
            trailing = ApplyTransitionDelta(trailing, jointAtStart: true, deltaAtHighEnd, transitionLength, falloff);
            AddSectionBoundaryTransition(pieces, edited.PointAtEnd, trailing.PointAtStart, tolerance);
            pieces.Add(trailing);
        }

        if (pieces.Count == 1)
        {
            resultCurve = edited;
            return true;
        }

        Curve[] joined = Curve.JoinCurves(
            pieces,
            Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance),
            preserveDirection: true);
        foreach (Curve piece in pieces)
            piece.Dispose();

        if (joined.Length != 1 || !joined[0].IsValid)
        {
            foreach (Curve curve in joined)
                curve.Dispose();
            error = "Failed to reassemble the edited curve section.";
            return false;
        }

        resultCurve = joined[0];
        return true;
    }

    /// <summary>
    /// Eases <paramref name="delta" /> into one end of an untouched stretch of curve so it meets the
    /// edited section without a step. Returns <paramref name="piece" /> itself when there is nothing
    /// to absorb or the stretch cannot be rebuilt, and otherwise disposes it and returns the
    /// replacement — so callers must assign the result back.
    /// </summary>
    private static Curve ApplyTransitionDelta(
        Curve piece,
        bool jointAtStart,
        double delta,
        double transitionLength,
        SoftEditFalloff falloff)
    {
        if (Math.Abs(delta) <= RhinoMath.ZeroTolerance || transitionLength <= RhinoMath.ZeroTolerance)
            return piece;

        NurbsCurve nurbs = piece.ToNurbsCurve();
        Curve? plan = CreatePlanCurve(nurbs);
        double pieceLength = plan?.GetLength() ?? double.NaN;

        // Clamped to the stretch that has to absorb it: beyond that there is no curve left to decay
        // in, and the far endpoint — the end of the whole curve — would move.
        double ramp = Math.Min(transitionLength, pieceLength);
        if (plan == null || !double.IsFinite(ramp) || ramp <= RhinoMath.ZeroTolerance)
        {
            plan?.Dispose();
            nurbs.Dispose();
            return piece;
        }

        // A straight neighbour carries control points only at its two ends, so there is nothing
        // between the joint and the far end for the falloff to act on — the delta would ramp evenly
        // across the whole stretch instead of dying out over the transition length. Knots inside the
        // band supply those control points without moving the curve or changing its type.
        InsertStationKnots(
            nurbs,
            plan,
            jointAtStart ? 0.0 : pieceLength - ramp,
            jointAtStart ? ramp : pieceLength,
            TransitionBandSamples);

        double[] greville = nurbs.GrevilleParameters();
        if (greville.Length == 0)
        {
            plan.Dispose();
            nurbs.Dispose();
            return piece;
        }

        var points = new List<Point3d>(greville.Length);
        foreach (double parameter in greville)
        {
            Point3d point = nurbs.PointAt(parameter);
            double station = Math.Clamp(
                plan.GetLength(new Interval(plan.Domain.T0, parameter)), 0.0, pieceLength);
            double distance = jointAtStart ? station : pieceLength - station;
            double factor = CalculateFalloffFactor(Math.Clamp(distance, 0.0, ramp) / ramp, falloff);
            points.Add(new Point3d(point.X, point.Y, point.Z + (delta * factor)));
        }

        plan.Dispose();
        if (!nurbs.SetGrevillePoints(points))
        {
            nurbs.Dispose();
            return piece;
        }

        piece.Dispose();
        return nurbs;
    }

    /// <summary>Control points the falloff gets to work with inside a transition band.</summary>
    private const int TransitionBandSamples = 12;

    /// <summary>
    /// Control points a blended section gets. Blending reads the terrain at control points, so on a
    /// straight run between two of them it would read the terrain twice and interpolate across
    /// whatever lies between — and the edge feather would have nothing to ramp over.
    /// </summary>
    private const int BlendSectionSamples = 32;

    /// <summary>
    /// Adds knots at evenly spaced plan stations, giving a curve control points where an elevation edit
    /// needs to shape something. Knot insertion is geometry-preserving, so this changes only what the
    /// curve can express, never where it runs. The plan curve shares the source domain, so its length
    /// solver is what maps a station back to a parameter.
    ///
    /// <para>Both ends of the band are included, not just the interior. A band boundary that falls
    /// inside the curve needs a control point of its own or the edit has nothing to land on there and
    /// leaks a straight tail across the whole remainder — a transition that was asked to die out in 20
    /// units instead petering out over the next 50.</para>
    /// </summary>
    private static void InsertStationKnots(
        NurbsCurve curve,
        Curve planCurve,
        double fromStation,
        double toStation,
        int sampleCount)
    {
        double span = toStation - fromStation;
        double planLength = planCurve.GetLength();
        if (sampleCount < 2 || span <= RhinoMath.ZeroTolerance || !double.IsFinite(planLength))
            return;

        for (int index = 0; index <= sampleCount; index++)
        {
            double station = fromStation + (span * index / sampleCount);

            // The curve's own ends already carry control points, and asking for a knot there is at
            // best a no-op.
            if (station <= RhinoMath.ZeroTolerance || station >= planLength - RhinoMath.ZeroTolerance)
                continue;

            if (planCurve.LengthParameter(station, out double parameter))
                curve.Knots.InsertKnot(parameter);
        }
    }

    /// <summary>
    /// Ramps an in-section edit up from nothing at each picked end over <paramref name="rampLength" />
    /// of plan distance, so the picked ends keep their elevation. Clamped to half the section, past
    /// which the two ramps would overlap and the edit would never reach full strength.
    /// </summary>
    private static double CalculateEdgeFeather(
        double station,
        double sectionLength,
        double rampLength,
        SoftEditFalloff falloff)
    {
        if (rampLength <= RhinoMath.ZeroTolerance)
            return 1.0;

        double ramp = Math.Min(rampLength, sectionLength * 0.5);
        if (ramp <= RhinoMath.ZeroTolerance)
            return 1.0;

        double distanceToNearestEnd = Math.Min(station, sectionLength - station);
        if (distanceToNearestEnd >= ramp)
            return 1.0;

        return CalculateFalloffFactor(1.0 - (distanceToNearestEnd / ramp), falloff);
    }

    private static void AddSectionBoundaryTransition(
        ICollection<Curve> pieces,
        Point3d from,
        Point3d to,
        double tolerance)
    {
        if (from.DistanceTo(to) <= Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance))
            return;

        pieces.Add(new LineCurve(from, to));
    }

    /// <summary>Samples taken along a curve when measuring how far it strays from its asked-for slope.</summary>
    private const int DeviationSamples = 512;

    /// <summary>
    /// How far a curve strays from the elevations it was asked for, and where.
    ///
    /// <para>The slope commands deliberately re-elevate a curve by moving its existing Greville points
    /// rather than rebuilding it from a dense sample, because that keeps the curve editable — same
    /// degree, same control points, still something a user can grab. The cost is that the result only
    /// *interpolates* the asked-for elevations, at the Greville abscissae; between them the elevation
    /// follows the NURBS basis while the ideal follows arc length. For a degree-1 polyline the two
    /// agree exactly and the deviation is zero. For a curved or high-degree curve with few control
    /// points they do not, and nothing on screen says so — a curve can read as a clean 5% and sag
    /// centimetres between its control points. Hence measuring it and saying so.</para>
    /// </summary>
    public readonly record struct CurveSlopeDeviation(
        double MaxDeviation,
        double Station,
        Point3d Location,
        int SampleCount)
    {
        /// <summary>True when the stray is larger than the document would call coincident.</summary>
        public bool ExceedsTolerance(double tolerance) =>
            MaxDeviation > Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance);
    }

    /// <summary>
    /// Compares a re-elevated curve against the elevation rule it was built from, sampling densely by
    /// plan station. <paramref name="prescribedElevationAtStation" /> is the same rule the edit applied,
    /// so this measures interpolation error alone — not a difference of intent.
    /// </summary>
    public static CurveSlopeDeviation MeasureElevationDeviation(
        Curve curve,
        Func<double, double> prescribedElevationAtStation,
        int sampleCount = DeviationSamples)
    {
        if (curve == null || prescribedElevationAtStation == null || sampleCount < 2)
            return default;

        using Curve? plan = CreatePlanCurve(curve);
        double planLength = plan?.GetLength() ?? double.NaN;
        if (plan == null || !double.IsFinite(planLength) || planLength <= RhinoMath.ZeroTolerance)
            return default;

        double worst = 0.0;
        double worstStation = 0.0;
        Point3d worstPoint = curve.PointAtStart;

        for (int index = 0; index <= sampleCount; index++)
        {
            double station = planLength * index / sampleCount;
            if (!plan.LengthParameter(station, out double parameter))
                continue;

            Point3d point = curve.PointAt(parameter);
            double deviation = Math.Abs(point.Z - prescribedElevationAtStation(station));
            if (deviation <= worst)
                continue;

            worst = deviation;
            worstStation = station;
            worstPoint = point;
        }

        return new CurveSlopeDeviation(worst, worstStation, worstPoint, sampleCount);
    }

    public static double CalculatePlanLength(Curve curve, double firstParameter, double secondParameter)
    {
        if (secondParameter < firstParameter)
            (firstParameter, secondParameter) = (secondParameter, firstParameter);

        using Curve? planCurve = CreatePlanCurve(curve);
        return planCurve?.GetLength(new Interval(firstParameter, secondParameter)) ?? double.NaN;
    }

    /// <summary>
    /// Exact World-XY projection used for plan stationing. The projection preserves the source
    /// domain, so callers that perform many station queries can retain it and use Rhino's native
    /// length/parameter solver instead of repeatedly chord-sampling the 3D curve.
    /// </summary>
    public static Curve? CreatePlanCurve(Curve curve) => Curve.ProjectToPlane(curve, Plane.WorldXY);

    public static Point3d FlattenToWorldXY(Point3d point)
    {
        return new Point3d(point.X, point.Y, 0.0);
    }

    public static double InterpolateTwoPointElevation(Point3d lowPoint, Point3d highPoint, Point3d samplePoint)
    {
        Point3d lowFlat = FlattenToWorldXY(lowPoint);
        Point3d highFlat = FlattenToWorldXY(highPoint);
        Point3d sampleFlat = FlattenToWorldXY(samplePoint);

        double distanceToHigh = sampleFlat.DistanceTo(highFlat);
        double distanceToLow = lowFlat.DistanceTo(sampleFlat);
        double totalDistance = distanceToHigh + distanceToLow;
        if (totalDistance <= RhinoMath.ZeroTolerance)
            return lowPoint.Z;

        double highWeight = distanceToHigh / totalDistance;
        double lowWeight = distanceToLow / totalDistance;
        return (highWeight * lowPoint.Z) + (lowWeight * highPoint.Z);
    }

    public static double InterpolateGradientElevation(Point3d basePoint, Point3d samplePoint, double promille)
    {
        double distance = FlattenToWorldXY(basePoint).DistanceTo(FlattenToWorldXY(samplePoint));
        return basePoint.Z + (distance * promille * 0.001);
    }

    public static bool ShouldUseSlopePercentageInput(double startElevation, double endElevation, double tolerance)
    {
        return Math.Abs(startElevation - endElevation) <= Math.Abs(tolerance);
    }

    public static bool TryCreateSlopeCurveFromEndPoints(
        Curve sourceCurve,
        out Curve? resultCurve,
        out double slopeRatio,
        out string? error)
    {
        resultCurve = null;
        slopeRatio = 0.0;

        Curve? projectedCurve = ProjectCurveToStartElevation(sourceCurve);
        if (projectedCurve == null)
        {
            error = "Failed to project the curve to a horizontal plane.";
            return false;
        }

        double projectedLength = projectedCurve.GetLength();
        if (projectedLength <= RhinoMath.ZeroTolerance)
        {
            error = "Curve is too short to slope.";
            return false;
        }

        slopeRatio = (sourceCurve.PointAtEnd.Z - sourceCurve.PointAtStart.Z) / projectedLength;
        return TryCreateSlopeCurve(sourceCurve, slopeRatio, out resultCurve, out error);
    }

    public static bool TryCreateSlopeCurve(
        Curve sourceCurve,
        double slopeRatio,
        out Curve? resultCurve,
        out string? error)
    {
        resultCurve = null;

        Curve? projectedCurve = ProjectCurveToStartElevation(sourceCurve);
        if (projectedCurve == null)
        {
            error = "Failed to project the curve to a horizontal plane.";
            return false;
        }

        NurbsCurve projectedNurbs = projectedCurve.ToNurbsCurve();
        double[] grevilleParameters = projectedNurbs.GrevilleParameters();
        if (grevilleParameters.Length == 0)
        {
            error = "Curve does not expose Greville points for rebuilding.";
            return false;
        }

        var grevillePoints = new List<Point3d>(grevilleParameters.Length);
        foreach (double parameter in grevilleParameters)
        {
            double length = projectedNurbs.GetLength(new Interval(projectedNurbs.Domain.T0, parameter));
            Point3d point = projectedNurbs.PointAt(parameter);
            point.Z += length * slopeRatio;
            grevillePoints.Add(point);
        }

        if (!projectedNurbs.SetGrevillePoints(grevillePoints))
        {
            error = "Failed to rebuild the sloped curve.";
            return false;
        }

        resultCurve = projectedNurbs;
        error = null;
        return true;
    }

    public static double CalculateSlopePercentMagnitude(Vector3d tangent)
    {
        double horizontalLength = Math.Sqrt((tangent.X * tangent.X) + (tangent.Y * tangent.Y));
        if (horizontalLength <= RhinoMath.ZeroTolerance)
            return double.PositiveInfinity;

        return Math.Abs(tangent.Z / horizontalLength) * 100.0;
    }

    /// <summary>
    /// Maximum batter angle accepted for <see cref="OffsetVerticalMode.Degrees"/>; matches the
    /// clamp used by the grading slopes in <c>MoleHill.Core.Grading.GradingSlope</c>.
    /// </summary>
    private const double MaxBatterAngleDegrees = 89.9;

    /// <summary>
    /// Resolves the vertical drop/rise of an offset feature line. The sign of
    /// <paramref name="value"/> is the sign of the result, so a falling batter is entered as a
    /// negative percent, angle or ratio.
    /// </summary>
    public static bool TryResolveVerticalDelta(
        OffsetVerticalMode mode,
        double value,
        double horizontalDistance,
        out double verticalDelta,
        out string? error)
    {
        verticalDelta = 0.0;
        error = null;

        // Civil3D drives the vertical from the offset distance, so the offset line stays parallel
        // to the source instead of dipping further at mitred corners.
        double run = Math.Abs(horizontalDistance);

        switch (mode)
        {
            case OffsetVerticalMode.Elevation:
                verticalDelta = value;
                return true;

            case OffsetVerticalMode.Percent:
                verticalDelta = SlopeAnalyzer.ConvertUnitToRatio(value, SlopeAnalyzer.SlopeUnit.Percent) * run;
                return true;

            case OffsetVerticalMode.Degrees:
                if (Math.Abs(value) >= MaxBatterAngleDegrees)
                {
                    error = $"Batter angle must be less than {MaxBatterAngleDegrees} degrees.";
                    return false;
                }

                verticalDelta = SlopeAnalyzer.ConvertUnitToRatio(value, SlopeAnalyzer.SlopeUnit.Degrees) * run;
                return true;

            case OffsetVerticalMode.Ratio:
                if (Math.Abs(value) <= RhinoMath.ZeroTolerance)
                {
                    error = "Ratio run must be non-zero.";
                    return false;
                }

                // value is the run of 1:n, so rise/run = 1/n and the sign carries through.
                verticalDelta = run / value;
                return true;

            default:
                error = "Unknown vertical offset mode.";
                return false;
        }
    }

    public static double CalculateDefaultLiftFactor(UnitSystem unitSystem)
    {
        return ModelUnits.FromMeters(2.0, unitSystem);
    }

    /// <summary>
    /// Offsets a 3D polyline in plan, re-lifts every offset vertex to the source elevation at its
    /// closest point (so the source's own longitudinal grade is preserved), then applies a constant
    /// <paramref name="verticalDelta"/>. The sign of <paramref name="offsetDistance"/> selects the side.
    /// </summary>
    public static bool TryGetOffsetFeaturePolyline(
        Curve sourceCurve,
        double offsetDistance,
        double verticalDelta,
        double tolerance,
        out Polyline polyline,
        out string? error)
    {
        polyline = new Polyline();

        if (!TryValidateDegreeOnePolyline(sourceCurve, out error))
            return false;

        Curve? projectedCurve = Curve.ProjectToPlane(sourceCurve, Plane.WorldXY);
        if (projectedCurve == null)
        {
            error = "Failed to project the input curve.";
            return false;
        }

        Curve[] offsetCurves = projectedCurve.Offset(
            Plane.WorldXY,
            offsetDistance,
            tolerance,
            CurveOffsetCornerStyle.Sharp);

        if (offsetCurves.Length == 0)
        {
            error = "Offset failed.";
            return false;
        }

        if (offsetCurves.Length != 1)
        {
            error = "Offset created multiple results. Cleanup intersections first.";
            return false;
        }

        if (!TryExtractPolyline(offsetCurves[0], tolerance, out Polyline offsetPolyline))
        {
            error = "Unable to convert the offset result to a polyline.";
            return false;
        }

        var liftedPoints = new List<Point3d>(offsetPolyline.Count);
        foreach (Point3d point in offsetPolyline)
        {
            if (!projectedCurve.ClosestPoint(point, out double parameter))
                continue;

            Point3d sourcePoint = sourceCurve.PointAt(parameter);
            liftedPoints.Add(new Point3d(point.X, point.Y, sourcePoint.Z + verticalDelta));
        }

        if (liftedPoints.Count < 2)
        {
            error = "Unable to build the lifted offset polyline.";
            return false;
        }

        if (sourceCurve.IsClosed && liftedPoints[0].DistanceTo(liftedPoints[^1]) > tolerance)
            liftedPoints.Add(liftedPoints[0]);

        polyline = new Polyline(liftedPoints);
        error = string.Empty;
        return polyline.Count >= 2;
    }

    public static bool TryCreateSoftEditedCurve(
        Curve sourceCurve,
        Point3d basePoint,
        double radius,
        Vector3d vector,
        SoftEditFalloff falloff,
        bool fixEnds,
        double tolerance,
        bool quickPreview,
        out Curve? resultCurve,
        out string? error)
    {
        resultCurve = null;

        if (radius <= RhinoMath.ZeroTolerance)
        {
            error = "Soft edit radius must be greater than zero.";
            return false;
        }

        double effectiveTolerance = Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance);
        bool lockOpenEnds = fixEnds && !sourceCurve.IsClosed;
        Point3d fixedStart = sourceCurve.PointAtStart;
        Point3d fixedEnd = sourceCurve.PointAtEnd;
        Curve candidate = sourceCurve.DuplicateCurve();
        var morph = new RadialSoftEditMorph(
            basePoint,
            radius,
            vector,
            falloff,
            lockOpenEnds,
            fixedStart,
            fixedEnd,
            effectiveTolerance)
        {
            Tolerance = effectiveTolerance,
            QuickPreview = quickPreview,
            PreserveStructure = false
        };

        if (!SpaceMorph.IsMorphable(candidate) || !morph.Morph(candidate))
        {
            candidate.Dispose();
            error = "Rhino could not soft-morph the curve.";
            return false;
        }

        bool startIsFixed = !lockOpenEnds ||
                            candidate.PointAtStart == fixedStart ||
                            candidate.SetStartPoint(fixedStart);
        bool endIsFixed = !lockOpenEnds ||
                          candidate.PointAtEnd == fixedEnd ||
                          candidate.SetEndPoint(fixedEnd);
        if (!startIsFixed || !endIsFixed)
        {
            candidate.Dispose();
            error = "Rhino could not preserve the curve endpoints.";
            return false;
        }

        if (!candidate.IsValid)
        {
            candidate.Dispose();
            error = "Soft edit produced an invalid curve.";
            return false;
        }

        resultCurve = candidate;
        error = null;
        return true;
    }

    public static List<Point3d> CalculateSoftEditPoints(
        IReadOnlyList<Point3d> points,
        Point3d basePoint,
        double radius,
        Vector3d vector,
        SoftEditFalloff falloff = SoftEditFalloff.Smooth)
    {
        var adjusted = new List<Point3d>(points.Count);
        if (radius <= RhinoMath.ZeroTolerance)
        {
            adjusted.AddRange(points);
            return adjusted;
        }

        for (int i = 0; i < points.Count; i++)
        {
            Point3d point = points[i];
            double factor = CalculateSoftEditFactor(point, basePoint, radius, falloff);
            adjusted.Add(point + (vector * factor));
        }

        return adjusted;
    }

    public static double EaseInOutSine(double t)
    {
        return -(Math.Cos(Math.PI * t) - 1.0) / 2.0;
    }

    public static double CalculatePlanDistance(Point3d a, Point3d b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public static bool BoundingBoxIntersectsPlanRadius(BoundingBox bounds, Point3d center, double radius)
    {
        if (!bounds.IsValid || radius <= RhinoMath.ZeroTolerance)
            return false;

        double dx = center.X < bounds.Min.X
            ? bounds.Min.X - center.X
            : center.X > bounds.Max.X
                ? center.X - bounds.Max.X
                : 0.0;
        double dy = center.Y < bounds.Min.Y
            ? bounds.Min.Y - center.Y
            : center.Y > bounds.Max.Y
                ? center.Y - bounds.Max.Y
                : 0.0;

        return (dx * dx) + (dy * dy) < radius * radius;
    }

    public static bool IsPointInsideNestedBoundaries(
        Point3d point,
        IReadOnlyList<Curve> boundaries,
        IReadOnlyList<BoundingBox> boundaryBoxes,
        double tolerance)
    {
        int containmentCount = 0;
        for (int i = 0; i < boundaries.Count; i++)
        {
            // Curve.Contains is costly; a point outside the boundary's XY box cannot be inside or on it.
            if (!ContainsXY(boundaryBoxes[i], point, tolerance))
                continue;

            PointContainment containment = boundaries[i].Contains(point, Plane.WorldXY, tolerance);
            if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                containmentCount++;
        }

        return containmentCount % 2 == 1;
    }

    public static bool ContainsXY(BoundingBox box, Point3d point, double tolerance)
    {
        return point.X >= box.Min.X - tolerance &&
               point.X <= box.Max.X + tolerance &&
               point.Y >= box.Min.Y - tolerance &&
               point.Y <= box.Max.Y + tolerance;
    }

    public static bool OverlapsXY(BoundingBox a, BoundingBox b, double tolerance)
    {
        return a.Min.X <= b.Max.X + tolerance &&
               b.Min.X <= a.Max.X + tolerance &&
               a.Min.Y <= b.Max.Y + tolerance &&
               b.Min.Y <= a.Max.Y + tolerance;
    }

    public static double GetSignedOffsetDistanceForSide(bool isLeftSide, double offsetDistance)
    {
        double absoluteDistance = Math.Abs(offsetDistance);
        return isLeftSide ? -absoluteDistance : absoluteDistance;
    }

    public static bool TryGetSignedOffsetDistance(
        Curve projectedCurve,
        Point3d referencePoint,
        double offsetDistance,
        out double signedDistance)
    {
        signedDistance = 0.0;
        double absoluteDistance = Math.Abs(offsetDistance);
        if (absoluteDistance <= RhinoMath.ZeroTolerance)
            return false;

        Point3d flatReference = FlattenToWorldXY(referencePoint);
        if (!projectedCurve.ClosestPoint(flatReference, out double parameter))
            return false;

        Point3d pointOnCurve = projectedCurve.PointAt(parameter);
        Vector3d tangent = projectedCurve.TangentAt(parameter);
        tangent.Z = 0.0;
        if (!tangent.Unitize())
            return false;

        Vector3d toReference = flatReference - pointOnCurve;
        toReference.Z = 0.0;
        if (toReference.Length <= RhinoMath.ZeroTolerance)
            return false;

        double crossZ = Vector3d.CrossProduct(tangent, toReference).Z;
        signedDistance = GetSignedOffsetDistanceForSide(crossZ >= 0.0, absoluteDistance);
        return true;
    }

    private static Curve? ProjectCurveToStartElevation(Curve curve)
    {
        Plane plane = new(curve.PointAtStart, Vector3d.ZAxis);
        return Curve.ProjectToPlane(curve.ToNurbsCurve(), plane);
    }

    private static double CalculateSoftEditFactor(
        Point3d point,
        Point3d basePoint,
        double radius,
        SoftEditFalloff falloff)
    {
        if (radius <= RhinoMath.ZeroTolerance)
            return 0.0;

        return CalculateFalloffFactor(CalculatePlanDistance(basePoint, point) / radius, falloff);
    }

    /// <summary>
    /// The one falloff curve every soft edit shares: full strength at <paramref name="normalizedDistance" />
    /// zero, nothing at one. Radial soft edits measure that distance in plan from a base point; the
    /// curve-section transition measures it along the curve from a joint. Same easing either way, so a
    /// user who has learned what Smooth looks like in one place has learned it in both.
    /// </summary>
    private static double CalculateFalloffFactor(double normalizedDistance, SoftEditFalloff falloff)
    {
        double factor = Math.Clamp(1.0 - normalizedDistance, 0.0, 1.0);
        return factor > 0.0 && falloff == SoftEditFalloff.Smooth
            ? EaseInOutSine(factor)
            : factor;
    }

    private sealed class RadialSoftEditMorph : SpaceMorph
    {
        private readonly Point3d _basePoint;
        private readonly double _radius;
        private readonly Vector3d _vector;
        private readonly SoftEditFalloff _falloff;
        private readonly bool _fixEnds;
        private readonly Point3d _fixedStart;
        private readonly Point3d _fixedEnd;
        private readonly double _fixedEndToleranceSquared;

        public RadialSoftEditMorph(
            Point3d basePoint,
            double radius,
            Vector3d vector,
            SoftEditFalloff falloff,
            bool fixEnds,
            Point3d fixedStart,
            Point3d fixedEnd,
            double tolerance)
        {
            _basePoint = basePoint;
            _radius = radius;
            _vector = vector;
            _falloff = falloff;
            _fixEnds = fixEnds;
            _fixedStart = fixedStart;
            _fixedEnd = fixedEnd;
            _fixedEndToleranceSquared = tolerance * tolerance;
        }

        public override Point3d MorphPoint(Point3d point)
        {
            if (_fixEnds &&
                (DistanceSquared(point, _fixedStart) <= _fixedEndToleranceSquared ||
                 DistanceSquared(point, _fixedEnd) <= _fixedEndToleranceSquared))
            {
                return point;
            }

            double factor = CalculateSoftEditFactor(point, _basePoint, _radius, _falloff);
            return point + (_vector * factor);
        }

        private static double DistanceSquared(Point3d a, Point3d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            return (dx * dx) + (dy * dy) + (dz * dz);
        }
    }

    private static bool TryExtractPolyline(Curve curve, double tolerance, out Polyline polyline)
    {
        if (curve.TryGetPolyline(out polyline))
            return polyline.Count >= 2;

        PolylineCurve? polylineCurve = curve.ToPolyline(tolerance, Math.PI / 36.0, 0.0, 0.0);
        return polylineCurve != null &&
               polylineCurve.TryGetPolyline(out polyline) &&
               polyline.Count >= 2;
    }

    private static bool TryValidateDegreeOnePolyline(Curve curve, out string? error)
    {
        if (curve.Degree != 1 || !curve.IsPolyline())
        {
            error = "Selected curve is not a degree-1 polyline.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
