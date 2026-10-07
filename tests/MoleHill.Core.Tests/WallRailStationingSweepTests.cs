using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Seeded random walls drawn the way users draw them: a centreline, optionally filleted, offset to both
/// sides with sharp or round joins, each rail on its own grade. Before corners were anchored as bends,
/// 15% of these folded; round joins, the Offset command's other corner style, folded most often.
/// </summary>
public class WallRailStationingSweepTests
{
    private const double Tolerance = 0.005;

    [Fact]
    public void Synchronize_RandomOffsetWalls_NeverFoldOrLeaveSubToleranceSpans()
    {
        int tested = 0;
        var failures = new List<string>();
        for (int seed = 0; seed < 1500; seed++)
        {
            var random = new Random(seed);
            double width = new[] { 0.2, 0.5, 1.5 }[random.Next(3)];
            List<(double X, double Y)> centreline = RandomCentreline(random, width);
            bool round = random.Next(2) == 0;
            double step = new[] { 5.0, 10.0, 30.0 }[random.Next(3)];
            if (random.Next(3) == 0)
                centreline = Fillet(centreline, width * (1 + random.NextDouble() * 4), step);
            double grade = new[] { 0.0, 0.5, 3.0 }[random.Next(3)];
            double[] left = Offset(centreline, width / 2, round, step, random, grade);
            double[] right = Offset(centreline, -width / 2, round, step, random, grade);
            if (SelfCrosses(left) || SelfCrosses(right) || Crosses(left, right))
                continue;

            tested++;
            var result = WallRailStationing.Synchronize(left, right, false, Tolerance);
            try
            {
                WallRailStationingTests.AssertStripDoesNotFold(result.First, result.Second);
                WallRailStationingTests.AssertVerticesRetained(left, result.First);
                WallRailStationingTests.AssertVerticesRetained(right, result.Second);
                AssertNoSubToleranceSpan(result, left, right);
            }
            catch (Exception exception)
            {
                failures.Add($"seed {seed} (width {width}, round {round}, step {step}): {exception.Message}");
            }
        }

        Assert.True(tested > 1000, $"Only {tested} valid walls generated.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {tested} walls failed: " + string.Join("; ", failures));
    }

    // A span shorter than the tolerance on both rails is one the stationing made, unless one rail drew
    // it: two of its own vertices that close are the user's geometry and are kept.
    private static void AssertNoSubToleranceSpan(WallRailStationing.Result result, double[] left, double[] right)
    {
        for (int i = 3; i < result.First.Length; i += 3)
        {
            double a = Distance(result.First, i - 3, i);
            double b = Distance(result.Second, i - 3, i);
            if (a > Tolerance || b > Tolerance)
                continue;
            bool drawn = (IsVertex(left, result.First, i - 3) && IsVertex(left, result.First, i)) ||
                         (IsVertex(right, result.Second, i - 3) && IsVertex(right, result.Second, i));
            Assert.True(drawn, $"Span {i / 3} is {Math.Max(a, b)} long.");
        }
    }

    private static bool IsVertex(double[] rail, double[] stations, int station)
    {
        for (int j = 0; j < rail.Length; j += 3)
            if (Math.Abs(rail[j] - stations[station]) < 1e-9 && Math.Abs(rail[j + 1] - stations[station + 1]) < 1e-9 &&
                Math.Abs(rail[j + 2] - stations[station + 2]) < 1e-9)
                return true;
        return false;
    }

    private static double Distance(double[] points, int p, int q)
    {
        double dx = points[q] - points[p], dy = points[q + 1] - points[p + 1], dz = points[q + 2] - points[p + 2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static List<(double X, double Y)> RandomCentreline(Random random, double width)
    {
        var points = new List<(double X, double Y)> { (0, 0) };
        int legs = random.Next(2, 9);
        double heading = 0;
        for (int i = 0; i < legs; i++)
        {
            if (i > 0)
                heading += (random.Next(2) == 0 ? 1 : -1) * (20 + random.NextDouble() * 115) * Math.PI / 180;
            double length = Math.Max(width * 4, 1 + random.NextDouble() * 25);
            points.Add((points[^1].X + length * Math.Cos(heading), points[^1].Y + length * Math.Sin(heading)));
        }
        return points;
    }

    private static List<(double X, double Y)> Fillet(List<(double X, double Y)> points, double radius, double stepDegrees)
    {
        var output = new List<(double X, double Y)> { points[0] };
        for (int i = 1; i < points.Count - 1; i++)
        {
            (double ax, double ay, double al) = Unit(points[i - 1], points[i]);
            (double bx, double by, double bl) = Unit(points[i], points[i + 1]);
            double turn = Math.Atan2(ax * by - ay * bx, ax * bx + ay * by);
            double setback = radius * Math.Tan(Math.Abs(turn) / 2);
            if (setback > 0.45 * Math.Min(al, bl))
            {
                output.Add(points[i]);
                continue;
            }
            double sx = points[i].X - ax * setback, sy = points[i].Y - ay * setback;
            double side = Math.Sign(turn);
            double cx = sx - ay * radius * side, cy = sy + ax * radius * side;
            double start = Math.Atan2(sy - cy, sx - cx);
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(turn) * 180 / Math.PI / stepDegrees));
            for (int k = 0; k <= steps; k++)
            {
                double angle = start + turn * k / steps;
                output.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }
        }
        output.Add(points[^1]);
        return output;
    }

    // Offsets to the left by distance (right when negative). The outside of each turn gets a miter or a
    // round join, as Rhino's Offset does; the inside of a turn is always the miter.
    private static double[] Offset(List<(double X, double Y)> line, double distance, bool round, double stepDegrees, Random random, double grade)
    {
        var points = new List<(double X, double Y)>();
        (double X, double Y) Normal(int i)
        {
            (double x, double y, _) = Unit(line[i], line[i + 1]);
            return (-y, x);
        }

        var first = Normal(0);
        points.Add((line[0].X + distance * first.X, line[0].Y + distance * first.Y));
        for (int i = 1; i < line.Count - 1; i++)
        {
            var a = Normal(i - 1);
            var b = Normal(i);
            double turn = a.X * b.Y - a.Y * b.X;
            bool outside = (turn > 0) != (distance > 0);
            if (outside && round)
            {
                double from = Math.Atan2(a.Y, a.X);
                double sweep = Math.Atan2(turn, a.X * b.X + a.Y * b.Y);
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) * 180 / Math.PI / stepDegrees));
                for (int k = 0; k <= steps; k++)
                {
                    double angle = from + sweep * k / steps;
                    points.Add((line[i].X + distance * Math.Cos(angle), line[i].Y + distance * Math.Sin(angle)));
                }
            }
            else
            {
                double mx = a.X + b.X, my = a.Y + b.Y, ml = Math.Sqrt(mx * mx + my * my);
                mx /= ml;
                my /= ml;
                double length = distance / (mx * a.X + my * a.Y);
                points.Add((line[i].X + length * mx, line[i].Y + length * my));
            }
        }
        var last = Normal(line.Count - 2);
        points.Add((line[^1].X + distance * last.X, line[^1].Y + distance * last.Y));

        var result = new double[points.Count * 3];
        double z = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0)
                z += (random.NextDouble() - 0.3) * grade;
            result[i * 3] = points[i].X;
            result[i * 3 + 1] = points[i].Y;
            result[i * 3 + 2] = z;
        }
        return result;
    }

    private static (double X, double Y, double Length) Unit((double X, double Y) from, (double X, double Y) to)
    {
        double x = to.X - from.X, y = to.Y - from.Y, length = Math.Sqrt(x * x + y * y);
        return (x / length, y / length, length);
    }

    private static bool SelfCrosses(double[] rail)
    {
        int segments = rail.Length / 3 - 1;
        for (int i = 0; i < segments; i++)
            for (int j = i + 2; j < segments; j++)
                if (SegmentsCross(rail, i, rail, j))
                    return true;
        return false;
    }

    private static bool Crosses(double[] a, double[] b)
    {
        for (int i = 0; i < a.Length / 3 - 1; i++)
            for (int j = 0; j < b.Length / 3 - 1; j++)
                if (SegmentsCross(a, i, b, j))
                    return true;
        return false;
    }

    private static bool SegmentsCross(double[] p, int i, double[] q, int j)
    {
        double ax = p[i * 3], ay = p[i * 3 + 1], bx = p[i * 3 + 3], by = p[i * 3 + 4];
        double cx = q[j * 3], cy = q[j * 3 + 1], dx = q[j * 3 + 3], dy = q[j * 3 + 4];
        double d1 = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        double d2 = (bx - ax) * (dy - ay) - (by - ay) * (dx - ax);
        double d3 = (dx - cx) * (ay - cy) - (dy - cy) * (ax - cx);
        double d4 = (dx - cx) * (by - cy) - (dy - cy) * (bx - cx);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }
}
