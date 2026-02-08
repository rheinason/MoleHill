namespace MoleHill.Core.Engine;

/// <summary>
/// Immutable result of a TIN triangulation.
/// Stores vertices (with Z), face indices, and edge indices.
/// </summary>
public sealed class TinResult
{
    /// <summary>Flat XYZ array: [x0, y0, z0, x1, y1, z1, …]</summary>
    public double[] Vertices { get; }

    /// <summary>Number of vertices.</summary>
    public int VertexCount { get; }

    /// <summary>Triangle face indices (3 per face): [i0, i1, i2, …]</summary>
    public int[] Faces { get; }

    /// <summary>Number of triangular faces.</summary>
    public int FaceCount { get; }

    /// <summary>Edge pairs: [a0, b0, a1, b1, …]</summary>
    public int[] Edges { get; }

    /// <summary>Number of edges.</summary>
    public int EdgeCount { get; }

    /// <summary>Naked (boundary) edge pairs.</summary>
    public int[] NakedEdges { get; }

    /// <summary>Number of naked edges.</summary>
    public int NakedEdgeCount { get; }

    public TinResult(double[] vertices, int vertexCount,
                     int[] faces, int faceCount,
                     int[] edges, int edgeCount,
                     int[] nakedEdges, int nakedEdgeCount)
    {
        Vertices = vertices;
        VertexCount = vertexCount;
        Faces = faces;
        FaceCount = faceCount;
        Edges = edges;
        EdgeCount = edgeCount;
        NakedEdges = nakedEdges;
        NakedEdgeCount = nakedEdgeCount;
    }

    /// <summary>
    /// Create a new TinResult with updated Z values but same topology.
    /// </summary>
    public TinResult WithUpdatedZ(double[] zValues)
    {
        if (zValues.Length != VertexCount)
            throw new ArgumentException($"Expected {VertexCount} Z values, got {zValues.Length}");

        var newVerts = new double[Vertices.Length];
        Array.Copy(Vertices, newVerts, Vertices.Length);

        for (int i = 0; i < VertexCount; i++)
            newVerts[i * 3 + 2] = zValues[i];

        return new TinResult(newVerts, VertexCount, Faces, FaceCount,
                             Edges, EdgeCount, NakedEdges, NakedEdgeCount);
    }
}
