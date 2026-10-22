using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Grade Path beside hard constraints: breaklines and walls built into the terrain as edges, as a Rhino
/// build has them, with a path placed against each at the distances and angles that have refused grades
/// before — running alongside inside the road, crossing at a shallow angle, a stub reaching into the road,
/// a path ending just short of a line, a wall beside or across the road. Every scene must grade (the path
/// is cut where it meets the constraint, never refused whole), keep the terrain watertight, and leave every
/// constraint at its own height. The one refusal allowed is a wall inside the road along its whole length.
/// </summary>
public class GradePathNearConstraintsStressTests
{
    private const double Spacing = 0.75;
    private const int Cells = 40;
    private const double Extent = Spacing * Cells;
    private const double PathY = Extent * 0.5;

    private readonly ITestOutputHelper _output;

    public GradePathNearConstraintsStressTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void GradePath_BesideBreaklinesAndWalls_GradesAndStaysWatertight()
    {
        var failures = new List<string>();
        var tally = new SortedDictionary<string, (int Graded, int Total)>(StringComparer.Ordinal);
        foreach (Scene scene in Scenes())
        {
            foreach (bool preferSplitKeep in new[] { false, true })
            {
                IndexedTriMesh terrain = BuildTerrain(scene);
                var request = new PathGradeRequest
                {
                    Terrain = terrain,
                    Paths = new[] { scene.Path },
                    HardConstraints = scene.Constraints,
                    ModelTolerance = 0.001,
                    PreferSplitKeep = preferSplitKeep
                };

                GradeOutcome outcome = PathGrader.Grade(request);
                string name = $"{scene.Name} (split-keep first {preferSplitKeep})";
                (int graded, int total) = tally.TryGetValue(scene.Family, out var t) ? t : (0, 0);
                if (scene.WhollyBlocked)
                {
                    // Nothing can be graded without burying the constraint: the refusal must say so.
                    if (outcome.Result != null || outcome.ErrorMessage?.Contains("along its whole length", StringComparison.Ordinal) != true)
                        failures.Add($"{name}: expected a whole-length block, got {(outcome.Result != null ? "a grade" : outcome.ErrorMessage)}");
                    continue;
                }

                if (outcome.Result is not { } result)
                {
                    failures.Add($"{name}: refused: {outcome.ErrorMessage}");
                    tally[scene.Family] = (graded, total + 1);
                    continue;
                }

                tally[scene.Family] = (graded + 1, total + 1);
                MeshTopologyValidator.BoundaryGraphAnalysis before = MeshTopologyValidator.AnalyzeBoundaryGraph(terrain.Faces, terrain.FaceCount);
                MeshTopologyValidator.BoundaryGraphAnalysis after = MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);
                if (after.NonManifoldEdgeCount > 0 || after.HasOpenBoundaryChains || after.BoundaryComponentCount > before.BoundaryComponentCount)
                    failures.Add($"{name}: topology {after.NonManifoldEdgeCount} non-manifold, open chains {after.HasOpenBoundaryChains}, {after.BoundaryComponentCount} loop(s).");
                if (result.HasEnvelopeFault)
                    failures.Add($"{name}: envelope clamped.");
                (double moved, double atX, double atY) = ConstraintHeightCheck.WorstChange(scene.Constraints, terrain, result);
                if (moved > 1e-3)
                    failures.Add($"{name}: a hard constraint was graded over (moved {moved:0.###} at {atX:0.##}, {atY - PathY:+0.##;-0.##} off the path).");
            }
        }

        foreach ((string family, (int graded, int total)) in tally)
            _output.WriteLine($"{family,-28} {graded,3}/{total,-3}");
        foreach (string failure in failures)
            _output.WriteLine(failure);

        Assert.True(failures.Count == 0, $"{failures.Count} failure(s):{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private sealed record Scene(
        string Family, string Name, PathGrader.PathDefinition Path, ConstraintPolyline[] Constraints, Func<double, double, double> Ground,
        bool WhollyBlocked = false);

    private static IEnumerable<Scene> Scenes()
    {
        foreach (double width in new[] { 2.0, 4.0 })
        {
            foreach (double rise in new[] { -1.0, 0.0, 1.0 })
            {
                double half = width * 0.5;
                Func<double, double, double> rolling = (x, y) => (0.08 * x) + (0.05 * y) + (0.4 * Math.Sin(x * 0.3) * Math.Cos(y * 0.2));
                string tag = $"w{width:0} rise{rise:+0;-0;0}";

                // Alongside: a breakline parallel to the road, from inside it to just beyond its edge.
                foreach (double offset in new[] { 0.3 * half, 0.9 * half, half, 1.1 * half, half + 0.5, half + 2.0 })
                {
                    yield return Make("parallel", $"{tag} parallel at {offset:0.##}", width, rise, rolling,
                        Line(rolling, 6.0, PathY + offset, 24.0, PathY + offset));
                }

                // Crossing the centreline at shrinking angles, through the path's middle vertex and between vertices.
                foreach (double at in new[] { 15.0, 11.0 })
                {
                    foreach (double angle in new[] { 90.0, 45.0, 20.0, 8.0 })
                    {
                        double r = Math.PI * angle / 180.0;
                        double reach = Math.Min(6.0, 4.0 / Math.Sin(r));
                        yield return Make("crossing", $"{tag} crossing at x{at:0} {angle:0} deg", width, rise, rolling,
                            Line(rolling, at - (Math.Cos(r) * reach), PathY - (Math.Sin(r) * reach), at + (Math.Cos(r) * reach), PathY + (Math.Sin(r) * reach)));
                    }
                }

                // A stub from outside reaching into the road without crossing the centreline.
                foreach (double at in new[] { 15.0, 12.0 })
                {
                    foreach (double tip in new[] { 0.2 * half, 0.6 * half, 0.95 * half })
                    {
                        yield return Make("stub", $"{tag} stub at x{at:0} to {tip:0.##}", width, rise, rolling,
                            Line(rolling, at, PathY + half + 4.0, at, PathY + tip));
                    }
                }

                // A line across the road just beyond the path's end, and just inside it.
                foreach (double beyond in new[] { -0.5, 0.05, 0.3, half, half + 1.0 })
                {
                    double x = 26.0 + beyond;
                    yield return Make("end", $"{tag} line {beyond:+0.##;-0.##} past end", width, rise, rolling,
                        Line(rolling, x, PathY - 5.0, x, PathY + 5.0));
                }

                // A bend (V) whose apex sits inside the road.
                foreach (double apex in new[] { 0.5 * half, -0.5 * half })
                {
                    yield return Make("bend", $"{tag} bend apex {apex:+0.##;-0.##}", width, rise, rolling,
                        Polyline(rolling, false, 10.0, PathY + half + 4.0, 15.0, PathY + apex, 20.0, PathY + half + 4.0));
                }

                // A closed ring (a pad's edge) whose side lies inside the road.
                foreach (double inset in new[] { 0.3 * half, half + 0.3 })
                {
                    double y0 = PathY + inset;
                    yield return Make("ring", $"{tag} ring edge at {inset:0.##}", width, rise, rolling,
                        Polyline(rolling, true, 12.0, y0, 18.0, y0, 18.0, y0 + 5.0, 12.0, y0 + 5.0));
                }

                // A wall: top and foot rails 0.15 apart, the ground stepped down beyond it, across the whole site.
                foreach (double height in new[] { 0.3, 1.2 })
                {
                    foreach (double offset in new[] { 0.5 * half, half + 0.3, half + 1.5 })
                    {
                        double top = PathY + offset;
                        Func<double, double, double> stepped = (x, y) => rolling(x, y) - (y > top + 0.075 ? height : 0.0);
                        // Inside the road the wall runs the site's whole length, so the path is blocked throughout.
                        yield return Make("wall-parallel", $"{tag} wall h{height:0.#} at {offset:0.##}", width, rise, stepped,
                            WallRails(stepped, 0.0, top, Extent, top, 0.15)) with { WhollyBlocked = offset < half };
                    }

                    foreach (double angle in new[] { 90.0, 30.0 })
                    {
                        // A wall across the road: the step follows the wall's line.
                        double r = Math.PI * angle / 180.0;
                        double dx = Math.Cos(r), dy = Math.Sin(r);
                        double nx = dy, ny = -dx;
                        Func<double, double, double> stepped = (x, y) =>
                            rolling(x, y) - ((((x - 15.0) * nx) + ((y - PathY) * ny)) > 0.075 ? height : 0.0);
                        double reach = Math.Min(20.0, (PathY - 0.5) / Math.Sin(r));
                        yield return Make("wall-crossing", $"{tag} wall h{height:0.#} across at {angle:0} deg", width, rise, stepped,
                            WallRails(stepped, 15.0 - (dx * reach), PathY - (dy * reach), 15.0 + (dx * reach), PathY + (dy * reach), 0.15));
                    }
                }
            }
        }
    }

    private static Scene Make(string family, string name, double width, double rise, Func<double, double, double> ground, params ConstraintPolyline[] constraints)
    {
        double[] xy = { 4.0, PathY, 15.0, PathY + 0.01, 26.0, PathY };
        var z = new double[3];
        for (int i = 0; i < 3; i++)
            z[i] = ground(xy[i * 2], xy[(i * 2) + 1]) + rise;
        var path = new PathGrader.PathDefinition(xy, z, 3, width, slopeAngleDeg: 33.0, fillSlopeAngleDeg: 30.0);
        return new Scene(family, $"{family}: {name}", path, constraints, ground);
    }

    private static ConstraintPolyline Line(Func<double, double, double> ground, double ax, double ay, double bx, double by) =>
        Polyline(ground, false, ax, ay, bx, by);

    private static ConstraintPolyline Polyline(Func<double, double, double> ground, bool closed, params double[] xy)
    {
        int count = xy.Length / 2;
        var points = new double[count * 3];
        for (int i = 0; i < count; i++)
        {
            double x = Math.Clamp(xy[i * 2], 0.2, Extent - 0.2);
            double y = Math.Clamp(xy[(i * 2) + 1], 0.2, Extent - 0.2);
            points[i * 3] = x;
            points[(i * 3) + 1] = y;
            points[(i * 3) + 2] = ground(x, y);
        }

        return new ConstraintPolyline(points, count, closed, PreserveInputElevation: true);
    }

    /// <summary>A wall's top rail on the drawn line and its foot rail <paramref name="gap"/> to the low side.</summary>
    private static ConstraintPolyline[] WallRails(
        Func<double, double, double> stepped, double ax, double ay, double bx, double by, double gap)
    {
        double dx = bx - ax, dy = by - ay;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        double px = dy / length, py = -dx / length;

        // Low side: the side the stepped ground is lower on.
        double mx = (ax + bx) * 0.5, my = (ay + by) * 0.5;
        if (stepped(mx + (px * 0.5), my + (py * 0.5)) > stepped(mx - (px * 0.5), my - (py * 0.5)))
        {
            px = -px;
            py = -py;
        }

        ConstraintPolyline top = Line((x, y) => stepped(x - (px * 0.2), y - (py * 0.2)), ax, ay, bx, by);
        ConstraintPolyline foot = Line((x, y) => stepped(x + (px * 0.2), y + (py * 0.2)),
            ax + (px * gap), ay + (py * gap), bx + (px * gap), by + (py * gap));
        return new[] { top, foot };
    }

    /// <summary>The site's lattice with every constraint triangulated in as edges, stationed at the lattice spacing.</summary>
    private static IndexedTriMesh BuildTerrain(Scene scene)
    {
        var xy = new List<double>();
        var z = new List<double>();
        var segments = new List<int>();
        var stationed = new List<(double Ax, double Ay, double Bx, double By)>();
        foreach (ConstraintPolyline constraint in scene.Constraints)
        {
            int count = constraint.PointCount;
            int spans = constraint.IsClosed ? count : count - 1;
            int first = xy.Count / 2;
            int previous = -1;
            for (int s = 0; s < spans; s++)
            {
                int t = (s + 1) % count;
                double ax = constraint.Points[s * 3], ay = constraint.Points[(s * 3) + 1], az = constraint.Points[(s * 3) + 2];
                double bx = constraint.Points[t * 3], by = constraint.Points[(t * 3) + 1], bz = constraint.Points[(t * 3) + 2];
                stationed.Add((ax, ay, bx, by));
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay))) / Spacing));
                for (int k = 0; k < steps; k++)
                {
                    double u = k / (double)steps;
                    int index = xy.Count / 2;
                    xy.Add(ax + ((bx - ax) * u));
                    xy.Add(ay + ((by - ay) * u));
                    z.Add(az + ((bz - az) * u));
                    if (previous >= 0)
                    {
                        segments.Add(previous);
                        segments.Add(index);
                    }

                    previous = index;
                }
            }

            if (constraint.IsClosed)
            {
                segments.Add(previous);
                segments.Add(first);
            }
            else
            {
                int last = xy.Count / 2;
                int lastPoint = count - 1;
                xy.Add(constraint.Points[lastPoint * 3]);
                xy.Add(constraint.Points[(lastPoint * 3) + 1]);
                z.Add(constraint.Points[(lastPoint * 3) + 2]);
                segments.Add(previous);
                segments.Add(last);
            }
        }

        for (int row = 0; row <= Cells; row++)
        {
            for (int column = 0; column <= Cells; column++)
            {
                double x = column * Spacing;
                double y = row * Spacing;
                if (stationed.Any(s => DistanceToSegment(x, y, s) < Spacing * 0.3))
                    continue;
                xy.Add(x);
                xy.Add(y);
                z.Add(scene.Ground(x, y));
            }
        }

        TinResult? tin = new TinEngine().Build(xy.ToArray(), z.ToArray(), segments.ToArray(), QualitySettings.None, out string? message);
        Assert.True(tin != null, $"{scene.Name}: terrain did not triangulate: {message}");
        return new IndexedTriMesh(tin!.Vertices, tin.VertexCount, tin.Faces, tin.FaceCount);
    }

    private static double DistanceToSegment(double x, double y, (double Ax, double Ay, double Bx, double By) s)
    {
        double dx = s.Bx - s.Ax, dy = s.By - s.Ay;
        double lengthSq = (dx * dx) + (dy * dy);
        double t = lengthSq > 0 ? Math.Clamp((((x - s.Ax) * dx) + ((y - s.Ay) * dy)) / lengthSq, 0.0, 1.0) : 0.0;
        double ex = s.Ax + (dx * t) - x, ey = s.Ay + (dy * t) - y;
        return Math.Sqrt((ex * ex) + (ey * ey));
    }
}
