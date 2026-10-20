using MoleHill.Core.Geometry;

namespace MoleHill.Core.Scan;

/// <summary>Joins already accepted paired runs only across short, compatible, unobstructed gaps.</summary>
internal static class ScanFeatureBridge
{
    internal sealed record Pair(ScanCurveGeometry High, ScanCurveGeometry Low, double ObservedLength, double Height, double MinimumHeight = double.NaN, double MaximumHeight = double.NaN);
    public static IReadOnlyList<Pair> Join(IReadOnlyList<Pair> input, ScanGround.Result ground,
        double maximumGap, double planTolerance, double heightTolerance, CancellationToken token)
    {
        var runs = input.ToList();
        if (maximumGap <= 0) return runs;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            (int A, int B, Pair First, Pair Second, double Gap)? best = null;
            for (int i = 0; i < runs.Count; i++) for (int j = i + 1; j < runs.Count; j++)
                for (int reverse = 0; reverse < 4; reverse++)
                {
                    var a = (reverse & 1) != 0 ? Reverse(runs[i]) : runs[i];
                    var b = (reverse & 2) != 0 ? Reverse(runs[j]) : runs[j];
                    if (Distance(a.High.Pieces[0].Start, a.High.Pieces[^1].End) < ground.Ground.CellSize * 1e-8 ||
                        Distance(b.High.Pieces[0].Start, b.High.Pieces[^1].End) < ground.Ground.CellSize * 1e-8) continue;
                    double gap = Distance(a.High.Pieces[^1].End, b.High.Pieces[0].Start);
                    if (gap <= ground.Ground.CellSize * 1e-5 || gap > maximumGap || best != null && gap >= best.Value.Gap) continue;
                    if (Compatible(a.High, b.High) && Compatible(a.Low, b.Low) &&
                        Clear(a.High, b.High, a, b) && Clear(a.Low, b.Low, a, b)) best = (i, j, a, b, gap);
                }
            if (best is not { } match) break;
            var first = match.First; var second = match.Second;
            double length = first.ObservedLength + second.ObservedLength;
            var joined = new Pair(Connect(first.High, second.High), Connect(first.Low, second.Low), length,
                (first.Height * first.ObservedLength + second.Height * second.ObservedLength) / length,
                Math.Min(double.IsNaN(first.MinimumHeight) ? first.Height : first.MinimumHeight, double.IsNaN(second.MinimumHeight) ? second.Height : second.MinimumHeight),
                Math.Max(double.IsNaN(first.MaximumHeight) ? first.Height : first.MaximumHeight, double.IsNaN(second.MaximumHeight) ? second.Height : second.MaximumHeight));
            runs[match.A] = joined; runs.RemoveAt(match.B);
        }
        return runs;

        bool Compatible(ScanCurveGeometry a, ScanCurveGeometry b)
        {
            var p = a.Pieces[^1].End; var q = b.Pieces[0].Start;
            double gap = Distance(p, q); if (gap > maximumGap || gap <= 0) return false;
            var before = a.Pieces[^1].PointAt(.9); var after = b.Pieces[0].PointAt(.1);
            double ax = p.X - before.X, ay = p.Y - before.Y, bx = after.X - q.X, by = after.Y - q.Y;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la <= 0 || lb <= 0) return false;
            double cosine = Math.Cos(Math.PI / 18);
            if ((ax * bx + ay * by) / (la * lb) < cosine ||
                (ax * (q.X - p.X) + ay * (q.Y - p.Y)) / (la * gap) < cosine ||
                (bx * (q.X - p.X) + by * (q.Y - p.Y)) / (lb * gap) < cosine) return false;
            double predictedA = p.Z + (p.Z - before.Z) / la * gap;
            double predictedB = q.Z - (after.Z - q.Z) / lb * gap;
            return Math.Abs(predictedA - q.Z) <= 2 * heightTolerance && Math.Abs(predictedB - p.Z) <= 2 * heightTolerance &&
                Math.Abs(ax * (q.Y - p.Y) - ay * (q.X - p.X)) / la <= planTolerance;
        }
        bool Clear(ScanCurveGeometry a, ScanCurveGeometry b, Pair first, Pair second)
        {
            var p = a.Pieces[^1].End; var q = b.Pieces[0].Start; var grid = ground.Ground;
            int steps = Math.Max(2, (int)Math.Ceiling(Distance(p, q) / (grid.CellSize * .5)));
            for (int i = 1; i < steps; i++)
            {
                double t = i / (double)steps;
                int x = (int)Math.Round((p.X + (q.X - p.X) * t - grid.OriginX) / grid.CellSize);
                int y = (int)Math.Round((p.Y + (q.Y - p.Y) * t - grid.OriginY) / grid.CellSize);
                if (x < 0 || y < 0 || x >= grid.Columns || y >= grid.Rows) return false;
                int node = y * grid.Columns + x;
                if (!ground.Domain[node] || !ground.IsGround[node] &&
                    ground.Surface.Values[node] - grid.Values[node] > Math.Max(first.Height, second.Height)) return false;
            }
            foreach (var run in runs) foreach (var rail in new[] { run.High, run.Low })
            {
                var points = rail.PointsXyz;
                for (int i = 0; i + 5 < points.Length; i += 3)
                {
                    double ax = points[i], ay = points[i + 1], bx = points[i + 3], by = points[i + 4];
                    double eps = grid.CellSize * grid.CellSize * 1e-10;
                    if (Geometry2D.Orient(p.X, p.Y, q.X, q.Y, ax, ay) * Geometry2D.Orient(p.X, p.Y, q.X, q.Y, bx, by) < -eps * eps &&
                        Geometry2D.Orient(ax, ay, bx, by, p.X, p.Y) * Geometry2D.Orient(ax, ay, bx, by, q.X, q.Y) < -eps * eps) return false;
                }
            }
            return true;
        }
        ScanCurveGeometry Connect(ScanCurveGeometry a, ScanCurveGeometry b)
        {
            var p = a.Pieces[^1].End; var q = b.Pieces[0].Start; double gap = Distance(p, q);
            var controls = new[] { p.X, p.Y, p.Z, q.X, q.Y, q.Z };
            var bridge = new ScanCurvePiece(1, new double[] { 0, 0, 1, 1 }, controls);
            double station = Length(a);
            var inferred = a.InferredIntervals.Concat(new[] { new ScanInferredInterval(station, station + gap) })
                .Concat(b.InferredIntervals.Select(i => new ScanInferredInterval(i.Start + station + gap, i.End + station + gap))).ToArray();
            double total = station + gap + Length(b);
            double observed = station * a.ObservedShare + Length(b) * b.ObservedShare;
            return new ScanCurveGeometry(a.Pieces.Concat(new[] { bridge }).Concat(b.Pieces).ToArray(),
                planTolerance, heightTolerance, observed / total, inferred);
        }
        Pair Reverse(Pair pair) => new(ReverseCurve(pair.High), ReverseCurve(pair.Low), pair.ObservedLength, pair.Height, pair.MinimumHeight, pair.MaximumHeight);
        ScanCurveGeometry ReverseCurve(ScanCurveGeometry curve)
        {
            double length = Length(curve);
            return new(ScanSurfaceGraph.Reverse(curve.Pieces).ToArray(), planTolerance, heightTolerance, curve.ObservedShare,
                curve.InferredIntervals.Reverse().Select(i => new ScanInferredInterval(length - i.End, length - i.Start)).ToArray());
        }
    }
    internal static double Length(ScanCurveGeometry curve)
    {
        double result = 0;
        for (int i = 0; i + 5 < curve.PointsXyz.Length; i += 3)
            result += Math.Sqrt(Math.Pow(curve.PointsXyz[i + 3] - curve.PointsXyz[i], 2) + Math.Pow(curve.PointsXyz[i + 4] - curve.PointsXyz[i + 1], 2));
        return result;
    }
    private static double Distance((double X, double Y, double Z) a, (double X, double Y, double Z) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
