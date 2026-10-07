namespace MoleHill.Core.Engine;

/// <summary>
/// A triangle mesh in the flat array format, carried as one value so its counts cannot be separated from
/// the arrays they describe. Vertices are <c>[x0, y0, z0, x1, ...]</c>, faces are vertex-index triples.
/// <para>
/// The arrays may be longer than the counts (a pooled or over-allocated buffer); the counts are what is
/// read. The constructor rejects a count the arrays cannot hold, which is the failure that used to surface
/// as an <see cref="IndexOutOfRangeException"/> on a worker thread when a caller paired extracted arrays
/// with the counts of the Rhino mesh it extracted them from.
/// </para>
/// </summary>
public readonly struct IndexedTriMesh
{
    public IndexedTriMesh(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(faces);
        if (vertexCount < 0 || (long)vertexCount * 3 > vertices.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(vertexCount),
                $"{vertexCount:N0} vertices need {(long)vertexCount * 3:N0} coordinates; the array holds {vertices.Length:N0}. " +
                "The counts must come from the same extraction as the arrays.");
        }

        if (faceCount < 0 || (long)faceCount * 3 > faces.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(faceCount),
                $"{faceCount:N0} faces need {(long)faceCount * 3:N0} indices; the array holds {faces.Length:N0}. " +
                "The counts must come from the same extraction as the arrays.");
        }

        Vertices = vertices;
        VertexCount = vertexCount;
        Faces = faces;
        FaceCount = faceCount;
    }

    /// <summary>A mesh whose counts are exactly the array lengths divided by three.</summary>
    public static IndexedTriMesh FromArrays(double[] vertices, int[] faces) =>
        new(vertices, vertices.Length / 3, faces, faces.Length / 3);

    public static IndexedTriMesh Empty { get; } = new(Array.Empty<double>(), 0, Array.Empty<int>(), 0);

    public double[] Vertices { get; }

    public int VertexCount { get; }

    public int[] Faces { get; }

    public int FaceCount { get; }

    public bool IsEmpty => VertexCount == 0 || FaceCount == 0;

    public void Deconstruct(out double[] vertices, out int vertexCount, out int[] faces, out int faceCount)
    {
        vertices = Vertices;
        vertexCount = VertexCount;
        faces = Faces;
        faceCount = FaceCount;
    }
}
