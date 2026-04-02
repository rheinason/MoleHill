using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

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
        return unitSystem switch
        {
            UnitSystem.Millimeters => 2000.0,
            UnitSystem.Meters => 2.0,
            _ => 2.0
        };
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

    public static bool TryGetCurveEditPoints(Curve curve, double tolerance, out List<Point3d> points)
    {
        points = new List<Point3d>();

        if (curve is PolylineCurve polylineCurve)
        {
            for (int i = 0; i < polylineCurve.PointCount; i++)
                points.Add(polylineCurve.Point(i));
            return points.Count > 1;
        }

        NurbsCurve? nurbsCurve = curve.ToNurbsCurve();
        if (nurbsCurve == null)
            return false;

        if (nurbsCurve.Degree == 1)
        {
            for (int i = 0; i < nurbsCurve.Points.Count; i++)
                points.Add(nurbsCurve.Points[i].Location);
            return points.Count > 1;
        }

        for (int i = 0; i < nurbsCurve.Points.Count; i++)
            points.Add(nurbsCurve.Points[i].Location);

        return points.Count > 1;
    }

    public static bool TryCreateSoftEditedCurve(
        Curve sourceCurve,
        IReadOnlyList<Point3d> adjustedPoints,
        double tolerance,
        out Curve? resultCurve,
        out string? error)
    {
        resultCurve = null;

        if (adjustedPoints.Count <= 1)
        {
            error = "Soft edit requires at least two editable points.";
            return false;
        }

        if (sourceCurve is PolylineCurve)
        {
            var polyline = new Polyline(adjustedPoints);
            if (sourceCurve.IsClosed && !polyline.IsClosed)
                polyline.Add(polyline[0]);

            resultCurve = new PolylineCurve(polyline);
            error = null;
            return true;
        }

        NurbsCurve? nurbsCurve = sourceCurve.ToNurbsCurve();
        if (nurbsCurve == null)
        {
            error = "Failed to convert the curve to a NURBS representation.";
            return false;
        }

        if (nurbsCurve.Points.Count != adjustedPoints.Count)
        {
            error = "Editable point count did not match the curve representation.";
            return false;
        }

        if (nurbsCurve.Degree == 1)
        {
            var polyline = new Polyline(adjustedPoints);
            if (sourceCurve.IsClosed && !polyline.IsClosed)
                polyline.Add(polyline[0]);

            resultCurve = new PolylineCurve(polyline);
            error = null;
            return true;
        }

        for (int i = 0; i < adjustedPoints.Count; i++)
        {
            ControlPoint controlPoint = nurbsCurve.Points[i];
            if (!nurbsCurve.Points.SetPoint(i, adjustedPoints[i], controlPoint.Weight))
            {
                error = "Failed to update the curve control points.";
                return false;
            }
        }

        resultCurve = nurbsCurve;
        error = null;
        return true;
    }

    public static List<Point3d> CalculateSoftEditPoints(
        IReadOnlyList<Point3d> points,
        Point3d basePoint,
        double radius,
        Vector3d vector)
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
            double factor = Math.Max(0.0, 1.0 - (Distance2d(basePoint, point) / radius));
            if (factor > 0.0)
                factor = EaseInOutSine(factor);

            adjusted.Add(point + (vector * factor));
        }

        return adjusted;
    }

    public static double EaseInOutSine(double t)
    {
        return -(Math.Cos(Math.PI * t) - 1.0) / 2.0;
    }

    public static double GetSoftEditTolerance(UnitSystem unitSystem)
    {
        return unitSystem switch
        {
            UnitSystem.Meters => 0.5,
            UnitSystem.Millimeters => 500.0,
            _ => 0.5
        };
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

    private static double Distance2d(Point3d a, Point3d b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
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
