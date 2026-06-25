using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private readonly record struct ConstraintLoop(double[] XyVertices, int VertexCount);

    private static ConstraintLoop BuildClosedConstraintLoop(double[] xyVertices, int vertexCount, double maxSegmentLength, double tolerance)
    {
        if (vertexCount < 3 || maxSegmentLength <= tolerance)
            return new ConstraintLoop((double[])xyVertices.Clone(), vertexCount);

        var points = new List<double>(vertexCount * 4);
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double ax = xyVertices[i * 2];
            double ay = xyVertices[i * 2 + 1];
            double bx = xyVertices[next * 2];
            double by = xyVertices[next * 2 + 1];
            double length = Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
            int divisions = Math.Max(1, (int)Math.Ceiling(length / maxSegmentLength));

            for (int step = 0; step < divisions; step++)
            {
                double t = step / (double)divisions;
                AddLoopPoint(points, ax + ((bx - ax) * t), ay + ((by - ay) * t), tolerance);
            }
        }

        return new ConstraintLoop(points.ToArray(), points.Count / 2);
    }

    private static void AddLoopPoint(List<double> points, double x, double y, double tolerance)
    {
        if (points.Count >= 2)
        {
            double dx = x - points[^2];
            double dy = y - points[^1];
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                return;
        }

        points.Add(x);
        points.Add(y);
    }

    private static double[] CreateConstraintPoints(double[] xyVertices, int vertexCount)
    {
        var points = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            points[i * 3] = xyVertices[i * 2];
            points[i * 3 + 1] = xyVertices[i * 2 + 1];
        }

        return points;
    }

    private static bool TryBuildExpandedOffsetPolygon(
        double[] polygonXy,
        int vertexCount,
        double[] distances,
        int cornerFanSegments,
        TerrainFaceGrid? TerrainFaceGrid,
        PadBoundary? pad,
        out double[] expandedPolygonXy,
        out double[] expandedOffsetXy,
        out string? failureReason)
    {
        failureReason = null;
        expandedPolygonXy = Array.Empty<double>();
        expandedOffsetXy = Array.Empty<double>();
        if (vertexCount < 3 || distances.Length < vertexCount)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight offset input was invalid.";
            return false;
        }
        bool anyPositive = false;
        for (int i = 0; i < vertexCount; i++) if (distances[i] > 1e-9) { anyPositive = true; break; }
        if (!anyPositive)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight offset did not extend beyond the pad boundary.";
            return false;
        }

        double signedArea = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double x0 = polygonXy[i * 2];
            double y0 = polygonXy[i * 2 + 1];
            double x1 = polygonXy[next * 2];
            double y1 = polygonXy[next * 2 + 1];
            signedArea += x0 * y1 - x1 * y0;
        }

        if (Math.Abs(signedArea) < 1e-12)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the pad boundary is degenerate.";
            return false;
        }

        bool ccw = signedArea > 0;
        var boundaryList = new List<double>(vertexCount * 2);
        var offsetList = new List<double>(vertexCount * 2);

        for (int i = 0; i < vertexCount; i++)
        {
            int prev = (i + vertexCount - 1) % vertexCount;
            int next = (i + 1) % vertexCount;

            double x0 = polygonXy[prev * 2];
            double y0 = polygonXy[prev * 2 + 1];
            double x1 = polygonXy[i * 2];
            double y1 = polygonXy[i * 2 + 1];
            double x2 = polygonXy[next * 2];
            double y2 = polygonXy[next * 2 + 1];

            double dx0 = x1 - x0;
            double dy0 = y1 - y0;
            double dx1 = x2 - x1;
            double dy1 = y2 - y1;
            double len0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            if (len0 < 1e-12 || len1 < 1e-12)
            {
                failureReason = "Grade Pad shoulder ring was skipped because the pad boundary contains repeated or zero-length edges.";
                return false;
            }

            double n0x = ccw ? dy0 / len0 : -dy0 / len0;
            double n0y = ccw ? -dx0 / len0 : dx0 / len0;
            double n1x = ccw ? dy1 / len1 : -dy1 / len1;
            double n1y = ccw ? -dx1 / len1 : dx1 / len1;
            double turnCross = dx0 * dy1 - dy0 * dx1;
            bool isReentrant = ccw ? turnCross < -1e-12 : turnCross > 1e-12;

            double d = distances[i];

            if (d <= 1e-9 || isReentrant)
            {
                boundaryList.Add(x1);
                boundaryList.Add(y1);
                offsetList.Add(x1);
                offsetList.Add(y1);
                continue;
            }

            if (cornerFanSegments >= 1 && !isReentrant)
            {
                double prevAngle = Math.Atan2(n0y, n0x);
                double nextAngle = Math.Atan2(n1y, n1x);
                double sweep = ComputeOutwardAngleSweep(prevAngle, nextAngle);
                if (Math.Abs(sweep) > 10.0 * Math.PI / 180.0)
                {
                    int fanCount = cornerFanSegments + 1;
                    for (int f = 0; f < fanCount; f++)
                    {
                        double theta = prevAngle + sweep * f / (fanCount - 1);
                        double rayX = Math.Cos(theta);
                        double rayY = Math.Sin(theta);
                        double rayDistance = ComputeCornerFanRayDistance(TerrainFaceGrid, pad, x1, y1, rayX, rayY, d);
                        boundaryList.Add(x1);
                        boundaryList.Add(y1);
                        offsetList.Add(x1 + rayX * rayDistance);
                        offsetList.Add(y1 + rayY * rayDistance);
                    }
                    continue;
                }
            }

            double line0x = x1 + n0x * d;
            double line0y = y1 + n0y * d;
            double line1x = x1 + n1x * d;
            double line1y = y1 + n1y * d;

            if (TryIntersectLines(line0x, line0y, dx0, dy0, line1x, line1y, dx1, dy1, out double ix, out double iy))
            {
                double offsetLen = Math.Sqrt((ix - x1) * (ix - x1) + (iy - y1) * (iy - y1));
                if (offsetLen <= d * 4.0 && !double.IsNaN(offsetLen) && !double.IsInfinity(offsetLen))
                {
                    boundaryList.Add(x1);
                    boundaryList.Add(y1);
                    offsetList.Add(ix);
                    offsetList.Add(iy);
                    continue;
                }
            }

            double point0x = x1 + (n0x * d);
            double point0y = y1 + (n0y * d);
            double point1x = x1 + (n1x * d);
            double point1y = y1 + (n1y * d);
            double option0Sq = ((point0x - x1) * (point0x - x1)) + ((point0y - y1) * (point0y - y1));
            double option1Sq = ((point1x - x1) * (point1x - x1)) + ((point1y - y1) * (point1y - y1));
            boundaryList.Add(x1);
            boundaryList.Add(y1);
            if (option0Sq <= option1Sq)
            {
                offsetList.Add(point0x);
                offsetList.Add(point0y);
            }
            else
            {
                offsetList.Add(point1x);
                offsetList.Add(point1y);
            }
        }

        expandedPolygonXy = boundaryList.ToArray();
        expandedOffsetXy = offsetList.ToArray();
        int expandedCount = expandedPolygonXy.Length / 2;

        if (cornerFanSegments == 0 && ClosedPolylineHasSelfIntersection(expandedOffsetXy, expandedCount))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight offset self-intersected.";
            expandedPolygonXy = Array.Empty<double>();
            expandedOffsetXy = Array.Empty<double>();
            return false;
        }

        return true;
    }

    private static double ComputeCornerFanRayDistance(
        TerrainFaceGrid? TerrainFaceGrid,
        PadBoundary? pad,
        double boundaryX,
        double boundaryY,
        double dirX,
        double dirY,
        double fallbackDistance)
    {
        double distance = Math.Max(0.0, fallbackDistance);
        if (TerrainFaceGrid == null || pad == null || distance <= 1e-9)
            return distance;

        double boundaryZ = pad.EvaluateZ(boundaryX, boundaryY);
        double terrainZ = TerrainFaceGrid.InterpolateZ(boundaryX, boundaryY);
        double branchSign = Math.Sign(terrainZ - boundaryZ);
        if (Math.Abs(branchSign) <= 1e-12)
            return distance;

        double slopeRatio = pad.SlopeRatioFor(branchSign);
        if (slopeRatio <= 1e-12)
            return distance;

        return ComputePadDaylightReach(
            TerrainFaceGrid,
            boundaryX,
            boundaryY,
            boundaryZ,
            dirX,
            dirY,
            slopeRatio,
            branchSign,
            distance,
            pad.MaxDistance);
    }

    internal static double ComputeOutwardAngleSweep(double fromAngle, double toAngle)
    {
        bool ccw = true; // BUGGY-TEST: force old CCW-only logic
        double diff = toAngle - fromAngle;
        diff = ((diff % (2.0 * Math.PI)) + 2.0 * Math.PI) % (2.0 * Math.PI);
        if (!ccw && diff < Math.PI) diff = diff - 2.0 * Math.PI;
        if (ccw && diff > Math.PI) diff = diff - 2.0 * Math.PI;
        return diff;
    }

    private static bool TryBuildOffsetPolygon(
        double[] polygonXy,
        int vertexCount,
        double[] distances,
        out double[] offsetXy,
        out string? failureReason)
    {
        bool ok = TryBuildExpandedOffsetPolygon(
            polygonXy, vertexCount, distances, 0,
            null, null,
            out _, out offsetXy, out failureReason);
        return ok;
    }

    private static bool ClosedPolylineHasSelfIntersection(double[] xy, int vertexCount)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            int iNext = (i + 1) % vertexCount;
            double ax = xy[i * 2];
            double ay = xy[i * 2 + 1];
            double bx = xy[iNext * 2];
            double by = xy[iNext * 2 + 1];
            if (((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)) <= 1e-12)
                continue;

            for (int j = i + 1; j < vertexCount; j++)
            {
                int jNext = (j + 1) % vertexCount;
                if (i == j || i == jNext || iNext == j || iNext == jNext)
                    continue;
                if (i == 0 && jNext == 0)
                    continue;

                double cx = xy[j * 2];
                double cy = xy[j * 2 + 1];
                double dx = xy[jNext * 2];
                double dy = xy[jNext * 2 + 1];
                if (((dx - cx) * (dx - cx)) + ((dy - cy) * (dy - cy)) <= 1e-12)
                    continue;

                if (SegmentsIntersect(ax, ay, bx, by, cx, cy, dx, dy))
                    return true;
            }
        }

        return false;
    }

    private static bool SegmentsIntersect(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        double o1 = Orientation(ax, ay, bx, by, cx, cy);
        double o2 = Orientation(ax, ay, bx, by, dx, dy);
        double o3 = Orientation(cx, cy, dx, dy, ax, ay);
        double o4 = Orientation(cx, cy, dx, dy, bx, by);

        if ((o1 > 0.0 && o2 < 0.0 || o1 < 0.0 && o2 > 0.0) &&
            (o3 > 0.0 && o4 < 0.0 || o3 < 0.0 && o4 > 0.0))
        {
            return true;
        }

        return Math.Abs(o1) <= 1e-12 && OnSegment(ax, ay, bx, by, cx, cy) ||
               Math.Abs(o2) <= 1e-12 && OnSegment(ax, ay, bx, by, dx, dy) ||
               Math.Abs(o3) <= 1e-12 && OnSegment(cx, cy, dx, dy, ax, ay) ||
               Math.Abs(o4) <= 1e-12 && OnSegment(cx, cy, dx, dy, bx, by);
    }

    private static double Orientation(double ax, double ay, double bx, double by, double cx, double cy)
    {
        return ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
    }

    private static bool PointInTriangle(
        double px,
        double py,
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy)
    {
        const double tolerance = 1e-12;
        double o1 = Orientation(ax, ay, bx, by, px, py);
        double o2 = Orientation(bx, by, cx, cy, px, py);
        double o3 = Orientation(cx, cy, ax, ay, px, py);
        bool hasNegative = o1 < -tolerance || o2 < -tolerance || o3 < -tolerance;
        bool hasPositive = o1 > tolerance || o2 > tolerance || o3 > tolerance;
        return !(hasNegative && hasPositive);
    }

    private static bool OnSegment(double ax, double ay, double bx, double by, double px, double py)
    {
        return px >= Math.Min(ax, bx) - 1e-12 &&
               px <= Math.Max(ax, bx) + 1e-12 &&
               py >= Math.Min(ay, by) - 1e-12 &&
               py <= Math.Max(ay, by) + 1e-12;
    }
}
