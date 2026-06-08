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

        if (!TryFindClosestPathLocation(preparedPath.SamplePath, px, py, out ClosestPathLocation closest))
            return false;

        if (closest.Distance > preparedPath.MaxInfluence + 1e-6)
            return false;

        if (closest.Distance <= preparedPath.HalfWidth + 1e-6)
        {
            insideRoad = true;
            candidateZ = closest.PathZ;
            weight = ComputeRoadBlendWeight(preparedPath.HalfWidth, closest.Distance);
            return true;
        }

        double distFromEdge = closest.Distance - preparedPath.HalfWidth;
        if (IsBlockedByBarrier(preparedBarriers, preparedPath.HalfWidth, closest, px, py, barrierScratch, barrierCandidates))
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

        double neededDist = preparedPath.SlopeRatio > 1e-12
            ? absDz / preparedPath.SlopeRatio
            : double.MaxValue;
        if (preparedPath.MaxDistance > 0)
            neededDist = Math.Min(neededDist, preparedPath.MaxDistance);
        if (preparedPath.ShoulderDistance > 0)
            neededDist = Math.Min(neededDist, preparedPath.ShoulderDistance);

        if (distFromEdge >= neededDist)
            return false;

        double rise = distFromEdge * preparedPath.SlopeRatio;
        if (rise >= absDz)
            return false;

        candidateZ = closest.PathZ + Math.Sign(dz) * rise;

        weight = ComputeShoulderBlendWeight(distFromEdge, neededDist);
        return weight > 1e-12;
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
