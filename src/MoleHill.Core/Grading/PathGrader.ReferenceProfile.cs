using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    private static void BuildShoulderReferenceProfile(
        ConstraintPath samplePath,
        SpatialHashGrid2D? segmentGrid,
        double halfWidth,
        double shoulderDistance,
        double[] xy,
        double[] z,
        int vertexCount,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        out double[] leftReferenceDz,
        out double[] rightReferenceDz)
    {
        leftReferenceDz = CreateNaNArray(samplePath.VertexCount);
        rightReferenceDz = CreateNaNArray(samplePath.VertexCount);
        if (samplePath.VertexCount < 2 || shoulderDistance <= 1e-9)
            return;

        var leftSum = new double[samplePath.VertexCount];
        var leftWeight = new double[samplePath.VertexCount];
        var rightSum = new double[samplePath.VertexCount];
        var rightWeight = new double[samplePath.VertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            double px = xy[i * 2];
            double py = xy[i * 2 + 1];
            if (!TryFindClosestPathLocation(
                    samplePath,
                    segmentGrid,
                    halfWidth + shoulderDistance + 1e-6,
                    px,
                    py,
                    barrierScratch,
                    barrierCandidates,
                    out ClosestPathLocation closest))
                continue;

            double distFromEdge = closest.Distance - halfWidth;
            if (distFromEdge <= 1e-6 || distFromEdge > shoulderDistance + 1e-6)
                continue;

            if (barriers.Segments.Length > 0 && IsBlockedByBarrier(barriers, halfWidth, closest, px, py, barrierScratch, barrierCandidates))
                continue;

            double dz = z[i] - closest.PathZ;
            if (Math.Abs(dz) <= 1e-9)
                continue;

            double normalizedDistance = Math.Clamp(distFromEdge / shoulderDistance, 0.0, 1.0);
            double sampleWeight = Math.Max(normalizedDistance * normalizedDistance, 1e-3);
            if (closest.SideSign >= 0.0)
                AccumulateReferenceSample(leftSum, leftWeight, closest.SegmentIndex, closest.SegmentT, dz, sampleWeight);
            else
                AccumulateReferenceSample(rightSum, rightWeight, closest.SegmentIndex, closest.SegmentT, dz, sampleWeight);
        }

        FinalizeReferenceProfile(leftSum, leftWeight, leftReferenceDz);
        FinalizeReferenceProfile(rightSum, rightWeight, rightReferenceDz);
    }

    private static void BuildShoulderReferenceProfileDirect(
        ConstraintPath samplePath,
        double halfWidth,
        double shoulderDistance,
        Func<double, double, double> interpolateOriginalZ,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        out double[] leftReferenceDz,
        out double[] rightReferenceDz)
    {
        int n = samplePath.VertexCount;
        leftReferenceDz  = new double[n];
        rightReferenceDz = new double[n];

        if (n < 2 || shoulderDistance <= 1e-9)
            return;

        for (int i = 0; i < n; i++)
        {
            double cx = samplePath.XyVertices[i * 2];
            double cy = samplePath.XyVertices[i * 2 + 1];
            double pathZ = samplePath.ZValues[i];

            // Use smooth tangent when available; fall back to ComputeDirection.
            double dx, dy;
            if (samplePath.TangentX != null && samplePath.TangentX.Length == n)
            { dx = samplePath.TangentX[i]; dy = samplePath.TangentY[i]; }
            else
            { ComputeDirection(samplePath.XyVertices, n, i, out dx, out dy); }
            double nx = -dy;  // left normal
            double ny =  dx;

            // Sample at 50 %, 75 %, 100 % of the shoulder reach measured from the road edge.
            for (int side = -1; side <= 1; side += 2)  // -1 = right, +1 = left
            {
                double roadEdgeX = cx + side * nx * halfWidth;
                double roadEdgeY = cy + side * ny * halfWidth;
                double s0 = SampleOriginalTerrainAlongShoulderRay(
                    interpolateOriginalZ,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    boundaryTolerance,
                    roadEdgeX,
                    roadEdgeY,
                    roadEdgeX + side * nx * (shoulderDistance * 0.50),
                    roadEdgeY + side * ny * (shoulderDistance * 0.50));
                double s1 = SampleOriginalTerrainAlongShoulderRay(
                    interpolateOriginalZ,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    boundaryTolerance,
                    roadEdgeX,
                    roadEdgeY,
                    roadEdgeX + side * nx * (shoulderDistance * 0.75),
                    roadEdgeY + side * ny * (shoulderDistance * 0.75));
                double s2 = SampleOriginalTerrainAlongShoulderRay(
                    interpolateOriginalZ,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    boundaryTolerance,
                    roadEdgeX,
                    roadEdgeY,
                    roadEdgeX + side * nx * shoulderDistance,
                    roadEdgeY + side * ny * shoulderDistance);
                double dz = (s0 + s1 + s2) / 3.0 - pathZ;

                if (side > 0)
                    leftReferenceDz[i]  = dz;
                else
                    rightReferenceDz[i] = dz;
            }
        }

        // One smoothing pass to avoid raw terrain frequency becoming grading oscillation.
        SmoothReferenceSamples(leftReferenceDz,  1);
        SmoothReferenceSamples(rightReferenceDz, 1);
    }

    private static double SampleOriginalTerrainAlongShoulderRay(
        Func<double, double, double> interpolateOriginalZ,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        double startX,
        double startY,
        double targetX,
        double targetY)
    {
        ResolveShoulderRayEndpoint(
            barriers,
            barrierScratch,
            barrierCandidates,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            boundaryTolerance,
            startX,
            startY,
            targetX,
            targetY,
            out _,
            out targetX,
            out targetY);
        return interpolateOriginalZ(targetX, targetY);
    }

    private static void ResolveShoulderRayEndpoint(
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        double startX,
        double startY,
        double targetX,
        double targetY,
        out ShoulderRayClipKind clipKind,
        out double resolvedX,
        out double resolvedY)
    {
        clipKind = ShoulderRayClipKind.None;
        resolvedX = targetX;
        resolvedY = targetY;

        if (barriers.Segments.Length > 0)
        {
            if (GradingBarriers.TryClipSegment(
                barriers,
                startX,
                startY,
                resolvedX,
                resolvedY,
                barrierScratch,
                barrierCandidates,
                out resolvedX,
                out resolvedY))
            {
                clipKind = ShoulderRayClipKind.Barrier;
            }
        }

        if (!hasBoundaryLoop)
            return;

        List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
            startX,
            startY,
            0.0,
            resolvedX,
            resolvedY,
            0.0,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            boundaryTolerance);

        double furthestEndT = double.MinValue;
        bool foundPiece = false;
        foreach (ClippedSegment piece in pieces)
        {
            if (piece.StartT > 1e-9 || piece.EndT <= furthestEndT)
                continue;

            resolvedX = piece.EndX;
            resolvedY = piece.EndY;
            furthestEndT = piece.EndT;
            foundPiece = true;
        }

        if (foundPiece)
        {
            if (clipKind == ShoulderRayClipKind.None)
                clipKind = ShoulderRayClipKind.Boundary;
            return;
        }

        if (!BoundaryClipper.IsInsideOrOnBoundary(startX, startY, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, boundaryTolerance))
        {
            clipKind = ShoulderRayClipKind.Boundary;
            resolvedX = startX;
            resolvedY = startY;
            return;
        }

        clipKind = ShoulderRayClipKind.Boundary;
        resolvedX = startX;
        resolvedY = startY;
    }

    private static void AccumulateReferenceSample(
        double[] sum,
        double[] weight,
        int segmentIndex,
        double segmentT,
        double dz,
        double sampleWeight)
    {
        if (segmentIndex < 0 || segmentIndex >= sum.Length - 1 || sampleWeight <= 0.0)
            return;

        double startWeight = sampleWeight * (1.0 - segmentT);
        double endWeight = sampleWeight * segmentT;
        sum[segmentIndex] += dz * startWeight;
        weight[segmentIndex] += startWeight;
        sum[segmentIndex + 1] += dz * endWeight;
        weight[segmentIndex + 1] += endWeight;
    }

    private static void FinalizeReferenceProfile(double[] sum, double[] weight, double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (weight[i] > 1e-9)
                values[i] = sum[i] / weight[i];
        }

        FillMissingReferenceSamples(values);
        SmoothReferenceSamples(values, 3);
    }

    private static void FillMissingReferenceSamples(double[] values)
    {
        int firstFinite = -1;
        for (int i = 0; i < values.Length; i++)
        {
            if (double.IsFinite(values[i]))
            {
                firstFinite = i;
                break;
            }
        }

        if (firstFinite < 0)
            return;

        for (int i = 0; i < firstFinite; i++)
            values[i] = values[firstFinite];

        int previousFinite = firstFinite;
        for (int i = firstFinite + 1; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
                continue;

            int nextFinite = i;
            if (nextFinite - previousFinite > 1)
            {
                double start = values[previousFinite];
                double end = values[nextFinite];
                int gap = nextFinite - previousFinite;
                for (int gapIndex = 1; gapIndex < gap; gapIndex++)
                {
                    double t = gapIndex / (double)gap;
                    values[previousFinite + gapIndex] = start + ((end - start) * t);
                }
            }

            previousFinite = nextFinite;
        }

        for (int i = previousFinite + 1; i < values.Length; i++)
            values[i] = values[previousFinite];
    }

    private static void SmoothReferenceSamples(double[] values, int passes)
    {
        if (values.Length < 3 || passes <= 0 || !HasFiniteSamples(values))
            return;

        var scratch = new double[values.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!double.IsFinite(values[i]))
                {
                    scratch[i] = values[i];
                    continue;
                }

                double weightedSum = values[i] * 0.5;
                double totalWeight = 0.5;

                if (i > 0 && double.IsFinite(values[i - 1]))
                {
                    weightedSum += values[i - 1] * 0.25;
                    totalWeight += 0.25;
                }

                if (i < values.Length - 1 && double.IsFinite(values[i + 1]))
                {
                    weightedSum += values[i + 1] * 0.25;
                    totalWeight += 0.25;
                }

                scratch[i] = weightedSum / totalWeight;
            }

            Array.Copy(scratch, values, values.Length);
        }
    }

    private static double[] CreateNaNArray(int length)
    {
        var values = new double[length];
        Array.Fill(values, double.NaN);
        return values;
    }

    private static bool HasFiniteSamples(double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (double.IsFinite(values[i]))
                return true;
        }

        return false;
    }

    private static bool TryGetShoulderReferenceDz(
        PreparedPath preparedPath,
        ClosestPathLocation closest,
        out double referenceDz)
    {
        referenceDz = 0.0;
        double[] values = closest.SideSign >= 0.0
            ? preparedPath.LeftReferenceDz
            : preparedPath.RightReferenceDz;
        if (values.Length < 2 || closest.SegmentIndex < 0 || closest.SegmentIndex >= values.Length - 1)
            return false;

        double start = values[closest.SegmentIndex];
        double end = values[closest.SegmentIndex + 1];
        if (!double.IsFinite(start) && !double.IsFinite(end))
            return false;

        if (!double.IsFinite(start))
            start = end;
        else if (!double.IsFinite(end))
            end = start;

        referenceDz = start + ((end - start) * closest.SegmentT);
        return double.IsFinite(referenceDz);
    }
}
