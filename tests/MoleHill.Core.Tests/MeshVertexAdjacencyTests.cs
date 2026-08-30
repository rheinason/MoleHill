using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshVertexAdjacencyTests
{
    /// <summary>Two triangles sharing edge 1-2: a quad split along its diagonal.</summary>
    private static List<int> TwoTriangleQuad() => new() { 0, 1, 2, 1, 3, 2 };

    private static List<int> FacesOf(MeshVertexAdjacency adjacency, int vertex)
    {
        var faces = new List<int>();
        foreach (int face in adjacency.FacesOf(vertex))
            faces.Add(face);

        faces.Sort();
        return faces;
    }

    [Fact]
    public void Build_SharedEdge_ListsBothIncidentFacesForSharedVertices()
    {
        var adjacency = MeshVertexAdjacency.Build(TwoTriangleQuad(), faceCount: 2, vertexCount: 4);

        Assert.Equal(new[] { 0 }, FacesOf(adjacency, 0));
        Assert.Equal(new[] { 0, 1 }, FacesOf(adjacency, 1));
        Assert.Equal(new[] { 0, 1 }, FacesOf(adjacency, 2));
        Assert.Equal(new[] { 1 }, FacesOf(adjacency, 3));
    }

    [Fact]
    public void Build_SharedEdge_ReturnsDeduplicatedSortedNeighbors()
    {
        var adjacency = MeshVertexAdjacency.Build(TwoTriangleQuad(), faceCount: 2, vertexCount: 4);

        // Vertex 1 touches 0 and 2 through face 0 and 2 and 3 through face 1 — 2 must appear once.
        Assert.Equal(new[] { 0, 2, 3 }, adjacency.NeighborsOf(1).ToArray());
        Assert.Equal(3, adjacency.NeighborCount(1));
        Assert.Equal(new[] { 1, 2 }, adjacency.NeighborsOf(0).ToArray());
    }

    [Fact]
    public void NeighborsContain_KnownAndUnknownNeighbors_MatchesNeighborList()
    {
        var adjacency = MeshVertexAdjacency.Build(TwoTriangleQuad(), faceCount: 2, vertexCount: 4);

        Assert.True(adjacency.NeighborsContain(0, 1));
        Assert.True(adjacency.NeighborsContain(0, 2));
        Assert.False(adjacency.NeighborsContain(0, 3));
        Assert.False(adjacency.NeighborsContain(0, 0));
    }

    [Fact]
    public void Build_TombstonedFace_ExcludesItFromAdjacency()
    {
        var tris = TwoTriangleQuad();
        tris[3] = -1; // tombstone face 1 the way a collapse does

        var adjacency = MeshVertexAdjacency.Build(tris, faceCount: 2, vertexCount: 4);

        Assert.Equal(new[] { 0 }, FacesOf(adjacency, 1));
        Assert.Equal(new[] { 1, 2 }, adjacency.NeighborsOf(0).ToArray());
        Assert.False(adjacency.HasNeighbors(3));
        Assert.Empty(FacesOf(adjacency, 3));
    }

    [Fact]
    public void Build_IsolatedVertex_ReportsNoNeighborsOrFaces()
    {
        var adjacency = MeshVertexAdjacency.Build(new List<int> { 0, 1, 2 }, faceCount: 1, vertexCount: 5);

        Assert.False(adjacency.HasNeighbors(4));
        Assert.Equal(0, adjacency.NeighborCount(4));
        Assert.Empty(FacesOf(adjacency, 4));
    }

    [Fact]
    public void Build_OutOfRangeVertexIndex_IsIgnoredRatherThanThrowing()
    {
        // Defensive: a stale index must not index past the CSR tables.
        var adjacency = MeshVertexAdjacency.Build(new List<int> { 0, 1, 9 }, faceCount: 1, vertexCount: 3);

        Assert.Equal(new[] { 1 }, adjacency.NeighborsOf(0).ToArray());
        Assert.Empty(FacesOf(adjacency, 2));
    }

    [Fact]
    public void AddFace_NewIncidence_AppearsInFaceEnumerationWithoutDuplicates()
    {
        var adjacency = MeshVertexAdjacency.Build(TwoTriangleQuad(), faceCount: 2, vertexCount: 4);

        adjacency.AddFace(0, 1);
        adjacency.AddFace(0, 1); // repeat
        adjacency.AddFace(0, 0); // already present in the CSR slice

        Assert.Equal(new[] { 0, 1 }, FacesOf(adjacency, 0));
    }

    [Fact]
    public void Rebuild_ReusedInstance_DropsPreviousTopologyAndAppendedFaces()
    {
        var adjacency = MeshVertexAdjacency.Build(TwoTriangleQuad(), faceCount: 2, vertexCount: 4);
        adjacency.AddFace(0, 1);

        MeshVertexAdjacency reused = MeshVertexAdjacency.Build(
            new List<int> { 0, 1, 2 }, faceCount: 1, vertexCount: 3, reuse: adjacency);

        Assert.Same(adjacency, reused);
        Assert.Equal(new[] { 0 }, FacesOf(reused, 0));
        Assert.Equal(new[] { 1, 2 }, reused.NeighborsOf(0).ToArray());
        Assert.Equal(3, reused.VertexCount);
    }

    [Fact]
    public void Rebuild_GrowingMesh_ExpandsBuffersInsteadOfOverflowing()
    {
        var adjacency = MeshVertexAdjacency.Build(new List<int> { 0, 1, 2 }, faceCount: 1, vertexCount: 3);

        // A fan of 64 triangles around a hub vertex — far larger than the first build's buffers.
        var tris = new List<int>();
        const int spokes = 64;
        for (int i = 0; i < spokes; i++)
            tris.AddRange(new[] { 0, 1 + i, 1 + ((i + 1) % spokes) });

        MeshVertexAdjacency.Build(tris, spokes, spokes + 1, reuse: adjacency);

        Assert.Equal(spokes, FacesOf(adjacency, 0).Count);
        Assert.Equal(spokes, adjacency.NeighborCount(0));
        Assert.Equal(3, adjacency.NeighborCount(1)); // hub + two fan siblings
    }
}
