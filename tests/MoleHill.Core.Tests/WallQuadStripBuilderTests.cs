using System;
using System.Collections.Generic;
using MoleHill.Core.Engine;
using MoleHill.Core.Retopo;
using Xunit;

namespace MoleHill.Core.Tests;

public class WallQuadStripBuilderTests
{
    [Fact]
    public void Build_ParallelRails_MakesRowsByColumnsWithRailsOnTheEdges()
    {
        // 5 stations; toe at z = 0, crest directly above at z = 2. edgeLength 1 → 2 rows.
        int n = 5;
        var toe = new double[n * 3];
        var top = new double[n * 3];
        for (int k = 0; k < n; k++)
        {
            toe[k * 3] = k; toe[k * 3 + 1] = 0; toe[k * 3 + 2] = 0;
            top[k * 3] = k; top[k * 3 + 1] = 0; top[k * 3 + 2] = 2;
        }

        WallQuadStripBuilder.Strip strip = WallQuadStripBuilder.Build(top, toe, closed: false, edgeLength: 1.0);

        int rows = 2;
        Assert.Equal((n - 1) * rows, strip.QuadCount); // 4 columns × 2 rows = 8
        Assert.Equal(n * (rows + 1), strip.VertexCount);

        int perColumn = rows + 1;
        for (int k = 0; k < n; k++)
        {
            int bottom = (k * perColumn) + 0;      // r = 0 → toe rail
            int crest = (k * perColumn) + rows;    // r = rows → top rail
            Assert.Equal(0.0, strip.Vertices[bottom * 3 + 2], 9);
            Assert.Equal(2.0, strip.Vertices[crest * 3 + 2], 9);
            Assert.Equal(k, strip.Vertices[bottom * 3], 9);
        }
    }
}

public class QuadRemesherSteepExclusionTests
{
    private static IReadOnlyList<SurfaceRemesher.ConstraintPolyline> NoConstraints =>
        Array.Empty<SurfaceRemesher.ConstraintPolyline>();

    // Rotate the flat grid about the x-axis so every face is steep by `deg` from vertical... expressed as a
    // tilt of the surface: deg = angle of the normal from +Z.
    private static (double[] verts, int[] faces) TiltedGrid(int n, double normalTiltDeg)
    {
        double a = normalTiltDeg * Math.PI / 180.0;
        double c = Math.Cos(a), s = Math.Sin(a);
        (double[] verts, int[] faces) = RetopoTestMeshes.Grid(n);
        for (int i = 0; i < verts.Length / 3; i++)
        {
            double y = verts[i * 3 + 1];
            verts[i * 3 + 1] = y * c; // z started at 0
            verts[i * 3 + 2] = y * s; // surface normal now tilts by `a` from vertical
        }

        return (verts, faces);
    }

    [Fact]
    public void Remesh_FlatGrid_KeepsQuadsUnderTheWallMask()
    {
        (double[] verts, int[] faces) = RetopoTestMeshes.Grid(8);

        QuadRemesher.Result r = QuadRemesher.Remesh(
            verts, faces, NoConstraints,
            new QuadRemesher.Options { EdgeLength = 1.0, Tolerance = 0.001, WallFaceMinSlopeDeg = 70 });

        Assert.True(r.QuadCount > 0); // flat faces are not steep → still quadded
    }

    [Fact]
    public void Remesh_SteepGrid_EmitsNoQuadsWhereMasked()
    {
        (double[] verts, int[] faces) = TiltedGrid(8, normalTiltDeg: 80); // near-vertical wall faces

        QuadRemesher.Result r = QuadRemesher.Remesh(
            verts, faces, NoConstraints,
            new QuadRemesher.Options { EdgeLength = 1.0, Tolerance = 0.001, WallFaceMinSlopeDeg = 70 });

        Assert.Equal(0, r.QuadCount); // all faces steeper than 70° → excluded
    }
}
