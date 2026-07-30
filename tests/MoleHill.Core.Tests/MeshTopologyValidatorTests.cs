using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshTopologyValidatorTests
{
    [Fact]
    public void AnalyzeBoundaryGraph_ClosedMesh_PreservesNoBoundaryOpenChainSpecialCase()
    {
        int[] faces =
        {
            0, 2, 1,
            0, 1, 3,
            1, 2, 3,
            2, 0, 3
        };

        var analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount: 4);

        Assert.Equal(0, analysis.BoundaryEdgeCount);
        Assert.Equal(0, analysis.BoundaryVertexCount);
        Assert.Equal(0, analysis.BoundaryComponentCount);
        Assert.True(analysis.HasOpenBoundaryChains);
        Assert.Equal(0, analysis.NonManifoldEdgeCount);
    }

    [Fact]
    public void AnalyzeBoundaryGraph_SparseVertexIds_DoesNotRequireDenseVertexStorage()
    {
        int[] faces = { 7, 1_000_000_000, 2_000_000_000 };

        var analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount: 1);

        Assert.Equal(3, analysis.BoundaryEdgeCount);
        Assert.Equal(3, analysis.BoundaryVertexCount);
        Assert.Equal(1, analysis.BoundaryComponentCount);
        Assert.False(analysis.HasOpenBoundaryChains);
        Assert.True(analysis.HasSingleClosedBoundaryLoop);
    }

    [Fact]
    public void AnalyzeBoundaryGraph_DisjointTriangles_ReportsTwoClosedComponents()
    {
        int[] faces =
        {
            0, 1, 2,
            10, 11, 12
        };

        var analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount: 2);

        Assert.Equal(6, analysis.BoundaryEdgeCount);
        Assert.Equal(6, analysis.BoundaryVertexCount);
        Assert.Equal(2, analysis.BoundaryComponentCount);
        Assert.False(analysis.HasOpenBoundaryChains);
    }

    [Fact]
    public void TryBuildBoundaryLoopsIndexed_DisjointSparseTriangles_ReturnsBothLoops()
    {
        int[] faces =
        {
            7, 1_000_000_000, 2_000_000_000,
            20, 30, 40
        };

        bool success = MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(
            faces,
            faceCount: 2,
            out List<int[]> loops);

        Assert.True(success);
        Assert.Equal(2, loops.Count);
        Assert.Contains(loops, loop => loop.ToHashSet().SetEquals(new[] { 7, 1_000_000_000, 2_000_000_000 }));
        Assert.Contains(loops, loop => loop.ToHashSet().SetEquals(new[] { 20, 30, 40 }));
    }

    [Fact]
    public void AnalyzeBoundaryGraph_BranchedBoundary_ReportsOpenChain()
    {
        int[] faces =
        {
            0, 1, 2,
            0, 3, 4
        };

        var analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount: 2);

        Assert.Equal(1, analysis.BoundaryComponentCount);
        Assert.True(analysis.HasOpenBoundaryChains);
        Assert.False(analysis.HasSingleClosedBoundaryLoop);
    }

    [Fact]
    public void AnalyzeBoundaryGraph_NonManifoldInternalEdge_ReportsInvalidTopology()
    {
        int[] faces =
        {
            0, 1, 2,
            1, 0, 3,
            0, 1, 4
        };

        var analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount: 3);

        Assert.Equal(1, analysis.NonManifoldEdgeCount);
        Assert.False(analysis.HasSingleClosedBoundaryLoop);
    }

    [Fact]
    public void AnalyzeBoundaryGraph_EdgeRunGreaterThanThree_CountsOneNonManifoldEdge()
    {
        int[] faces =
        {
            0, 1, 2,
            1, 0, 3,
            0, 1, 4,
            1, 0, 5
        };

        var analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount: 4);

        Assert.Equal(1, analysis.NonManifoldEdgeCount);
        Assert.True(MeshTopologyValidator.HasNonManifoldEdge(faces, faceCount: 4));
    }

    [Fact]
    public void AnalyzeBoundaryGraph_RandomSparseFaces_MatchesLegacyDictionaryAnalysis()
    {
        int[] vertexIds =
        {
            0, 2, 5, 17, 43, 101, 509, 4_099, 1_000_003, 1_000_000_000
        };
        var random = new Random(104729);

        for (int scenario = 0; scenario < 500; scenario++)
        {
            int faceCount = random.Next(0, 31);
            var faces = new int[faceCount * 3];
            for (int face = 0; face < faceCount; face++)
            {
                int a = random.Next(vertexIds.Length);
                int b;
                int c;
                do b = random.Next(vertexIds.Length); while (b == a);
                do c = random.Next(vertexIds.Length); while (c == a || c == b);
                faces[face * 3] = vertexIds[a];
                faces[face * 3 + 1] = vertexIds[b];
                faces[face * 3 + 2] = vertexIds[c];
            }

            MeshTopologyValidator.BoundaryGraphAnalysis expected =
                AnalyzeBoundaryGraphWithLegacyDictionaries(faces, faceCount);
            MeshTopologyValidator.BoundaryGraphAnalysis actual =
                MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);

            Assert.Equal(expected, actual);
        }
    }

    private static MeshTopologyValidator.BoundaryGraphAnalysis AnalyzeBoundaryGraphWithLegacyDictionaries(
        int[] faces,
        int faceCount)
    {
        var edgeCounts = new Dictionary<long, int>();
        for (int face = 0; face < faceCount; face++)
        {
            int a = faces[face * 3];
            int b = faces[face * 3 + 1];
            int c = faces[face * 3 + 2];
            CountEdge(edgeCounts, a, b);
            CountEdge(edgeCounts, b, c);
            CountEdge(edgeCounts, c, a);
        }

        var adjacency = new Dictionary<int, List<int>>();
        var degree = new Dictionary<int, int>();
        int boundaryEdgeCount = 0;
        int nonManifoldEdgeCount = 0;
        foreach ((long edge, int count) in edgeCounts)
        {
            if (count > 2)
            {
                nonManifoldEdgeCount++;
                continue;
            }

            if (count != 1)
                continue;

            boundaryEdgeCount++;
            int a = (int)(edge >> 32);
            int b = (int)(edge & 0xFFFFFFFFL);
            AddNeighbor(adjacency, degree, a, b);
            AddNeighbor(adjacency, degree, b, a);
        }

        if (boundaryEdgeCount == 0)
        {
            return new MeshTopologyValidator.BoundaryGraphAnalysis(
                0,
                0,
                0,
                HasOpenBoundaryChains: true,
                nonManifoldEdgeCount);
        }

        bool hasOpenBoundaryChains = degree.Values.Any(value => value != 2);
        int componentCount = 0;
        var visited = new HashSet<int>();
        foreach (int seed in adjacency.Keys)
        {
            if (!visited.Add(seed))
                continue;

            componentCount++;
            var stack = new Stack<int>();
            stack.Push(seed);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                foreach (int next in adjacency[current])
                {
                    if (visited.Add(next))
                        stack.Push(next);
                }
            }
        }

        return new MeshTopologyValidator.BoundaryGraphAnalysis(
            boundaryEdgeCount,
            degree.Count,
            componentCount,
            hasOpenBoundaryChains,
            nonManifoldEdgeCount);
    }

    private static void CountEdge(Dictionary<long, int> edgeCounts, int a, int b)
    {
        long key = a < b
            ? ((long)a << 32) | (uint)b
            : ((long)b << 32) | (uint)a;
        edgeCounts[key] = edgeCounts.GetValueOrDefault(key) + 1;
    }

    private static void AddNeighbor(
        Dictionary<int, List<int>> adjacency,
        Dictionary<int, int> degree,
        int from,
        int to)
    {
        if (!adjacency.TryGetValue(from, out List<int>? neighbors))
        {
            neighbors = new List<int>(2);
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
        degree[from] = degree.GetValueOrDefault(from) + 1;
    }
}
