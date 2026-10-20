namespace MoleHill.Core.Scan;

/// <summary>
/// Directed dual-grid boundaries with the mask on the left; diagonal contacts form separate loops. With
/// <c>regions</c>, a boundary also runs between masked nodes of different regions.
/// </summary>
internal static class ScanGridEdges
{
    public readonly record struct Edge(int Start, int End, int Inside, int Outside);
    public sealed record Chain(IReadOnlyList<Edge> Edges, bool Closed);

    public static List<Chain> Trace(bool[] mask, ScanGrid grid, Func<int, int, bool>? include = null,
        CancellationToken cancellationToken = default) => Trace(mask, grid.Columns, grid.Rows, include, null, cancellationToken);

    public static List<Chain> Trace(bool[] mask, int columns, int rows, Func<int, int, bool>? include = null,
        int[]? regions = null, CancellationToken cancellationToken = default)
    {
        int corners = columns + 1;
        var edges = new List<Edge>();
        for (int row = 0; row < rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int column = 0; column < columns; column++)
            {
                int node = row * columns + column;
                if (!mask[node])
                    continue;
                int bl = row * corners + column, br = bl + 1, tl = bl + corners, tr = tl + 1;
                Add(bl, br, row > 0 ? node - columns : -1);
                Add(br, tr, column + 1 < columns ? node + 1 : -1);
                Add(tr, tl, row + 1 < rows ? node + columns : -1);
                Add(tl, bl, column > 0 ? node - 1 : -1);
                void Add(int start, int end, int other)
                {
                    if ((other < 0 || !mask[other] || regions != null && regions[other] != regions[node]) && (include == null || include(node, other)))
                        edges.Add(new Edge(start, end, node, other));
                }
            }
        }
        var outgoing = new Dictionary<int, List<int>>();
        var incoming = new Dictionary<int, int>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (!outgoing.TryGetValue(edges[i].Start, out List<int>? list))
                outgoing[edges[i].Start] = list = new List<int>();
            list.Add(i);
            incoming[edges[i].End] = incoming.GetValueOrDefault(edges[i].End) + 1;
        }
        var visited = new bool[edges.Count];
        var result = new List<Chain>();
        for (int i = 0; i < edges.Count; i++)
            if (!incoming.ContainsKey(edges[i].Start))
                Follow(i);
        for (int i = 0; i < edges.Count; i++)
            Follow(i);
        return result;

        void Follow(int first)
        {
            if (visited[first])
                return;
            cancellationToken.ThrowIfCancellationRequested();
            var chain = new List<Edge>();
            int current = first;
            while (!visited[current])
            {
                visited[current] = true;
                Edge edge = edges[current];
                chain.Add(edge);
                if (edge.End == edges[first].Start)
                    break;
                if (!outgoing.TryGetValue(edge.End, out List<int>? next))
                    break;
                int best = -1;
                double turn = double.NegativeInfinity;
                double dx = edge.End % corners - edge.Start % corners, dy = edge.End / corners - edge.Start / corners;
                foreach (int candidate in next)
                {
                    // A loop keeps to its own region; the next region's boundary can leave the same corner.
                    if (visited[candidate] || regions != null && regions[edges[candidate].Inside] != regions[edge.Inside])
                        continue;
                    Edge n = edges[candidate];
                    double nx = n.End % corners - n.Start % corners, ny = n.End / corners - n.Start / corners;
                    double angle = Math.Atan2(dx * ny - dy * nx, dx * nx + dy * ny);
                    if (angle > turn)
                    {
                        best = candidate;
                        turn = angle;
                    }
                }
                if (best < 0)
                    break;
                current = best;
            }
            result.Add(new Chain(chain, chain[^1].End == chain[0].Start));
        }
    }

    public static (double X, double Y, double Z) Corner(ScanGrid grid, int corner)
    {
        int column = corner % (grid.Columns + 1), row = corner / (grid.Columns + 1);
        double x = grid.OriginX + Math.Clamp(column - 0.5, 0, grid.Columns - 1) * grid.CellSize;
        double y = grid.OriginY + Math.Clamp(row - 0.5, 0, grid.Rows - 1) * grid.CellSize;
        ScanGridSampling.TryHeight(grid, x, y, out double z);
        return (x, y, z);
    }
}
