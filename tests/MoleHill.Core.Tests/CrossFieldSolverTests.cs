using System;
using System.Collections.Generic;
using MoleHill.Core.Engine;
using MoleHill.Core.Retopo;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Stage 1 of quad retopology: the 2-D cross-field. Covers feature/boundary alignment, smoothness,
/// determinism, and robustness (no NaN) on an irregular mesh.
/// </summary>
public class CrossFieldSolverTests
{
    private static IReadOnlyList<ConstraintPolyline> NoConstraints =>
        Array.Empty<ConstraintPolyline>();

    // n x n vertex grid, right-triangulated, optionally rotated by phi in XY (z = 0; irrelevant to field).
    private static (double[] verts, int[] faces) Grid(int n, double phi)
    {
        double c = Math.Cos(phi), s = Math.Sin(phi);
        var verts = new double[n * n * 3];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int idx = (j * n) + i;
                verts[idx * 3] = (i * c) - (j * s);
                verts[idx * 3 + 1] = (i * s) + (j * c);
                verts[idx * 3 + 2] = 0;
            }

        var faces = new List<int>();
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                int a = (j * n) + i, b = (j * n) + i + 1, d = ((j + 1) * n) + i + 1, e = ((j + 1) * n) + i;
                faces.AddRange(new[] { a, b, d, a, d, e });
            }

        return (verts, faces.ToArray());
    }

    // Angular distance in the 4-RoSy quotient [0, pi/2).
    private static double QuadAngleDistance(double a, double b)
    {
        double d = Math.Abs(a - b) % (Math.PI / 2.0);
        return Math.Min(d, (Math.PI / 2.0) - d);
    }

    [Fact]
    public void Solve_AxisAlignedGrid_FieldIsAxisAligned()
    {
        (double[] verts, int[] faces) = Grid(6, 0.0);

        CrossFieldSolver.Result r = CrossFieldSolver.Solve(
            verts, faces, NoConstraints, new CrossFieldSolver.Options { Tolerance = 0.001 });

        Assert.True(r.Success, r.Warning);
        for (int i = 0; i < r.Theta.Length; i++)
            Assert.True(QuadAngleDistance(r.Theta[i], 0.0) < 1e-6, $"vertex {i} theta {r.Theta[i]} not axis-aligned");
    }

    [Fact]
    public void Solve_RotatedGrid_FieldAlignsToTheBoundaryTangent()
    {
        double phi = 20.0 * Math.PI / 180.0;
        (double[] verts, int[] faces) = Grid(6, phi);

        CrossFieldSolver.Result r = CrossFieldSolver.Solve(
            verts, faces, NoConstraints, new CrossFieldSolver.Options { Tolerance = 0.001 });

        Assert.True(r.Success, r.Warning);
        // The boundary (the only feature) is rotated by phi, so the whole field aligns to phi.
        for (int i = 0; i < r.Theta.Length; i++)
            Assert.True(QuadAngleDistance(r.Theta[i], phi) < 1e-6, $"vertex {i} theta {r.Theta[i]} != phi {phi}");

        // Boundary vertices are pinned; there is at least one.
        bool anyPinned = false;
        foreach (bool p in r.Pinned) anyPinned |= p;
        Assert.True(anyPinned, "expected boundary vertices to be pinned");
    }

    [Fact]
    public void Solve_PinnedVerticesRetainTheirFeatureDirection()
    {
        double phi = 35.0 * Math.PI / 180.0;
        (double[] verts, int[] faces) = Grid(6, phi);

        CrossFieldSolver.Result r = CrossFieldSolver.Solve(
            verts, faces, NoConstraints, new CrossFieldSolver.Options { Tolerance = 0.001 });

        Assert.True(r.Success, r.Warning);
        for (int i = 0; i < r.Pinned.Length; i++)
            if (r.Pinned[i])
                Assert.True(QuadAngleDistance(r.Theta[i], phi) < 1e-9, $"pinned vertex {i} drifted from its feature tangent");
    }

    [Fact]
    public void Solve_IsDeterministic()
    {
        (double[] verts, int[] faces) = Grid(6, 0.3);

        CrossFieldSolver.Result a = CrossFieldSolver.Solve(verts, faces, NoConstraints, new CrossFieldSolver.Options { Tolerance = 0.001 });
        CrossFieldSolver.Result b = CrossFieldSolver.Solve(verts, faces, NoConstraints, new CrossFieldSolver.Options { Tolerance = 0.001 });

        Assert.Equal(a.Theta.Length, b.Theta.Length);
        for (int i = 0; i < a.Theta.Length; i++)
            Assert.Equal(a.Theta[i], b.Theta[i], 12);
    }

    // Roof: two planes folding along y = 0 (z = |y|) — an irregular, creased case.
    private static (double[] verts, int[] faces) RoofMesh()
    {
        var verts = new List<double>();
        for (int j = -2; j <= 2; j++)
            for (int i = -2; i <= 2; i++)
                verts.AddRange(new double[] { i, j, Math.Abs(j) });
        int Idx(int i, int j) => ((j + 2) * 5) + (i + 2);
        var faces = new List<int>();
        for (int j = -2; j < 2; j++)
            for (int i = -2; i < 2; i++)
                faces.AddRange(new[] { Idx(i, j), Idx(i + 1, j), Idx(i + 1, j + 1), Idx(i, j), Idx(i + 1, j + 1), Idx(i, j + 1) });
        return (verts.ToArray(), faces.ToArray());
    }

    [Fact]
    public void Solve_CreasedMesh_ProducesFiniteFieldInRangeWithPins()
    {
        (double[] verts, int[] faces) = RoofMesh();

        CrossFieldSolver.Result r = CrossFieldSolver.Solve(
            verts, faces, NoConstraints, new CrossFieldSolver.Options { CreaseAngleDeg = 30, Tolerance = 0.001 });

        Assert.True(r.Success, r.Warning);
        int pinnedCount = 0;
        for (int i = 0; i < r.Theta.Length; i++)
        {
            Assert.True(double.IsFinite(r.Theta[i]), $"vertex {i} theta not finite");
            Assert.InRange(r.Theta[i], 0.0, Math.PI / 2.0);
            if (r.Pinned[i]) pinnedCount++;
        }

        Assert.True(pinnedCount > 0, "crease + boundary should pin vertices");
    }
}
