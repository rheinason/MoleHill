using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private static double[]? AddPadShoulderConstraint(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        List<double> xyList,
        List<double> zList,
        SpatialVertexHash vertHash,
        TerrainFaceGrid TerrainFaceGrid,
        List<(int a, int b)> segList,
        double dedupTol,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (!TryBuildShoulderLoop(
            padLoopXy,
            padLoopVertexCount,
            shoulderDistances,
            boundaryLoop,
            boundaryVertexCount,
            dedupTol,
            out var shoulderXy,
            out _))
        {
            return null;
        }

        int shoulderVertexCount = shoulderXy.Length / 2;
        var shoulderIndices = new int[shoulderVertexCount];
        for (int i = 0; i < shoulderVertexCount; i++)
        {
            double px = shoulderXy[i * 2];
            double py = shoulderXy[i * 2 + 1];

            int near = vertHash.FindNearest(xyList, px, py, dedupTol);
            if (near >= 0)
            {
                shoulderIndices[i] = near;
            }
            else
            {
                shoulderIndices[i] = zList.Count;
                xyList.Add(px);
                xyList.Add(py);
                zList.Add(TerrainFaceGrid.InterpolateZ(px, py));
                vertHash.Insert(shoulderIndices[i], px, py);
            }
        }

        int AddVertex(double x, double y)
        {
            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0)
                return near;
            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(TerrainFaceGrid.InterpolateZ(x, y));
            vertHash.Insert(idx, x, y);
            return idx;
        }

        // Add shoulder ring segments, clipping each at the first barrier hit.
        // When clipped, the arc terminates at the barrier intersection, producing
        // open support runs instead of a single closed ring.
        for (int i = 0; i < shoulderVertexCount; i++)
        {
            int next = (i + 1) % shoulderVertexCount;
            double ax = shoulderXy[i * 2],    ay = shoulderXy[i * 2 + 1];
            double bx = shoulderXy[next * 2], by = shoulderXy[next * 2 + 1];

            bool clipped = GradingBarriers.TryClipSegment(
                barriers, ax, ay, bx, by,
                barrierScratch, barrierCandidates,
                out double cbx, out double cby);

            int startIdx = shoulderIndices[i];
            int endIdx = clipped ? AddVertex(cbx, cby) : shoulderIndices[next];

            if (startIdx != endIdx)
                segList.Add((startIdx, endIdx));
        }

        return shoulderXy;
    }

    private static bool TryBuildShoulderLoop(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderXy,
        out string? skipReason)
    {
        shoulderXy = Array.Empty<double>();
        skipReason = null;
        double maxDist = 0;
        foreach (double d in shoulderDistances) if (d > maxDist) maxDist = d;
        if (maxDist <= tolerance)
            return false;

        if (!TryBuildShoulderLoopWithClipper(
            padLoopXy,
            padLoopVertexCount,
            shoulderDistances,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            out shoulderXy,
            out string? offsetFailure))
        {
            skipReason = offsetFailure ?? "Grade Pad shoulder ring was skipped because the daylight offset could not be constructed cleanly.";
            return false;
        }

        return true;
    }

    private static bool TryBuildShoulderLoopFromSections(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderLoopXy,
        out string? failureReason,
        int cornerFanSegments = 0)
    {
        shoulderLoopXy = Array.Empty<double>();
        failureReason = null;

        if (!TryBuildPadTransitionQuadsFromSections(
                padLoopXy,
                padLoopVertexCount,
                shoulderXy,
                tolerance,
                out List<double[]> stripLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight strips degenerated.";
            return false;
        }

        if (!ClipperGeometry.TryUnionClosedLoops(stripLoops, tolerance, out List<double[]> unionLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region could not be unioned cleanly.";
            return false;
        }

        if (boundaryLoop != null)
        {
            if (!ClipperGeometry.TryIntersectClosedLoops(unionLoops, boundaryLoop, tolerance, out unionLoops))
            {
                failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region exited the terrain boundary.";
                return false;
            }
        }

        if (!ClipperGeometry.TryPickLargestLoop(unionLoops, out shoulderLoopXy))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region produced no valid outer loop.";
            return false;
        }

        if (!ClipperGeometry.TrySimplifyClosedLoop(shoulderLoopXy, tolerance, out shoulderLoopXy) ||
            shoulderLoopXy.Length / 2 < 3)
        {
            shoulderLoopXy = Array.Empty<double>();
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region degenerated after simplification.";
            return false;
        }

        return true;
    }

    private static bool TryBuildOrderedShoulderLoopFromSections(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double tolerance,
        out double[] shoulderLoopXy,
        out string? failureReason)
    {
        shoulderLoopXy = Array.Empty<double>();
        failureReason = null;
        if (padLoopVertexCount < 3 || shoulderXy.Length < padLoopVertexCount * 2)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight sections were invalid.";
            return false;
        }

        var points = new List<double>(padLoopVertexCount * 2);
        for (int i = 0; i < padLoopVertexCount; i++)
        {
            double bx = padLoopXy[i * 2];
            double by = padLoopXy[i * 2 + 1];
            double sx = shoulderXy[i * 2];
            double sy = shoulderXy[i * 2 + 1];
            if (DistanceSquaredXY(bx, by, sx, sy) <= tolerance * tolerance)
                continue;

            AddLoopPoint(points, sx, sy, tolerance);
        }

        if (points.Count >= 4 &&
            DistanceSquaredXY(points[0], points[1], points[^2], points[^1]) <= tolerance * tolerance)
        {
            points.RemoveRange(points.Count - 2, 2);
        }

        if (points.Count / 2 < 3)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight sections collapsed.";
            return false;
        }

        shoulderLoopXy = points.ToArray();
        return true;
    }

    private static void AppendCornerFanStrips(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double tolerance,
        int cornerFanSegments,
        List<double[]> stripLoops)
    {
        if (padLoopVertexCount < 3) return;
        double signedArea = ClipperGeometry.SignedArea(padLoopXy);
        if (Math.Abs(signedArea) < 1e-12) return;
        bool ccw = signedArea > 0.0;
        int fanCount = cornerFanSegments + 1;

        for (int i = 0; i < padLoopVertexCount; i++)
        {
            int prev = (i + padLoopVertexCount - 1) % padLoopVertexCount;
            int next = (i + 1) % padLoopVertexCount;

            double x0 = padLoopXy[prev * 2];
            double y0 = padLoopXy[prev * 2 + 1];
            double x1 = padLoopXy[i * 2];
            double y1 = padLoopXy[i * 2 + 1];
            double x2 = padLoopXy[next * 2];
            double y2 = padLoopXy[next * 2 + 1];

            double dx0 = x1 - x0;
            double dy0 = y1 - y0;
            double dx1 = x2 - x1;
            double dy1 = y2 - y1;
            double len0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            if (len0 < 1e-12 || len1 < 1e-12) continue;

            double n0x = ccw ? dy0 / len0 : -dy0 / len0;
            double n0y = ccw ? -dx0 / len0 : dx0 / len0;
            double n1x = ccw ? dy1 / len1 : -dy1 / len1;
            double n1y = ccw ? -dx1 / len1 : dx1 / len1;

            double turnCross = dx0 * dy1 - dy0 * dx1;
            bool isReentrant = ccw ? turnCross < -1e-12 : turnCross > 1e-12;
            if (isReentrant) continue;

            double prevAngle = Math.Atan2(n0y, n0x);
            double nextAngle = Math.Atan2(n1y, n1x);
            double sweep = ComputeOutwardAngleSweep(prevAngle, nextAngle, ccw);
            if (Math.Abs(sweep) <= 10.0 * Math.PI / 180.0) continue;

            double svx = shoulderXy[i * 2] - x1;
            double svy = shoulderXy[i * 2 + 1] - y1;
            double d = (svx * n0x + svy * n0y);
            if (d <= tolerance) continue;

            for (int f = 0; f < fanCount - 1; f++)
            {
                double theta0 = prevAngle + sweep * f / (fanCount - 1);
                double theta1 = prevAngle + sweep * (f + 1) / (fanCount - 1);
                double ax = x1 + Math.Cos(theta0) * d;
                double ay = y1 + Math.Sin(theta0) * d;
                double bx = x1 + Math.Cos(theta1) * d;
                double by = y1 + Math.Sin(theta1) * d;
                double[] tri = [x1, y1, ax, ay, bx, by];
                if (Math.Abs(ClipperGeometry.SignedArea(tri)) > tolerance * tolerance)
                    stripLoops.Add(tri);
            }
        }
    }

    private static bool TryBuildShoulderLoopWithClipper(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderXy,
        out string? failureReason)
    {
        shoulderXy = Array.Empty<double>();
        failureReason = null;

        if (!TryBuildPadTransitionQuads(padLoopXy, padLoopVertexCount, shoulderDistances, tolerance, out List<double[]> stripLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight strips degenerated.";
            return false;
        }

        if (!ClipperGeometry.TryUnionClosedLoops(stripLoops, tolerance, out List<double[]> unionLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight region could not be unioned cleanly.";
            return false;
        }

        if (boundaryLoop != null)
        {
            if (!ClipperGeometry.TryIntersectClosedLoops(unionLoops, boundaryLoop, tolerance, out unionLoops))
            {
                failureReason = "Grade Pad shoulder ring was skipped because the daylight region exited the terrain boundary.";
                return false;
            }
        }

        if (!ClipperGeometry.TryPickLargestLoop(unionLoops, out shoulderXy))
        {
            failureReason = "Grade Pad shoulder ring was skipped because Clipper produced no valid outer loop.";
            return false;
        }

        if (!ClipperGeometry.TrySimplifyClosedLoop(shoulderXy, tolerance, out shoulderXy) ||
            shoulderXy.Length / 2 < 3)
        {
            shoulderXy = Array.Empty<double>();
            failureReason = "Grade Pad shoulder ring was skipped because the daylight region degenerated after simplification.";
            return false;
        }

        double maxDistance = 0.0;
        foreach (double distance in shoulderDistances)
            maxDistance = Math.Max(maxDistance, distance);

        ConstraintLoop resampledShoulder = BuildClosedConstraintLoop(
            shoulderXy,
            shoulderXy.Length / 2,
            ComputePadConstraintSegmentLength(maxDistance),
            tolerance);
        shoulderXy = resampledShoulder.XyVertices;

        return true;
    }

    private static bool TryBuildPadTransitionQuads(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double tolerance,
        out List<double[]> stripLoops)
    {
        stripLoops = new List<double[]>(padLoopVertexCount);
        if (padLoopVertexCount < 3 || shoulderDistances.Length < padLoopVertexCount)
            return false;

        bool ccw = ClipperGeometry.SignedArea(padLoopXy) > 0.0;
        for (int i = 0; i < padLoopVertexCount; i++)
        {
            int next = (i + 1) % padLoopVertexCount;
            double ax = padLoopXy[i * 2];
            double ay = padLoopXy[i * 2 + 1];
            double bx = padLoopXy[next * 2];
            double by = padLoopXy[next * 2 + 1];
            double dx = bx - ax;
            double dy = by - ay;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= tolerance)
                continue;

            double d0 = Math.Max(0.0, shoulderDistances[i]);
            double d1 = Math.Max(0.0, shoulderDistances[next]);
            if (d0 <= tolerance && d1 <= tolerance)
                continue;

            double nx = ccw ? dy / length : -dy / length;
            double ny = ccw ? -dx / length : dx / length;
            double sax = ax + (nx * d0);
            double say = ay + (ny * d0);
            double sbx = bx + (nx * d1);
            double sby = by + (ny * d1);
            double[] quad =
            [
                ax, ay,
                bx, by,
                sbx, sby,
                sax, say
            ];

            if (Math.Abs(ClipperGeometry.SignedArea(quad)) <= tolerance * tolerance)
                continue;

            stripLoops.Add(quad);
        }

        return stripLoops.Count > 0;
    }

    private static bool TryBuildPadTransitionQuadsFromSections(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double tolerance,
        out List<double[]> stripLoops)
    {
        stripLoops = new List<double[]>(padLoopVertexCount);
        if (padLoopVertexCount < 3 || shoulderXy.Length < padLoopVertexCount * 2)
            return false;

        for (int i = 0; i < padLoopVertexCount; i++)
        {
            int next = (i + 1) % padLoopVertexCount;
            double ax = padLoopXy[i * 2];
            double ay = padLoopXy[i * 2 + 1];
            double bx = padLoopXy[next * 2];
            double by = padLoopXy[next * 2 + 1];
            double sax = shoulderXy[i * 2];
            double say = shoulderXy[i * 2 + 1];
            double sbx = shoulderXy[next * 2];
            double sby = shoulderXy[next * 2 + 1];

            double edgeDx = bx - ax;
            double edgeDy = by - ay;
            if ((edgeDx * edgeDx) + (edgeDy * edgeDy) <= tolerance * tolerance)
                continue;

            double[] quad =
            [
                ax, ay,
                bx, by,
                sbx, sby,
                sax, say
            ];

            if (Math.Abs(ClipperGeometry.SignedArea(quad)) <= tolerance * tolerance)
                continue;

            stripLoops.Add(quad);
        }

        return stripLoops.Count > 0;
    }

    private static bool PolygonHasConcaveVertex(double[] polygonXy, int vertexCount)
    {
        if (vertexCount < 4)
            return false;

        double signedArea = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            signedArea += (polygonXy[i * 2] * polygonXy[next * 2 + 1]) - (polygonXy[next * 2] * polygonXy[i * 2 + 1]);
        }

        if (Math.Abs(signedArea) <= 1e-12)
            return false;

        bool ccw = signedArea > 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int prev = (i + vertexCount - 1) % vertexCount;
            int next = (i + 1) % vertexCount;

            double ax = polygonXy[i * 2] - polygonXy[prev * 2];
            double ay = polygonXy[i * 2 + 1] - polygonXy[prev * 2 + 1];
            double bx = polygonXy[next * 2] - polygonXy[i * 2];
            double by = polygonXy[next * 2 + 1] - polygonXy[i * 2 + 1];
            double cross = (ax * by) - (ay * bx);

            if (ccw ? cross < -1e-12 : cross > 1e-12)
                return true;
        }

        return false;
    }
}