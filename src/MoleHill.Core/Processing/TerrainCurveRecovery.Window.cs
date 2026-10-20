using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Processing;

/// <summary>
/// Local trials for informative recovery. Adding or removing one input only re-triangulates the faces it
/// touches, so a trial rebuilds that neighbourhood alone and compares it with and without the input.
/// Rebuilding the whole terrain for every trial made a 2,000-input scan recovery take tens of minutes.
/// </summary>
public static partial class TerrainCurveRecovery
{
    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
    {
        public static Box Of(double[] xyz)
        {
            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
            for (int i = 0; i + 2 < xyz.Length; i += 3)
            { x0 = Math.Min(x0, xyz[i]); y0 = Math.Min(y0, xyz[i + 1]); x1 = Math.Max(x1, xyz[i]); y1 = Math.Max(y1, xyz[i + 1]); }
            return new(x0, y0, x1, y1);
        }
        public Box Grow(double margin) => new(MinX - margin, MinY - margin, MaxX + margin, MaxY + margin);
        public Box Union(Box other) => new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
        public bool Overlaps(Box other) => MinX <= other.MaxX && other.MinX <= MaxX && MinY <= other.MaxY && other.MinY <= MaxY;
        public bool Contains(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
        public bool Touches(double ax, double ay, double bx, double by) =>
            Math.Max(ax, bx) >= MinX && Math.Min(ax, bx) <= MaxX && Math.Max(ay, by) >= MinY && Math.Min(ay, by) <= MaxY;
    }

    /// <summary>Uniform buckets of point or face ids by plan box.</summary>
    private sealed class PlanBuckets
    {
        private readonly double _x0, _y0, _cell;
        private readonly int _columns, _rows;
        private readonly List<int>[] _cells;

        public PlanBuckets(Box extent, double cell, int count, Func<int, Box> boxOf)
        {
            _x0 = extent.MinX; _y0 = extent.MinY;
            _cell = Math.Max(cell, 1e-9);
            _columns = Math.Clamp((int)((extent.MaxX - extent.MinX) / _cell) + 1, 1, 4096);
            _rows = Math.Clamp((int)((extent.MaxY - extent.MinY) / _cell) + 1, 1, 4096);
            _cells = new List<int>[_columns * _rows];
            for (int i = 0; i < count; i++)
            {
                Box b = boxOf(i);
                (int c0, int r0, int c1, int r1) = Range(b);
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                        (_cells[r * _columns + c] ??= new List<int>()).Add(i);
            }
        }

        private (int, int, int, int) Range(Box b) => (
            Math.Clamp((int)Math.Floor((b.MinX - _x0) / _cell), 0, _columns - 1), Math.Clamp((int)Math.Floor((b.MinY - _y0) / _cell), 0, _rows - 1),
            Math.Clamp((int)Math.Floor((b.MaxX - _x0) / _cell), 0, _columns - 1), Math.Clamp((int)Math.Floor((b.MaxY - _y0) / _cell), 0, _rows - 1));

        public IEnumerable<int> Query(Box b)
        {
            (int c0, int r0, int c1, int r1) = Range(b);
            var seen = new HashSet<int>();
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (_cells[r * _columns + c] is List<int> list)
                        foreach (int id in list)
                            if (seen.Add(id)) yield return id;
        }
    }

    /// <summary>
    /// One local trial's measure over the evaluated vertices: worst miss, area-weighted excess over each vertex's
    /// own tolerance, and the worst miss as a multiple of that tolerance (1 = exactly at tolerance).
    /// </summary>
    private readonly record struct LocalError(double Maximum, double Excess, double Ratio);

    private sealed class LocalTrials
    {
        private readonly double[] _vertices;
        private readonly double[] _weights;
        private readonly double[] _tolerances;
        private readonly double _planTolerance, _lookup;
        private readonly PlanBuckets _reference;
        private PlanBuckets? _faces;
        private TinResult? _tin;

        public LocalTrials(double[] vertices, int count, double[] weights, double tolerance, double planTolerance)
            : this(vertices, count, weights, Enumerable.Repeat(tolerance, count).ToArray(), planTolerance) { }

        public LocalTrials(double[] vertices, int count, double[] weights, double[] tolerances, double planTolerance)
        {
            _vertices = vertices; _weights = weights; _tolerances = tolerances; _planTolerance = planTolerance;
            _lookup = tolerances.Where(t => t > 0).DefaultIfEmpty(planTolerance).Min() * 1e-3;
            Box extent = Box.Of(vertices[..(count * 3)]);
            double cell = Math.Sqrt(Math.Max((extent.MaxX - extent.MinX) * (extent.MaxY - extent.MinY), 1e-12) / Math.Max(count, 1)) * 8;
            _reference = new PlanBuckets(extent, cell, count, i => new Box(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3], vertices[i * 3 + 1]));
        }

        /// <summary>Indexes the current global reconstruction's faces; windows are drawn from them.</summary>
        public void SetReconstruction(TinResult tin)
        {
            _tin = tin;
            Box extent = Box.Of(tin.Vertices[..(tin.VertexCount * 3)]);
            double cell = Math.Sqrt(Math.Max((extent.MaxX - extent.MinX) * (extent.MaxY - extent.MinY), 1e-12) / Math.Max(tin.FaceCount, 1)) * 4;
            _faces = new PlanBuckets(extent, cell, tin.FaceCount, Face);
        }

        private Box Face(int f)
        {
            double[] v = _tin!.Vertices; int[] faces = _tin.Faces;
            int a = faces[f * 3] * 3, b = faces[f * 3 + 1] * 3, c = faces[f * 3 + 2] * 3;
            return new(Math.Min(v[a], Math.Min(v[b], v[c])), Math.Min(v[a + 1], Math.Min(v[b + 1], v[c + 1])),
                Math.Max(v[a], Math.Max(v[b], v[c])), Math.Max(v[a + 1], Math.Max(v[b + 1], v[c + 1])));
        }

        /// <summary>
        /// The faces an input's box touches can all be re-triangulated; their union is measured, and one
        /// further ring of their largest size supplies the neighbours that triangulation needs.
        /// </summary>
        public (Box Measured, Box Context) Window(Box input)
        {
            Box measured = input;
            double largest = _planTolerance;
            foreach (int f in _faces!.Query(input))
            {
                Box face = Face(f);
                if (!face.Overlaps(input)) continue;
                measured = measured.Union(face);
                largest = Math.Max(largest, Math.Max(face.MaxX - face.MinX, face.MaxY - face.MinY));
            }
            return (measured, measured.Grow(largest));
        }

        public LocalError? Measure(Box measured, Box context, IEnumerable<(double[] Points, bool Closed)> chains, IEnumerable<double> spots)
        {
            var xy = new List<double>(); var z = new List<double>(); var segments = new List<int>();
            var index = new Dictionary<CellKey, int>();
            double cell = Math.Max(_planTolerance * 1e-3, 1e-9);
            foreach ((double[] points, bool closed) in chains)
            {
                int n = points.Length / 3;
                for (int i = 0; i < (closed ? n : n - 1); i++)
                {
                    int a = i * 3, b = (i + 1) % n * 3;
                    if (!context.Touches(points[a], points[a + 1], points[b], points[b + 1])) continue;
                    int p = Add(points[a], points[a + 1], points[a + 2]), q = Add(points[b], points[b + 1], points[b + 2]);
                    if (p != q) { segments.Add(p); segments.Add(q); }
                }
                if (n == 1 && context.Contains(points[0], points[1])) Add(points[0], points[1], points[2]);
            }
            using (IEnumerator<double> s = spots.GetEnumerator())
                while (s.MoveNext())
                {
                    double x = s.Current; s.MoveNext(); double y = s.Current; s.MoveNext();
                    if (context.Contains(x, y)) Add(x, y, s.Current);
                }
            if (z.Count < 3) return null;
            TinResult? tin = new TinEngine().Build(xy.ToArray(), z.ToArray(), segments.ToArray(), QualitySettings.None, out _,
                useConvexHull: true, includeEdgeTopology: false);
            if (tin == null) return null;
            var projector = new MeshHeightProjector(tin.Vertices, tin.VertexCount, tin.Faces, tin.FaceCount);
            double maximum = 0, excess = 0, ratio = 0, lookup = _lookup;
            foreach (int v in _reference.Query(measured))
            {
                if (_weights[v] <= 0) continue;
                double x = _vertices[v * 3], y = _vertices[v * 3 + 1];
                if (!measured.Contains(x, y) || !projector.TryGetHeightExtremes(x, y, lookup, out double low, out double high)) continue;
                double miss = Math.Max(Math.Abs(_vertices[v * 3 + 2] - low), Math.Abs(_vertices[v * 3 + 2] - high));
                maximum = Math.Max(maximum, miss);
                excess += _weights[v] * Math.Max(0, miss - _tolerances[v]);
                ratio = Math.Max(ratio, miss / _tolerances[v]);
            }
            return new LocalError(maximum, excess, ratio);

            int Add(double px, double py, double pz)
            {
                var key = new CellKey((long)Math.Round(px / cell), (long)Math.Round(py / cell));
                if (index.TryGetValue(key, out int existing)) return existing;
                index[key] = z.Count; xy.Add(px); xy.Add(py); z.Add(pz);
                return z.Count - 1;
            }
        }
    }
}
