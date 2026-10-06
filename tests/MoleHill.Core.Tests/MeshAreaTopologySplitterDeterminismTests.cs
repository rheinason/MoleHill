using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Boundary-to-face mapping runs in parallel over faces. These guard the two properties that makes
/// safe: each face owns its own cut data (no cross-thread contention), and the per-face accumulation
/// order is pinned, so repeated runs must be bit-identical rather than merely equivalent.
/// </summary>
public class MeshAreaTopologySplitterDeterminismTests
{
    private static void BuildGrid(int side, double extent, out double[] verts, out int[] faces)
    {
        int vc = (side + 1) * (side + 1);
        verts = new double[vc * 3];
        for (int j = 0; j <= side; j++)
        for (int i = 0; i <= side; i++)
        {
            int v = j * (side + 1) + i;
            verts[v * 3] = extent * i / side;
            verts[v * 3 + 1] = extent * j / side;
            verts[v * 3 + 2] = i * 0.13 + j * 0.07;
        }

        faces = TestMeshes.GridFaces(side + 1, side + 1);
    }

    /// <summary>A many-lobed star, so boundary segments cross a large number of faces at many angles.</summary>
    private static MeshAreaSplitter.AreaBoundary DenseStar(double cx, double cy, double rOuter, double rInner, int lobes)
    {
        var xy = new List<double>(lobes * 4);
        for (int i = 0; i < lobes * 2; i++)
        {
            double angle = Math.PI * i / lobes;
            double r = (i % 2 == 0) ? rOuter : rInner;
            xy.Add(cx + r * Math.Cos(angle));
            xy.Add(cy + r * Math.Sin(angle));
        }

        return new MeshAreaSplitter.AreaBoundary(xy.ToArray(), xy.Count / 2);
    }

    [Fact]
    public void SplitPreservingTopology_DenseOverlappingBoundaries_IsBitIdenticalAcrossRuns()
    {
        BuildGrid(60, 1000.0, out var verts, out var faces);
        var areas = new[]
        {
            DenseStar(400, 400, 330, 150, 97),
            DenseStar(620, 560, 300, 120, 89),   // deliberately overlaps the first
            DenseStar(500, 500, 140, 40, 61)     // nested inside both
        };

        MeshAreaSplitter.SplitResult? Run() => MeshAreaSplitter.SplitPreservingTopology(
            verts, verts.Length / 3, faces, faces.Length / 3, areas, 0.1, out _);

        var baseline = Run();
        Assert.NotNull(baseline);
        Assert.True(baseline!.FaceCount > faces.Length / 3, "boundaries should have subdivided faces");

        for (int run = 0; run < 6; run++)
        {
            var repeat = Run();
            Assert.NotNull(repeat);
            Assert.Equal(baseline.VertexCount, repeat!.VertexCount);
            Assert.Equal(baseline.FaceCount, repeat.FaceCount);
            Assert.Equal(baseline.Vertices, repeat.Vertices);
            Assert.Equal(baseline.Faces, repeat.Faces);
            Assert.Equal(baseline.FaceAreaIndex, repeat.FaceAreaIndex);
        }
    }

    [Fact]
    public void SplitPreservingTopology_ConformedMesh_HasNoUnpairedInteriorEdges()
    {
        BuildGrid(40, 1000.0, out var verts, out var faces);
        var areas = new[] { DenseStar(500, 500, 380, 160, 71) };

        var result = MeshAreaSplitter.SplitPreservingTopology(
            verts, verts.Length / 3, faces, faces.Length / 3, areas, 0.1, out _);
        Assert.NotNull(result);

        // Single-use edges must lie on the original perimeter; interior single-use edges
        // indicate a crack or T-junction even when no edge has more than two uses.
        var uses = new Dictionary<(int, int), int>();
        for (int f = 0; f < result!.FaceCount; f++)
        {
            int a = result.Faces[f * 3], b = result.Faces[f * 3 + 1], c = result.Faces[f * 3 + 2];
            foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = u < v ? (u, v) : (v, u);
                uses[key] = uses.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        foreach (var (edge, count) in uses)
        {
            double ax = result.Vertices[edge.Item1 * 3], ay = result.Vertices[edge.Item1 * 3 + 1];
            double bx = result.Vertices[edge.Item2 * 3], by = result.Vertices[edge.Item2 * 3 + 1];
            bool perimeter = (ax == 0 && bx == 0) || (ax == 1000 && bx == 1000) ||
                             (ay == 0 && by == 0) || (ay == 1000 && by == 1000);
            Assert.True(count == (perimeter ? 1 : 2),
                $"Edge {edge} has {count} uses; perimeter={perimeter}.");
        }
    }

    [Fact]
    public void SplitPreservingTopology_CountsExceedArrays_ReturnsDiagnosisInsteadOfThrowing()
    {
        BuildGrid(20, 1000.0, out var verts, out var faces);
        var areas = new[] { DenseStar(500, 500, 380, 160, 41) };

        int realFaceCount = faces.Length / 3;
        int realVertexCount = verts.Length / 3;

        // Exactly the shape of the Zones regression: arrays from a normalized copy of the mesh, counts
        // taken from the larger un-normalized original.
        var result = MeshAreaSplitter.SplitPreservingTopology(
            verts, realVertexCount, faces, realFaceCount + 500, areas, 0.1, out string? error);

        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("inconsistent", error!, StringComparison.OrdinalIgnoreCase);

        var vertexResult = MeshAreaSplitter.SplitPreservingTopology(
            verts, realVertexCount + 500, faces, realFaceCount, areas, 0.1, out string? vertexError);

        Assert.Null(vertexResult);
        Assert.NotNull(vertexError);
        Assert.Contains("inconsistent", vertexError!, StringComparison.OrdinalIgnoreCase);
    }
}
