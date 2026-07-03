using System;
using System.Collections.Generic;
using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Covers the connectivity-preserving "Local refine" remesh: coarse triangles subdivide in place on the
/// surface, fine triangles and all input vertices are kept, features/creases stay pinned, and the valence
/// flip pass regularizes without denting or crossing features.
/// </summary>
public class LocalMeshRefinerTests
{
    private static IReadOnlyList<SurfaceRemesher.ConstraintPolyline> NoConstraints =>
        Array.Empty<SurfaceRemesher.ConstraintPolyline>();

    // Unit square (z from a linear plane z = x) triangulated into two triangles.
    private static (double[] verts, int[] faces) TiltedSquare()
    {
        var verts = new double[]
        {
            0, 0, 0,
            1, 0, 1,
            1, 1, 1,
            0, 1, 0,
        };
        var faces = new[] { 0, 1, 2, 0, 2, 3 };
        return (verts, faces);
    }

    private static double MaxEdgeLength(double[] v, int[] f)
    {
        double max = 0;
        for (int t = 0; t < f.Length / 3; t++)
            for (int e = 0; e < 3; e++)
            {
                int a = f[t * 3 + e], b = f[t * 3 + ((e + 1) % 3)];
                double dx = v[a * 3] - v[b * 3], dy = v[a * 3 + 1] - v[b * 3 + 1], dz = v[a * 3 + 2] - v[b * 3 + 2];
                max = Math.Max(max, Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)));
            }
        return max;
    }

    [Fact]
    public void Refine_CoarseSquare_SubdividesUntilUnderTarget()
    {
        (double[] verts, int[] faces) = TiltedSquare();

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0.4, Tolerance = 0.001, DoFlips = true });

        Assert.True(r.Success, r.Warning);
        Assert.True(r.AddedVertices > 0);
        Assert.True(MaxEdgeLength(r.Vertices, r.Faces) <= 0.4 + 1e-9,
            $"max edge {MaxEdgeLength(r.Vertices, r.Faces)} should be <= target 0.4");
    }

    [Fact]
    public void Refine_KeepsAllInputVerticesInPlace()
    {
        (double[] verts, int[] faces) = TiltedSquare();

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0.4, Tolerance = 0.001 });

        // Original vertices are appended-to, never reindexed or moved.
        for (int i = 0; i < verts.Length; i++)
            Assert.Equal(verts[i], r.Vertices[i], 9);
    }

    [Fact]
    public void Refine_NewVerticesLieExactlyOnTheInputSurface()
    {
        // Input surface is the plane z = x; every added midpoint must satisfy z == x (no off-surface darts).
        (double[] verts, int[] faces) = TiltedSquare();

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0.3, Tolerance = 0.001 });

        Assert.True(r.Success, r.Warning);
        for (int i = 0; i < r.Vertices.Length / 3; i++)
            Assert.Equal(r.Vertices[i * 3], r.Vertices[i * 3 + 2], 9); // z == x
    }

    [Fact]
    public void Refine_ProducesWatertightManifoldMesh()
    {
        (double[] verts, int[] faces) = TiltedSquare();

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0.25, Tolerance = 0.001 });

        Assert.True(r.Success, r.Warning);
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(r.Faces, r.Faces.Length / 3);
        Assert.True(topology.HasSingleClosedBoundaryLoop);
    }

    [Fact]
    public void Refine_AlreadyFine_IsNoOpForSubdivision()
    {
        (double[] verts, int[] faces) = TiltedSquare();

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 100.0, Tolerance = 0.001, DoFlips = false });

        Assert.True(r.Success, r.Warning);
        Assert.Equal(0, r.AddedVertices);
        Assert.Equal(faces.Length, r.Faces.Length);
    }

    // ---- Crease preservation (roof folding along y = 0, z = |y|) --------------------------------------

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
    public void Refine_PreservesCrease_NoTriangleStraddlesTheRidge()
    {
        (double[] verts, int[] faces) = RoofMesh();

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0.6, CreaseAngleDeg = 30, Tolerance = 0.001, DoFlips = true });

        Assert.True(r.Success, r.Warning);

        // The fold is at y = 0. If the crease is respected, no triangle spans from y < 0 to y > 0 (which
        // would chord across the ridge and dent the surface). Vertices never move, so this is exact.
        for (int t = 0; t < r.Faces.Length / 3; t++)
        {
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int e = 0; e < 3; e++)
            {
                double y = r.Vertices[r.Faces[t * 3 + e] * 3 + 1];
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

            Assert.False(minY < -1e-9 && maxY > 1e-9, $"triangle {t} straddles the ridge (y {minY}..{maxY})");
        }

        // And the surface stays exactly z = |y| everywhere (on-surface midpoints, no rounding of the fold).
        for (int i = 0; i < r.Vertices.Length / 3; i++)
            Assert.Equal(Math.Abs(r.Vertices[i * 3 + 1]), r.Vertices[i * 3 + 2], 9);
    }

    // ---- Quality (sliver-removing) flips -------------------------------------------------------------

    private static double MinMeshAngleDeg(double[] v, int[] f)
    {
        double min = double.MaxValue;
        for (int t = 0; t < f.Length / 3; t++)
        {
            int a = f[t * 3], b = f[t * 3 + 1], c = f[t * 3 + 2];
            min = Math.Min(min, Math.Min(Corner(a, b, c), Math.Min(Corner(b, c, a), Corner(c, a, b))));
        }

        return min * 180.0 / Math.PI;

        double Corner(int p, int q, int r)
        {
            double e1x = v[q * 3] - v[p * 3], e1y = v[q * 3 + 1] - v[p * 3 + 1];
            double e2x = v[r * 3] - v[p * 3], e2y = v[r * 3 + 1] - v[p * 3 + 1];
            double l1 = Math.Sqrt((e1x * e1x) + (e1y * e1y)), l2 = Math.Sqrt((e2x * e2x) + (e2y * e2y));
            return Math.Acos(Math.Clamp(((e1x * e2x) + (e1y * e2y)) / (l1 * l2), -1, 1));
        }
    }

    [Fact]
    public void Refine_QualityFlips_RemoveSliverAndConverge()
    {
        // A thin "cap": the shared edge 0–1 gives a near-degenerate triangle (0,1,2); the alternate
        // diagonal 2–3 splits the quad into two well-shaped triangles. A flat mesh, so the crease guard
        // permits it.
        var verts = new double[] { 0, 0, 0, 10, 0, 0, 5, 0.3, 0, 5, -3, 0 };
        var faces = new[] { 0, 1, 2, 0, 3, 1 };

        double before = MinMeshAngleDeg(verts, faces);

        LocalMeshRefiner.Result r = LocalMeshRefiner.Refine(
            verts, faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0, Tolerance = 0.001, DoFlips = true });

        Assert.True(r.Success, r.Warning);
        Assert.Equal(1, r.Flips);
        double after = MinMeshAngleDeg(r.Vertices, r.Faces);
        Assert.True(after > before + 5.0, $"min angle should improve markedly ({before:F1}° -> {after:F1}°)");

        // Converged: a second pass finds nothing more to flip.
        LocalMeshRefiner.Result again = LocalMeshRefiner.Refine(
            r.Vertices, r.Faces, NoConstraints,
            new LocalMeshRefiner.Options { TargetEdgeLength = 0, Tolerance = 0.001, DoFlips = true });
        Assert.Equal(0, again.Flips);
    }
}
