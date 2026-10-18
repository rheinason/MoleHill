using MoleHill.Core.Processing;

namespace MoleHill.Core.Scan;

/// <summary>Exact segment clipping to soft lattice cells, retaining paved/unknown islands as holes.</summary>
public sealed class ScanSoftRegion : ITerrainRecoveryRegion
{
    private readonly ScanSurfaceAreas.Result _areas;

    public ScanSoftRegion(ScanSurfaceAreas.Result areas) => _areas = areas ?? throw new ArgumentNullException(nameof(areas));

    public bool Contains(double x, double y)
    {
        ScanGrid grid = _areas.Ground;
        double fx = (x - grid.OriginX) / grid.CellSize, fy = (y - grid.OriginY) / grid.CellSize;
        if (!double.IsFinite(fx + fy) || fx < -1e-9 || fy < -1e-9 || fx > grid.Columns - 1 + 1e-9 || fy > grid.Rows - 1 + 1e-9)
            return false;
        int column = (int)Math.Floor(fx + 0.5), row = (int)Math.Floor(fy + 0.5);
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int c = column + dx, r = row + dy;
                if (c < 0 || r < 0 || c >= grid.Columns || r >= grid.Rows || Math.Abs(fx - c) > 0.5 + 1e-9 || Math.Abs(fy - r) > 0.5 + 1e-9)
                    continue;
                if (_areas.SoftMask[r * grid.Columns + c])
                    return true;
            }
        }
        return false;
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
            Axis(points[a], points[b], grid.OriginX, grid.Columns);
            Axis(points[a + 1], points[b + 1], grid.OriginY, grid.Rows);
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

            void Axis(double start, double end, double origin, int nodes)
            {
                double delta = end - start;
                if (Math.Abs(delta) < grid.CellSize * 1e-12)
                    return;
                Add(origin);
                Add(origin + (nodes - 1) * grid.CellSize);
                int first = (int)Math.Max(0, Math.Ceiling((Math.Min(start, end) - origin) / grid.CellSize - 0.5));
                int last = (int)Math.Min(nodes - 2, Math.Floor((Math.Max(start, end) - origin) / grid.CellSize - 0.5));
                for (int k = first; k <= last; k++)
                    Add(origin + (k + 0.5) * grid.CellSize);
                void Add(double coordinate)
                {
                    double t = (coordinate - start) / delta;
                    if (t > 0 && t < 1)
                        cuts.Add(t);
                }
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
