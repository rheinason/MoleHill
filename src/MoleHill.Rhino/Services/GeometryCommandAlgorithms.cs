using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal enum SoftEditFalloff
{
    Linear,
    Smooth
}

internal static class GeometryCommandAlgorithms
{
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

    public static double CalculateDefaultLiftFactor(UnitSystem unitSystem)
    {
        return ModelUnits.FromMeters(2.0, unitSystem);
    }

    public static bool TryGetLiftedOffsetPolyline(
        Curve sourceCurve,
        double offsetDistance,
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
            liftedPoints.Add(new Point3d(point.X, point.Y, sourcePoint.Z));
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

    public static bool IsPointInsideNestedBoundaries(Point3d point, IReadOnlyList<Curve> boundaries, double tolerance)
    {
        int containmentCount = 0;
        for (int i = 0; i < boundaries.Count; i++)
        {
            PointContainment containment = boundaries[i].Contains(point, Plane.WorldXY, tolerance);
            if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                containmentCount++;
        }

        return containmentCount % 2 == 1;
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

        double factor = Math.Max(0.0, 1.0 - (CalculatePlanDistance(basePoint, point) / radius));
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
