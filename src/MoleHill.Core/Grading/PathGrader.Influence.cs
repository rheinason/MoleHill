using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    private static bool TryComputePathInfluence(
        PreparedPath preparedPath,
        PreparedBarriers preparedBarriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        double px,
        double py,
        double originalZ,
        out bool insideRoad,
        out double candidateZ,
        out double weight)
    {
        insideRoad = false;
        candidateZ = 0.0;
        weight = 0.0;

        if (px < preparedPath.MinX || px > preparedPath.MaxX || py < preparedPath.MinY || py > preparedPath.MaxY)
            return false;

        if (!TryFindClosestPathLocation(
                preparedPath.SamplePath,
                preparedPath.SegmentGrid,
                preparedPath.MaxInfluence + 1e-6,
                px,
                py,
                barrierScratch,
                barrierCandidates,
                out ClosestPathLocation closest))
            return false;

        if (closest.Distance > preparedPath.MaxInfluence + 1e-6)
            return false;

        double edgeDistance = GetLocalEdgeDistance(preparedPath.SamplePath, closest, preparedPath.HalfWidth, out double edgeX, out double edgeY);
        if (closest.Distance <= edgeDistance + 1e-6)
        {
            insideRoad = true;
            candidateZ = closest.PathZ;
            weight = ComputeRoadBlendWeight(edgeDistance, closest.Distance);
            return true;
        }

        double distFromEdge = closest.Distance - edgeDistance;
        if (preparedBarriers.Segments.Length > 0 &&
            GradingBarriers.IsCrossedByBarrier(preparedBarriers, edgeX, edgeY, px, py, barrierScratch, barrierCandidates))
            return false;

        double dzActual = originalZ - closest.PathZ;
        double dz = dzActual;
        if (TryGetShoulderReferenceDz(preparedPath, closest, out double referenceDz) &&
            Math.Abs(referenceDz) > 1e-12)
        {
            // Only use the reference when it agrees with the vertex's actual terrain
            // direction (same sign) or when dzActual is near-zero (flat terrain dip/bump
            // that needs lifting/lowering by the smooth profile).
            // Opposite-sign means the reference profile came from the wrong side
            // (SideSign flip); using it would push the vertex in the wrong direction.
            if (referenceDz * dzActual >= -1e-12)
                dz = referenceDz;
        }

        double absDz = Math.Abs(dz);
        if (absDz <= 1e-12)
            return false;

        // dz = terrainZ - roadZ: > 0 terrain above grade (cut), < 0 below (fill).
        double slopeRatio = preparedPath.SlopeRatioForBranch(Math.Sign(dz));
        double neededDist = slopeRatio > 1e-12
            ? absDz / slopeRatio
            : double.MaxValue;
        if (preparedPath.MaxDistance > 0)
            neededDist = Math.Min(neededDist, preparedPath.MaxDistance);
        if (preparedPath.ShoulderDistance > 0)
            neededDist = Math.Min(neededDist, preparedPath.ShoulderDistance);

        if (distFromEdge >= neededDist)
            return false;

        double rise = distFromEdge * slopeRatio;
        if (rise >= absDz)
            return false;

        candidateZ = closest.PathZ + Math.Sign(dz) * rise;

        weight = ComputeShoulderBlendWeight(distFromEdge, neededDist);
        return weight > 1e-12;
    }

    private static double GetLocalEdgeDistance(
        ConstraintPath path,
        ClosestPathLocation closest,
        double fallbackHalfWidth,
        out double edgeX,
        out double edgeY)
    {
        double[]? edge = closest.SideSign >= 0.0 ? path.LeftEdgeXy : path.RightEdgeXy;
        if (edge is null || closest.SegmentIndex < 0 || closest.SegmentIndex >= path.VertexCount - 1)
        {
            double side = closest.SideSign >= 0.0 ? 1.0 : -1.0;
            edgeX = closest.ProjectedX + (-closest.DirectionY * fallbackHalfWidth * side);
            edgeY = closest.ProjectedY + (closest.DirectionX * fallbackHalfWidth * side);
            return fallbackHalfWidth;
        }

        int a = closest.SegmentIndex * 2;
        int b = (closest.SegmentIndex + 1) * 2;
        edgeX = edge[a] + ((edge[b] - edge[a]) * closest.SegmentT);
        edgeY = edge[a + 1] + ((edge[b + 1] - edge[a + 1]) * closest.SegmentT);
        double dx = edgeX - closest.ProjectedX;
        double dy = edgeY - closest.ProjectedY;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double ComputeRoadBlendWeight(double halfWidth, double closestDist)
    {
        if (halfWidth <= 1e-9)
            return 4.0;

        double closeness = 1.0 - Math.Clamp(closestDist / halfWidth, 0.0, 1.0);
        return 1.0 + (closeness * closeness * 3.0);
    }

    private static double ComputeShoulderBlendWeight(double distFromEdge, double neededDist)
    {
        if (neededDist <= 1e-9)
            return 0.0;

        double closeness = 1.0 - Math.Clamp(distFromEdge / neededDist, 0.0, 1.0);
        return closeness * closeness;
    }
}
