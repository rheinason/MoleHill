using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Randomised smoothing, checked against the rules every smooth keeps (<see cref="MeshSmoother.FindViolations(double[], double[], MeshSmoother.PreparedSmoothingData, double, double, int, double)"/>):
/// held points do not move, and no point leaves the input range of the points it could have mixed with —
/// smoothing makes no new peak or pit. Scenes are irregular (jittered) meshes over noisy, spiky ground,
/// whole-mesh or region smoothing, breaklines at no, partial and full hold, and 1–25 passes, through both
/// the prepared path the Rhino Smooth stage uses and the one-shot path Grasshopper uses.
///
/// <para>Deterministic per seed; a failure names the seeds. <c>MOLEHILL_GRADING_SWEEP</c> sets a deeper
/// count.</para>
/// </summary>
public class MeshSmootherSweepTests
{
    private const int DefaultSceneCount = 60;
    private const int Cells = 30;
    private const double Spacing = 1.0;
    private const double Tolerance = 1e-9;

    [Fact]
    public void SmoothPrepared_RandomScenes_KeepHeldPointsAndMakeNoNewExtremes()
    {
        var failures = new List<string>();
        for (int seed = 1; seed <= SceneCount(); seed++)
            RunPreparedScene(30_000 + seed, failures);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The scenes a 2,000-scene sweep found making a new peak or pit (0.5-2 cm, one point, one or two
    /// passes): a fitted plane landing outside an irregular one-ring. Pinned so the default run keeps them.
    /// </summary>
    [Theory]
    [InlineData(31389)]
    [InlineData(31575)]
    [InlineData(31896)]
    public void SmoothPrepared_SceneWhoseFittedPlaneOvershotItsRing_MakesNoNewExtreme(int seed)
    {
        var failures = new List<string>();
        RunPreparedScene(seed, failures);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void FindViolations_SpikeAndMovedBorderPoint_AreBothReported()
    {
        var random = new Random(1);
        IndexedTriMesh mesh = RandomMesh(random);
        MeshSmoother.PreparedSmoothingData prepared = MeshSmoother.Prepare(
            mesh, Array.Empty<(double[], int)>(), Array.Empty<MeshSmoother.BreaklinePolyline>(), 0.01);
        double[] output = MeshSmoother.SmoothPrepared(mesh.Vertices, prepared, 0.5, 0.0, 3);
        int interior = ((Cells / 2) * (Cells + 1)) + (Cells / 2);
        output[(interior * 3) + 2] += 50.0;
        output[2] += 1.0;

        IReadOnlyList<SmoothingViolation> violations = MeshSmoother.FindViolations(mesh.Vertices, output, prepared, 0.5, 0.0, 3, Tolerance);

        Assert.Contains(violations, v => v.VertexIndex == interior && !v.IsHeld);
        Assert.Contains(violations, v => v.VertexIndex == 0 && v.IsHeld);
    }

    private static void RunPreparedScene(int seed, List<string> failures)
    {
        {
            var random = new Random(seed);
            IndexedTriMesh mesh = RandomMesh(random);
            (double[] xyVerts, int vertCount)[] regions = RandomRegions(random).Select(r => (r.xyVerts, r.vertCount)).ToArray();
            MeshSmoother.BreaklinePolyline[] breaklines = RandomBreaklines(random);
            double strength = 0.1 + (random.NextDouble() * 0.9);
            double fixity = new[] { 0.0, 0.5, 1.0 }[random.Next(3)];
            int iterations = 1 + random.Next(25);

            MeshSmoother.PreparedSmoothingData prepared = MeshSmoother.Prepare(mesh, regions, breaklines, snapTolerance: 0.01);
            double[] output = MeshSmoother.SmoothPrepared(mesh.Vertices, prepared, strength, fixity, iterations);

            Check(
                $"prepared seed {seed} ({regions.Length} region(s), {breaklines.Length} breakline(s) at {fixity}, {iterations} pass(es))",
                mesh,
                output,
                MeshSmoother.FindViolations(mesh.Vertices, output, prepared, strength, fixity, iterations, Tolerance),
                failures);
        }
    }

    [Fact]
    public void Smooth_RandomScenes_KeepHeldPointsAndMakeNoNewExtremes()
    {
        var failures = new List<string>();
        for (int seed = 1; seed <= SceneCount(); seed++)
        {
            var random = new Random(40_000 + seed);
            IndexedTriMesh mesh = RandomMesh(random);
            (double[] xyVerts, int vertCount, double strength)[] regions = RandomRegions(random);
            MeshSmoother.BreaklinePolyline[] breaklines = RandomBreaklines(random);
            double strength = 0.1 + (random.NextDouble() * 0.9);
            double fixity = new[] { 0.0, 0.5, 1.0 }[random.Next(3)];
            int iterations = 1 + random.Next(25);

            double[] output = MeshSmoother.Smooth(mesh, regions, strength, breaklines, fixity, 0.01, iterations);

            Check(
                $"one-shot seed {40_000 + seed} ({regions.Length} region(s), {breaklines.Length} breakline(s) at {fixity}, {iterations} pass(es))",
                mesh,
                output,
                MeshSmoother.FindViolations(mesh, output, regions, strength, breaklines, fixity, 0.01, iterations, Tolerance),
                failures);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void Check(string scene, IndexedTriMesh mesh, double[] output, IReadOnlyList<SmoothingViolation> violations, List<string> failures)
    {
        if (violations.Count > 0)
        {
            int held = violations.Count(v => v.IsHeld);
            failures.Add(
                $"{scene}: {held} held point(s) moved, {violations.Count - held} new peak(s) or pit(s) " +
                $"(worst {violations.Max(v => v.Overshoot):0.####}).");
        }

        for (int i = 0; i < mesh.VertexCount; i++)
        {
            if (output[i * 3] != mesh.Vertices[i * 3] || output[(i * 3) + 1] != mesh.Vertices[(i * 3) + 1])
            {
                failures.Add($"{scene}: smoothing moved point {i} in plan.");
                break;
            }
        }
    }

    private static int SceneCount() =>
        int.TryParse(Environment.GetEnvironmentVariable("MOLEHILL_GRADING_SWEEP"), out int count) && count > 0
            ? count
            : DefaultSceneCount;

    /// <summary>
    /// A grid jittered in plan by up to a third of its spacing, so one-rings are irregular — where a fitted
    /// plane can land outside its neighbours' range — over a slope with noise and occasional spikes and pits.
    /// </summary>
    private static IndexedTriMesh RandomMesh(Random random)
    {
        int side = Cells + 1;
        double slopeX = (random.NextDouble() - 0.5) * 0.4;
        double slopeY = (random.NextDouble() - 0.5) * 0.4;
        double noise = random.NextDouble() * 0.6;
        var vertices = new double[side * side * 3];
        for (int row = 0; row < side; row++)
        {
            for (int column = 0; column < side; column++)
            {
                int index = (row * side) + column;
                bool border = row == 0 || column == 0 || row == Cells || column == Cells;
                double x = (column * Spacing) + (border ? 0.0 : (random.NextDouble() - 0.5) * 0.66 * Spacing);
                double y = (row * Spacing) + (border ? 0.0 : (random.NextDouble() - 0.5) * 0.66 * Spacing);
                double z = (slopeX * x) + (slopeY * y) + ((random.NextDouble() - 0.5) * noise);
                if (random.NextDouble() < 0.02)
                    z += (random.NextDouble() - 0.5) * 6.0;
                vertices[index * 3] = x;
                vertices[(index * 3) + 1] = y;
                vertices[(index * 3) + 2] = z;
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

    /// <summary>None (smooth everything), or one or two quads with their own strengths.</summary>
    private static (double[] xyVerts, int vertCount, double strength)[] RandomRegions(Random random)
    {
        int count = random.Next(3);
        var regions = new (double[], int, double)[count];
        for (int r = 0; r < count; r++)
        {
            double cx = Cells * (0.3 + (random.NextDouble() * 0.4));
            double cy = Cells * (0.3 + (random.NextDouble() * 0.4));
            double h = 3.0 + (random.NextDouble() * 8.0);
            regions[r] = (new[] { cx - h, cy - h, cx + h, cy - h, cx + h, cy + h, cx - h, cy + h }, 4, 0.2 + (random.NextDouble() * 0.8));
        }

        return regions;
    }

    /// <summary>Zero to three breaklines running through mesh vertices, so some points sit exactly on them.</summary>
    private static MeshSmoother.BreaklinePolyline[] RandomBreaklines(Random random)
    {
        int count = random.Next(4);
        var breaklines = new MeshSmoother.BreaklinePolyline[count];
        for (int b = 0; b < count; b++)
        {
            double y = 2 + random.Next(Cells - 4);
            breaklines[b] = new MeshSmoother.BreaklinePolyline(new[] { 1.0, y, Cells - 1.0, y + random.Next(-3, 4) }, 2, false);
        }

        return breaklines;
    }
}
