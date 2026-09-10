using MoleHill.Core.Engine;
using MoleHill.Core.Retopo;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Cross-field diffusion stops on a residual instead of always spending its whole iteration budget
/// (which reaches 2,000 sweeps on a large mesh). The stopping rule is only justified if the field it
/// leaves is the same field the exhausted budget would have left, and if it is still deterministic.
/// </summary>
public class CrossFieldConvergenceTests
{
    private static readonly IReadOnlyList<SurfaceRemesher.ConstraintPolyline> NoConstraints =
        Array.Empty<SurfaceRemesher.ConstraintPolyline>();

    [Fact]
    public void Solve_StopsWellShortOfItsIterationBudget()
    {
        (double[] vertices, int[] faces) = Sheet(40);

        CrossFieldSolver.Result result = Solve(vertices, faces, iterations: 2000);

        Assert.True(result.Success, result.Warning);
        Assert.True(result.Converged, "Diffusion should reach its tolerance on a plain sheet.");
        Assert.True(result.Iterations < 2000, $"Ran {result.Iterations} of 2000 sweeps.");
        Assert.True(result.FinalResidual <= 1e-7);
    }

    [Fact]
    public void Solve_ConvergedField_MatchesTheFullyIteratedField()
    {
        // The decisive check: the early exit must not leave the field short of where the full budget
        // would have taken it.
        (double[] vertices, int[] faces) = Sheet(40);

        CrossFieldSolver.Result converged = Solve(vertices, faces, iterations: 2000);
        CrossFieldSolver.Result exhausted = Solve(vertices, faces, iterations: 2000, convergenceTolerance: 0.0);

        Assert.True(converged.Success, converged.Warning);
        Assert.True(exhausted.Success, exhausted.Warning);
        Assert.Equal(2000, exhausted.Iterations);
        Assert.Equal(converged.Theta.Length, exhausted.Theta.Length);
        for (int i = 0; i < converged.Theta.Length; i++)
            Assert.Equal(exhausted.Theta[i], converged.Theta[i], 9);
    }

    [Fact]
    public void Solve_ConvergedField_MatchesTheFullyIteratedFieldOnAnIrregularMesh()
    {
        (double[] vertices, int[] faces) = IrregularSheet();

        CrossFieldSolver.Result converged = Solve(vertices, faces, iterations: 2000);
        CrossFieldSolver.Result exhausted = Solve(vertices, faces, iterations: 2000, convergenceTolerance: 0.0);

        Assert.True(converged.Success, converged.Warning);
        for (int i = 0; i < converged.Theta.Length; i++)
            Assert.Equal(exhausted.Theta[i], converged.Theta[i], 9);
    }

    [Fact]
    public void Solve_FieldThatHasNotConverged_RunsItsWholeBudgetAndIsUnchanged()
    {
        // A disc's boundary tangent sweeps through every direction, so diffusion is still moving when
        // the budget runs out. The stopping rule must not fire there, and must leave that run exactly
        // as it was: the budget, not the tolerance, is what binds on this shape.
        (double[] vertices, int[] faces) = Disc(rings: 20);

        CrossFieldSolver.Result result = Solve(vertices, faces, iterations: 120);
        CrossFieldSolver.Result exhausted = Solve(vertices, faces, iterations: 120, convergenceTolerance: 0.0);

        Assert.True(result.Success, result.Warning);
        Assert.False(result.Converged);
        Assert.Equal(120, result.Iterations);
        Assert.True(result.FinalResidual > 1e-7);
        Assert.Equal(exhausted.Theta, result.Theta);
    }

    [Fact]
    public void Solve_IsDeterministic()
    {
        (double[] vertices, int[] faces) = IrregularSheet();

        CrossFieldSolver.Result first = Solve(vertices, faces, iterations: 2000);
        CrossFieldSolver.Result second = Solve(vertices, faces, iterations: 2000);

        Assert.Equal(first.Iterations, second.Iterations);
        Assert.Equal(first.FinalResidual, second.FinalResidual);
        Assert.Equal(first.Theta, second.Theta);
        Assert.Equal(first.Pinned, second.Pinned);
    }

    [Fact]
    public void Solve_PinnedVerticesAreUntouchedByTheStoppingRule()
    {
        (double[] vertices, int[] faces) = Sheet(20);

        CrossFieldSolver.Result converged = Solve(vertices, faces, iterations: 2000);
        CrossFieldSolver.Result exhausted = Solve(vertices, faces, iterations: 2000, convergenceTolerance: 0.0);

        Assert.Equal(exhausted.Pinned, converged.Pinned);
        Assert.Contains(converged.Pinned, isPinned => isPinned);
        for (int i = 0; i < converged.Pinned.Length; i++)
        {
            if (converged.Pinned[i])
                Assert.Equal(exhausted.Theta[i], converged.Theta[i], 12);
        }
    }

    [Fact]
    public void Solve_ExplicitLowIterationCount_IsStillHonouredAsAMaximum()
    {
        (double[] vertices, int[] faces) = IrregularSheet();

        CrossFieldSolver.Result result = Solve(vertices, faces, iterations: 3);

        Assert.True(result.Iterations <= 3);
    }

    [Fact]
    public void Solve_EveryAngleStaysInTheQuarterTurnRange()
    {
        (double[] vertices, int[] faces) = IrregularSheet();

        CrossFieldSolver.Result result = Solve(vertices, faces, iterations: 2000);

        Assert.All(result.Theta, angle => Assert.InRange(angle, 0.0, Math.PI / 2.0));
    }

    private static CrossFieldSolver.Result Solve(
        double[] vertices,
        int[] faces,
        int iterations,
        double convergenceTolerance = 1e-7)
    {
        return CrossFieldSolver.Solve(
            vertices,
            faces,
            NoConstraints,
            new CrossFieldSolver.Options
            {
                CreaseAngleDeg = 30.0,
                Tolerance = 1e-6,
                Iterations = iterations,
                ConvergenceTolerance = convergenceTolerance
            });
    }

    private static (double[] Vertices, int[] Faces) Sheet(int n)
    {
        var xs = new double[n];
        for (int i = 0; i < n; i++)
            xs[i] = i;

        return Grid(xs, xs);
    }

    /// <summary>Non-uniform spacing, so the field has something to diffuse across.</summary>
    private static (double[] Vertices, int[] Faces) IrregularSheet()
    {
        var xs = new List<double>();
        for (double x = 0.0; x < 12.0; x += 3.0) xs.Add(x);
        for (double x = 12.0; x < 20.0; x += 0.75) xs.Add(x);
        for (double x = 20.0; x < 40.0; x += 3.0) xs.Add(x);
        xs.Add(40.0);

        var ys = new List<double>();
        for (double y = 0.0; y < 30.0; y += 2.0) ys.Add(y);
        ys.Add(30.0);

        return Grid(xs.ToArray(), ys.ToArray());
    }

    /// <summary>A radial disc: its boundary tangent takes every direction, so the field really diffuses.</summary>
    private static (double[] Vertices, int[] Faces) Disc(int rings)
    {
        int segments = Math.Max(8, rings * 3);
        var vertices = new List<double> { 0.0, 0.0, 0.0 };
        for (int ring = 1; ring <= rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                double angle = (segment * 2.0 * Math.PI) / segments;
                vertices.Add(Math.Cos(angle) * ring);
                vertices.Add(Math.Sin(angle) * ring);
                vertices.Add(0.0);
            }
        }

        int Index(int ring, int segment) => 1 + ((ring - 1) * segments) + (((segment % segments) + segments) % segments);

        var faces = new List<int>();
        for (int segment = 0; segment < segments; segment++)
        {
            faces.Add(0);
            faces.Add(Index(1, segment));
            faces.Add(Index(1, segment + 1));
        }

        for (int ring = 1; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                faces.Add(Index(ring, segment));
                faces.Add(Index(ring + 1, segment));
                faces.Add(Index(ring + 1, segment + 1));
                faces.Add(Index(ring, segment));
                faces.Add(Index(ring + 1, segment + 1));
                faces.Add(Index(ring, segment + 1));
            }
        }

        return (vertices.ToArray(), faces.ToArray());
    }

    private static (double[] Vertices, int[] Faces) Grid(double[] xs, double[] ys)
    {
        int nx = xs.Length;
        int ny = ys.Length;
        var vertices = new double[nx * ny * 3];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v = (j * nx) + i;
                vertices[v * 3] = xs[i];
                vertices[(v * 3) + 1] = ys[j];
                vertices[(v * 3) + 2] = Math.Sin(xs[i] * 0.15) * 2.0;
            }
        }

        var faces = new int[(nx - 1) * (ny - 1) * 6];
        int f = 0;
        for (int j = 0; j < ny - 1; j++)
        {
            for (int i = 0; i < nx - 1; i++)
            {
                int v00 = (j * nx) + i;
                int v10 = v00 + 1;
                int v01 = v00 + nx;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, faces);
    }
}
