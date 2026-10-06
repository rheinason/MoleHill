namespace MoleHill.Core.Grading;

/// <summary>
/// The point set of one face's local re-triangulation: the face's corners, its edge splits, and the cut
/// points inside it, merged within tolerance.
/// </summary>
internal sealed class LocalPointBuilder
{
    private readonly double _toleranceSquared;
    private readonly bool _protectCorners;

    /// <summary>Bit mask of the face edges each point lies on (a corner lies on two), or 0.</summary>
    private readonly List<int> _edges = new();

    /// <param name="tolerance">Merge distance.</param>
    /// <param name="protectCorners">
    /// When true, a point added with a corner mask (two edge bits) is always appended, never merged. A face's
    /// corners are distinct mesh vertices even when the edge between two of them is under tolerance, and
    /// merging them drops the triangle on that edge - a hole.
    /// </param>
    public LocalPointBuilder(double tolerance, bool protectCorners)
    {
        _toleranceSquared = tolerance * tolerance;
        _protectCorners = protectCorners;
    }

    public List<double> Xy { get; } = new();
    public List<double> Z { get; } = new();

    public int Count => Z.Count;

    /// <summary>
    /// Adds a point, or returns the nearest existing one within tolerance. A point on a face edge
    /// (<paramref name="edgeMask"/>) never merges into a point lying only on other edges: in a sliver, an
    /// edge passes within tolerance of the far corner or of the next edge, and a single point cannot lie on
    /// both, so merging them leaves the edge cut on one side and whole on the other.
    /// </summary>
    public int Add(Point2D point, double z, int edgeMask = 0)
    {
        if (_protectCorners && IsCornerMask(edgeMask))
            return Append(point, z, edgeMask);

        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < Z.Count; i++)
        {
            if (edgeMask != 0 && _edges[i] != 0 && (_edges[i] & edgeMask) == 0)
                continue;

            double dx = Xy[i * 2] - point.X;
            double dy = Xy[(i * 2) + 1] - point.Y;
            double distance = (dx * dx) + (dy * dy);
            if (distance <= _toleranceSquared && distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
                if (edgeMask == 0)
                    break; // untagged points keep the first match, as every caller expects
            }
        }

        if (best >= 0)
        {
            if (_edges[best] == 0)
                _edges[best] = edgeMask;
            return best;
        }

        return Append(point, z, edgeMask);
    }

    public Point2D GetPoint(int index) => new(Xy[index * 2], Xy[(index * 2) + 1]);

    private static bool IsCornerMask(int edgeMask) => edgeMask is 0b011 or 0b101 or 0b110;

    private int Append(Point2D point, double z, int edgeMask)
    {
        int index = Z.Count;
        Xy.Add(point.X);
        Xy.Add(point.Y);
        Z.Add(z);
        _edges.Add(edgeMask);
        return index;
    }
}
