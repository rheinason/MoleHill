using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Where two sets of breaklines cross in plan at different heights. Both cannot be edges of one 2.5D terrain:
/// a mesh has one height at the crossing, so honouring one line means moving the other. A retaining wall in
/// breakline mode keeps both inputs as drawn, so a rail crossing a constrained contour or breakline above or
/// below it is a conflict for the user to resolve, not something a rebuild can repair.
/// </summary>
public static class BreaklineHeightConflicts
{
    /// <summary>One crossing: its plan position and the two heights there.</summary>
    public readonly record struct Conflict(double X, double Y, double ZA, double ZB);

    /// <summary>
    /// Every crossing of a segment of <paramref name="a"/> with one of <paramref name="b"/> whose heights there
    /// differ by more than <paramref name="heightTolerance"/>, in the order of <paramref name="a"/>'s segments.
    /// Only proper crossings count; lines that merely touch at a vertex do not conflict.
    /// </summary>
    public static List<Conflict> Find(
        IReadOnlyList<ConstraintPolyline> a,
        IReadOnlyList<ConstraintPolyline> b,
        double heightTolerance)
    {
        var conflicts = new List<Conflict>();
        foreach (ConstraintPolyline lineA in a)
        {
            (double aMinX, double aMinY, double aMaxX, double aMaxY) = Bounds(lineA);
            foreach (ConstraintPolyline lineB in b)
            {
                (double bMinX, double bMinY, double bMaxX, double bMaxY) = Bounds(lineB);
                if (bMaxX < aMinX || bMinX > aMaxX || bMaxY < aMinY || bMinY > aMaxY)
                    continue;

                int segmentsA = SegmentCount(lineA), segmentsB = SegmentCount(lineB);
                for (int i = 0; i < segmentsA; i++)
                {
                    int i1 = (i + 1) % lineA.PointCount;
                    double ax0 = lineA.Points[i * 3], ay0 = lineA.Points[i * 3 + 1], az0 = lineA.Points[i * 3 + 2];
                    double ax1 = lineA.Points[i1 * 3], ay1 = lineA.Points[i1 * 3 + 1], az1 = lineA.Points[i1 * 3 + 2];
                    double sMinX = Math.Min(ax0, ax1), sMaxX = Math.Max(ax0, ax1), sMinY = Math.Min(ay0, ay1), sMaxY = Math.Max(ay0, ay1);
                    if (sMaxX < bMinX || sMinX > bMaxX || sMaxY < bMinY || sMinY > bMaxY)
                        continue;

                    for (int j = 0; j < segmentsB; j++)
                    {
                        int j1 = (j + 1) % lineB.PointCount;
                        double bx0 = lineB.Points[j * 3], by0 = lineB.Points[j * 3 + 1];
                        double bx1 = lineB.Points[j1 * 3], by1 = lineB.Points[j1 * 3 + 1];
                        if (Math.Max(bx0, bx1) < sMinX || Math.Min(bx0, bx1) > sMaxX || Math.Max(by0, by1) < sMinY || Math.Min(by0, by1) > sMaxY)
                            continue;

                        if (!TryProperCrossing(ax0, ay0, ax1, ay1, bx0, by0, bx1, by1, out double t, out double u))
                            continue;

                        double za = az0 + ((az1 - az0) * t);
                        double zb = lineB.Points[j * 3 + 2] + ((lineB.Points[j1 * 3 + 2] - lineB.Points[j * 3 + 2]) * u);
                        if (Math.Abs(za - zb) > heightTolerance)
                            conflicts.Add(new Conflict(ax0 + ((ax1 - ax0) * t), ay0 + ((ay1 - ay0) * t), za, zb));
                    }
                }
            }
        }

        return conflicts;
    }

    private static int SegmentCount(ConstraintPolyline line) =>
        line.PointCount < 2 ? 0 : line.IsClosed && line.PointCount > 2 ? line.PointCount : line.PointCount - 1;

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(ConstraintPolyline line)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int k = 0; k < line.PointCount; k++)
        {
            minX = Math.Min(minX, line.Points[k * 3]); maxX = Math.Max(maxX, line.Points[k * 3]);
            minY = Math.Min(minY, line.Points[k * 3 + 1]); maxY = Math.Max(maxY, line.Points[k * 3 + 1]);
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>True when the open segments cross at one interior point of each (parameters strictly inside 0..1).</summary>
    private static bool TryProperCrossing(
        double ax0, double ay0, double ax1, double ay1, double bx0, double by0, double bx1, double by1, out double t, out double u)
    {
        t = u = 0.0;
        double rx = ax1 - ax0, ry = ay1 - ay0, sx = bx1 - bx0, sy = by1 - by0;
        double denominator = (rx * sy) - (ry * sx);
        if (Math.Abs(denominator) <= 1e-15 * Math.Max(1.0, Math.Abs(rx * sy) + Math.Abs(ry * sx)))
            return false;

        double qx = bx0 - ax0, qy = by0 - ay0;
        t = ((qx * sy) - (qy * sx)) / denominator;
        u = ((qx * ry) - (qy * rx)) / denominator;
        const double inside = 1e-9;
        return t > inside && t < 1 - inside && u > inside && u < 1 - inside;
    }
}
