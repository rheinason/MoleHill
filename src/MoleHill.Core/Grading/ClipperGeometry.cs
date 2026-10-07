using Clipper2Lib;
using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Grading;

internal static class ClipperGeometry
{
    internal static double SimplifyTolerance(double tolerance) =>
        ScaleAwareTolerance.ResolveLength(tolerance, tolerance);

    internal static bool TryUnionClosedLoops(
        IReadOnlyList<double[]> loops,
        double tolerance,
        out List<double[]> resultLoops)
    {
        resultLoops = new List<double[]>();
        if (loops.Count == 0)
            return false;

        PathsD subject = BuildClosedPaths(loops);
        if (subject.Count == 0)
            return false;

        subject = Clipper.SimplifyPaths(subject, SimplifyTolerance(tolerance), isClosedPath: true);
        if (subject.Count == 0)
            return false;

        PathsD solution = Clipper.Union(subject, new PathsD(), FillRule.NonZero, PrecisionFor(tolerance));
        resultLoops = ToClosedLoops(solution, tolerance);
        return resultLoops.Count > 0;
    }

    internal static bool TryIntersectClosedLoops(
        IReadOnlyList<double[]> subjectLoops,
        double[] clipLoop,
        double tolerance,
        out List<double[]> resultLoops)
    {
        resultLoops = new List<double[]>();
        if (subjectLoops.Count == 0 || clipLoop.Length < 6)
            return false;

        PathsD subject = BuildClosedPaths(subjectLoops);
        PathsD clip = BuildClosedPaths([clipLoop]);
        if (subject.Count == 0 || clip.Count == 0)
            return false;

        subject = Clipper.SimplifyPaths(subject, SimplifyTolerance(tolerance), isClosedPath: true);
        clip = Clipper.SimplifyPaths(clip, SimplifyTolerance(tolerance), isClosedPath: true);
        if (subject.Count == 0 || clip.Count == 0)
            return false;

        PathsD solution = Clipper.Intersect(subject, clip, FillRule.NonZero, PrecisionFor(tolerance));
        resultLoops = ToClosedLoops(solution, tolerance);
        return resultLoops.Count > 0;
    }

    /// <summary>
    /// Offsets a closed loop outward (positive <paramref name="delta"/>) or inward (negative) by the
    /// given distance, returning the resulting outer loop. Used to grow a grading region a margin
    /// beyond its daylight so the remesh patch boundary sits in untouched, terrain-elevation ground.
    /// </summary>
    internal static bool TryOffsetClosedLoop(double[] loop, double delta, double tolerance, out double[] offsetLoop)
    {
        offsetLoop = Array.Empty<double>();
        if (loop.Length < 6)
            return false;

        PathsD subject = BuildClosedPaths([loop]);
        if (subject.Count == 0)
            return false;

        PathsD inflated = Clipper.InflatePaths(
            subject,
            delta,
            JoinType.Round,
            EndType.Polygon,
            miterLimit: 2.0,
            precision: PrecisionFor(tolerance),
            arcTolerance: 0.0);
        List<double[]> loops = ToClosedLoops(inflated, tolerance);
        return TryPickLargestLoop(loops, out offsetLoop);
    }

    /// <summary>
    /// Inflates an open polyline (e.g. a Grade Path centerline) into a closed ribbon loop of the given
    /// half-width, squared off at both ends. Used to derive a zone boundary that tracks a path's width
    /// without the caller drawing/maintaining a separate polygon.
    /// </summary>
    internal static bool TryInflateOpenPolylineToLoop(double[] xyPolyline, int vertexCount, double delta, double tolerance, out double[] loop)
    {
        loop = Array.Empty<double>();
        if (vertexCount < 2 || delta <= 0.0)
            return false;

        var path = new PathD(vertexCount);
        for (int i = 0; i < vertexCount; i++)
            path.Add(new PointD(xyPolyline[i * 2], xyPolyline[(i * 2) + 1]));

        var subject = new PathsD { path };
        PathsD inflated = Clipper.InflatePaths(
            subject,
            delta,
            JoinType.Round,
            EndType.Butt,
            miterLimit: 2.0,
            precision: PrecisionFor(tolerance),
            arcTolerance: 0.0);
        List<double[]> loops = ToClosedLoops(inflated, tolerance);
        return TryPickLargestLoop(loops, out loop);
    }

    internal static bool TrySimplifyClosedLoop(double[] loop, double tolerance, out double[] simplifiedLoop)
    {
        simplifiedLoop = Array.Empty<double>();
        if (loop.Length < 6)
            return false;

        PathsD simplified = Clipper.SimplifyPaths(BuildClosedPaths([loop]), SimplifyTolerance(tolerance), isClosedPath: true);
        List<double[]> loops = ToClosedLoops(simplified, tolerance);
        if (loops.Count == 0)
            return false;

        simplifiedLoop = loops[0];
        return true;
    }

    internal static bool TrySimplifyOpenPolyline(double[] xyPolyline, double tolerance, out double[] simplifiedPolyline)
    {
        simplifiedPolyline = Array.Empty<double>();
        int pointCount = xyPolyline.Length / 2;
        if (pointCount < 2)
            return false;

        var path = new PathD(pointCount);
        for (int i = 0; i < pointCount; i++)
            path.Add(new PointD(xyPolyline[i * 2], xyPolyline[i * 2 + 1]));

        PathD simplified = Clipper.SimplifyPath(path, SimplifyTolerance(tolerance), isClosedPath: false);
        if (simplified.Count < 2)
            return false;

        var points = new List<double>(simplified.Count * 2);
        foreach (PointD point in simplified)
        {
            if (points.Count >= 2)
            {
                double dx = point.x - points[^2];
                double dy = point.y - points[^1];
                if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                    continue;
            }

            points.Add(point.x);
            points.Add(point.y);
        }

        if (points.Count < 4)
            return false;

        simplifiedPolyline = points.ToArray();
        return true;
    }

    internal static bool TryPickLargestLoop(IReadOnlyList<double[]> loops, out double[] largestLoop)
    {
        largestLoop = Array.Empty<double>();
        double largestArea = double.MinValue;
        foreach (double[] loop in loops)
        {
            double area = Math.Abs(SignedArea(loop));
            if (area <= largestArea)
                continue;

            largestArea = area;
            largestLoop = loop;
        }

        return largestLoop.Length >= 6;
    }

    internal static double SignedArea(double[] xyLoop) => Geometry2D.SignedArea(xyLoop, xyLoop.Length / 2);

    private static int PrecisionFor(double tolerance)
    {
        double resolved = Math.Max(Math.Abs(tolerance), double.Epsilon);
        // Clipper's historical PathsD default was two decimal places. A 0.001 model tolerance therefore
        // quantized at 0.01; preserve that ratio while moving the decimal precision with scaled models.
        return Math.Clamp((int)Math.Ceiling(-Math.Log10(resolved)) - 1, -8, 8);
    }

    private static PathsD BuildClosedPaths(IReadOnlyList<double[]> loops)
    {
        var paths = new PathsD(loops.Count);
        foreach (double[] loop in loops)
        {
            int vertexCount = loop.Length / 2;
            if (vertexCount < 3)
                continue;

            var path = new PathD(vertexCount);
            for (int i = 0; i < vertexCount; i++)
                path.Add(new PointD(loop[i * 2], loop[i * 2 + 1]));

            paths.Add(path);
        }

        return paths;
    }

    private static List<double[]> ToClosedLoops(PathsD paths, double tolerance)
    {
        var loops = new List<double[]>(paths.Count);
        foreach (PathD path in paths)
        {
            if (path.Count < 3)
                continue;

            var loop = new List<double>(path.Count * 2);
            for (int i = 0; i < path.Count; i++)
            {
                PointD point = path[i];
                if (loop.Count >= 2)
                {
                    double dx = point.x - loop[^2];
                    double dy = point.y - loop[^1];
                    if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                        continue;
                }

                loop.Add(point.x);
                loop.Add(point.y);
            }

            if (loop.Count >= 6)
            {
                double dx = loop[0] - loop[^2];
                double dy = loop[1] - loop[^1];
                if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                {
                    loop.RemoveRange(loop.Count - 2, 2);
                }
            }

            if (loop.Count >= 6)
                loops.Add(loop.ToArray());
        }

        return loops;
    }
}
