using MoleHill.Core.Processing;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Scan;

/// <summary>Clips to the fitted soft-area outlines, retaining paved/unknown islands as holes.</summary>
public sealed class ScanSoftRegion : ITerrainRecoveryRegion
{
    private readonly ScanSurfaceAreas.Result _areas;
    private readonly List<(double X0, double Y0, double X1, double Y1)> _edges = new();
    private readonly List<int>[] _rows;
    private readonly double _bottom;
    private readonly double _rowHeight;

    public ScanSoftRegion(ScanSurfaceAreas.Result areas)
    {
        _areas = areas ?? throw new ArgumentNullException(nameof(areas));
        _bottom = areas.Ground.OriginY - areas.Ground.CellSize;
        _rowHeight = Math.Max(areas.Ground.CellSize, areas.Ground.Rows * areas.Ground.CellSize / 128);
        _rows = Enumerable.Range(0, 130).Select(_ => new List<int>()).ToArray();
        foreach (var boundary in areas.Boundaries.Where(b => b.Kind == ScanSurfaceKind.Soft))
            for (int i = 0; i + 5 < boundary.Points.Length; i += 3)
            {
                var p = boundary.Points;
                int index = _edges.Count;
                _edges.Add((p[i], p[i + 1], p[i + 3], p[i + 4]));
                for (int row = Row(Math.Min(p[i + 1], p[i + 4])); row <= Row(Math.Max(p[i + 1], p[i + 4])); row++)
                    _rows[row].Add(index);
            }
    }

    private int Row(double y) => (int)Math.Clamp(Math.Floor((y - _bottom) / _rowHeight), 0, _rows.Length - 1);

    public bool Contains(double x, double y)
    {
        if (!double.IsFinite(x + y))
            return false;
        bool inside = false;
        double epsilon = _areas.Ground.CellSize * 1e-8;
        foreach (int index in _rows[Row(y)])
        {
            var e = _edges[index];
            if (x >= Math.Min(e.X0, e.X1) - epsilon && x <= Math.Max(e.X0, e.X1) + epsilon &&
                y >= Math.Min(e.Y0, e.Y1) - epsilon && y <= Math.Max(e.Y0, e.Y1) + epsilon &&
                Math.Abs(Geometry2D.Orient(e.X0, e.Y0, e.X1, e.Y1, x, y)) <= epsilon * (Math.Abs(e.X1 - e.X0) + Math.Abs(e.Y1 - e.Y0)))
                return true;
            if ((e.Y0 > y) != (e.Y1 > y) && x < e.X0 + (y - e.Y0) * (e.X1 - e.X0) / (e.Y1 - e.Y0))
                inside = !inside;
        }
        return inside;
    }

    public IReadOnlyList<RegionInputClipper.InputPolyline> Clip(double[] points, bool closed)
    {
        var output = new List<RegionInputClipper.InputPolyline>();
        if (points.Length < 6)
            return output;
        ScanGrid grid = _areas.Ground;
        var current = new List<double>();
        int count = points.Length / 3, segmentCount = closed ? count : count - 1;
        for (int segment = 0; segment < segmentCount; segment++)
        {
            int a = segment * 3, b = ((segment + 1) % count) * 3;
            double dx = points[b] - points[a], dy = points[b + 1] - points[a + 1];
            var cuts = new List<double> { 0, 1 };
            var candidates = new HashSet<int>();
            for (int row = Row(Math.Min(points[a + 1], points[b + 1])); row <= Row(Math.Max(points[a + 1], points[b + 1])); row++)
                candidates.UnionWith(_rows[row]);
            foreach (int index in candidates)
            {
                var e = _edges[index];
                if (Math.Max(e.X0, e.X1) < Math.Min(points[a], points[b]) || Math.Min(e.X0, e.X1) > Math.Max(points[a], points[b]))
                    continue;
                double da = Geometry2D.Orient(e.X0, e.Y0, e.X1, e.Y1, points[a], points[a + 1]);
                double db = Geometry2D.Orient(e.X0, e.Y0, e.X1, e.Y1, points[b], points[b + 1]);
                double ea = Geometry2D.Orient(points[a], points[a + 1], points[b], points[b + 1], e.X0, e.Y0);
                double eb = Geometry2D.Orient(points[a], points[a + 1], points[b], points[b + 1], e.X1, e.Y1);
                if (da != db && (ea <= 0 && eb >= 0 || ea >= 0 && eb <= 0))
                {
                    double t = da / (da - db);
                    if (t > 0 && t < 1)
                        cuts.Add(t);
                }
            }
            cuts.Sort();
            double runStart = double.NaN, runEnd = 0;
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double t0 = cuts[i], t1 = cuts[i + 1];
                if (t1 - t0 < 1e-12)
                    continue;
                double middle = (t0 + t1) * 0.5;
                if (!Contains(points[a] + middle * dx, points[a + 1] + middle * dy))
                {
                    Emit();
                    Flush();
                    continue;
                }
                if (double.IsNaN(runStart))
                    runStart = t0;
                runEnd = t1;
            }
            Emit();

            void Emit()
            {
                if (double.IsNaN(runStart))
                    return;
                double x0 = points[a] + runStart * dx, y0 = points[a + 1] + runStart * dy;
                double z0 = points[a + 2] + runStart * (points[b + 2] - points[a + 2]);
                if (current.Count > 0 && !Same(current[^3], current[^2], x0, y0))
                    Flush();
                if (current.Count == 0)
                    current.AddRange(new[] { x0, y0, z0 });
                current.AddRange(new[] { points[a] + runEnd * dx, points[a + 1] + runEnd * dy,
                    points[a + 2] + runEnd * (points[b + 2] - points[a + 2]) });
                runStart = double.NaN;
            }

        }
        Flush();
        if (closed && output.Count > 1)
        {
            double[] first = output[0].Points, last = output[^1].Points;
            if (Same(last[^3], last[^2], first[0], first[1]))
            {
                double[] joined = last.Concat(first.Skip(3)).ToArray();
                output[0] = new(joined, joined.Length / 3, false);
                output.RemoveAt(output.Count - 1);
            }
        }
        return output;

        bool Same(double x0, double y0, double x1, double y1) => Math.Abs(x0 - x1) + Math.Abs(y0 - y1) <= grid.CellSize * 1e-8;
        void Flush()
        {
            if (current.Count >= 6)
            {
                bool isClosed = closed && current.Count >= 12 && Same(current[0], current[1], current[^3], current[^2]);
                double[] line = isClosed ? current.Take(current.Count - 3).ToArray() : current.ToArray();
                output.Add(new(line, line.Length / 3, isClosed));
            }
            current.Clear();
        }
    }
}
