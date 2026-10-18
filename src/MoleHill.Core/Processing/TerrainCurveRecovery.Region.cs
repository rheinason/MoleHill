namespace MoleHill.Core.Processing;

/// <summary>Clips recovered contour geometry before it participates in the next reconstruction.</summary>
public static partial class TerrainCurveRecovery
{
    private static IEnumerable<RecoveredContour> ClipContour(RecoveredContour? contour, ITerrainRecoveryRegion? region,
        double pickX, double pickY)
    {
        if (contour == null)
            yield break;
        if (region == null)
        {
            yield return contour;
            yield break;
        }
        // A level can cross many disconnected lawns. Spend this pick on the fragment that addresses
        // its error; the other fragments can be selected later if their own reconstruction needs them.
        RegionInputClipper.InputPolyline? nearest = null;
        double best = double.PositiveInfinity;
        foreach (RegionInputClipper.InputPolyline part in region.Clip(contour.PointsXyz, contour.IsClosed))
        {
            if (part.PointCount < (part.IsClosed ? 3 : 2))
                continue;
            for (int i = 0; i < (part.IsClosed ? part.PointCount : part.PointCount - 1); i++)
            {
                int a = i * 3, b = (i + 1) % part.PointCount * 3;
                double dx = part.Points[b] - part.Points[a], dy = part.Points[b + 1] - part.Points[a + 1];
                double lengthSquared = dx * dx + dy * dy;
                double t = lengthSquared == 0 ? 0 : Math.Clamp(((pickX - part.Points[a]) * dx + (pickY - part.Points[a + 1]) * dy) / lengthSquared, 0, 1);
                double ex = part.Points[a] + t * dx - pickX, ey = part.Points[a + 1] + t * dy - pickY;
                double distance = ex * ex + ey * ey;
                if (distance < best)
                {
                    best = distance;
                    nearest = part;
                }
            }
        }
        if (nearest is RegionInputClipper.InputPolyline chosen)
            yield return new RecoveredContour { Level = contour.Level, PointsXyz = chosen.Points, IsClosed = chosen.IsClosed };
    }
}
