using System;
using System.Collections.Generic;
using MoleHill.Core.Engine;
using MoleHill.Core.Retopo;
using Xunit;

namespace MoleHill.Core.Tests;

internal static class RetopoTestMeshes
{
    // n x n vertex grid at integer coords, right-triangulated. tiltZ => z = x (a plane); else z = 0.
    public static (double[] verts, int[] faces) Grid(int n, bool tiltZ = false)
    {
        var verts = new double[n * n * 3];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int idx = (j * n) + i;
                verts[idx * 3] = i;
                verts[idx * 3 + 1] = j;
                verts[idx * 3 + 2] = tiltZ ? i : 0;
            }

        var faces = new List<int>();
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                int a = (j * n) + i, b = (j * n) + i + 1, c = ((j + 1) * n) + i + 1, d = ((j + 1) * n) + i;
                faces.AddRange(new[] { a, b, c, a, c, d });
            }

        return (verts, faces.ToArray());
    }
}

public class GuidedParametrizerTests
{
    [Fact]
    public void Solve_AxisGrid_UFollowsXAndVFollowsYAtUnitSpacing()
    {
        (double[] verts, int[] faces) = RetopoTestMeshes.Grid(6);
        int n = 6;
        var theta = new double[n * n]; // axis-aligned field (θ = 0)

        (double[] u, double[] v) = GuidedParametrizer.Solve(verts, faces, theta, h: 1.0);

        // u ≈ x (+const), v ≈ y (+const): one lattice step per unit at h = 1.
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n - 1; i++)
            {
                int here = (j * n) + i, right = (j * n) + i + 1;
                Assert.Equal(1.0, u[right] - u[here], 3); // Δu per +x step ≈ 1
                Assert.Equal(0.0, v[right] - v[here], 3); // v does not vary in x
            }

        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n; i++)
            {
                int here = (j * n) + i, up = ((j + 1) * n) + i;
                Assert.Equal(1.0, v[up] - v[here], 3); // Δv per +y step ≈ 1
                Assert.Equal(0.0, u[up] - u[here], 3);
            }
    }
}

public class QuadExtractorTests
{
    // Direct u = x, v = y for a grid, so extraction is tested in isolation from the solver.
    private static (double[] u, double[] v) IdentityUv(double[] verts)
    {
        int count = verts.Length / 3;
        var u = new double[count];
        var v = new double[count];
        for (int i = 0; i < count; i++)
        {
            u[i] = verts[i * 3];
            v[i] = verts[i * 3 + 1];
        }

        return (u, v);
    }

    [Fact]
    public void Extract_AxisGrid_ProducesRegularAllQuadGrid()
    {
        (double[] verts, int[] faces) = RetopoTestMeshes.Grid(5); // coords 0..4
        (double[] u, double[] v) = IdentityUv(verts);

        QuadExtractor.QuadMesh mesh = QuadExtractor.Extract(verts, faces, u, v, 1e-6);

        Assert.Equal(16, mesh.QuadCount);   // 4 x 4 cells
        Assert.Equal(25, mesh.VertexCount); // 5 x 5 lattice
        Assert.Empty(mesh.Tris);
    }

    [Fact]
    public void Extract_LiftsZFromTheSourceSurface()
    {
        (double[] verts, int[] faces) = RetopoTestMeshes.Grid(5, tiltZ: true); // z = x plane
        (double[] u, double[] v) = IdentityUv(verts);

        QuadExtractor.QuadMesh mesh = QuadExtractor.Extract(verts, faces, u, v, 1e-6);

        Assert.True(mesh.VertexCount > 0);
        for (int i = 0; i < mesh.VertexCount; i++)
            Assert.Equal(mesh.Vertices[i * 3], mesh.Vertices[i * 3 + 2], 6); // z == x
    }
}

public class QuadRemesherTests
{
    private static IReadOnlyList<SurfaceRemesher.ConstraintPolyline> NoConstraints =>
        Array.Empty<SurfaceRemesher.ConstraintPolyline>();

    [Fact]
    public void Remesh_AxisGrid_IsQuadDominant()
    {
        (double[] verts, int[] faces) = RetopoTestMeshes.Grid(8);

        QuadRemesher.Result r = QuadRemesher.Remesh(
            verts, faces, NoConstraints, new QuadRemesher.Options { EdgeLength = 1.0, Tolerance = 0.001 });

        Assert.True(r.Success, r.Warning);
        Assert.True(r.QuadCount > 0);
        for (int i = 0; i < r.Vertices.Length; i++)
            Assert.True(double.IsFinite(r.Vertices[i]));
    }

    // Roof folding along y = 0 (z = |y|) — an irregular, creased case; must not crash or NaN.
    private static (double[] verts, int[] faces) RoofMesh()
    {
        var verts = new List<double>();
        for (int j = -3; j <= 3; j++)
            for (int i = -3; i <= 3; i++)
                verts.AddRange(new double[] { i, j, Math.Abs(j) });
        int Idx(int i, int j) => ((j + 3) * 7) + (i + 3);
        var faces = new List<int>();
        for (int j = -3; j < 3; j++)
            for (int i = -3; i < 3; i++)
                faces.AddRange(new[] { Idx(i, j), Idx(i + 1, j), Idx(i + 1, j + 1), Idx(i, j), Idx(i + 1, j + 1), Idx(i, j + 1) });
        return (verts.ToArray(), faces.ToArray());
    }

    [Fact]
    public void Remesh_CreasedMesh_RunsAndStaysFinite()
    {
        (double[] verts, int[] faces) = RoofMesh();

        QuadRemesher.Result r = QuadRemesher.Remesh(
            verts, faces, NoConstraints, new QuadRemesher.Options { EdgeLength = 1.0, CreaseAngleDeg = 30, Tolerance = 0.001 });

        // Whatever it produces, it must be finite and quad-dominant when it succeeds.
        for (int i = 0; i < r.Vertices.Length; i++)
            Assert.True(double.IsFinite(r.Vertices[i]), $"vertex component {i} not finite");
        if (r.Success)
            Assert.True(r.QuadCount > 0);
    }
}
