using MoleHill.Core.Geometry;
using MoleHill.Core.Processing;

namespace MoleHill.Core.Scan;

/// <summary>Dominant plan axes, orthogonal outlines with retained courtyards, and ground-level sampling.</summary>
public static partial class ScanBuildings
{
    private static List<List<P2>> BuildFootprints(Candidate candidate, int[] labels, ScanGrid surface, Options options,
        double originX, double originY, HashSet<int> roofed, out double angle, out HashSet<int> kept)
    {
        angle = 0;
        kept = new HashSet<int>();
        List<P2> points = candidate.Nodes.Where(roofed.Contains).Select(n => new P2(surface.NodeX(n % surface.Columns) - originX,
            surface.NodeY(n / surface.Columns) - originY)).ToList();
        List<P2> hull = ConvexHull(points);
        if (hull.Count < 3)
            return new();
        double bestArea = double.PositiveInfinity;
        for (int i = 0; i < hull.Count; i++)
        {
            P2 a = hull[i], b = hull[(i + 1) % hull.Count];
            double theta = Math.Atan2(b.Y - a.Y, b.X - a.X), cosine = Math.Cos(theta), sine = Math.Sin(theta);
            double minU = double.PositiveInfinity, maxU = double.NegativeInfinity, minV = minU, maxV = maxU;
            foreach (P2 p in hull)
            {
                double u = p.X * cosine + p.Y * sine, v = -p.X * sine + p.Y * cosine;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            double area = (maxU - minU) * (maxV - minV);
            if (area < bestArea)
            {
                bestArea = area;
                angle = theta;
            }
        }
        double cell = surface.CellSize;
        angle = FacadeAngle(candidate, surface, options, angle);
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double left = points.Min(p => p.X * cos + p.Y * sin) - cell;
        double bottom = points.Min(p => -p.X * sin + p.Y * cos) - cell;
        double right = points.Max(p => p.X * cos + p.Y * sin) + cell;
        double top = points.Max(p => -p.X * sin + p.Y * cos) + cell;
        int columns = (int)Math.Ceiling((right - left) / cell) + 1, rows = (int)Math.Ceiling((top - bottom) / cell) + 1;
        var mask = new bool[checked(columns * rows)];
        for (int row = 0; row < rows; row++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            for (int column = 0; column < columns; column++)
            {
                double u = left + column * cell, v = bottom + row * cell;
                double x = originX + u * cos - v * sin, y = originY + u * sin + v * cos;
                int c = (int)Math.Round((x - surface.OriginX) / cell), r = (int)Math.Round((y - surface.OriginY) / cell);
                mask[row * columns + column] = c >= 0 && r >= 0 && c < surface.Columns && r < surface.Rows &&
                    labels[r * surface.Columns + c] == candidate.Label && roofed.Contains(r * surface.Columns + c);
            }
        }
        int[] components = GridMorphology.Label(mask, columns, rows, out List<int> sizes, out _);
        if (sizes.Count == 0)
            return new();
        int largest = sizes.IndexOf(sizes.Max());
        for (int i = 0; i < mask.Length; i++)
            mask[i] = components[i] == largest;
        bool[] inverse = mask.Select(value => !value).ToArray();
        int[] holes = GridMorphology.Label(inverse, columns, rows, out List<int> holeSizes, out List<bool> edge);
        for (int i = 0; i < mask.Length; i++)
            if (holes[i] >= 0 && !edge[holes[i]] && holeSizes[holes[i]] * cell * cell < Math.Max(options.MinimumArea * 0.1, cell * cell * 4))
                mask[i] = true;
        CloseDiagonalContacts(mask, columns, rows);
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                if (mask[row * columns + column])
                {
                    double u = left + column * cell, v = bottom + row * cell;
                    kept.Add(NodeAt(surface, originX + u * cos - v * sin, originY + u * sin + v * cos));
                }
        var grid = new ScanGrid(left, bottom, cell, columns, rows, new double[mask.Length]);
        var rawLoops = new List<List<P2>>();
        foreach (ScanGridEdges.Chain chain in ScanGridEdges.Trace(mask, grid, cancellationToken: options.CancellationToken))
        {
            if (!chain.Closed || chain.Edges.Count < 4)
                continue;
            List<P2> loop = chain.Edges.Select(e =>
            {
                (double x, double y, _) = ScanGridEdges.Corner(grid, e.Start);
                return new P2(x, y);
            }).ToList();
            RemoveRedundant(loop, cell * 1e-8);
            if (loop.Count >= 4)
                rawLoops.Add(loop);
        }
        var regular = new List<List<P2>>();
        foreach (List<P2> raw in rawLoops)
        {
            var loop = Regularize(raw, options.FootprintTolerance);
            RemoveRedundant(loop, cell * 1e-8);
            if (loop.Count < 4 || Math.Abs(SignedArea(loop)) < cell * cell || SelfIntersects(loop))
                loop = Regularize(raw, options.FootprintTolerance * .5);
            regular.Add(loop);
        }
        List<List<P2>> world = Convert(regular);
        if (!LoopsAreSimple(regular, cell * 1e-7))
            world = Convert(rawLoops);
        double areaResult = Math.Abs(world.Sum(SignedArea));
        double[][] containment = world.Select(loop => loop.SelectMany(p => new[] { p.X, p.Y }).ToArray()).ToArray();
        double intersection = points.Count(p => InsideLoops(p, containment)) * cell * cell;
        double sourceArea = candidate.Nodes.Count * cell * cell;
        double union = sourceArea + areaResult - intersection;
        if (union <= 0 || intersection / union < 0.7)
            world = Convert(rawLoops);
        return world;

        List<List<P2>> Convert(List<List<P2>> loops) => loops.Select(loop => loop.Select(p => new P2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos)).ToList()).ToList();
    }

    private static List<P2> Regularize(List<P2> raw, double tolerance)
    {
        double[] flat = raw.SelectMany(p => new[] { p.X, p.Y, 0.0 }).ToArray();
        double[] simple = TerrainCurveRecovery.SimplifyClosed(flat, tolerance, double.PositiveInfinity);
        var points = Enumerable.Range(0, simple.Length / 3).Select(i => new P2(simple[i * 3], simple[i * 3 + 1])).ToList();
        if (points.Count < 4 || SelfIntersects(points))
            return raw;
        // Adjacent short raster steps belong to one facade. Infer its support from the whole run
        // rather than requiring an uninterrupted grid edge several cells long.
        var lines = new List<(bool Vertical, double At, double Weight)>();
        for (int i = 0; i < points.Count; i++)
        {
            P2 a = points[i], b = points[(i + 1) % points.Count];
            bool vertical = Math.Abs(b.Y - a.Y) > Math.Abs(b.X - a.X);
            double weight = vertical ? Math.Abs(b.Y - a.Y) : Math.Abs(b.X - a.X);
            double at = vertical ? (a.X + b.X) * .5 : (a.Y + b.Y) * .5;
            if (lines.Count > 0 && lines[^1].Vertical == vertical)
            {
                var last = lines[^1];
                lines[^1] = (vertical, (last.At * last.Weight + at * weight) / (last.Weight + weight), last.Weight + weight);
            }
            else
                lines.Add((vertical, at, weight));
        }
        if (lines.Count > 1 && lines[0].Vertical == lines[^1].Vertical)
        {
            var a = lines[0]; var b = lines[^1];
            lines[0] = (a.Vertical, (a.At * a.Weight + b.At * b.Weight) / (a.Weight + b.Weight), a.Weight + b.Weight);
            lines.RemoveAt(lines.Count - 1);
        }
        if (lines.Count < 4)
            return points;
        var fitted = new List<P2>();
        for (int i = 0; i < lines.Count; i++)
        {
            var a = lines[(i + lines.Count - 1) % lines.Count]; var b = lines[i];
            fitted.Add(a.Vertical ? new P2(a.At, b.At) : new P2(b.At, a.At));
        }
        double[] xy = fitted.SelectMany(p => new[] { p.X, p.Y }).ToArray();
        // Orthogonalisation is conditional: non-orthogonal annexes retain their thinned outline.
        double[] errors = raw.Select(p => Geometry2D.DistanceToPolygon(p.X, p.Y, xy, fitted.Count)).OrderBy(e => e).ToArray();
        if (SelfIntersects(fitted) || errors[(int)((errors.Length - 1) * .95)] > tolerance || errors[^1] > tolerance * 2)
            return points;
        return fitted;
    }

    private static double FacadeAngle(Candidate candidate, ScanGrid surface, Options options, double fallback)
    {
        int minColumn = candidate.Nodes.Min(n => n % surface.Columns), maxColumn = candidate.Nodes.Max(n => n % surface.Columns);
        int minRow = candidate.Nodes.Min(n => n / surface.Columns), maxRow = candidate.Nodes.Max(n => n / surface.Columns);
        int columns = maxColumn - minColumn + 3, rows = maxRow - minRow + 3;
        var mask = new bool[columns * rows];
        foreach (int node in candidate.Nodes)
            mask[(node / surface.Columns - minRow + 1) * columns + node % surface.Columns - minColumn + 1] = true;
        var grid = new ScanGrid(0, 0, surface.CellSize, columns, rows, new double[mask.Length]);
        double x = 0, y = 0, weight = 0;
        foreach (var chain in ScanGridEdges.Trace(mask, grid, cancellationToken: options.CancellationToken))
        {
            if (!chain.Closed) continue;
            double[] raw = chain.Edges.SelectMany(e =>
            {
                var p = ScanGridEdges.Corner(grid, e.Start);
                return new[] { p.X, p.Y, 0.0 };
            }).ToArray();
            double[] simple = TerrainCurveRecovery.SimplifyClosed(raw, Math.Max(options.FootprintTolerance, surface.CellSize * .75), double.PositiveInfinity);
            for (int i = 0; i < simple.Length; i += 3)
            {
                int next = (i + 3) % simple.Length;
                double dx = simple[next] - simple[i], dy = simple[next + 1] - simple[i + 1];
                double lengthSquared = dx * dx + dy * dy;
                if (lengthSquared < options.FootprintTolerance * options.FootprintTolerance * 4) continue;
                // Fourfold directions combine parallel and perpendicular facades. Long runs outweigh
                // small annexes and raster diagonals; an extremal bounding-box edge has no special vote.
                double theta = Math.Atan2(dy, dx) * 4;
                x += lengthSquared * Math.Cos(theta); y += lengthSquared * Math.Sin(theta); weight += lengthSquared;
            }
        }
        return weight > 0 && Math.Sqrt(x * x + y * y) > weight * .5 ? Math.Atan2(y, x) * .25 : fallback;
    }

    private static List<P2> ConvexHull(List<P2> points)
    {
        P2[] sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        var hull = new List<P2>();
        foreach (P2 p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count;
        for (int i = sorted.Length - 2; i >= 0; i--)
        {
            P2 p = sorted[i];
            while (hull.Count > lower && Cross(hull[^2], hull[^1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        if (hull.Count > 1)
            hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static void RemoveRedundant(List<P2> points, double epsilon)
    {
        bool changed = true;
        while (changed && points.Count > 3)
        {
            changed = false;
            for (int i = points.Count - 1; i >= 0; i--)
            {
                P2 a = points[(i + points.Count - 1) % points.Count], b = points[i], c = points[(i + 1) % points.Count];
                if (Math.Abs(Cross(a, b, c)) <= epsilon * (Math.Abs(c.X - a.X) + Math.Abs(c.Y - a.Y) + epsilon))
                {
                    points.RemoveAt(i);
                    changed = true;
                }
            }
        }
    }

    private static bool SelfIntersects(List<P2> loop)
    {
        for (int i = 0; i < loop.Count; i++)
            for (int j = i + 2; j < loop.Count; j++)
            {
                if (i == 0 && j == loop.Count - 1)
                    continue;
                P2 a = loop[i], b = loop[(i + 1) % loop.Count], c = loop[j], d = loop[(j + 1) % loop.Count];
                if (Cross(a, b, c) * Cross(a, b, d) < 0 && Cross(c, d, a) * Cross(c, d, b) < 0)
                    return true;
            }
        return false;
    }

    private static void CloseDiagonalContacts(bool[] mask, int columns, int rows)
    {
        // A checkerboard corner makes vertical building walls meet four times along one edge.
        // Fill the better-supported missing cell, within one raster cell of the observed outline.
        var queue = new Queue<int>();
        for (int row = 0; row + 1 < rows; row++)
            for (int column = 0; column + 1 < columns; column++)
                queue.Enqueue(row * columns + column);
        while (queue.TryDequeue(out int index))
        {
            int a = index, b = a + 1, c = a + columns, d = c + 1;
            if (mask[a] != mask[d] || mask[b] != mask[c] || mask[a] == mask[b])
                continue;
            int first = mask[a] ? b : a, second = mask[a] ? c : d;
            int changed = Neighbours(first) >= Neighbours(second) ? first : second;
            mask[changed] = true;
            int x = changed % columns, y = changed / columns;
            for (int dy = -1; dy <= 0; dy++)
                for (int dx = -1; dx <= 0; dx++)
                    if (x + dx >= 0 && y + dy >= 0 && x + dx + 1 < columns && y + dy + 1 < rows)
                        queue.Enqueue((y + dy) * columns + x + dx);
        }
        int Neighbours(int i)
        {
            int x = i % columns, y = i / columns;
            return (x > 0 && mask[i - 1] ? 1 : 0) + (x + 1 < columns && mask[i + 1] ? 1 : 0) +
                (y > 0 && mask[i - columns] ? 1 : 0) + (y + 1 < rows && mask[i + columns] ? 1 : 0);
        }
    }

    private static bool LoopsAreSimple(List<List<P2>> loops, double epsilon)
    {
        var edges = new List<(int Loop, int Index, P2 A, P2 B)>();
        for (int loop = 0; loop < loops.Count; loop++)
            for (int i = 0; i < loops[loop].Count; i++)
                edges.Add((loop, i, loops[loop][i], loops[loop][(i + 1) % loops[loop].Count]));
        for (int i = 0; i < edges.Count; i++)
            for (int j = i + 1; j < edges.Count; j++)
            {
                var a = edges[i]; var b = edges[j];
                if (a.Loop == b.Loop && (Math.Abs(a.Index - b.Index) == 1 || Math.Abs(a.Index - b.Index) == loops[a.Loop].Count - 1))
                    continue;
                if (On(a.A, b.A, b.B) || On(a.B, b.A, b.B) || On(b.A, a.A, a.B) || On(b.B, a.A, a.B) ||
                    Cross(a.A, a.B, b.A) * Cross(a.A, a.B, b.B) < 0 && Cross(b.A, b.B, a.A) * Cross(b.A, b.B, a.B) < 0)
                    return false;
            }
        return true;
        bool On(P2 p, P2 a, P2 b) => Math.Abs(Cross(a, b, p)) <= epsilon * (Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y)) &&
            p.X >= Math.Min(a.X, b.X) - epsilon && p.X <= Math.Max(a.X, b.X) + epsilon &&
            p.Y >= Math.Min(a.Y, b.Y) - epsilon && p.Y <= Math.Max(a.Y, b.Y) + epsilon;
    }

    private static double Cross(P2 a, P2 b, P2 c) => Geometry2D.Orient(a.X, a.Y, b.X, b.Y, c.X, c.Y);
    private static bool InsideLoops(P2 p, IReadOnlyList<double[]> loops)
    {
        bool inside = false;
        foreach (double[] loop in loops)
            if (Geometry2D.PointInPolygon(p.X, p.Y, loop, loop.Length / 2))
                inside = !inside;
        return inside;
    }

    private static double GroundAlongFootprint(List<List<P2>> footprints, ScanGrid ground, double originX, double originY)
    {
        double minimum = double.PositiveInfinity;
        foreach (List<P2> loop in footprints)
            for (int i = 0; i < loop.Count; i++)
            {
                P2 a = loop[i], b = loop[(i + 1) % loop.Count];
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Square(b.X - a.X) + Square(b.Y - a.Y)) / ground.CellSize));
                for (int step = 0; step <= steps; step++)
                {
                    double t = (double)step / steps;
                    if (ScanGridSampling.TryHeight(ground, originX + a.X + (b.X - a.X) * t, originY + a.Y + (b.Y - a.Y) * t, out double z))
                        minimum = Math.Min(minimum, z);
                }
            }
        return minimum;
    }
}
