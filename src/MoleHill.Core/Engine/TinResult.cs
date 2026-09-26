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

    /// <summary>
    /// Per-output-vertex index into the original input Z array (Triangle.NET's Vertex.ID at build
    /// time), or a negative value for Steiner points with no direct input correspondence. This is
    /// the only safe key for re-applying a new Z array — output vertex order does not match input
    /// order in general.
    /// </summary>
    public int[] SourceIds { get; }

    public TinResult(double[] vertices, int vertexCount,
                     int[] faces, int faceCount,
                     int[] edges, int edgeCount,
                     int[] nakedEdges, int nakedEdgeCount,
                     int[] sourceIds)
    {
        Vertices = vertices;
        VertexCount = vertexCount;
        Faces = faces;
        FaceCount = faceCount;
        Edges = edges;
        EdgeCount = edgeCount;
        NakedEdges = nakedEdges;
        NakedEdgeCount = nakedEdgeCount;
        SourceIds = sourceIds;
    }

    /// <summary>
    /// Create a new TinResult with updated Z values but same topology. Maps each output vertex back
    /// to its input Z via <see cref="SourceIds"/> — never by output position (an incremental edit
    /// renumbers output vertices, so a positional mapping assigns elevations to the wrong points).
    /// </summary>
    public TinResult WithUpdatedZ(double[] zValues)
    {
        var newVerts = new double[Vertices.Length];
        Array.Copy(Vertices, newVerts, Vertices.Length);

        for (int i = 0; i < VertexCount; i++)
        {
            int srcId = SourceIds[i];
            if (srcId >= 0 && srcId < zValues.Length)
                newVerts[i * 3 + 2] = zValues[srcId];
        }

        return new TinResult(newVerts, VertexCount, Faces, FaceCount,
                             Edges, EdgeCount, NakedEdges, NakedEdgeCount, SourceIds);
    }
}
