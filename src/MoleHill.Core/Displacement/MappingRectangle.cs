namespace MoleHill.Core.Displacement;

/// <summary>
/// An image tile placed by a drawn rectangle. Convention: the curve's <b>start point is the image's
/// bottom-left corner</b>, and the image's bottom edge runs from it <b>counter-clockwise</b> around the
/// rectangle (seen in plan), so the rest of the rectangle lies above the bottom edge and the image is never
/// mirrored. The drawing direction therefore does not matter — a rectangle traced clockwise from the same
/// corner places the image identically — while the start corner does: start at another corner to turn the
/// image a quarter turn.
/// </summary>
public readonly record struct MappingRectangle(double OriginX, double OriginY, double Width, double Height, double RotationDegrees)
{
    /// <summary>The placement of the image tile: origin at the start corner, rotated to the bottom edge.</summary>
    public DisplacementPlacement Placement => new(OriginX, OriginY, RotationDegrees);

    /// <summary>
    /// Reads a closed polyline (flat XYZ, <paramref name="pointCount"/> points; a repeated closing point is
    /// allowed) as a level rectangle within <paramref name="tolerance"/>. Points lying on a side are ignored,
    /// but the start point must be a corner. On failure <paramref name="error"/> says what is wrong with it.
    /// </summary>
    public static bool TryFromPolyline(double[] xyz, int pointCount, double tolerance, out MappingRectangle rectangle, out string? error)
    {
        ArgumentNullException.ThrowIfNull(xyz);
        rectangle = default;
        double tol = Math.Max(tolerance, 1e-12);
        int count = Math.Min(pointCount, xyz.Length / 3);
        var points = new List<(double X, double Y, double Z)>(count);
        for (int i = 0; i < count; i++)
            points.Add((xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]));

        if (points.Count > 1 && Distance(points[0], points[^1]) <= tol)
            points.RemoveAt(points.Count - 1);

        double minZ = points.Count == 0 ? 0.0 : points.Min(p => p.Z);
        double maxZ = points.Count == 0 ? 0.0 : points.Max(p => p.Z);
        if (maxZ - minZ > tol)
        {
            error = "The mapping rectangle is not level: draw it flat in plan (all its corners at one height).";
            return false;
        }

        // Drop repeated points and points along a side, keeping the order.
        bool changed = true;
        while (changed && points.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < points.Count && points.Count >= 3; i++)
            {
                var previous = points[(i + points.Count - 1) % points.Count];
                var current = points[i];
                var next = points[(i + 1) % points.Count];
                if (Distance(previous, current) > tol && DistanceToLine(current, previous, next) > tol)
                    continue;

                if (i == 0)
                {
                    error = "The mapping rectangle's start point is not a corner. Start it at the corner that is the image's bottom-left.";
                    return false;
                }

                points.RemoveAt(i);
                changed = true;
                break;
            }
        }

        if (points.Count != 4)
        {
            error = $"The mapping curve has {points.Count} corners; a rectangle has 4.";
            return false;
        }

        // Counter-clockwise from the start corner: a clockwise trace is walked the other way round.
        double area = 0.0;
        for (int i = 0; i < 4; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 4];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        if (area < 0.0)
            points = new List<(double, double, double)> { points[0], points[3], points[2], points[1] };

        var c0 = points[0];
        double e1x = points[1].X - c0.X, e1y = points[1].Y - c0.Y;
        double e2x = points[3].X - c0.X, e2y = points[3].Y - c0.Y;
        double width = Math.Sqrt((e1x * e1x) + (e1y * e1y));
        double height = Math.Sqrt((e2x * e2x) + (e2y * e2y));
        if (width <= tol || height <= tol)
        {
            error = "The mapping rectangle has no area.";
            return false;
        }

        double squareness = Math.Abs((e1x * e2x) + (e1y * e2y)) / Math.Max(width, height);
        double closure = Math.Sqrt(
            Math.Pow(points[2].X - (c0.X + e1x + e2x), 2) +
            Math.Pow(points[2].Y - (c0.Y + e1y + e2y), 2));
        if (squareness > tol || closure > tol)
        {
            error = "The mapping curve is not a rectangle: its corners are not square. Draw it with Rhino's Rectangle command.";
            return false;
        }

        rectangle = new MappingRectangle(c0.X, c0.Y, width, height, Math.Atan2(e1y, e1x) * 180.0 / Math.PI);
        error = null;
        return true;
    }

    private static double Distance((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double DistanceToLine((double X, double Y, double Z) p, (double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-15)
            return Distance(p, a);
        return Math.Abs(((p.X - a.X) * dy) - ((p.Y - a.Y) * dx)) / length;
    }
}
