using System;
using System.Collections.Generic;
using System.Linq;
using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Covers crease detection + transient crease preservation in the remesh: feature edges (e.g. a batter
/// toe) are pinned so the re-triangulation keeps them smooth, without persisting any breakline.
/// </summary>
public class SurfaceRemesherCreaseTests
{
    // A "roof": two planes meeting along the ridge y = 0 at a clear fold. The ridge edges fold ~53deg.
    private static (double[] verts, int[] faces) RoofMesh()
    {
        // grid x in {-2,-1,0,1,2}, y in {-2,-1,0,1,2}; z = |y| (fold along y=0)
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
    public void DetectCreaseEdges_Roof_FindsTheRidgeNotTheFlats()
    {
        (double[] verts, int[] faces) = RoofMesh();
        double cos30 = Math.Cos(30.0 * Math.PI / 180.0);

        List<(int a, int b)> creases = SurfaceRemesher.DetectCreaseEdges(verts, faces, faces.Length / 3, cos30);

        Assert.NotEmpty(creases);
        // Every detected crease edge must lie ON the ridge (both endpoints at y == 0).
        foreach ((int a, int b) in creases)
        {
            Assert.Equal(0.0, verts[a * 3 + 1], 6);
            Assert.Equal(0.0, verts[b * 3 + 1], 6);
        }
    }

    [Fact]
    public void DetectCreaseEdges_FlatMesh_FindsNothing()
    {
        var verts = new double[] { -1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0 };
        var faces = new[] { 0, 1, 2, 0, 2, 3 };
        Assert.Empty(SurfaceRemesher.DetectCreaseEdges(verts, faces, 2, Math.Cos(20.0 * Math.PI / 180.0)));
    }

    private static int RidgeEdgeCount(SurfaceRemesher.Result r)
    {
        int vertexCount = r.Vertices.Length / 3, faceCount = r.Faces.Length / 3;
        var ridge = new HashSet<int>();
        for (int i = 0; i < vertexCount; i++)
            if (Math.Abs(r.Vertices[i * 3 + 1]) < 1e-6)
                ridge.Add(i);
        int ridgeEdges = 0;
        for (int f = 0; f < faceCount; f++)
            for (int e = 0; e < 3; e++)
            {
                int a = r.Faces[f * 3 + e], b = r.Faces[f * 3 + ((e + 1) % 3)];
                if (ridge.Contains(a) && ridge.Contains(b)) ridgeEdges++;
            }
        return ridgeEdges;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    public void Remesh_WithCreasePreservation_KeepsRidgeEdges(double maxArea)
    {
        (double[] verts, int[] faces) = RoofMesh();

        SurfaceRemesher.Result r = SurfaceRemesher.Remesh(
            verts, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new SurfaceRemesher.Options { Tolerance = 0.01, MinAngle = 20, MaxArea = maxArea, PreserveCreaseAngleDeg = 30 });

        Assert.True(r.Success, r.Warning);
        Assert.True(RidgeEdgeCount(r) > 0, "ridge crease should be preserved as mesh edges");
    }

    [Fact]
    public void Remesh_CreasePreservation_SurvivesReducedSeedFallback()
    {
        // PreferReducedInteriorSeed forces the reduced-seed pass (the one a MaxArea remesh tends to pick).
        // Crease detection must still run there — this would fail before the fallback carried the setting.
        (double[] verts, int[] faces) = RoofMesh();

        SurfaceRemesher.Result r = SurfaceRemesher.Remesh(
            verts, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new SurfaceRemesher.Options
            {
                Tolerance = 0.01,
                MaxArea = 0.5,
                PreserveCreaseAngleDeg = 30,
                PreferReducedInteriorSeed = true
            });

        Assert.True(r.Success, r.Warning);
        Assert.True(RidgeEdgeCount(r) > 0, "crease must survive the reduced-seed fallback too");
    }
}
