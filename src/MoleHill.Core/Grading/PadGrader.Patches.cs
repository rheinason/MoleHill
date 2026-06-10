using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private static bool TryBuildProtectedStitchLoop(
        double[] daylightLoopXy,
        double apronDistance,
        double[]? terrainBoundaryLoop,
        int terrainBoundaryVertexCount,
        double tolerance,
        out double[] stitchLoopXy,
        out string? skipReason)
    {
        stitchLoopXy = Array.Empty<double>();
        skipReason = null;
        int daylightVertexCount = daylightLoopXy.Length / 2;
        if (daylightVertexCount < 3)
        {
            skipReason = "Grade Pad protected apron skipped because the daylight loop was invalid.";
            return false;
        }

        if (apronDistance <= tolerance * 4.0)
            return false;

        var distances = new double[daylightVertexCount];
        Array.Fill(distances, apronDistance);
        if (!TryBuildShoulderLoopWithClipper(
                daylightLoopXy,
                daylightVertexCount,
                distances,
                terrainBoundaryLoop,
                terrainBoundaryVertexCount,
                tolerance,
                out stitchLoopXy,
                out string? offsetFailure))
        {
            skipReason = offsetFailure ?? "Grade Pad protected apron skipped because the outer offset could not be constructed cleanly.";
            return false;
        }

        stitchLoopXy = SimplifyClosedLoopByShortEdges(stitchLoopXy, Math.Max(tolerance * 4.0, 1e-6));
        if ((stitchLoopXy.Length / 2) < 3 || LoopsCoincide(stitchLoopXy, daylightLoopXy, tolerance * 4.0))
        {
            stitchLoopXy = Array.Empty<double>();
            skipReason = "Grade Pad protected apron skipped because the outer apron loop collapsed to the daylight loop.";
            return false;
        }

        return true;
    }

    private static IReadOnlyList<OutputPolyline> BuildPadBoundaryPolylines(PadBoundary[] pads)
    {
        var polylines = new List<OutputPolyline>(pads.Length);
        foreach (var pad in pads)
        {
            var xyz = new double[pad.VertexCount * 3];
            for (int i = 0; i < pad.VertexCount; i++)
            {
                double x = pad.XyVertices[i * 2];
                double y = pad.XyVertices[i * 2 + 1];
                xyz[i * 3] = x;
                xyz[i * 3 + 1] = y;
                xyz[i * 3 + 2] = pad.EvaluateZ(x, y);
            }

            polylines.Add(new OutputPolyline(xyz, pad.VertexCount, isClosed: true));
        }

        return polylines;
    }

    private static List<GradingPatch> BuildPadPatchSummaries(IReadOnlyList<PadBoundary> pads)
    {
        var patches = new List<GradingPatch>(pads.Count);
        for (int i = 0; i < pads.Count; i++)
        {
            var pad = pads[i];
            patches.Add(new GradingPatch
            {
                OwnerKey = $"pad:{i}",
                Kind = GradingPatchKind.Pad,
                Priority = ComputePadOwnershipPriority(pad),
                OwnedRegionLoopXy = (double[])pad.XyVertices.Clone(),
                DaylightLoopXy = Array.Empty<double>(),
                StitchLoopXy = Array.Empty<double>(),
                DirtyBounds = GradingPatch.ComputeBounds(pad.XyVertices),
                UsesFallbackBand = false
            });
        }

        return patches;
    }

    private static string[] BuildPadSlopeDiagnostics(
        int padIndex,
        PreparedPadSections prepared,
        PatchMeshResult patch,
        double tolerance)
    {
        int measuredFaceCount = 0;
        double minSlopeDeg = double.MaxValue;
        double maxSlopeDeg = 0.0;
        double slopeSumDeg = 0.0;
        double targetSlopeDeg = prepared.Pad.SlopeAngleDeg;
        double toleranceSquared = tolerance * tolerance;
        double minimumHorizontalArea2 = Math.Max(toleranceSquared, 1e-12);
        double minimumTriangleAltitude = Math.Max(tolerance * 4.0, 1e-8);
        double maxSlopeX = 0.0;
        double maxSlopeY = 0.0;

        for (int faceIndex = 0; faceIndex < patch.FaceCount; faceIndex++)
        {
            int a = patch.Faces[faceIndex * 3];
            int b = patch.Faces[(faceIndex * 3) + 1];
            int c = patch.Faces[(faceIndex * 3) + 2];
            double cx = (patch.Vertices[a * 3] + patch.Vertices[b * 3] + patch.Vertices[c * 3]) / 3.0;
            double cy = (patch.Vertices[(a * 3) + 1] + patch.Vertices[(b * 3) + 1] + patch.Vertices[(c * 3) + 1]) / 3.0;

            if (PointInPolygon(cx, cy, prepared.BoundaryLoopXy, prepared.BoundaryVertexCount))
                continue;
            if (!PointInPolygon(cx, cy, prepared.ShoulderXy, prepared.BoundaryVertexCount))
                continue;
            if (DistToPolygon(cx, cy, prepared.BoundaryLoopXy, prepared.BoundaryVertexCount) <= tolerance * 2.0)
                continue;

            double ax = patch.Vertices[a * 3];
            double ay = patch.Vertices[(a * 3) + 1];
            double az = patch.Vertices[(a * 3) + 2];
            double bx = patch.Vertices[b * 3];
            double by = patch.Vertices[(b * 3) + 1];
            double bz = patch.Vertices[(b * 3) + 2];
            double cxVertex = patch.Vertices[c * 3];
            double cyVertex = patch.Vertices[(c * 3) + 1];
            double horizontalArea2 = Math.Abs(((bx - ax) * (cyVertex - ay)) - ((cxVertex - ax) * (by - ay)));
            if (horizontalArea2 <= minimumHorizontalArea2)
                continue;

            double ux = bx - ax;
            double uy = by - ay;
            double uz = bz - az;
            double vx = cxVertex - ax;
            double vy = cyVertex - ay;
            double vz = patch.Vertices[(c * 3) + 2] - az;
            double edgeAbSq = (ux * ux) + (uy * uy);
            double edgeBcSq = ((cxVertex - bx) * (cxVertex - bx)) + ((cyVertex - by) * (cyVertex - by));
            double edgeCaSq = ((ax - cxVertex) * (ax - cxVertex)) + ((ay - cyVertex) * (ay - cyVertex));
            double longestEdge = Math.Sqrt(Math.Max(edgeAbSq, Math.Max(edgeBcSq, edgeCaSq)));
            if (longestEdge <= tolerance || horizontalArea2 / longestEdge <= minimumTriangleAltitude)
                continue;

            double nx = (uy * vz) - (uz * vy);
            double ny = (uz * vx) - (ux * vz);
            double nz = (ux * vy) - (uy * vx);
            double normalLengthSquared = (nx * nx) + (ny * ny) + (nz * nz);
            if (normalLengthSquared <= toleranceSquared * toleranceSquared)
                continue;

            double slopeDeg = Math.Atan2(Math.Sqrt((nx * nx) + (ny * ny)), Math.Abs(nz)) * 180.0 / Math.PI;
            minSlopeDeg = Math.Min(minSlopeDeg, slopeDeg);
            if (slopeDeg > maxSlopeDeg)
            {
                maxSlopeDeg = slopeDeg;
                maxSlopeX = cx;
                maxSlopeY = cy;
            }

            slopeSumDeg += slopeDeg;
            measuredFaceCount++;
        }

        if (measuredFaceCount == 0)
        {
            return
            [
                $"Grade Pad[{padIndex}] batter slope check: no measurable batter faces inside the shoulder loop.",
                $"Grade Pad[{padIndex}] shoulder quality warning: no measurable batter surface was produced; expected a visible shoulder/batter band."
            ];
        }

        double avgSlopeDeg = slopeSumDeg / measuredFaceCount;
        double maxDeltaDeg = Math.Max(Math.Abs(minSlopeDeg - targetSlopeDeg), Math.Abs(maxSlopeDeg - targetSlopeDeg));
        var diagnostics = new List<string>
        {
            $"Grade Pad[{padIndex}] batter slope check: target={targetSlopeDeg:F2} deg, faces={measuredFaceCount}, min={minSlopeDeg:F2}, avg={avgSlopeDeg:F2}, max={maxSlopeDeg:F2}, max delta={maxDeltaDeg:F2} deg."
        };

        if (maxDeltaDeg > 5.0)
        {
            diagnostics.Add(
                $"Grade Pad[{padIndex}] batter slope warning: output deviates from target by up to {maxDeltaDeg:F2} deg near ({maxSlopeX:F3}, {maxSlopeY:F3}); inspect clipped daylight, nearby pads, or terrain-boundary constraints.");
        }

        double avgDeltaDeg = Math.Abs(avgSlopeDeg - targetSlopeDeg);
        if (avgDeltaDeg > 8.0 || minSlopeDeg < targetSlopeDeg - 12.0)
        {
            diagnostics.Add(
                $"Grade Pad[{padIndex}] shoulder quality warning: batter surface is poorly formed; average slope delta={avgDeltaDeg:F2} deg, min slope={minSlopeDeg:F2} deg, target={targetSlopeDeg:F2} deg. This usually means shoulder insertion is too sparse or overlapping pads collapsed the batter band.");
        }

        return diagnostics.ToArray();
    }

    private static bool LoopsCoincide(double[] leftLoopXy, double[] rightLoopXy, double tolerance)
    {
        int leftCount = leftLoopXy.Length / 2;
        int rightCount = rightLoopXy.Length / 2;
        if (leftCount == 0 || rightCount == 0)
            return false;

        double toleranceSquared = tolerance * tolerance;
        for (int i = 0; i < leftCount; i++)
        {
            double x = leftLoopXy[i * 2];
            double y = leftLoopXy[(i * 2) + 1];
            bool found = false;
            for (int j = 0; j < rightCount; j++)
            {
                if (DistanceSquaredXY(x, y, rightLoopXy[j * 2], rightLoopXy[(j * 2) + 1]) <= toleranceSquared)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }

    private static double[] SimplifyClosedLoopByShortEdges(double[] loopXy, double minEdgeLength)
    {
        int vertexCount = loopXy.Length / 2;
        if (vertexCount < 4)
            return loopXy;

        double minEdgeLengthSquared = minEdgeLength * minEdgeLength;
        var keep = new bool[vertexCount];
        Array.Fill(keep, true);

        bool changed;
        do
        {
            changed = false;
            for (int i = 0; i < vertexCount; i++)
            {
                if (!keep[i])
                    continue;

                int next = FindNextKeptIndex(keep, i);
                if (next == i)
                    break;

                double distanceSquared = DistanceSquaredXY(
                    loopXy[i * 2],
                    loopXy[(i * 2) + 1],
                    loopXy[next * 2],
                    loopXy[(next * 2) + 1]);
                if (distanceSquared <= minEdgeLengthSquared && keep.Count(static value => value) > 3)
                {
                    keep[next] = false;
                    changed = true;
                }
            }
        }
        while (changed);

        int keptCount = keep.Count(static value => value);
        if (keptCount == vertexCount)
            return loopXy;

        var simplified = new double[keptCount * 2];
        int write = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            if (!keep[i])
                continue;

            simplified[write * 2] = loopXy[i * 2];
            simplified[(write * 2) + 1] = loopXy[(i * 2) + 1];
            write++;
        }

        return simplified;
    }

    private static int FindNextKeptIndex(bool[] keep, int start)
    {
        int count = keep.Length;
        for (int offset = 1; offset <= count; offset++)
        {
            int candidate = (start + offset) % count;
            if (keep[candidate])
                return candidate;
        }

        return start;
    }

    private static double DistanceSquaredXY(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return (dx * dx) + (dy * dy);
    }
}
