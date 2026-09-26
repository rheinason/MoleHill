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
