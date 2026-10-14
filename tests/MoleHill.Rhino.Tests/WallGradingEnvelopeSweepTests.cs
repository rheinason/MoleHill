using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Shared;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Randomised Retaining Wall grades through the real rail planner (<see cref="RetainingWallGradePlanner"/>),
/// each required to stay inside the grading envelope and watertight — the wall counterpart of Core's
/// <c>GradingEnvelopeSweepTests</c>. A wall is two one-sided rails at different heights, each battering
/// away from the other: a different path through the grader from any road, and the one with the longest
/// bug history. Scenes vary the plan shape (straight, bent, closed ring), the rail gap and wall height,
/// the toe's seat in the ground, per-side slopes, Max Distance, and a second terraced wall. One tier order
/// only: a one-sided rail always defers the explicit tier to split-keep, so preferring split-keep grades
/// the same scene twice (checked: identical results).
///
/// <para>Point3d is a managed struct, so this runs without the native runtime. Deterministic per seed; a
/// failure names the seeds. <c>MOLEHILL_GRADING_SWEEP</c> sets a deeper count.</para>
/// </summary>
public class WallGradingEnvelopeSweepTests
{
    private const int DefaultSceneCount = 24;
    private const double Spacing = 0.75;
    private const int Cells = 40;
    private const double Extent = Spacing * Cells;
    private const double WallTolerance = 0.003;

    [Fact]
    public void GradeWall_RandomScenes_StayInEnvelopeAndWatertight()
    {
        var failures = new List<string>();
        int graded = 0;
        int scenes = SceneCount();
        for (int seed = 1; seed <= scenes; seed++)
        {
            {
                var random = new Random(20_000 + seed);
                Ground ground = Ground.Random(random);
                IndexedTriMesh terrain = BuildTerrain(ground);

                string shape = (random.Next(3)) switch { 0 => "straight", 1 => "bent", _ => "ring" };
                var walls = new List<RetainingWallPlannerCore.PlannedWall> { RandomWall(random, ground, shape, offset: 0.0) };
                bool terraced = shape != "ring" && random.NextDouble() < 0.25;
                if (terraced)
                    walls.Add(RandomWall(random, ground, shape, offset: 4.0 + (random.NextDouble() * 4.0)));

                PathGrader.PathDefinition[] rails = RetainingWallGradePlanner.Build(walls, RandomOptions(random)).ToArray();
                var request = new PathGradeRequest
                {
                    Terrain = terrain,
                    Paths = rails,
                    HardConstraints = Array.Empty<ConstraintPolyline>(),
                    ModelTolerance = WallTolerance,
                    PreferSplitKeep = false
                };

                GradeOutcome outcome = PathGrader.Grade(request);
                if (outcome.Result is not { } result)
                    continue;

                graded++;
                string scene = $"wall seed {20_000 + seed} ({shape}{(terraced ? ", terraced" : string.Empty)})";
                Check(scene, terrain, result, PathGrader.FindEnvelopeViolations(request, result), failures);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(graded >= scenes * 3 / 4, $"Only {graded} of {scenes} wall scenes graded; the sweep is not exercising the grader.");
    }

    private static void Check(
        string scene,
        IndexedTriMesh terrain,
        GradingResult result,
        IReadOnlyList<GradingEnvelopeViolation> violations,
        List<string> failures)
    {
        if (result.HasEnvelopeFault)
        {
            string clamped = result.StructuredDiagnostics.First(d => d.Code.EndsWith(".envelope.clamped", StringComparison.Ordinal)).Message;
            failures.Add($"{scene}: {clamped}");
        }

        if (violations.Count > 0)
            failures.Add($"{scene}: {violations.Count} point(s) outside the envelope after grading (worst {violations.Max(v => v.Overshoot):0.###}).");

        MeshTopologyValidator.BoundaryGraphAnalysis before = MeshTopologyValidator.AnalyzeBoundaryGraph(terrain.Faces, terrain.FaceCount);
        MeshTopologyValidator.BoundaryGraphAnalysis after = MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);
        if (after.NonManifoldEdgeCount > 0 || after.HasOpenBoundaryChains || after.BoundaryComponentCount > before.BoundaryComponentCount)
        {
            failures.Add(
                $"{scene}: topology {after.NonManifoldEdgeCount} non-manifold edge(s), open chains {after.HasOpenBoundaryChains}, " +
                $"{after.BoundaryComponentCount} boundary loop(s) from {before.BoundaryComponentCount}.");
        }
    }

    private static int SceneCount() =>
        int.TryParse(Environment.GetEnvironmentVariable("MOLEHILL_GRADING_SWEEP"), out int count) && count > 0
            ? count
            : DefaultSceneCount;

    private static RetainingWallGradePlanner.Options RandomOptions(Random random)
    {
        RetainingWallGradePlanner.SideSlopes Side() => random.NextDouble() < 0.5
            ? RetainingWallGradePlanner.SideSlopes.Inherit
            : new RetainingWallGradePlanner.SideSlopes(20.0 + (random.NextDouble() * 40.0), 20.0 + (random.NextDouble() * 40.0));

        return new RetainingWallGradePlanner.Options
        {
            FillAngleDeg = 20.0 + (random.NextDouble() * 40.0),
            CutAngleDeg = random.NextDouble() < 0.5 ? 0.0 : 20.0 + (random.NextDouble() * 40.0),
            Toe = Side(),
            Top = Side(),
            MaxDistance = random.NextDouble() < 0.2 ? 2.0 + (random.NextDouble() * 4.0) : 0.0,
            Tolerance = WallTolerance
        };
    }

    /// <summary>
    /// A wall: the toe seated up to 0.4 into the ground (or on it), the top 0.5–3 above the toe, the rails
    /// 0.2–1 apart in plan with the top on the side the ground is higher, so the wall retains something.
    /// </summary>
    private static RetainingWallPlannerCore.PlannedWall RandomWall(Random random, Ground ground, string shape, double offset)
    {
        Point2d[] plan = shape switch
        {
            "ring" => Ring(random),
            "bent" => Bent(random),
            _ => Straight(random)
        };
        bool closed = shape == "ring";
        if (offset > 0.0)
            plan = Offset(plan, closed, offset);

        double gap = 0.2 + (random.NextDouble() * 0.8);
        double height = 0.5 + (random.NextDouble() * 2.5);
        double seat = random.NextDouble() < 0.5 ? 0.0 : random.NextDouble() * 0.4;

        // Put the top rail on whichever side the ground rises, judged at the wall's middle.
        Point2d[] plus = Offset(plan, closed, gap);
        Point2d[] minus = Offset(plan, closed, -gap);
        int mid = plan.Length / 2;
        Point2d[] topPlan = ground.Z(plus[mid].X, plus[mid].Y) >= ground.Z(minus[mid].X, minus[mid].Y) ? plus : minus;

        var toe = new Point3d[plan.Length];
        var top = new Point3d[plan.Length];
        for (int i = 0; i < plan.Length; i++)
        {
            double toeZ = ground.Z(plan[i].X, plan[i].Y) - seat;
            toe[i] = new Point3d(plan[i].X, plan[i].Y, toeZ);
            top[i] = new Point3d(topPlan[i].X, topPlan[i].Y, toeZ + height);
        }

        return new RetainingWallPlannerCore.PlannedWall(
            new RetainingWallPlannerCore.WallRails(toe, top, closed, minWidth: gap),
            brep: null,
            curveA: 0,
            curveB: 1,
            pairLine: new Line(toe[0], top[0]));
    }

    private static Point2d[] Straight(Random random)
    {
        double y = Extent * (0.35 + (random.NextDouble() * 0.3));
        double tilt = (random.NextDouble() - 0.5) * Extent * 0.3;
        var points = new Point2d[8];
        for (int i = 0; i < points.Length; i++)
        {
            double t = i / (double)(points.Length - 1);
            points[i] = new Point2d(6.0 + (t * (Extent - 12.0)), y + (t * tilt));
        }

        return points;
    }

    private static Point2d[] Bent(Random random)
    {
        double cx = Extent * 0.5;
        double cy = Extent * (0.4 + (random.NextDouble() * 0.2));
        double angle = (30.0 + (random.NextDouble() * 100.0)) * Math.PI / 180.0;
        double arm = Extent * 0.3;
        var points = new List<Point2d>();
        for (int i = 6; i >= 1; i--)
            points.Add(new Point2d(cx - (arm * i / 6.0), cy));
        points.Add(new Point2d(cx, cy));
        for (int i = 1; i <= 6; i++)
            points.Add(new Point2d(cx + (arm * i / 6.0 * Math.Cos(angle)), cy + (arm * i / 6.0 * Math.Sin(angle))));
        return points.ToArray();
    }

    private static Point2d[] Ring(Random random)
    {
        double cx = Extent * (0.45 + (random.NextDouble() * 0.1));
        double cy = Extent * (0.45 + (random.NextDouble() * 0.1));
        double radius = 4.0 + (random.NextDouble() * 6.0);
        int count = 6 + random.Next(10);
        var points = new Point2d[count];
        for (int i = 0; i < count; i++)
        {
            double a = 2.0 * Math.PI * i / count;
            points[i] = new Point2d(cx + (radius * Math.Cos(a)), cy + (radius * Math.Sin(a)));
        }

        return points;
    }

    /// <summary>Offsets a plan polyline sideways by averaging the neighbouring segment normals.</summary>
    private static Point2d[] Offset(Point2d[] plan, bool closed, double distance)
    {
        int n = plan.Length;
        var result = new Point2d[n];
        for (int i = 0; i < n; i++)
        {
            Point2d prev = closed ? plan[(i - 1 + n) % n] : plan[Math.Max(0, i - 1)];
            Point2d next = closed ? plan[(i + 1) % n] : plan[Math.Min(n - 1, i + 1)];
            double tx = next.X - prev.X;
            double ty = next.Y - prev.Y;
            double length = Math.Sqrt((tx * tx) + (ty * ty));
            result[i] = new Point2d(plan[i].X + (-ty / length * distance), plan[i].Y + (tx / length * distance));
        }

        return result;
    }

    private static IndexedTriMesh BuildTerrain(Ground ground)
    {
        int side = Cells + 1;
        var vertices = new double[side * side * 3];
        for (int row = 0; row < side; row++)
        {
            for (int column = 0; column < side; column++)
            {
                int index = (row * side) + column;
                vertices[index * 3] = column * Spacing;
                vertices[(index * 3) + 1] = row * Spacing;
                vertices[(index * 3) + 2] = ground.Z(column * Spacing, row * Spacing);
            }
        }

        var faces = new int[Cells * Cells * 6];
        int face = 0;
        for (int row = 0; row < Cells; row++)
        {
            for (int column = 0; column < Cells; column++)
            {
                int a = (row * side) + column;
                faces[face++] = a; faces[face++] = a + 1; faces[face++] = a + side + 1;
                faces[face++] = a; faces[face++] = a + side + 1; faces[face++] = a + side;
            }
        }

        return new IndexedTriMesh(vertices, side * side, faces, faces.Length / 3);
    }

    /// <summary>A tilted plane with rolling waves: hillsides a wall would be built into.</summary>
    private sealed class Ground
    {
        private double _slopeX, _slopeY, _amplitude, _waveX, _waveY, _phase;

        public static Ground Random(Random random) => new()
        {
            _slopeX = (random.NextDouble() - 0.5) * 0.4,
            _slopeY = (random.NextDouble() - 0.5) * 0.4,
            _amplitude = random.NextDouble() * 1.2,
            _waveX = 0.1 + (random.NextDouble() * 0.3),
            _waveY = 0.1 + (random.NextDouble() * 0.3),
            _phase = random.NextDouble() * Math.PI * 2.0
        };

        public double Z(double x, double y) =>
            (_slopeX * x) + (_slopeY * y) + (_amplitude * Math.Sin((x * _waveX) + _phase) * Math.Cos(y * _waveY));
    }
}
