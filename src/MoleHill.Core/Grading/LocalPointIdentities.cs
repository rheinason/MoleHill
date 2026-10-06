namespace MoleHill.Core.Grading;

/// <summary>
/// What each local point of a face's re-triangulation stands for: one of the face's corners, a split of one
/// of its edges, or anything else (resolved by position). A point that merged onto two different edges, or
/// two different corners, has no single identity and is resolved by position.
/// </summary>
internal sealed class LocalPointIdentities
{
    private const int Ambiguous = -2;
    private readonly Dictionary<int, int> _corner = new();
    private readonly Dictionary<int, int> _edge = new();

    public void SetCorner(int local, int globalVertex) =>
        _corner[local] = _corner.TryGetValue(local, out int existing) && existing != globalVertex ? Ambiguous : globalVertex;

    public void SetEdge(int local, int edgeIndex) =>
        _edge[local] = _edge.TryGetValue(local, out int existing) && existing != edgeIndex ? Ambiguous : edgeIndex;

    public bool TryGetCorner(int local, out int globalVertex) =>
        _corner.TryGetValue(local, out globalVertex) && globalVertex != Ambiguous;

    public bool TryGetEdge(int local, out int edgeIndex)
    {
        edgeIndex = -1;
        if (_corner.ContainsKey(local) || !_edge.TryGetValue(local, out int edge) || edge == Ambiguous)
            return false;

        edgeIndex = edge;
        return true;
    }
}
