namespace MoleHill.Core.Grading;

/// <summary>
/// Reusable old-vertex-index → new-vertex-index map for extracting many sub-meshes from one result.
/// </summary>
/// <remarks>
/// A <see cref="HashSet{T}"/> of used vertices plus a <see cref="Dictionary{TKey,TValue}"/> remap per
/// sub-mesh allocates two whole-vertex-set structures for every extraction. This holds two arrays sized
/// once by the source vertex count and separates extractions with a stamp, so extracting B sub-meshes
/// costs one allocation, not B. Vertices are claimed in first-touch order, which is the order the
/// hash-set-then-dictionary form produced.
/// </remarks>
public sealed class SubMeshVertexRemap
{
    private readonly int[] _mapped;
    private readonly int[] _stamp;
    private int _current;

    public SubMeshVertexRemap(int vertexCount)
    {
        if (vertexCount < 0)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));

        _mapped = new int[vertexCount];
        _stamp = new int[vertexCount];
    }

    /// <summary>Starts a new sub-mesh, discarding the previous one's claims.</summary>
    public void Begin()
    {
        if (_current == int.MaxValue)
        {
            Array.Clear(_stamp);
            _current = 0;
        }

        _current++;
    }

    /// <summary>Returns the new index for <paramref name="vertexIndex"/> if it was already claimed.</summary>
    public bool TryGet(int vertexIndex, out int newIndex)
    {
        if (_stamp[vertexIndex] == _current)
        {
            newIndex = _mapped[vertexIndex];
            return true;
        }

        newIndex = -1;
        return false;
    }

    /// <summary>Records the new index for <paramref name="vertexIndex"/> in the current sub-mesh.</summary>
    public void Set(int vertexIndex, int newIndex)
    {
        _stamp[vertexIndex] = _current;
        _mapped[vertexIndex] = newIndex;
    }
}
