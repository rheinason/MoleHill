// Reusable geometry algorithms for selected terrain-input preparation commands.
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace MoleHill.Rhino.Services;

internal readonly record struct TerrainValidationOptions(
    bool RemoveDuplicateObjects,
    bool JoinNearEndpoints,
    bool CollapseShortSegments,
    bool CleanDuplicateVertices,
    double Tolerance);

internal sealed class TerrainValidationSummary
{
    public int SelectedObjects { get; set; }
    public int EligibleObjects { get; set; }
    public int DuplicateObjectsRemoved { get; set; }
    public int DuplicateSegmentsRemoved { get; set; }
    public int JoinedCurveCount { get; set; }
    public int ShortSegmentsCollapsed { get; set; }
    public int DuplicateVerticesRemoved { get; set; }

    public int OutputObjects { get; set; }
}

internal sealed class TerrainInputGeometry
{
    public required Guid ObjectId { get; init; }
    public Curve? Curve { get; init; }
    public Point3d? Point { get; init; }
    public required ObjectAttributes Attributes { get; init; }
    public bool GeometryChanged { get; init; }

    public TerrainInputGeometry Duplicate()
    {
        return new TerrainInputGeometry
        {
            ObjectId = ObjectId,
            Curve = Curve?.DuplicateCurve(),
            Point = Point,
            Attributes = Attributes.Duplicate(),
            GeometryChanged = GeometryChanged
        };
    }
}

internal sealed class TerrainValidationPreparation
{
    public required IReadOnlyList<TerrainInputGeometry> Objects { get; init; }
    public required TerrainValidationSummary Summary { get; init; }
}

internal readonly record struct TerrainCurveSplitParameters(
    double[] Parameters,
    bool HasIntersection);

internal static class TerrainInputCommandAlgorithms
{
    public static TerrainValidationPreparation PrepareValidation(
        IReadOnlyList<TerrainInputGeometry> source,
        TerrainValidationOptions options)
    {
        double tolerance = Math.Max(Math.Abs(options.Tolerance), RhinoMath.ZeroTolerance);
        var summary = new TerrainValidationSummary
        {
            SelectedObjects = source.Count,
            EligibleObjects = source.Count
        };

        var working = source.Select(item => item.Duplicate()).ToList();

        if (options.RemoveDuplicateObjects)
            RemoveDuplicateObjects(working, tolerance, summary);

        if (options.JoinNearEndpoints)
            JoinNearEndpoints(working, tolerance, summary);

        for (int i = 0; i < working.Count; i++)
        {
            TerrainInputGeometry item = working[i];
            if (item.Curve == null ||
                (!options.RemoveDuplicateObjects && !options.CollapseShortSegments && !options.CleanDuplicateVertices))
                continue;

            if (!TryCleanCurve(
                    item.Curve,
                    tolerance,
                    options.RemoveDuplicateObjects,
                    options.CollapseShortSegments,
                    options.CleanDuplicateVertices,
                    out Curve? cleaned,
                    out int collapsed,
                    out int duplicates,
                    out int duplicateSegments))
            {
                continue;
            }

            item.Curve.Dispose();
            if (cleaned == null)
            {
                working.RemoveAt(i);
                i--;
                summary.ShortSegmentsCollapsed += collapsed;
                summary.DuplicateVerticesRemoved += duplicates;
                summary.DuplicateSegmentsRemoved += duplicateSegments;
                continue;
            }

            working[i] = new TerrainInputGeometry
            {
                ObjectId = item.ObjectId,
                Curve = cleaned,
                Attributes = item.Attributes,
                GeometryChanged = true
            };
            summary.ShortSegmentsCollapsed += collapsed;
            summary.DuplicateVerticesRemoved += duplicates;
            summary.DuplicateSegmentsRemoved += duplicateSegments;
        }

        summary.OutputObjects = working.Count;
        return new TerrainValidationPreparation { Objects = working, Summary = summary };
    }

    public static TerrainCurveSplitParameters GetIntersectionSplitParameters(
        Curve first,
        Curve second,
        double tolerance)
    {
        var parameters = new List<double>();
        CurveIntersections intersections = Intersection.CurveCurve(
            first,
            second,
            Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance),
            Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance));

        for (int i = 0; i < intersections.Count; i++)
        {
            IntersectionEvent intersection = intersections[i];
            if (intersection.IsPoint)
            {
                AddParameter(parameters, first, intersection.ParameterA, tolerance);
            }
            else
            {
                AddParameter(parameters, first, intersection.OverlapA.T0, tolerance);
                AddParameter(parameters, first, intersection.OverlapA.T1, tolerance);
            }
        }

        double[] unique = NormalizeSplitParameters(first, parameters, tolerance);
        return new TerrainCurveSplitParameters(unique, unique.Length > 0);
    }

    public static double[] NormalizeSplitParameters(
        Curve curve,
        IEnumerable<double> parameters,
        double tolerance)
    {
        double effectiveTolerance = Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance);
        double parameterEpsilon = Math.Max(Math.Abs(curve.Domain.Length) * 1e-12, RhinoMath.ZeroTolerance);
        double[] ordered = parameters
            .Where(parameter => double.IsFinite(parameter) &&
                                parameter > curve.Domain.T0 &&
                                parameter < curve.Domain.T1)
            .OrderBy(parameter => parameter)
            .ToArray();
        var unique = new List<double>(ordered.Length);
        foreach (double parameter in ordered)
        {
            if (unique.Count == 0)
            {
                unique.Add(parameter);
                continue;
            }

            double previous = unique[^1];
            if (Math.Abs(parameter - previous) <= parameterEpsilon)
                continue;

            double separation = curve.GetLength(new Interval(previous, parameter));
            if (!double.IsFinite(separation) || separation > effectiveTolerance)
                unique.Add(parameter);
        }

        return unique.ToArray();
    }

    public static bool TryCreateDrapedPolyline(
        Curve source,
        IReadOnlyList<Mesh> meshes,
        double spacing,
        double tolerance,
        out Polyline polyline,
        out string? error)
    {
        polyline = new Polyline();
        error = null;

        if (meshes.Count == 0)
        {
            error = "The drape source does not contain a usable mesh.";
            return false;
        }

        double length = source.GetLength();
        double effectiveSpacing = Math.Max(Math.Abs(spacing), Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance));
        if (length <= Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance))
        {
            error = "The source curve is too short to drape.";
            return false;
        }

        int segmentCount = Math.Max(1, (int)Math.Ceiling(length / effectiveSpacing));
        var points = new List<Point3d>(segmentCount + 1);
        for (int i = 0; i <= segmentCount; i++)
        {
            double distance = length * i / segmentCount;
            if (!source.LengthParameter(distance, out double parameter))
                parameter = i == 0 ? source.Domain.T0 : source.Domain.T1;

            Point3d sourcePoint = source.PointAt(parameter);
            Point3d planPoint = new(sourcePoint.X, sourcePoint.Y, 0.0);
            if (!TryProjectPointToHighestMesh(meshes, planPoint, tolerance, out Point3d hit))
            {
                error = $"No vertical terrain hit was found near sample {i + 1} of {segmentCount + 1}.";
                return false;
            }

            if (points.Count == 0 || points[^1].DistanceTo(hit) > Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance))
                points.Add(hit);
        }

        if (points.Count < 2)
        {
            error = "Drape produced fewer than two usable points.";
            return false;
        }

        polyline = new Polyline(points);
        return polyline.IsValid;
    }

    public static bool TryProjectPointToHighestMesh(
        IReadOnlyList<Mesh> meshes,
        Point3d point,
        double tolerance,
        out Point3d projectedPoint)
    {
        projectedPoint = Point3d.Unset;
        double bestZ = double.NegativeInfinity;
        bool found = false;

        foreach (Mesh mesh in meshes)
        {
            BoundingBox bounds = mesh.GetBoundingBox(true);
            if (!bounds.IsValid)
                continue;

            double height = Math.Max(bounds.Max.Z - bounds.Min.Z, Math.Abs(tolerance));
            double padding = Math.Max(Math.Max(Math.Abs(tolerance), height) * 2.0, double.Epsilon);
            var line = new Line(
                new Point3d(point.X, point.Y, bounds.Min.Z - padding),
                new Point3d(point.X, point.Y, bounds.Max.Z + padding));

            Point3d[] hits = Intersection.MeshLineSorted(mesh, line, out _);
            if (hits == null)
                continue;

            foreach (Point3d hit in hits)
            {
                if (!hit.IsValid || hit.Z <= bestZ)
                    continue;

                bestZ = hit.Z;
                projectedPoint = new Point3d(point.X, point.Y, hit.Z);
                found = true;
            }
        }

        return found;
    }

    public static bool TryCreateWallRails(
        IReadOnlyList<Point3d> sourcePoints,
        double signedPlanOffset,
        double heightOffset,
        double tolerance,
        out Polyline sourceRail,
        out Polyline generatedRail,
        out string? error)
    {
        sourceRail = new Polyline();
        generatedRail = new Polyline();
        error = null;

        if (sourcePoints.Count < 2)
        {
            error = "A wall requires at least two points.";
            return false;
        }

        sourceRail = new Polyline(sourcePoints);
        if (!sourceRail.IsValid)
        {
            error = "The drawn wall rail is invalid.";
            return false;
        }

        var sourceCurve = new PolylineCurve(sourceRail);
        if (!GeometryCommandAlgorithms.TryGetOffsetFeaturePolyline(
                sourceCurve,
                signedPlanOffset,
                heightOffset,
                Math.Max(Math.Abs(tolerance), RhinoMath.ZeroTolerance),
                out generatedRail,
                out error))
        {
            sourceCurve.Dispose();
            return false;
        }

        sourceCurve.Dispose();
        if (!generatedRail.IsValid)
        {
            error = "The generated wall rail is invalid.";
            return false;
        }

        return true;
    }

    private static void RemoveDuplicateObjects(
        List<TerrainInputGeometry> working,
        double tolerance,
        TerrainValidationSummary summary)
    {
        for (int i = working.Count - 1; i >= 0; i--)
        {
            bool duplicate = false;
            for (int j = 0; j < i; j++)
            {
                if (AreEquivalent(working[i], working[j], tolerance))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
                continue;

            working[i].Curve?.Dispose();
            working.RemoveAt(i);
            summary.DuplicateObjectsRemoved++;
        }
    }

    private static void JoinNearEndpoints(
        List<TerrainInputGeometry> working,
        double tolerance,
        TerrainValidationSummary summary)
    {
        for (int i = 0; i < working.Count; i++)
        {
            TerrainInputGeometry first = working[i];
            if (first.Curve == null)
                continue;

            bool joinedCurve;
            do
            {
                joinedCurve = false;
                for (int j = i + 1; j < working.Count; j++)
                {
                    TerrainInputGeometry second = working[j];
                    if (second.Curve == null || first.Attributes.LayerIndex != second.Attributes.LayerIndex)
                        continue;

                    Curve[] joined = Curve.JoinCurves(new[] { first.Curve, second.Curve }, tolerance);
                    if (joined.Length != 1)
                    {
                        DisposeCurves(joined);
                        continue;
                    }

                    first.Curve.Dispose();
                    second.Curve.Dispose();
                    first = new TerrainInputGeometry
                    {
                        ObjectId = first.ObjectId,
                        Curve = joined[0],
                        Attributes = first.Attributes,
                        GeometryChanged = true
                    };
                    working[i] = first;
                    working.RemoveAt(j);
                    summary.JoinedCurveCount++;
                    joinedCurve = true;
                    break;
                }
            }
            while (joinedCurve);
        }
    }

    private static bool AreEquivalent(TerrainInputGeometry first, TerrainInputGeometry second, double tolerance)
    {
        if (first.Attributes.LayerIndex != second.Attributes.LayerIndex)
            return false;

        if (first.Point is { } firstPoint && second.Point is { } secondPoint)
            return firstPoint.DistanceTo(secondPoint) <= tolerance;

        if (first.Curve == null || second.Curve == null)
            return false;

        Curve a = first.Curve;
        Curve b = second.Curve;
        if (a.IsClosed != b.IsClosed || Math.Abs(a.GetLength() - b.GetLength()) > tolerance)
            return false;

        bool sameDirection = true;
        bool reverseDirection = true;
        for (int i = 0; i <= 8; i++)
        {
            double t = i / 8.0;
            Point3d aPoint = a.PointAt(a.Domain.ParameterAt(t));
            Point3d bPoint = b.PointAt(b.Domain.ParameterAt(t));
            Point3d reversePoint = b.PointAt(b.Domain.ParameterAt(1.0 - t));
            sameDirection &= aPoint.DistanceTo(bPoint) <= tolerance;
            reverseDirection &= aPoint.DistanceTo(reversePoint) <= tolerance;
        }

        return sameDirection || reverseDirection;
    }

    private static bool TryCleanCurve(
        Curve source,
        double tolerance,
        bool removeDuplicateSegments,
        bool collapseShortSegments,
        bool cleanDuplicateVertices,
        out Curve? cleaned,
        out int collapsed,
        out int duplicates,
        out int duplicateSegments)
    {
        cleaned = null;
        collapsed = 0;
        duplicates = 0;
        duplicateSegments = 0;

        if (removeDuplicateSegments && source is PolyCurve)
        {
            if (TryRemoveRetracedPolyCurveSegments(
                    source,
                    tolerance,
                    out Curve? polyCurveCleaned,
                    out duplicateSegments))
            {
                cleaned = polyCurveCleaned;
                return true;
            }
        }

        if (!source.TryGetPolyline(out Polyline polyline) || polyline.Count < 2)
            return false;

        var points = polyline.ToList();

        if (removeDuplicateSegments)
            points = RemoveDuplicateSegments(points, tolerance, out duplicateSegments);

        bool closed = points.Count > 2 && points[0].DistanceTo(points[^1]) <= tolerance;
        if (closed)
            points.RemoveAt(points.Count - 1);

        if (cleanDuplicateVertices)
        {
            for (int i = points.Count - 1; i > 0; i--)
            {
                if (points[i].DistanceTo(points[i - 1]) > tolerance)
                    continue;

                points.RemoveAt(i);
                duplicates++;
            }

            if (closed && points.Count > 3 && points[^1].DistanceTo(points[0]) <= tolerance)
            {
                points.RemoveAt(points.Count - 1);
                duplicates++;
            }
        }

        if (collapseShortSegments)
        {
            bool changed;
            do
            {
                changed = false;
                if (points.Count <= (closed ? 3 : 2))
                    break;

                int firstIndex = 1;
                int lastIndexExclusive = closed ? points.Count : points.Count - 1;
                for (int i = firstIndex; i < lastIndexExclusive; i++)
                {
                    int previousIndex = (i - 1 + points.Count) % points.Count;
                    int nextIndex = (i + 1) % points.Count;
                    if (points[previousIndex].DistanceTo(points[i]) > tolerance &&
                        points[i].DistanceTo(points[nextIndex]) > tolerance)
                        continue;

                    points.RemoveAt(i);
                    collapsed++;
                    changed = true;
                    break;
                }
            }
            while (changed);
        }

        if (closed)
        {
            if (points.Count < 3)
                return false;
            points.Add(points[0]);
        }

        if (points.Count < 2)
            return false;

        bool changedGeometry = collapsed > 0 || duplicates > 0 || duplicateSegments > 0 || points.Count != polyline.Count;
        if (!changedGeometry)
            return false;

        cleaned = new PolylineCurve(new Polyline(points));
        return cleaned.IsValid;
    }

    private static bool TryRemoveRetracedPolyCurveSegments(
        Curve source,
        double tolerance,
        out Curve? cleaned,
        out int removed)
    {
        cleaned = null;
        removed = 0;

        Curve[] segments = source.DuplicateSegments();
        if (segments.Length < 2)
        {
            DisposeCurves(segments);
            return false;
        }

        int retracedSuffixStart = FindRetracedSuffixStart(segments, tolerance);
        if (retracedSuffixStart < 1)
        {
            DisposeCurves(segments);
            return false;
        }

        removed = segments.Length - retracedSuffixStart;
        Curve[] retained = segments.Take(retracedSuffixStart).ToArray();
        if (retained.Length == 1)
        {
            cleaned = retained[0].DuplicateCurve();
            DisposeCurves(segments);
            return cleaned.IsValid;
        }

        Curve[] joined = Curve.JoinCurves(retained, tolerance);
        if (joined.Length != 1)
        {
            DisposeCurves(joined);
            DisposeCurves(segments);
            cleaned = null;
            removed = 0;
            return false;
        }

        cleaned = joined[0];
        DisposeCurves(segments);
        return true;
    }

    private static int FindRetracedSuffixStart(IReadOnlyList<Curve> segments, double tolerance)
    {
        for (int suffixStart = 1; suffixStart < segments.Count; suffixStart++)
        {
            int retainedIndex = suffixStart - 1;
            bool matched = true;
            for (int suffixIndex = suffixStart; suffixIndex < segments.Count; suffixIndex++)
            {
                if (retainedIndex < 0 ||
                    !TryMatchReverseTraversal(
                        segments[retainedIndex],
                        segments[suffixIndex],
                        tolerance,
                        out bool consumedRetainedSegment))
                {
                    matched = false;
                    break;
                }

                if (consumedRetainedSegment)
                {
                    retainedIndex--;
                    continue;
                }

                // A partial final segment is the common result when Rhino joins two
                // coincident curves whose segment boundaries do not line up exactly.
                if (suffixIndex != segments.Count - 1)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return suffixStart;
        }

        return -1;
    }

    private static bool TryMatchReverseTraversal(
        Curve retained,
        Curve retraced,
        double tolerance,
        out bool consumedRetainedSegment)
    {
        consumedRetainedSegment = false;
        if (retained.PointAtEnd.DistanceTo(retraced.PointAtStart) > tolerance ||
            retraced.GetLength() > retained.GetLength() + tolerance)
        {
            return false;
        }

        double previousParameter = retained.Domain.T1;
        double parameterTolerance = Math.Max(Math.Abs(retained.Domain.Length) * 1e-10, RhinoMath.ZeroTolerance);
        for (int i = 0; i <= 8; i++)
        {
            double t = i / 8.0;
            Point3d retracedPoint = retraced.PointAt(retraced.Domain.ParameterAt(t));
            if (!retained.ClosestPoint(retracedPoint, out double retainedParameter, tolerance) ||
                retracedPoint.DistanceTo(retained.PointAt(retainedParameter)) > tolerance ||
                retainedParameter > previousParameter + parameterTolerance)
            {
                return false;
            }

            previousParameter = retainedParameter;
        }

        consumedRetainedSegment = retained.PointAtStart.DistanceTo(retraced.PointAtEnd) <= tolerance &&
                                  Math.Abs(retained.GetLength() - retraced.GetLength()) <= tolerance;
        return true;
    }

    private static void DisposeCurves(IEnumerable<Curve> curves)
    {
        foreach (Curve curve in curves)
            curve.Dispose();
    }

    private static List<Point3d> RemoveDuplicateSegments(
        IReadOnlyList<Point3d> source,
        double tolerance,
        out int removed)
    {
        removed = 0;
        if (source.Count < 2)
            return source.ToList();

        var seen = new List<(Point3d Start, Point3d End)>();
        var result = new List<Point3d> { source[0] };
        for (int i = 0; i < source.Count - 1; i++)
        {
            Point3d start = source[i];
            Point3d end = source[i + 1];
            if (start.DistanceTo(end) <= tolerance)
                continue;

            if (FindDuplicateSegment(seen, start, end, tolerance) >= 0)
            {
                removed++;
                continue;
            }

            seen.Add((start, end));
            if (result[^1].DistanceTo(start) > tolerance)
                result.Add(start);
            if (result[^1].DistanceTo(end) > tolerance)
                result.Add(end);
        }

        return result;
    }

    private static int FindDuplicateSegment(
        IReadOnlyList<(Point3d Start, Point3d End)> segments,
        Point3d start,
        Point3d end,
        double tolerance)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            (Point3d existingStart, Point3d existingEnd) = segments[i];
            bool sameDirection = existingStart.DistanceTo(start) <= tolerance &&
                                 existingEnd.DistanceTo(end) <= tolerance;
            bool reverseDirection = existingStart.DistanceTo(end) <= tolerance &&
                                    existingEnd.DistanceTo(start) <= tolerance;
            if (sameDirection || reverseDirection)
                return i;
        }

        return -1;
    }

    private static void AddParameter(List<double> parameters, Curve curve, double parameter, double tolerance)
    {
        Interval domain = curve.Domain;
        Point3d point = curve.PointAt(parameter);
        if (point.DistanceTo(curve.PointAtStart) <= tolerance || point.DistanceTo(curve.PointAtEnd) <= tolerance)
            return;

        if (parameter <= domain.T0 || parameter >= domain.T1)
            return;

        parameters.Add(parameter);
    }
}
