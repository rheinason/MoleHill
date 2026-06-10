using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The watertight 2.5D invariant under grade-on-grade composition: grading a pad, then grading a
/// path on the pad's output (the sequential pipeline the product uses), must never punch holes. The
/// final mesh must be a single watertight 2.5D surface — no interior holes, no open chains, manifold.
/// </summary>
public class GradeOnGradeWatertightTests
{
    private static (double[] v, int vc, int[] f, int fc) SlopedGrid(int n, double step, double zPerUnitY)
    {
        var xy = new List<double>();
        var z = new List<double>();
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            xy.Add(i * step); xy.Add(j * step);
            z.Add(j * step * zPerUnitY);
        }

        var outcome = TriangulationHelper.Triangulate(xy, z.Count, new List<(int, int)>(), 0, 0, convex: false, 0);
        var ex = TriangleNetExtractor.Extract(outcome.Mesh!);
        var v = new double[ex.VertexCount * 3];
        for (int i = 0; i < ex.VertexCount; i++)
        {
            v[i * 3] = ex.Xy[i * 2];
            v[i * 3 + 1] = ex.Xy[i * 2 + 1];
            int src = ex.SourceIds[i];
            v[i * 3 + 2] = src >= 0 && src < z.Count ? z[src] : 0.0;
        }

        return (v, ex.VertexCount, ex.Faces, ex.FaceCount);
    }

    private static void AssertWatertight2dCdt(GradingResult result, string context)
    {
        var topo = MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);
        Assert.True(topo.BoundaryComponentCount == 1,
            $"{context}: expected a single boundary loop (no holes), got {topo.BoundaryComponentCount}.");
        Assert.False(topo.HasOpenBoundaryChains, $"{context}: open boundary chains (not watertight).");
        Assert.Equal(0, topo.NonManifoldEdgeCount);
    }

    [Fact]
    public void GradePadThenPath_AdjacentShoulders_StayWatertight()
    {
        var t = SlopedGrid(n: 11, step: 10.0, zPerUnitY: 0.1); // 100x100, z 0..10

        // A pad, then a road whose corridor runs right alongside the pad so their batters interact.
        double[] padXy = { 30, 30, 60, 30, 60, 60, 30, 60 };
        var pads = new[] { new PadGrader.PadBoundary(padXy, 4, targetZ: 4.0, slopeAngleDeg: 33.0) };
        GradingResult? padResult = PadGrader.Grade(t.v, t.vc, t.f, t.fc, pads, null, out string? padErr);
        Assert.True(padResult != null, padErr);
        AssertWatertight2dCdt(padResult!, "after Grade Pad");

        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 70.0, 80.0, 72.0 },
            zValues: new[] { 6.0, 7.0 },
            vertexCount: 2,
            width: 8.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        GradingResult? pathResult = PathGrader.Grade(
            padResult!.Vertices, padResult.VertexCount,
            padResult.Faces, padResult.FaceCount,
            new[] { path }, out string? pathErr);

        Assert.True(pathResult != null, pathErr);
        AssertWatertight2dCdt(pathResult!, "after Grade Path on the pad-graded terrain");
    }
}
