using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// The split vertices of each mesh edge, shared by the two faces on it, for algorithms that re-triangulate
/// faces one at a time (constraint insertion, zone splitting). A split within tolerance of an endpoint is
/// that endpoint; within tolerance of an existing split of the same edge, that split; otherwise a new vertex
/// placed exactly on the edge, its elevation linear along it.
/// </summary>
/// <remarks>
/// Resolving splits by position instead - the nearest vertex within tolerance - lets a split merge into a
/// split of the next edge over wherever two edges pass within tolerance of each other (a band of slivers).
/// One face then takes the point as on its edge and the neighbour as inside its face, and the two overlap.
/// Keyed by edge, both faces land on one vertex that lies on the edge they share.
/// </remarks>
internal sealed class MeshEdgeSplitRegistry
{
    private readonly List<double> _vertices;
    private readonly Func<double, double, double, int> _append;
    private readonly double _tolerance;
    private readonly Dictionary<long, List<(double T, int Vertex)>> _splits =
        IndexedMeshTools.CreateEdgeKeyMap<List<(double T, int Vertex)>>(64);
    private readonly Dictionary<int, long> _hostOf = new();

    /// <param name="vertices">The growing global XYZ vertex list.</param>
    /// <param name="append">Adds a vertex at exactly the given position and returns its index.</param>
    public MeshEdgeSplitRegistry(List<double> vertices, Func<double, double, double, int> append, double tolerance)
    {
        _vertices = vertices;
        _append = append;
        _tolerance = tolerance;
    }

    /// <summary>The edge a split vertex was placed on.</summary>
    public bool TryGetHost(int vertex, out long edgeKey) => _hostOf.TryGetValue(vertex, out edgeKey);

    public int Resolve(int start, int end, double x, double y)
    {
        int u = Math.Min(start, end), v = Math.Max(start, end);
        double ux = _vertices[u * 3], uy = _vertices[(u * 3) + 1], uz = _vertices[(u * 3) + 2];
        double dx = _vertices[v * 3] - ux, dy = _vertices[(v * 3) + 1] - uy;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= _tolerance)
            return u;

        double t = Math.Clamp((((x - ux) * dx) + ((y - uy) * dy)) / (length * length), 0.0, 1.0);
        double parameterTolerance = _tolerance / length;
        if (t <= parameterTolerance)
            return u;
        if (t >= 1.0 - parameterTolerance)
            return v;

        long key = IndexedMeshTools.GetEdgeKey(u, v);
        if (!_splits.TryGetValue(key, out var splits))
            _splits[key] = splits = new List<(double T, int Vertex)>(2);

        int best = -1;
        double bestDistance = double.MaxValue;
        foreach ((double existingT, int vertex) in splits)
        {
            double distance = Math.Abs(existingT - t);
            if (distance <= parameterTolerance && distance < bestDistance)
            {
                best = vertex;
                bestDistance = distance;
            }
        }

        if (best >= 0)
            return best;

        double z = uz + (t * (_vertices[(v * 3) + 2] - uz));
        int created = _append(ux + (t * dx), uy + (t * dy), z);
        splits.Add((t, created));
        _hostOf[created] = key;
        return created;
    }
}
