using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Interop;

/// <summary>
/// Deterministic, point-budgeted Toposolid sampling. Starts with boundary/breakline-critical points,
/// adds spatial cell extrema, then iteratively inserts the largest measured TIN reconstruction errors.
/// </summary>
public static class ToposolidPointReducer
{
    public sealed class ReductionResult
    {
        public required double[] Vertices { get; init; }

        public int PointCount => Vertices.Length / 3;

        public required double MaximumMeasuredVerticalError { get; init; }
    }

    private readonly record struct Point(double X, double Y, double Z);

    private readonly record struct SampleError(Point Point, double Error);

    private readonly record struct CellKey(long X, long Y);

    private sealed class PointAccumulator
    {
        private readonly double _tolerance;
        private readonly double _inverseTolerance;
        private readonly Dictionary<CellKey, List<int>> _cells = new();

        public PointAccumulator(double tolerance)
        {
            _tolerance = Math.Max(tolerance, 1e-9);
            _inverseTolerance = 1.0 / _tolerance;
        }

        public List<Point> Points { get; } = new();

        public bool TryAdd(Point point, out string? error)
        {
            error = null;
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            {
                error = "Encountered a non-finite Toposolid point.";
                return false;
            }

            CellKey cell = ToCell(point);
            for (long x = cell.X - 1; x <= cell.X + 1; x++)
            {
                for (long y = cell.Y - 1; y <= cell.Y + 1; y++)
                {
                    if (!_cells.TryGetValue(new CellKey(x, y), out List<int>? candidates))
                        continue;
                    foreach (int index in candidates)
                    {
                        Point existing = Points[index];
                        if (Math.Abs(existing.X - point.X) > _tolerance ||
                            Math.Abs(existing.Y - point.Y) > _tolerance)
                            continue;
                        if (Math.Abs(existing.Z - point.Z) > _tolerance * 4.0)
                        {
                            error = $"Toposolid points have conflicting elevations at XY ({point.X:G10}, {point.Y:G10}).";
                            return false;
                        }

                        return true;
                    }
                }
            }

            int newIndex = Points.Count;
            Points.Add(point);
            if (!_cells.TryGetValue(cell, out List<int>? list))
            {
                list = new List<int>(1);
                _cells[cell] = list;
            }

            list.Add(newIndex);
            return true;
        }

        public bool Contains(Point point)
        {
            CellKey cell = ToCell(point);
            for (long x = cell.X - 1; x <= cell.X + 1; x++)
            {
                for (long y = cell.Y - 1; y <= cell.Y + 1; y++)
                {
                    if (!_cells.TryGetValue(new CellKey(x, y), out List<int>? candidates))
                        continue;
                    foreach (int index in candidates)
                    {
                        Point existing = Points[index];
                        if (Math.Abs(existing.X - point.X) <= _tolerance &&
                            Math.Abs(existing.Y - point.Y) <= _tolerance)
                            return true;
                    }
                }
            }

            return false;
        }

        private CellKey ToCell(Point point) => new(
            (long)Math.Floor(point.X * _inverseTolerance),
            (long)Math.Floor(point.Y * _inverseTolerance));
    }

    public static bool TryReduce(
        double[] sourceVertices,
        int sourcePointCount,
        double[] criticalVertices,
        int criticalPointCount,
        int maximumPointCount,
        double verticalTolerance,
        double xyTolerance,
        out ReductionResult? result,
        out string? error)
    {
        result = null;
        error = null;
        if (sourcePointCount < 3 || sourceVertices.Length < sourcePointCount * 3)
        {
            error = "At least three source points are required for Toposolid reduction.";
            return false;
        }

        if (criticalPointCount < 0 || criticalVertices.Length < criticalPointCount * 3)
        {
            error = "Critical Toposolid point data is incomplete.";
            return false;
        }

        if (maximumPointCount < 3 || criticalPointCount > maximumPointCount)
        {
            error = "The Toposolid point budget is smaller than the required critical point set.";
            return false;
        }

        var source = ReadPoints(sourceVertices, sourcePointCount);
        var selected = new PointAccumulator(xyTolerance);
        foreach (Point point in ReadPoints(criticalVertices, criticalPointCount))
        {
            if (!selected.TryAdd(point, out error))
                return false;
        }

        int initialTarget = Math.Min(
            maximumPointCount,
            Math.Max(256, (int)Math.Ceiling(Math.Sqrt(source.Count) * 4.0)));
        AddGridExtrema(source, initialTarget, selected);
        FillDeterministically(source, initialTarget, selected);

        double maximumError = double.PositiveInfinity;
        for (int iteration = 0; iteration < 10; iteration++)
        {
            if (!TryMeasureErrors(source, selected, xyTolerance, out List<SampleError> errors, out maximumError, out error))
                return false;
            if (maximumError <= verticalTolerance || selected.Points.Count >= maximumPointCount || errors.Count == 0)
                break;

            int remaining = maximumPointCount - selected.Points.Count;
            int addCount = Math.Min(remaining, Math.Max(64, selected.Points.Count / 2));
            foreach (SampleError sample in errors
                         .OrderByDescending(item => item.Error)
                         .ThenBy(item => item.Point.X)
                         .ThenBy(item => item.Point.Y)
                         .Take(addCount))
            {
                selected.TryAdd(sample.Point, out _);
            }
        }

        if (selected.Points.Count < 3)
        {
            error = "Toposolid reduction produced fewer than three unique points.";
            return false;
        }

        result = new ReductionResult
        {
            Vertices = Flatten(selected.Points),
            MaximumMeasuredVerticalError = maximumError
        };
        return true;
    }

    private static List<Point> ReadPoints(double[] values, int count)
    {
        var result = new List<Point>(count);
        for (int index = 0; index < count; index++)
            result.Add(new Point(values[index * 3], values[index * 3 + 1], values[index * 3 + 2]));
        return result;
    }

    private static double[] Flatten(IReadOnlyList<Point> points)
    {
        var result = new double[points.Count * 3];
        for (int index = 0; index < points.Count; index++)
        {
            result[index * 3] = points[index].X;
            result[index * 3 + 1] = points[index].Y;
            result[index * 3 + 2] = points[index].Z;
        }

        return result;
    }

    private static void AddGridExtrema(IReadOnlyList<Point> source, int target, PointAccumulator selected)
    {
        if (source.Count == 0 || selected.Points.Count >= target)
            return;

        double minX = source.Min(point => point.X);
        double maxX = source.Max(point => point.X);
        double minY = source.Min(point => point.Y);
        double maxY = source.Max(point => point.Y);
        double width = Math.Max(maxX - minX, 1e-9);
        double height = Math.Max(maxY - minY, 1e-9);
        double aspect = width / height;
        int cellTarget = Math.Max(1, (target - selected.Points.Count) / 2);
        int xCells = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cellTarget * aspect)));
        int yCells = Math.Max(1, (int)Math.Ceiling((double)cellTarget / xCells));
        var extrema = new Dictionary<CellKey, (Point Min, Point Max)>();
        foreach (Point point in source)
        {
            long x = Math.Min(xCells - 1, (long)Math.Floor((point.X - minX) / width * xCells));
            long y = Math.Min(yCells - 1, (long)Math.Floor((point.Y - minY) / height * yCells));
            var key = new CellKey(x, y);
            if (!extrema.TryGetValue(key, out var pair))
                extrema[key] = (point, point);
            else
                extrema[key] = (point.Z < pair.Min.Z ? point : pair.Min, point.Z > pair.Max.Z ? point : pair.Max);
        }

        foreach (var pair in extrema.OrderBy(item => item.Key.Y).ThenBy(item => item.Key.X).Select(item => item.Value))
        {
            if (selected.Points.Count >= target)
                break;
            selected.TryAdd(pair.Min, out _);
            if (selected.Points.Count < target)
                selected.TryAdd(pair.Max, out _);
        }
    }

    private static void FillDeterministically(IReadOnlyList<Point> source, int target, PointAccumulator selected)
    {
        if (selected.Points.Count >= target)
            return;
        int stride = Math.Max(1, source.Count / Math.Max(1, target - selected.Points.Count));
        for (int index = 0; index < source.Count && selected.Points.Count < target; index += stride)
            selected.TryAdd(source[index], out _);
    }

    private static bool TryMeasureErrors(
        IReadOnlyList<Point> source,
        PointAccumulator selected,
        double tolerance,
        out List<SampleError> errors,
        out double maximumError,
        out string? error)
    {
        errors = new List<SampleError>();
        maximumError = 0.0;
        double[] xyz = Flatten(selected.Points);
        var xy = new double[selected.Points.Count * 2];
        var z = new double[selected.Points.Count];
        for (int index = 0; index < selected.Points.Count; index++)
        {
            xy[index * 2] = xyz[index * 3];
            xy[index * 2 + 1] = xyz[index * 3 + 1];
            z[index] = xyz[index * 3 + 2];
        }

        var engine = new TinEngine();
        TinResult? triangulation = engine.Build(
            xy,
            z,
            Array.Empty<int>(),
            QualitySettings.None,
            out error,
            useConvexHull: true,
            includeEdgeTopology: false);
        if (triangulation == null)
            return false;

        var projector = new MeshHeightProjector(
            triangulation.Vertices,
            triangulation.VertexCount,
            triangulation.Faces,
            triangulation.FaceCount);
        foreach (Point point in source)
        {
            if (selected.Contains(point))
                continue;
            if (!projector.TryProjectZ(point.X, point.Y, point.Z, tolerance, out double projectedZ, out _))
                continue;
            double sampleError = Math.Abs(projectedZ - point.Z);
            maximumError = Math.Max(maximumError, sampleError);
            errors.Add(new SampleError(point, sampleError));
        }

        return true;
    }
}
