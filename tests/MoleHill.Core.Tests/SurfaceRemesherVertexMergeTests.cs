using System;
using System.Collections.Generic;
using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Covers <see cref="SurfaceRemesher.Options.VertexMergeTolerance"/> — the "merge by distance" that
/// collapses near-duplicate input vertices (e.g. batter-toe pinches) the model-tolerance dedup leaves
/// distinct.
/// </summary>
public class SurfaceRemesherVertexMergeTests
{
    // A flat square with two near-coincident interior vertices (0.04 apart) — a pinch the remesh would
    // otherwise keep. Built through the real triangulator so the input is a valid mesh.
    private static (double[] verts, int[] faces) PinchedMesh()
    {
        var xy = new List<double> { 0, 0, 6, 0, 6, 6, 0, 6, 3.00, 3.0, 3.04, 3.0 };
        TriangulationOutcome outcome = TriangulationHelper.Triangulate(
            xy, xy.Count / 2, new List<(int, int)>(), maxArea: 0, minAngle: 0, convex: true);
        Assert.NotNull(outcome.Mesh);
        TriangleNetExtractor.Result e = TriangleNetExtractor.Extract(outcome.Mesh!);

        var verts = new double[e.VertexCount * 3];
        for (int i = 0; i < e.VertexCount; i++)
        {
            verts[i * 3] = e.Xy[i * 2];
            verts[i * 3 + 1] = e.Xy[i * 2 + 1];
            verts[i * 3 + 2] = 0.0; // flat
        }
        return (verts, e.Faces);
    }

    private static int CountPairsWithin(double[] verts, double distance)
    {
        int n = verts.Length / 3, pairs = 0;
        double d2 = distance * distance;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dx = verts[i * 3] - verts[j * 3];
                double dy = verts[i * 3 + 1] - verts[j * 3 + 1];
                if ((dx * dx) + (dy * dy) <= d2) pairs++;
            }
        return pairs;
    }

    [Fact]
    public void Remesh_WithoutMerge_KeepsNearDuplicatePinch()
    {
        (double[] verts, int[] faces) = PinchedMesh();
        Assert.Equal(1, CountPairsWithin(verts, 0.1)); // the 0.04 pinch is present in the input

        SurfaceRemesher.Result r = SurfaceRemesher.Remesh(
            verts, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new SurfaceRemesher.Options { Tolerance = 0.01, MinAngle = 20 });

        Assert.True(r.Success);
        Assert.Equal(1, CountPairsWithin(r.Vertices, 0.1)); // pinch survives the default dedup
    }

    [Fact]
    public void Remesh_WithMergeTolerance_CollapsesNearDuplicatePinch()
    {
        (double[] verts, int[] faces) = PinchedMesh();

        SurfaceRemesher.Result r = SurfaceRemesher.Remesh(
            verts, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new SurfaceRemesher.Options { Tolerance = 0.01, MinAngle = 20, VertexMergeTolerance = 0.1 });

        Assert.True(r.Success);
        Assert.Equal(0, CountPairsWithin(r.Vertices, 0.05)); // the pinch is merged away
    }
}
