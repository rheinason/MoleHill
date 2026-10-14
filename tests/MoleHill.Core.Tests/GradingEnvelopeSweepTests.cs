using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Randomised Grade Path and Grade Pad scenes, each checked against the invariants every grade must keep:
/// no point leaves the grading envelope (no tier had to clamp, and none remains outside it), and the mesh
/// stays manifold and watertight. The scenes lean on what has broken grading before: ground that starts
/// below a design edge and rises above it further out, kerbs (hard constraints) close behind a batter,
/// Grade Lines, asymmetric cut and fill slopes, and both tier orders.
///
/// <para>Deterministic: every scene comes from its seed, and a failure names the seeds to replay. Set
/// <c>MOLEHILL_GRADING_SWEEP</c> to a scene count for a deeper run than the default.</para>
/// </summary>
public class GradingEnvelopeSweepTests
{
    private const int DefaultSceneCount = 60;
    private const double Spacing = 0.75;
    private const int Cells = 40;
    private const double Extent = Spacing * Cells;

    [Fact]
    public void GradePath_RandomScenes_StayInEnvelopeAndWatertight()
    {
        var failures = new List<string>();
        int graded = 0;
        int scenes = SceneCount();
        for (int seed = 1; seed <= scenes; seed++)
        {
            foreach (bool preferSplitKeep in new[] { false, true })
            {
                var random = new Random(seed);
                Ground ground = Ground.Random(random);
                IndexedTriMesh terrain = BuildTerrain(ground);
                PathGrader.PathDefinition path = RandomPath(random, ground, out double[] centreline);
                ConstraintPolyline[] kerb = random.NextDouble() < 0.5
                    ? KerbAlong(centreline, ground, offset: (path.Width * 0.5) + 1.0 + (random.NextDouble() * 4.0), side: random.Next(2) == 0 ? 1.0 : -1.0)
                    : Array.Empty<ConstraintPolyline>();

                var request = new PathGradeRequest
                {
                    Terrain = terrain,
                    Paths = new[] { path },
                    HardConstraints = kerb,
                    ModelTolerance = 0.001,
                    PreferSplitKeep = preferSplitKeep
                };

                GradeOutcome outcome = PathGrader.Grade(request);
                if (outcome.Result is not { } result)
                    continue;

                graded++;
                string scene = $"path seed {seed} (prefer split-keep {preferSplitKeep}, kerb {kerb.Length > 0})";
                Check(scene, terrain, result, PathGrader.FindEnvelopeViolations(request, result), failures);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(graded >= scenes, $"Only {graded} of {scenes * 2} path scenes graded; the sweep is not exercising the graders.");
    }

    [Fact]
    public void GradePad_RandomScenes_StayInEnvelopeAndWatertight()
    {
        var failures = new List<string>();
        int graded = 0;
        int scenes = SceneCount();
        for (int seed = 1; seed <= scenes; seed++)
        {
            var random = new Random(10_000 + seed);
            Ground ground = Ground.Random(random);
            IndexedTriMesh terrain = BuildTerrain(ground);
            int padCount = random.NextDouble() < 0.3 ? 2 : 1;
            var pads = new PadGrader.PadBoundary[padCount];
            for (int p = 0; p < padCount; p++)
                pads[p] = RandomPad(random, ground);

            ConstraintPolyline[] kerb = random.NextDouble() < 0.4
                ? KerbAlong(new[] { 2.0, Extent * 0.2, Extent - 2.0, Extent * 0.25 }, ground, offset: 0.0, side: 1.0)
                : Array.Empty<ConstraintPolyline>();

            var request = new PadGradeRequest
            {
                Terrain = terrain,
                Pads = pads,
                HardConstraints = kerb,
                ModelTolerance = 0.001
            };

            GradeOutcome outcome = PadGrader.Grade(request);
            if (outcome.Result is not { } result)
                continue;

            graded++;
            string scene = $"pad seed {10_000 + seed} ({padCount} pad(s), kerb {kerb.Length > 0})";
            Check(scene, terrain, result, PadGrader.FindEnvelopeViolations(request, result), failures);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(graded >= scenes / 2, $"Only {graded} of {scenes} pad scenes graded; the sweep is not exercising the graders.");
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

    /// <summary>
    /// A path across the terrain: two to four vertices, a Grade Line about one time in six, its height
    /// offset from the ground by up to 1.5 either way, independent cut and fill slopes.
    /// </summary>
    private static PathGrader.PathDefinition RandomPath(Random random, Ground ground, out double[] centreline)
    {
        int vertexCount = 2 + random.Next(3);
        centreline = new double[vertexCount * 2];
        double y0 = Extent * (0.3 + (random.NextDouble() * 0.4));
        var z = new double[vertexCount];
        double offset = (random.NextDouble() * 3.0) - 1.5;
        for (int i = 0; i < vertexCount; i++)
        {
            double t = vertexCount == 1 ? 0.0 : i / (double)(vertexCount - 1);
            double x = 4.0 + (t * (Extent - 8.0));
            double y = y0 + ((random.NextDouble() - 0.5) * Extent * 0.25);
            centreline[i * 2] = x;
            centreline[(i * 2) + 1] = y;
            z[i] = ground.Z(x, y) + offset + ((random.NextDouble() - 0.5) * 0.4);
        }

        double width = random.NextDouble() < 0.17 ? 0.0 : 1.0 + (random.NextDouble() * 3.0);
        double cutSlope = 20.0 + (random.NextDouble() * 40.0);
        double fillSlope = random.NextDouble() < 0.5 ? 0.0 : 20.0 + (random.NextDouble() * 40.0);
        return new PathGrader.PathDefinition(
            (double[])centreline.Clone(), z, vertexCount, width, slopeAngleDeg: cutSlope, fillSlopeAngleDeg: fillSlope);
    }

    /// <summary>A rectangle (or a slightly skewed quad) up to 2 m above or below the ground at its centre.</summary>
    private static PadGrader.PadBoundary RandomPad(Random random, Ground ground)
    {
        double cx = Extent * (0.3 + (random.NextDouble() * 0.4));
        double cy = Extent * (0.3 + (random.NextDouble() * 0.4));
        double hx = 1.5 + (random.NextDouble() * 4.0);
        double hy = 1.5 + (random.NextDouble() * 4.0);
        double skew = (random.NextDouble() - 0.5) * hx * 0.6;
        double[] xy =
        {
            cx - hx, cy - hy,
            cx + hx, cy - hy,
            cx + hx + skew, cy + hy,
            cx - hx + skew, cy + hy
        };
        double targetZ = ground.Z(cx, cy) + ((random.NextDouble() * 4.0) - 2.0);
        double cutSlope = 20.0 + (random.NextDouble() * 40.0);
        double fillSlope = random.NextDouble() < 0.5 ? 0.0 : 20.0 + (random.NextDouble() * 40.0);
        return new PadGrader.PadBoundary(xy, 4, targetZ, slopeAngleDeg: cutSlope, fillSlopeAngleDeg: fillSlope);
    }

    /// <summary>A kerb: two lines 0.1 apart, the outer 0.1 lower, following the ground.</summary>
    private static ConstraintPolyline[] KerbAlong(double[] line, Ground ground, double offset, double side)
    {
        int count = line.Length / 2;
        var top = new double[count * 3];
        var foot = new double[count * 3];
        for (int i = 0; i < count; i++)
        {
            int a = Math.Max(0, i - 1);
            int b = Math.Min(count - 1, i + 1);
            double tx = line[b * 2] - line[a * 2];
            double ty = line[(b * 2) + 1] - line[(a * 2) + 1];
            double length = Math.Sqrt((tx * tx) + (ty * ty));
            double nx = -ty / length * side;
            double ny = tx / length * side;
            double x = Math.Clamp(line[i * 2] + (nx * offset), 0.5, Extent - 0.5);
            double y = Math.Clamp(line[(i * 2) + 1] + (ny * offset), 0.5, Extent - 0.5);
            double fx = Math.Clamp(x + (nx * 0.1), 0.5, Extent - 0.5);
            double fy = Math.Clamp(y + (ny * 0.1), 0.5, Extent - 0.5);
            top[i * 3] = x;
            top[(i * 3) + 1] = y;
            top[(i * 3) + 2] = ground.Z(x, y);
            foot[i * 3] = fx;
            foot[(i * 3) + 1] = fy;
            foot[(i * 3) + 2] = ground.Z(fx, fy) - 0.1;
        }

        return new[]
        {
            new ConstraintPolyline(top, count, false, true),
            new ConstraintPolyline(foot, count, false, true)
        };
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
                double x = column * Spacing;
                double y = row * Spacing;
                vertices[index * 3] = x;
                vertices[(index * 3) + 1] = y;
                vertices[(index * 3) + 2] = ground.Z(x, y);
            }
        }

        var faces = new int[Cells * Cells * 6];
        int face = 0;
        for (int row = 0; row < Cells; row++)
        {
            for (int column = 0; column < Cells; column++)
            {
                int a = (row * side) + column;
                int b = a + 1;
                int c = a + side + 1;
                int d = a + side;
                faces[face++] = a; faces[face++] = b; faces[face++] = c;
                faces[face++] = a; faces[face++] = c; faces[face++] = d;
            }
        }

        return new IndexedTriMesh(vertices, side * side, faces, faces.Length / 3);
    }

    /// <summary>
    /// Ground: a tilted plane, rolling waves, and about one time in three a terrace — a band where the ground
    /// steps up by up to 1.2, the verge-then-terrace section that once stood a batter 5 m proud.
    /// </summary>
    private sealed class Ground
    {
        private double _slopeX, _slopeY, _waveAmplitude, _waveX, _waveY, _phase;
        private double _terraceY, _terraceRise, _terraceWidth;

        public static Ground Random(Random random)
        {
            var ground = new Ground
            {
                _slopeX = (random.NextDouble() - 0.5) * 0.3,
                _slopeY = (random.NextDouble() - 0.5) * 0.3,
                _waveAmplitude = random.NextDouble() * 1.2,
                _waveX = 0.1 + (random.NextDouble() * 0.4),
                _waveY = 0.1 + (random.NextDouble() * 0.4),
                _phase = random.NextDouble() * Math.PI * 2.0
            };

            if (random.NextDouble() < 0.35)
            {
                ground._terraceY = Extent * (0.35 + (random.NextDouble() * 0.4));
                ground._terraceRise = (random.NextDouble() * 2.4) - 1.2;
                ground._terraceWidth = 0.3 + (random.NextDouble() * 1.5);
            }

            return ground;
        }

        public double Z(double x, double y)
        {
            double z = (_slopeX * x) + (_slopeY * y) +
                (_waveAmplitude * Math.Sin((x * _waveX) + _phase) * Math.Cos(y * _waveY));
            if (_terraceWidth > 0.0)
                z += _terraceRise * Math.Clamp((y - _terraceY) / _terraceWidth, 0.0, 1.0);
            return z;
        }
    }
}
