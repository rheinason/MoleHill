using MoleHill.Core.Processing;
using MoleHill.Core.Routing;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Scan;

/// <summary>
/// Traces persistent steps in inferred ground as paired measured top/toe breaklines. Local slopes on
/// both sides are subtracted before testing a rise, so a steep plane is not mistaken for a wall.
/// </summary>
public static class ScanWalls
{
    public sealed class Options
    {
        public required double MinimumHeight { get; init; }
        public required double MinimumLength { get; init; }
        public required double PlanTolerance { get; init; }
        public required double VerticalTolerance { get; init; }
        /// <summary>Keep rails away from a coarser contouring mesh's truncated outer rim.</summary>
        public double BorderMargin { get; init; }
        /// <summary>Largest plan width over which a step may be smeared; zero uses four raster cells.</summary>
        public double MaximumWidth { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }

    public sealed class Wall
    {
        public required FittedRouteCurve TopCurve { get; init; }
        public required FittedRouteCurve ToeCurve { get; init; }
        /// <summary>Light polylines of the fitted output curves, used as fixed TIN edges.</summary>
        public required double[] Top { get; init; }
        public required double[] Toe { get; init; }
        public required double Length { get; init; }
        public required double MeanHeight { get; init; }
    }

    private readonly record struct Crossing(int Start, int End, int Top, int Toe, double Height);

    public static IReadOnlyList<Wall> Trace(ScanGrid ground, Options options, IndexedTriMesh? contouringMesh = null)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(options);
        if (!Positive(options.MinimumHeight) || !Positive(options.MinimumLength) ||
            !Positive(options.PlanTolerance) || !Positive(options.VerticalTolerance) ||
            !double.IsFinite(options.BorderMargin) || options.BorderMargin < 0 ||
            !double.IsFinite(options.MaximumWidth) || options.MaximumWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Wall dimensions and tolerances must be finite and positive.");
        options.CancellationToken.ThrowIfCancellationRequested();
        MeshHeightProjector? footprint = contouringMesh is IndexedTriMesh mesh
            ? new(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount) : null;
        int columns = ground.Columns, rows = ground.Rows;
        int band = options.MaximumWidth > 0
            ? (int)Math.Clamp(Math.Ceiling(options.MaximumWidth / ground.CellSize), 1, Math.Max(1, Math.Max(columns, rows))) : 4;
        int margin = (int)Math.Clamp(Math.Ceiling(options.BorderMargin / ground.CellSize), band + 2, Math.Max(band + 2, Math.Max(columns, rows)));
        var crossings = new List<Crossing>();
        int cornerColumns = columns + 1;
        for (int row = margin; row < rows - margin - 1; row++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            for (int column = margin; column < columns - margin - 1; column++)
            {
                int a = row * columns + column;
                // The directed dual edge has its high ground on the left. This makes stair-stepped
                // diagonal walls chain deterministically without joining unrelated crossing branches.
                Add(a, a + 1, 1, (row + 1) * cornerColumns + column + 1, row * cornerColumns + column + 1);
                Add(a, a + columns, columns, (row + 1) * cornerColumns + column, (row + 1) * cornerColumns + column + 1);
            }
        }

        var outgoing = new Dictionary<int, List<int>>();
        var incoming = new Dictionary<int, int>();
        for (int i = 0; i < crossings.Count; i++)
        {
            Crossing edge = crossings[i];
            if (!outgoing.TryGetValue(edge.Start, out List<int>? list))
                outgoing[edge.Start] = list = new List<int>();
            list.Add(i);
            incoming[edge.End] = incoming.GetValueOrDefault(edge.End) + 1;
        }

        var used = new bool[crossings.Count];
        var result = new List<Wall>();
        for (int i = 0; i < crossings.Count; i++)
        {
            if (incoming.GetValueOrDefault(crossings[i].Start) != 1 || outgoing[crossings[i].Start].Count != 1)
                Follow(i);
        }
        for (int i = 0; i < crossings.Count; i++)
            Follow(i); // Closed chains have no endpoint.
        return result;

        void Add(int a, int b, int stride, int start, int end)
        {
            double za = ground.Values[a], zb = ground.Values[b];
            // Estimate the trend beyond the whole allowed transition, not within a partly rasterised
            // wall. Then follow its adjacent rises as one band, keeping the actual outer top/toe nodes.
            double before = ground.Values[a - band * stride], before2 = ground.Values[a - (band + 1) * stride];
            double after = ground.Values[b + band * stride], after2 = ground.Values[b + (band + 1) * stride];
            if (!double.IsFinite(za + zb + before + before2 + after + after2))
                return;
            double slope = (before - before2 + after2 - after) * 0.5;
            double first = zb - za - slope;
            double noise = options.MinimumHeight * 0.25;
            if (Math.Abs(first) < noise)
                return;
            double previous = za - ground.Values[a - stride] - slope;
            if (previous * first > 0 && Math.Abs(previous) >= noise)
                return; // An earlier crossing owns this same transition band.
            int last = b, width = 1;
            while (width < band)
            {
                double next = ground.Values[last + stride] - ground.Values[last] - slope;
                if (!double.IsFinite(next) || next * first <= 0 || Math.Abs(next) < noise)
                    break;
                last += stride;
                width++;
            }
            double following = ground.Values[last + stride] - ground.Values[last] - slope;
            if (width == band && following * first > 0 && Math.Abs(following) >= noise)
                return; // A broader ramp is not a measured wall.
            zb = ground.Values[last];
            double residual = zb - za - slope * width;
            if (Math.Abs(residual) < options.MinimumHeight || residual * (zb - za) <= 0)
                return;
            crossings.Add(residual > 0 ? new(start, end, last, a, Math.Abs(residual)) : new(end, start, a, last, Math.Abs(residual)));
        }

        void Follow(int first)
        {
            if (used[first])
                return;
            options.CancellationToken.ThrowIfCancellationRequested();
            var chain = new List<Crossing>();
            int current = first;
            while (!used[current])
            {
                used[current] = true;
                Crossing edge = crossings[current];
                chain.Add(edge);
                if (incoming.GetValueOrDefault(edge.End) != 1 || !outgoing.TryGetValue(edge.End, out List<int>? next) || next.Count != 1)
                    break;
                current = next[0];
            }
            double length = chain.Count * ground.CellSize;
            if (length < options.MinimumLength || chain.Count < 2)
                return;
            var top = new List<double>();
            var toe = new List<double>();
            foreach (Crossing edge in chain)
            {
                Append(top, edge.Top);
                Append(toe, edge.Toe);
            }
            bool closed = chain[^1].End == chain[0].Start;
            if (closed)
            {
                top.AddRange(top.Take(3).ToArray());
                toe.AddRange(toe.Take(3).ToArray());
            }
            if (top.Count < 6 || toe.Count < 6)
                return;
            // Each rail must stay on its own side of a one-cell step. Applying a full-cell smoothing
            // tolerance independently to top and toe can make their plan curves overlap or cross.
            double plan = Math.Min(options.PlanTolerance, ground.CellSize * 0.15);
            FittedRouteCurve Fit(List<double> points)
            {
                double[] simple = TerrainCurveRecovery.SimplifyOpen(points.ToArray(), plan, options.VerticalTolerance);
                return RouteCurveFitter.Fit(new IReadOnlyList<double>[] { simple }, plan, options.VerticalTolerance, 0, 0, 0);
            }
            FittedRouteCurve topCurve = Fit(top), toeCurve = Fit(toe);
            if (topCurve.Pieces.Count == 0 || toeCurve.Pieces.Count == 0)
                return;
            if (topCurve.MaxPlanDeviation > plan * 1.01 || toeCurve.MaxPlanDeviation > plan * 1.01 ||
                topCurve.MaxVerticalDeviation > options.VerticalTolerance * 1.01 || toeCurve.MaxVerticalDeviation > options.VerticalTolerance * 1.01)
                return;
            if (!Inside(topCurve.PointsXyz) || !Inside(toeCurve.PointsXyz))
                return;
            length = (PlanLength(topCurve.PointsXyz) + PlanLength(toeCurve.PointsXyz)) * 0.5;
            if (length < options.MinimumLength)
                return;
            result.Add(new Wall
            {
                TopCurve = topCurve,
                ToeCurve = toeCurve,
                Top = TerrainCurveRecovery.SimplifyOpen(topCurve.PointsXyz, plan * 0.1, options.VerticalTolerance * 0.1),
                Toe = TerrainCurveRecovery.SimplifyOpen(toeCurve.PointsXyz, plan * 0.1, options.VerticalTolerance * 0.1),
                Length = length,
                MeanHeight = chain.Average(edge => edge.Height),
            });
        }

        bool Inside(double[] points)
        {
            if (footprint == null)
                return true;
            for (int i = 0; i < points.Length; i += 3)
                if (!footprint.TryGetHeightExtremes(points[i], points[i + 1], ground.CellSize * 1e-6, out _, out _))
                    return false;
            return true;
        }

        void Append(List<double> points, int node)
        {
            double x = ground.NodeX(node % columns), y = ground.NodeY(node / columns);
            if (points.Count > 0 && points[^3] == x && points[^2] == y)
                return;
            points.Add(x);
            points.Add(y);
            points.Add(ground.Values[node]);
        }
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static double PlanLength(double[] points)
    {
        double length = 0;
        for (int i = 3; i < points.Length; i += 3)
        {
            double dx = points[i] - points[i - 3], dy = points[i + 1] - points[i - 2];
            length += Math.Sqrt(dx * dx + dy * dy);
        }
        return length;
    }
}
