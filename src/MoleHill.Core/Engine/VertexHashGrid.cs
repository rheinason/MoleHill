namespace MoleHill.Core.Engine;

/// <summary>
/// Nearest mesh vertex (flat XYZ array) within a tolerance, by plan position. A tie keeps the first vertex
/// visited (cells row by row, then insertion order). Shared by <see cref="LocalMeshRefiner"/> and Retopo's
/// cross-field solver. Not interchangeable with <c>SpatialVertexHash</c> (first match, not nearest),
/// <c>SurfaceRemesher.NearVertexIndex</c> (lowest index wins a tie, wider search) or the face-cut
/// <c>GlobalPointLookup</c> (appends as it resolves): each one's tie rule decides which vertex a merge keeps.
/// </summary>
internal sealed class VertexHashGrid
{
    private readonly double[] _vertices;
    private readonly Dictionary<long, List<int>> _cells = new(IndexedMeshTools.CellKeyComparer.Instance);
    private readonly double _inverseCellSize;

    public VertexHashGrid(double[] vertices, int vertexCount, double cellSize)
    {
        _vertices = vertices;
        _inverseCellSize = 1.0 / cellSize;
        for (int i = 0; i < vertexCount; i++)
        {
            long key = CellKey(vertices[i * 3], vertices[i * 3 + 1]);
            if (!_cells.TryGetValue(key, out List<int>? bucket))
            {
                bucket = new List<int>(2);
                _cells.Add(key, bucket);
            }

            bucket.Add(i);
        }
    }

    public int FindNearest(double x, double y, double tolerance)
    {
        double bestDistanceSquared = tolerance * tolerance;
        int best = -1;
        long centerX = (long)Math.Floor(x * _inverseCellSize);
        long centerY = (long)Math.Floor(y * _inverseCellSize);
        for (long cy = centerY - 1; cy <= centerY + 1; cy++)
        {
            for (long cx = centerX - 1; cx <= centerX + 1; cx++)
            {
                if (!_cells.TryGetValue((cx << 32) | (uint)cy, out List<int>? bucket))
                    continue;

                foreach (int index in bucket)
                {
                    double dx = _vertices[index * 3] - x;
                    double dy = _vertices[index * 3 + 1] - y;
                    double distanceSquared = (dx * dx) + (dy * dy);
                    if (distanceSquared < bestDistanceSquared)
                    {
                        bestDistanceSquared = distanceSquared;
                        best = index;
                    }
                }
            }
        }

        return best;
    }

    private long CellKey(double x, double y)
    {
        long cx = (long)Math.Floor(x * _inverseCellSize);
        long cy = (long)Math.Floor(y * _inverseCellSize);
        return (cx << 32) | (uint)cy;
    }
}
