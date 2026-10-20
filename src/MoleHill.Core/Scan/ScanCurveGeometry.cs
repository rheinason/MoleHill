using MoleHill.Core.Routing;

namespace MoleHill.Core.Scan;

/// <summary>Canonical scan geometry. Rational pieces preserve circular plan shape while heights vary.</summary>
public sealed class ScanCurveGeometry
{
    public ScanCurveGeometry(IReadOnlyList<ScanCurvePiece> pieces, double planTolerance, double heightTolerance,
        double observedShare = 1, IReadOnlyList<ScanInferredInterval>? inferredIntervals = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (!double.IsFinite(planTolerance) || planTolerance <= 0 || !double.IsFinite(heightTolerance) || heightTolerance <= 0 ||
            !double.IsFinite(observedShare) || observedShare < 0 || observedShare > 1)
            throw new ArgumentOutOfRangeException(nameof(planTolerance));
        Pieces = pieces;
        ObservedShare = observedShare;
        InferredIntervals = inferredIntervals ?? Array.Empty<ScanInferredInterval>();
        PlanTolerance = planTolerance;
        VerticalTolerance = heightTolerance;
        var points = new List<double>();
        foreach (var piece in pieces)
        {
            var start = piece.PointAt(0);
            if (points.Count == 0) Add(start);
            else if (Math.Abs(points[^3] - start.X) > planTolerance * 1e-5 ||
                Math.Abs(points[^2] - start.Y) > planTolerance * 1e-5 ||
                Math.Abs(points[^1] - start.Z) > heightTolerance * 1e-5)
                throw new ArgumentException($"Scan curve pieces must meet at their endpoints: ({points[^3]:G17}, {points[^2]:G17}, {points[^1]:G17}) to ({start.X:G17}, {start.Y:G17}, {start.Z:G17}).", nameof(pieces));
            var knots = piece.Knots.Distinct().OrderBy(k => k).ToArray();
            for (int span = 0; span + 1 < knots.Length; span++)
                Subdivide(knots[span], piece.PointAt(knots[span]), knots[span + 1], piece.PointAt(knots[span + 1]), 0);
            void Subdivide(double a, (double X, double Y, double Z) p, double b, (double X, double Y, double Z) q, int depth)
            {
                double mid = (a + b) * .5;
                var m = piece.PointAt(mid);
                var l = piece.PointAt(a * .75 + b * .25);
                var r = piece.PointAt(a * .25 + b * .75);
                bool flat = Near(l, .25) && Near(m, .5) && Near(r, .75);
                if (flat || depth >= 18) { Add(q); return; }
                Subdivide(a, p, mid, m, depth + 1); Subdivide(mid, m, b, q, depth + 1);
                bool Near((double X, double Y, double Z) v, double t) =>
                    Math.Sqrt(Math.Pow(v.X - (p.X + (q.X - p.X) * t), 2) + Math.Pow(v.Y - (p.Y + (q.Y - p.Y) * t), 2)) <= planTolerance * .05 &&
                    Math.Abs(v.Z - (p.Z + (q.Z - p.Z) * t)) <= heightTolerance * .05;
            }
        }
        PointsXyz = points.ToArray();
        void Add((double X, double Y, double Z) p) { points.Add(p.X); points.Add(p.Y); points.Add(p.Z); }
    }
    public IReadOnlyList<ScanCurvePiece> Pieces { get; }
    public double[] PointsXyz { get; }
    public int ControlPointCount => Pieces.Sum(p => p.ControlPointCount) - Math.Max(0, Pieces.Count - 1);
    public double PlanTolerance { get; }
    public double VerticalTolerance { get; }
    public double ObservedShare { get; }
    public IReadOnlyList<ScanInferredInterval> InferredIntervals { get; }
    public static ScanCurveGeometry FromFitted(FittedRouteCurve curve, double plan, double height) =>
        new(curve.Pieces.Select(p => new ScanCurvePiece(p.Degree, p.Knots, p.ControlPoints)).ToArray(), plan, height);
}

