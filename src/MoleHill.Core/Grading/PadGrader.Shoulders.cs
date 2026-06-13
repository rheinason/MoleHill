using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
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
