using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Resolves a cut point to an output vertex: the nearest existing vertex within tolerance, or a new one.
/// Each cell is a singly linked list threaded through one array and appended in vertex order, so
/// nearest-point ties resolve exactly as per-cell lists would - including duplicate XY vertices at
/// different Z - without a list object per cell.
/// </summary>
internal sealed class GlobalPointLookup
{
    private readonly List<double> _vertices;
    private readonly double _toleranceSquared;
    private readonly double _inverseCellSize;
    private readonly Dictionary<long, (int Head, int Tail)> _cells = new(IndexedMeshTools.CellKeyComparer.Instance);
    private readonly List<int> _next;

    public GlobalPointLookup(List<double> vertices, double tolerance)
    {
        _vertices = vertices;
        double resolvedTolerance = Math.Max(tolerance, 1e-9);
        _toleranceSquared = resolvedTolerance * resolvedTolerance;
        _inverseCellSize = 1.0 / resolvedTolerance;

        int vertexCount = vertices.Count / 3;
        _next = new List<int>(vertexCount);
        for (int i = 0; i < vertexCount; i++)
            Register(i, vertices[i * 3], vertices[(i * 3) + 1]);
    }

    public int Resolve(Point2D point, double z)
    {
        if (TryFind(point, out int existing))
            return existing;

        return Append(point.X, point.Y, z);
    }

    /// <summary>Adds a vertex exactly where given, without merging, and registers it for later lookups.</summary>
    public int Append(double x, double y, double z)
    {
        int index = _vertices.Count / 3;
        _vertices.Add(x);
        _vertices.Add(y);
        _vertices.Add(z);
        Register(index, x, y);
        return index;
    }

    /// <summary>Twice the plan area of the triangle on three resolved vertices.</summary>
    public double ProjectedCross(int a, int b, int c)
    {
        double ax = _vertices[a * 3], ay = _vertices[(a * 3) + 1];
        return Math.Abs(((_vertices[b * 3] - ax) * (_vertices[(c * 3) + 1] - ay)) -
                        ((_vertices[(b * 3) + 1] - ay) * (_vertices[c * 3] - ax)));
    }

    private bool TryFind(Point2D point, out int index)
    {
        long cellX = ToCell(point.X);
        long cellY = ToCell(point.Y);
        double bestDistanceSquared = double.MaxValue;
        int bestIndex = -1;

        for (long dx = -1; dx <= 1; dx++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                if (!_cells.TryGetValue(FaceCutGeometry.PackKey(cellX + dx, cellY + dy), out var cell))
                    continue;

                for (int candidate = cell.Head; candidate >= 0; candidate = _next[candidate])
                {
                    double deltaX = _vertices[candidate * 3] - point.X;
                    double deltaY = _vertices[(candidate * 3) + 1] - point.Y;
                    double distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
                    if (distanceSquared > _toleranceSquared || distanceSquared >= bestDistanceSquared)
                        continue;

                    bestDistanceSquared = distanceSquared;
                    bestIndex = candidate;
                }
            }
        }

        index = bestIndex;
        return bestIndex >= 0;
    }

    private void Register(int index, double x, double y)
    {
        long key = FaceCutGeometry.PackKey(ToCell(x), ToCell(y));
        _next.Add(-1);
        if (_cells.TryGetValue(key, out var cell))
        {
            _next[cell.Tail] = index;
            _cells[key] = (cell.Head, index);
        }
        else
            _cells[key] = (index, index);
    }

    private long ToCell(double value) => (long)Math.Floor(value * _inverseCellSize);
}
