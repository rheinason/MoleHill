namespace MoleHill.Core.Scan;

/// <summary>Removes non-manifold label alternations at junctions of spatial roof patches.</summary>
public static partial class ScanBuildings
{
    private static void SimplifySpatialPatches(List<RoofCell> cells, Dictionary<long, List<CellEdge>> edges,
        RoofModel model, double minimumArea, CancellationToken cancellationToken)
    {
        var neighbours = Enumerable.Range(0, cells.Count).Select(_ => new List<int>()).ToArray();
        foreach (List<CellEdge> uses in edges.Values)
            if (uses.Count == 2)
            {
                neighbours[uses[0].Cell].Add(uses[1].Cell); neighbours[uses[1].Cell].Add(uses[0].Cell);
            }
        int merged = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var visited = new bool[cells.Count];
            List<int>? smallest = null; double smallestArea = minimumArea;
            var queue = new Queue<int>();
            for (int start = 0; start < cells.Count; start++)
            {
                if (visited[start])
                    continue;
                var patch = new List<int>(); double area = 0;
                visited[start] = true; queue.Enqueue(start);
                while (queue.TryDequeue(out int cell))
                {
                    patch.Add(cell); area += Math.Abs(SignedArea(cells[cell].Polygon));
                    foreach (int next in neighbours[cell])
                        if (!visited[next] && cells[next].Plane == cells[start].Plane)
                        {
                            visited[next] = true; queue.Enqueue(next);
                        }
                }
                if (area < smallestArea && patch.Any(cell => neighbours[cell].Any(next => cells[next].Plane != cells[start].Plane)))
                {
                    smallest = patch; smallestArea = area;
                }
            }
            if (smallest == null)
                break;
            int previous = cells[smallest[0]].Plane;
            int[] alternatives = smallest.SelectMany(cell => neighbours[cell]).Select(cell => cells[cell].Plane).Where(plane => plane != previous).Distinct().ToArray();
            int selected = alternatives.MinBy(plane => smallest.Sum(cell => Math.Abs(SignedArea(cells[cell].Polygon)) *
                cells[cell].Polygon.Average(p => Square(model.Planes[plane].Height(p.X, p.Y) - model.Planes[previous].Height(p.X, p.Y)))));
            foreach (int cell in smallest)
                cells[cell] = cells[cell] with { Plane = selected };
            merged++;
            // Every edit removes one entire connected patch into a neighbour: patch count strictly
            // falls, so there is no oscillation. Final accuracy is checked on the constructed shell.
        }
        if (merged > 0)
            model.Diagnostic += $" Merged {merged} sub-minimum-area spatial roof patch(es).";
    }

    private static void RegularizeRoofJunctions(List<RoofCell> cells, int[][] cellXy, List<P2> points,
        Dictionary<long, List<CellEdge>> edges, RoofModel model, double baseZ, double epsilon, CancellationToken cancellationToken)
    {
        var incident = new List<int>[points.Count];
        var links = new List<(int A, int B)>[points.Count];
        var boundary = new bool[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            incident[i] = new(); links[i] = new();
        }
        for (int cell = 0; cell < cells.Count; cell++)
            foreach (int vertex in cellXy[cell].Distinct())
                incident[vertex].Add(cell);
        foreach (List<CellEdge> uses in edges.Values)
        {
            CellEdge edge = uses[0];
            if (uses.Count == 1)
                boundary[edge.A] = boundary[edge.B] = true;
            else if (uses.Count == 2)
            {
                links[edge.A].Add((edge.Cell, uses[1].Cell));
                links[edge.B].Add((edge.Cell, uses[1].Cell));
            }
        }
        var order = new int[points.Count][];
        for (int vertex = 0; vertex < points.Count; vertex++)
        {
            var neighbours = incident[vertex].ToDictionary(cell => cell, _ => new List<int>(2));
            foreach (var link in links[vertex])
            {
                neighbours[link.A].Add(link.B); neighbours[link.B].Add(link.A);
            }
            int first = boundary[vertex] ? incident[vertex].FirstOrDefault(cell => neighbours[cell].Count == 1, incident[vertex][0]) : incident[vertex][0];
            var walk = new List<int>();
            int previous = -1, current = first;
            while (!walk.Contains(current))
            {
                walk.Add(current);
                int next = neighbours[current].FirstOrDefault(other => other != previous, -1);
                if (next < 0)
                    break;
                previous = current; current = next;
            }
            // An invalid plan fan is left to the final shell validator. Plane relabelling cannot
            // repair a footprint that touches itself, and must not hide it.
            order[vertex] = walk.Count == incident[vertex].Count ? walk.ToArray() : Array.Empty<int>();
        }
        bool[] bad = Enumerable.Range(0, points.Count).Select(Bad).ToArray();
        int repairs = 0;
        bool changed = true;
        while (changed && bad.Any(value => value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed = false;
            for (int vertex = 0; vertex < points.Count; vertex++)
            {
                if (!bad[vertex])
                    continue;
                int bestCell = -1, bestPlane = -1, bestReduction = 0;
                double bestCost = double.PositiveInfinity;
                int[] candidates = incident[vertex].Select(cell => cells[cell].Plane).Distinct().ToArray();
                foreach (int cell in incident[vertex])
                {
                    int old = cells[cell].Plane;
                    int[] affected = cellXy[cell].Distinct().ToArray();
                    int before = affected.Count(v => bad[v]);
                    foreach (int plane in candidates)
                    {
                        if (plane == old)
                            continue;
                        cells[cell] = cells[cell] with { Plane = plane };
                        int reduction = before - affected.Count(Bad);
                        if (reduction <= 0)
                            continue;
                        double cost = Math.Abs(SignedArea(cells[cell].Polygon)) * cells[cell].Polygon.Average(p =>
                            Square(model.Planes[old].Height(p.X, p.Y) - model.Planes[plane].Height(p.X, p.Y)));
                        if (reduction > bestReduction || reduction == bestReduction && cost < bestCost)
                        {
                            bestReduction = reduction; bestCost = cost; bestCell = cell; bestPlane = plane;
                        }
                    }
                    cells[cell] = cells[cell] with { Plane = old };
                }
                if (bestCell < 0)
                    continue;
                cells[bestCell] = cells[bestCell] with { Plane = bestPlane };
                foreach (int affected in cellXy[bestCell].Distinct())
                    bad[affected] = Bad(affected);
                repairs++; changed = true;
            }
        }
        if (repairs > 0)
            model.Diagnostic += $" Resolved {repairs} spatial roof junction(s); final shell accuracy is remeasured.";

        bool Bad(int vertex)
        {
            int[] fan = order[vertex];
            if (fan.Length == 0)
                return false;
            P2 p = points[vertex];
            double[] heights = fan.Select(cell => model.Planes[cells[cell].Plane].Height(p.X, p.Y)).ToArray();
            if (boundary[vertex])
                heights = heights.Append(baseZ).ToArray();
            double[] levels = heights.Distinct().OrderBy(z => z).ToArray();
            for (int i = 0; i + 1 < levels.Length; i++)
            {
                if (levels[i + 1] - levels[i] <= epsilon)
                    continue;
                double level = (levels[i] + levels[i + 1]) * .5;
                int transitions = 0;
                for (int j = 0; j < heights.Length; j++)
                    if ((heights[j] > level) != (heights[(j + 1) % heights.Length] > level))
                        transitions++;
                if (transitions > 2)
                    return true;
            }
            return false;
        }
    }
}
