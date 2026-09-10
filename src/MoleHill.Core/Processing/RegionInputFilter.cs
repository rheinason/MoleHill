using MoleHill.Core.Grading;

namespace MoleHill.Core.Processing;

/// <summary>
/// Keeps only the input points that fall inside a work-region boundary (expanded by a margin), so the
/// triangulator can crop computation to a sub-area for fast iteration on part of a large terrain. Pure
/// geometry (no Rhino), so it is unit-tested. With no boundary every point is kept (full terrain).
/// </summary>
public static class RegionInputFilter
{
    /// <summary>
    /// Returns a per-point keep mask. A point is kept when it lies inside any polygon, or within
    /// <paramref name="margin"/> of a polygon edge (so edge triangles are well-formed before the
    /// boundary constraint trims the overhang). <paramref name="pointsXyz"/> is flat XYZ (3-stride);
    /// each polygon in <paramref name="polygonsXy"/> is a flat XY loop (2-stride).
    /// </summary>
    public static bool[] KeepPointsInside(
        double[] pointsXyz,
        int count,
        IReadOnlyList<double[]> polygonsXy,
        double margin)
    {
        var keep = new bool[count];
        if (polygonsXy == null || polygonsXy.Count == 0)
        {
            for (int i = 0; i < count; i++)
                keep[i] = true;
            return keep;
        }

        double marginClamped = Math.Max(margin, 0.0);

        // Prepare each loop once. Every input point is tested against every boundary, so on a detailed
        // boundary this loop is points x edges; prepared loops reject on bounds and, above their
        // vertex threshold, walk only the edges that can cross the query's Y. The answers are the same.
        List<PreparedPolygon> prepared = PreparedPolygon.CreateAll(polygonsXy);
        if (prepared.Count == 0)
            return keep;

        for (int i = 0; i < count; i++)
        {
            double x = pointsXyz[i * 3];
            double y = pointsXyz[i * 3 + 1];
            foreach (PreparedPolygon polygon in prepared)
            {
                if (polygon.Contains(x, y) ||
                    (marginClamped > 0.0 && polygon.IsWithin(x, y, marginClamped)))
                {
                    keep[i] = true;
                    break;
                }
            }
        }

        return keep;
    }
}
